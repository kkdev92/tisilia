using System.Diagnostics;
using System.Text.Json;
using Tisilia.Generator.Additional;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Sends requests the way Explorer builds them: the runtime's contract interpreter (its built dist) and the additional codecs module
/// encode each case from the exported contract, and fetch sends it. Each result is "status body", or "code: message" when the
/// encoder refuses the arguments before sending. In a form body, <c>{"$map": [[key, value], …]}</c> stands for a map field's value:
/// a Map whose keys and values the field's codecs read from those editor texts, as Explorer reads its rows; <c>{"$input": "…"}</c>
/// is a value the field's codec reads from that editor text, and <c>{"$file": {"name", "text"}}</c> a file of that text's UTF-8 bytes. A JSON body
/// <c>{"$json": "…"}</c> is that JSON text, read by the body's codec as Explorer reads its JSON editor (an XML body's too); a binary body
/// <c>{"$utf8": "…"}</c> is the UTF-8 bytes of that text, and <c>{"$stream": {"size": n}}</c> a ReadableStream of n bytes (i % 251, 16 KiB chunks). With <c>execute</c> the runtime also sends the request and decodes the
/// response (its execute API): each result is that result as JSON, with 64-bit integers as strings and maps as <c>{"$entries": [[key, value], …]}</c>.
/// In the arguments, <c>{"$same": path}</c> is the value at that path of the arguments (<c>"/body/first"</c>): a shared value, or a value
/// that contains itself when the path is an ancestor, and <c>{"$entries": [[key, value], …]}</c> is a Map; in a result, a value reached
/// again is <c>{"$same": the path where it was first}</c>.
/// </summary>
internal static class InterpreterRequests
{
    // subscribe: each case is {"args": …, "reconnect": …} sent through the runtime's subscribe API; each result is
    // {"result": …, "events": [{"id", "data"}, …]}
    public static async Task<IReadOnlyList<string>> SendAsync(string directory, string contract, string baseUrl, IReadOnlyList<(string Operation, object Args)> cases, bool execute = false, bool subscribe = false)
    {
        var runtime = Path.Combine(FixtureTests.RepoRoot(), "src", "frontend", "runtime", "dist", "index.js");
        Assert.True(File.Exists(runtime), "build the runtime first (npm run build): " + runtime);
        var contractPath = Path.Combine(directory, "contract.json");
        var modulePath = Path.Combine(directory, AdditionalModule.ScriptFile);
        var casesPath = Path.Combine(directory, "cases.json");
        var script = Path.Combine(directory, "send.mjs");
        await File.WriteAllTextAsync(contractPath, contract);
        await File.WriteAllBytesAsync(modulePath, AdditionalModule.Script());
        await File.WriteAllTextAsync(casesPath, JsonSerializer.Serialize(cases.Select(c => new { operation = c.Operation, args = c.Args })));
        await File.WriteAllTextAsync(script, """
            import { readFileSync } from "node:fs";
            import { pathToFileURL } from "node:url";
            const [runtime, contract, module, moduleId, baseUrl, cases, mode] = process.argv.slice(2);
            const { createContractRegistry, createCodecContext, execute, parseJson, prepareRequest, subscribe } = await import(pathToFileURL(runtime).href);
            const exports = await import(pathToFileURL(module).href);
            const document = JSON.parse(readFileSync(contract, "utf8"));
            const { operations, registry } = createContractRegistry(document, { modules: new Map([[moduleId, exports]]) });
            const parse = (use, text) => registry.get(use.codecId).parseRequestInput(text, createCodecContext());
            const maps = (fields, body) => {
              for (const field of fields) {
                const value = body?.[field.name];
                if (value === undefined) { continue; }
                const one = (v) => v?.$input !== undefined ? parse(field.use, v.$input)
                  : v?.$file !== undefined ? { bytes: new TextEncoder().encode(v.$file.text), fileName: v.$file.name } : v;
                if (value?.$map !== undefined) { body[field.name] = new Map(value.$map.map(([k, v]) => [parse(field.keyUse, k), parse(field.use, v)])); }
                else if (field.kind === "object") { for (const item of field.repeated ? value : [value]) { maps(field.fields, item); } }
                else { body[field.name] = field.repeated ? value.map(one) : one(value); }
              }
            };
            // a value reached again (a shared value or a cycle of a ReferenceHandler.Preserve response) is {"$same": its first path}
            const plain = (value) => {
              const seen = new Map();
              const walk = (v, path) => {
                if (typeof v === "bigint") { return v.toString(); }
                if (v === null || typeof v !== "object" || v instanceof Uint8Array) { return v; }
                if (!Object.isFrozen(v)) {
                  if (seen.has(v)) { return { $same: seen.get(v) }; }
                  seen.set(v, path);
                }
                if (v instanceof Map || v?.constructor?.name === "TisiliaMap") { return { $entries: [...v.entries()].map(([k, x]) => [walk(k, path), walk(x, path + "/" + String(k))]) }; }
                if (Array.isArray(v)) { return v.map((x, i) => walk(x, path + "/" + i)); }
                return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, walk(x, path + "/" + k)]));
              };
              return walk(value, "");
            };
            // {"$same": path} in the arguments is the value at that path of the arguments: a shared value, or a cycle when it is an ancestor;
            // {"$entries": [[key, value], …]} is a Map of those entries
            const linked = (root) => {
              const at = (path) => path.split("/").slice(1).reduce((v, k) => v?.[k], root);
              const visit = (v) => {
                for (const [k, x] of Object.entries(v)) {
                  if (x === null || typeof x !== "object") { continue; }
                  if (typeof x.$same === "string" && Object.keys(x).length === 1) { v[k] = at(x.$same); }
                  else if (Array.isArray(x.$entries) && Object.keys(x).length === 1) { visit(x.$entries); v[k] = new Map(x.$entries); }
                  else { visit(x); }
                }
              };
              visit(root);
              return root;
            };
            const results = [];
            for (const { operation, args: given } of JSON.parse(readFileSync(cases, "utf8"))) {
              const args = linked(given);
              try {
                const form = document.operations.find(o => o.id === operation).requestBody;
                if (form?.kind === "form" && args.body !== undefined) { maps(form.fields, args.body); }
                if ((form?.kind === "json" || form?.kind === "xml") && args.body?.$json !== undefined) { args.body = registry.get(form.use.codecId).parseRequestInput(parseJson(args.body.$json), createCodecContext()); }
                if (form?.kind === "binary" && args.body?.$utf8 !== undefined) { args.body = new TextEncoder().encode(args.body.$utf8); }
                if (form?.kind === "binary" && args.body?.$stream !== undefined) {
                  const size = args.body.$stream.size; let sent = 0;
                  args.body = new ReadableStream({ pull(c) { if (sent >= size) { c.close(); return; } const n = Math.min(16384, size - sent); c.enqueue(Uint8Array.from({ length: n }, (_, i) => (sent + i) % 251)); sent += n; } });
                }
                if (mode === "subscribe") {
                  const events = [];
                  const result = await subscribe(operations.get(operation), args.args ?? {}, { baseUrl, ...(args.reconnect === undefined ? {} : { reconnect: args.reconnect }) }, e => { events.push({ id: e.id, data: e.data }); });
                  results.push(JSON.stringify({ result, events }, (_, v) => typeof v === "bigint" ? v.toString() : v));
                  continue;
                }
                if (mode === "execute") {
                  results.push(JSON.stringify(plain(await execute(operations.get(operation), args, { baseUrl }))));
                  continue;
                }
                const prepared = prepareRequest(operations.get(operation), args, { baseUrl });
                const response = await fetch(prepared.url, { method: prepared.method, headers: prepared.headers, body: prepared.bodyBytes ?? prepared.bodyStream, ...(prepared.bodyStream === undefined ? {} : { duplex: "half" }) });
                results.push(response.status + " " + await response.text());
              } catch (error) {
                results.push(error.code + ": " + error.message);
              }
            }
            console.log(JSON.stringify(results));
            """);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { script, runtime, contractPath, modulePath, AdditionalModule.ModuleId, baseUrl, casesPath, subscribe ? "subscribe" : execute ? "execute" : "send" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        return JsonSerializer.Deserialize<string[]>(await output)!;
    }
}
