import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import { parseJson } from "@kkdev92/tisilia-runtime";
import { credentialShapes } from "../src/auth.js";
import { apiBaseOf, buildArgs, copyToClipboard, describeValue, execute, executionStatus, filterOperations, loadExplorer, prettyWire, preview, redactHeaders, redactTree, responseOf, summarize, type ExplorerModel } from "../src/explorer.js";
import { curlSnippet } from "../src/snippets.js";

const snippetOptions = { apiId: "sample-api", operationId: "users.put", baseUrl: "http://api.test", credentials: [], reveal: false } as const;

const sampleDir = fileURLToPath(new URL("../../../../tests/fixtures/", import.meta.url));
const contractText = readFileSync(sampleDir + "minimal-api.contract.json", "utf8");

async function load(document: string = contractText): Promise<ExplorerModel> {
  return loadExplorer({
    baseHref: "http://app.test/__tisilia/",
    fetchImpl: (async (input: RequestInfo | URL) => {
      expect(String(input)).toBe("http://app.test/__tisilia/contract");
      return new Response(document, { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch,
    importImpl: async (url) => {
      // the host serves each module's declared browser artifact path under its module id; here they are read from the frozen fixtures (tests/fixtures)
      const artifacts: Record<string, string> = {
        "http://app.test/__tisilia/modules/demo.money/modules/demo.money/money.js": "modules/demo.money/money.js",
        "http://app.test/__tisilia/modules/demo.portable/modules/demo.portable/demo.portable.portable.js": "modules/demo.portable/demo.portable.portable.js",
      };
      const file = artifacts[url];
      expect(file, `unexpected module url ${url}`).toBeDefined();
      return (await import(pathToFileURL(sampleDir + file).href)) as Record<string, unknown>;
    },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Explorer core", () => {
  it("loads the contract and modules from the authorized routes and lists operations with capabilities", async () => {
    const model = await load();
    expect(model.moduleErrors).toEqual([]);
    const summaries = summarize(model);
    // contract order (the exporter's endpoint order), the portable operation included
    expect(summaries.map((s) => s.id)).toEqual(["users.get", "users.put", "portable.money", "portable.shape", "portable.invoice", "portable.tree", "users.labels", "users.tags", "mvc.money"]);
    expect(summaries.every((s) => s.missingCapabilities.length === 0)).toBe(true);
    expect(filterOperations(summaries, "put", undefined).map((s) => s.id)).toEqual(["users.put"]);
    expect(filterOperations(summaries, "", "users").length).toBe(4);
  });

  it("builds typed arguments through request-input factories and previews the exact request without credentials", async () => {
    const model = await load();
    const op = model.operations.find((o) => o.id === "users.put")!;
    const built = buildArgs(model, op, { parameters: { id: "550e8400-e29b-41d4-a716-446655440000" }, body: '{"revision":9007199254740993,"nickname":null,"balance":"42.1"}' });
    expect(built.errors).toEqual([]);
    const prepared = preview(model.registry.operations.get("users.put")!, built.args, { baseUrl: "http://api.test", bearerToken: "secret" });
    expect(prepared.url.href).toBe("http://api.test/users/550e8400-e29b-41d4-a716-446655440000");
    expect(prepared.bodyText).toBe('{"revision":9007199254740993,"nickname":null,"balance":"42.1000"}');
    expect(prepared.headers.some(([n]) => n === "authorization")).toBe(false);
    // the code shown for the call follows the redaction mode: the body is hidden unless values are shown
    expect(curlSnippet(prepared, snippetOptions)).toContain("--data-raw '•••'");
    expect(curlSnippet(prepared, { ...snippetOptions, reveal: true })).toContain("9007199254740993");

    const bad = buildArgs(model, op, { parameters: { id: "nope" }, body: "{" });
    expect(bad.errors.map((e) => e.path)).toEqual(["/id", "/body"]);
    const missing = buildArgs(model, op, { parameters: {}, body: "" });
    expect(missing.errors.map((e) => e.message)).toEqual(["required", "request body is required"]);
  });

  it("executes through the runtime pipeline in typed and raw mode and keeps int64/decimal text in the display tree", async () => {
    const model = await load();
    const seen: { url: string; body: string | undefined; auth: string | null }[] = [];
    vi.stubGlobal(
      "fetch",
      (async (input: RequestInfo | URL, init?: RequestInit) => {
        const request = new Request(input, init);
        seen.push({ url: request.url, body: init?.body === undefined ? undefined : new TextDecoder().decode(init.body as Uint8Array), auth: request.headers.get("authorization") });
        return new Response('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":9007199254740993,"nickname":"neo","balance":{"amount":"42.1000","currency":"JPY"},"bonus":525,"createdAt":"2026-09-30T15:04:05.1234567+09:00"}', { status: 200, headers: { "content-type": "application/json", "set-cookie": "s=1", etag: "v1" } });
      }) as typeof fetch,
    );
    const op = model.registry.operations.get("users.put")!;
    const built = buildArgs(model, model.operations.find((o) => o.id === "users.put")!, { parameters: { id: "550e8400-e29b-41d4-a716-446655440000" }, body: '{"revision":1,"balance":"42.1"}' });
    const typed = await execute(op, built.args, { baseUrl: "http://api.test", bearerToken: "secret-token" });
    expect(typed.mode).toBe("typed");
    expect(typed.result?.kind).toBe("response");
    expect(seen[0]?.auth).toBe("Bearer secret-token");
    expect(seen[0]?.body).toBe('{"revision":1,"balance":"42.1000"}');
    const tree = describeValue(typed.result, "result");
    const data = tree.children?.find((c) => c.label === "data");
    expect(data?.children?.find((c) => c.label === "revision")).toMatchObject({ kind: "int64", text: "9007199254740993" });
    expect(data?.children?.find((c) => c.label === "balance")?.children?.find((c) => c.label === "amount")).toMatchObject({ kind: "decimal", text: "42.1000" });
    // the member-level MoneyCents converter decodes hundredths through its own codec (property context)
    expect(data?.children?.find((c) => c.label === "bonus")?.children?.find((c) => c.label === "amount")).toMatchObject({ kind: "decimal", text: "5.25" });
    expect(data?.children?.find((c) => c.label === "createdAt")).toMatchObject({ kind: "datetime-offset", text: "2026-09-30T15:04:05.1234567+09:00" });
    expect(redactTree(tree, "mask").children?.find((c) => c.label === "data")?.children?.find((c) => c.label === "revision")?.text).toBe("•••");

    const raw = await execute(op, built.args, { baseUrl: "http://api.test", rawBody: '{"revision":"not a number"}' });
    expect(raw.mode).toBe("raw");
    expect(seen[1]?.body).toBe('{"revision":"not a number"}');
    expect(raw.result?.kind).toBe("response");
    // the response body as the page lays it out: masked by default, the server's number lexemes once shown (never a JS number)
    const wire = parseJson(new TextDecoder().decode(raw.raw?.kind === "raw" ? raw.raw.body : new Uint8Array()));
    expect(prettyWire(wire, "mask")).toContain('"revision": •••');
    expect(prettyWire(wire, "mask")).not.toContain("neo");
    expect(prettyWire(wire, "reveal")).toContain('"revision": 9007199254740993');
    expect(responseOf(typed)).toEqual({ status: 200, caseId: "users.put.ok" });
  });

  it("reports a response beyond the configured body limit as a limit failure instead of decoding a partial body", async () => {
    const model = await load();
    vi.stubGlobal(
      "fetch",
      (async () => new Response('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":1,"nickname":null,"balance":{"amount":"1.0000","currency":"JPY"},"bonus":0,"createdAt":"2026-09-30T15:04:05+09:00"}', { status: 200, headers: { "content-type": "application/json" } })) as typeof fetch,
    );
    const op = model.registry.operations.get("users.get")!;
    const built = buildArgs(model, model.operations.find((o) => o.id === "users.get")!, { parameters: { id: "550e8400-e29b-41d4-a716-446655440000" }, body: "" });
    expect(built.errors).toEqual([]);
    const limited = await execute(op, built.args, { baseUrl: "http://api.test", bearerToken: "secret-token", limits: { maxBodyBytes: 16 } });
    expect(limited.error).toBeUndefined();
    expect(limited.result).toMatchObject({ kind: "limit-failure", limit: "maxBodyBytes" });
    // the display tree shows the failure as data, with the limit name as text; the status line names the limit
    expect(describeValue(limited.result, "result").children?.find((c) => c.label === "limit")).toMatchObject({ kind: "string", text: "maxBodyBytes" });
    expect(executionStatus(limited)).toMatchObject({ level: "limit" });
    expect(executionStatus(limited).text).toContain("maxBodyBytes");
    // what a call records about its credentials has no values, so its code names a placeholder even with values shown
    const sentWith = credentialShapes([{ kind: "bearer", token: "secret-token" }, { kind: "api-key", header: "X-Api-Key", value: "k-123" }]);
    expect(JSON.stringify(sentWith)).not.toMatch(/secret-token|k-123/);
    const code = curlSnippet(limited.prepared!, { ...snippetOptions, operationId: "users.get", credentials: sentWith, reveal: true });
    expect(code).toContain('-H "authorization: Bearer $TOKEN"');
    expect(code).toContain('-H "x-api-key: $API_KEY"');
    expect(code).not.toMatch(/secret-token|k-123/);
    const unlimited = await execute(op, built.args, { baseUrl: "http://api.test" });
    expect(unlimited.result?.kind).toBe("response");
    expect(executionStatus(unlimited)).toMatchObject({ level: "ok" });
    expect(executionStatus(unlimited).text).toContain("users.get.ok (200)");
  });

  it("names cancellation and unsendable requests in the status line instead of showing a partial result", async () => {
    const model = await load();
    vi.stubGlobal("fetch", (async () => new Response("{}", { status: 200, headers: { "content-type": "application/json" } })) as typeof fetch);
    const op = model.registry.operations.get("users.get")!;
    const built = buildArgs(model, model.operations.find((o) => o.id === "users.get")!, { parameters: { id: "550e8400-e29b-41d4-a716-446655440000" }, body: "" });
    const cancelled = await execute(op, built.args, { baseUrl: "http://api.test", signal: AbortSignal.abort() });
    expect(cancelled.result?.kind).toBe("cancelled");
    expect(executionStatus(cancelled)).toMatchObject({ level: "cancelled" });
    // a request that cannot be prepared (missing path parameter) is reported as not sent, with no result at all
    const unsendable = await execute(op, {}, { baseUrl: "http://api.test" });
    expect(unsendable.result).toBeUndefined();
    expect(executionStatus(unsendable)).toMatchObject({ level: "error" });
    expect(unsendable.prepared).toBeUndefined();
    expect(responseOf(unsendable)).toEqual({ status: undefined, caseId: undefined });
  });

  it("keeps credentials and responses out of browser storage: only prefs.ts touches it, for the language and the theme", () => {
    const srcDir = fileURLToPath(new URL("../src/", import.meta.url));
    // components live in subfolders: every source file counts
    const sources = (readdirSync(srcDir, { recursive: true }) as string[]).filter((f) => f.endsWith(".ts") || f.endsWith(".vue")).map((f) => f.replace(/\\/g, "/"));
    expect(sources.length).toBeGreaterThan(0);
    expect(sources).toContain("prefs.ts");
    for (const file of sources) {
      const code = readFileSync(srcDir + file, "utf8");
      if (file === "prefs.ts") {
        // local storage alone, written in one place with a value that is one of a fixed list (the reads keep only such values)
        expect(code, file).not.toMatch(/sessionStorage|indexedDB|openDatabase|document\.cookie|caches\.open/);
        expect(code.match(/\.setItem\(/g), file).toHaveLength(1);
        expect(code, file).toMatch(/write\(pageStorage\(\), storageKeys\.locale, locale\)/);
        expect(code, file).toMatch(/write\(pageStorage\(\), storageKeys\.theme, theme\)/);
        expect(code.match(/write\(/g), file).toHaveLength(3); // the definition and the two calls
        continue;
      }
      expect(code, file).not.toMatch(/localStorage|sessionStorage|indexedDB|openDatabase|document\.cookie|caches\.open|\.setItem\(/);
    }
  });

  it("renders text only and nothing the CSP refuses: no inline style attributes, v-html, innerHTML or eval", () => {
    const srcDir = fileURLToPath(new URL("../src/", import.meta.url));
    const sources = (readdirSync(srcDir, { recursive: true }) as string[]).filter((f) => f.endsWith(".ts") || f.endsWith(".vue"));
    expect(sources.length).toBeGreaterThan(10);
    for (const file of sources) {
      const code = readFileSync(srcDir + file, "utf8");
      // style-src 'self': a style attribute in markup is refused (CSSOM from script is not)
      expect(code, file).not.toMatch(/\sstyle="/);
      expect(code, file).not.toMatch(/v-html|innerHTML|outerHTML|insertAdjacentHTML|document\.write|new Function\(|\beval\(/);
    }
  });

  it("never reveals builtin secret headers and masks non-safe headers by default", () => {
    const headers = redactHeaders(
      [
        ["set-cookie", "s=1"],
        ["etag", "v1"],
        ["x-request-id", "abc"],
      ],
      "mask",
    );
    expect(headers).toEqual([
      { name: "set-cookie", value: "•••", masked: true },
      { name: "etag", value: "v1", masked: false },
      { name: "x-request-id", value: "•••", masked: true },
    ]);
    expect(redactHeaders([["set-cookie", "s=1"]], "reveal")[0]?.masked).toBe(true);
  });

  it("caps long lists in the decoded view and says how many it left out", () => {
    const capped = "501 more not shown here — the JSON view has every item";
    const list = describeValue(Array.from({ length: 1001 }, (_, i) => i), "items");
    expect(list.children).toHaveLength(501);
    expect(list.children![499]).toMatchObject({ label: "499", kind: "number", text: "499" });
    expect(list.children![500]).toMatchObject({ kind: "truncated", text: capped });
    // the marker is not a value: masking leaves it readable
    expect(redactTree(list, "mask").children![500]!.text).toBe(capped);
    expect(describeValue(new Map(Array.from({ length: 1001 }, (_, i) => [i, i])), "").children![500]).toMatchObject({ kind: "truncated", text: capped });
    expect(describeValue(Object.fromEntries(Array.from({ length: 1001 }, (_, i) => ["k" + i, i])), "").children![500]).toMatchObject({ kind: "truncated", text: capped });
    expect(describeValue([1, 2], "").children).toHaveLength(2);
  });

  it("reports a module without a browser artifact only when the browser needs one of its exports", async () => {
    const document = JSON.parse(contractText) as { modules: unknown[] };
    const digest = "sha256:" + "0".repeat(64);
    document.modules.push(
      // server-only roles (a behavior, a resolver): nothing to load in the browser
      { id: "demo.items", version: "1.0.0", artifacts: [{ target: "dotnet", path: "bin/Demo.dll", digest }], exports: [{ name: "ItemNameSetter", role: "behavior", targets: ["dotnet"] }], dependencyIds: [], license: "MIT", noticeFiles: [] },
      // a codec the browser would run, shipped for .NET only
      { id: "demo.half", version: "1.0.0", artifacts: [{ target: "dotnet", path: "bin/Demo.dll", digest }], exports: [{ name: "halfResponseDecode", role: "codec", targets: ["browser", "node"] }], dependencyIds: [], license: "MIT", noticeFiles: [] },
    );
    const model = await load(JSON.stringify(document));
    expect(model.moduleErrors).toEqual(["module 'demo.half' has no browser artifact; its codecs run only in the server/Node client"]);
  });

  it("calls the API where the page is, below a PathBase or a proxy's prefix", () => {
    // the host names the Explorer's route; what precedes it in the page's URL is where the API is
    expect(apiBaseOf("http://app.test/__tisilia/index.html", "/__tisilia")).toBe("http://app.test");
    expect(apiBaseOf("http://app.test/myapp/__tisilia/index.html", "/__tisilia")).toBe("http://app.test/myapp");
    expect(apiBaseOf("https://host:8443/a/b/tools/explorer/index.html?x=1#y", "/tools/explorer/")).toBe("https://host:8443/a/b");
    expect(apiBaseOf("http://app.test/myapp/index.html", "/")).toBe("http://app.test/myapp");
    // an older host (no route) or a page elsewhere: the origin, as before
    expect(apiBaseOf("http://app.test/myapp/__tisilia/index.html", undefined)).toBe("http://app.test");
    expect(apiBaseOf("http://app.test/other/index.html", "/__tisilia")).toBe("http://app.test");
  });

  it("copies, or says why not where the browser has no clipboard (plain http from a LAN address)", async () => {
    const written: string[] = [];
    expect(await copyToClipboard("curl -X GET 'http://app.test/'", { writeText: async (text) => void written.push(text) })).toEqual({ copied: true });
    expect(written).toEqual(["curl -X GET 'http://app.test/'"]);
    // navigator.clipboard is [SecureContext]: undefined over http from anything but localhost
    const insecure = await copyToClipboard("x", undefined);
    expect(insecure.copied).toBe(false);
    expect(insecure.copied ? "" : insecure.reason).toContain("https or on localhost");
    // writeText rejects without permission or focus
    const refused = await copyToClipboard("x", { writeText: () => Promise.reject(new DOMException("Document is not focused.", "NotAllowedError")) });
    expect(refused.copied ? "" : refused.reason).toContain("Document is not focused.");
  });
});
