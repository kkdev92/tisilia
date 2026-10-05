import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import { describe, expect, it } from "vitest";
import { CodecRegistry, scalarCodec, webNumbers } from "../src/index.js";
import { builtinOracle, runRunner } from "../src/conformance/runner.js";

const contract = {
  types: [
    { id: "std.int64", shape: { kind: "primitive", primitiveId: "tisilia.int64@0.3" } },
    { id: "std.decimal", shape: { kind: "primitive", primitiveId: "tisilia.decimal@0.3" } },
    { id: "std.json-value", shape: { kind: "primitive", primitiveId: "tisilia.json-value@0.3" } },
  ],
  codecs: [
    { id: "std.int64.codec.r", typeId: "std.int64", capabilities: { request: { equivalenceId: "std.int64.request" }, response: { equivalenceId: "std.int64.response" }, requestKey: { equivalenceId: "std.int64.key" }, responseKey: { equivalenceId: "std.int64.key" } } },
    { id: "std.decimal.codec", typeId: "std.decimal", capabilities: { request: { equivalenceId: "std.decimal.request" }, response: { equivalenceId: "std.decimal.response" } } },
    { id: "std.json-value.codec", typeId: "std.json-value", capabilities: { request: { equivalenceId: "std.json-value.request" }, response: { equivalenceId: "std.json-value.response" } } },
  ],
  equivalences: [
    { id: "std.int64.request", domainTypeId: "std.int64", scope: "request", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.structural@0.3" } },
    { id: "std.int64.response", domainTypeId: "std.int64", scope: "response", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.structural@0.3" } },
    { id: "std.int64.key", domainTypeId: "std.int64", scope: "key", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.structural@0.3" } },
    { id: "std.decimal.request", domainTypeId: "std.decimal", scope: "request", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.numeric@0.3" } },
    { id: "std.decimal.response", domainTypeId: "std.decimal", scope: "response", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.numeric@0.3" } },
    { id: "std.json-value.request", domainTypeId: "std.json-value", scope: "request", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.exact-wire@0.3" } },
    { id: "std.json-value.response", domainTypeId: "std.json-value", scope: "response", typescriptOracle: { kind: "builtin", id: "tisilia.oracle.exact-wire@0.3" } },
  ],
  comparers: [],
  bindings: [],
};

function record(fields: Record<string, unknown>): string {
  return JSON.stringify({ format: "tisilia.runner-message", version: "0.3", sessionId: "s.test", kind: "request", profileId: "p", context: [], ...fields });
}

async function exchange(lines: string[]): Promise<Record<string, unknown>[]> {
  const dir = mkdtempSync(join(tmpdir(), "tisilia-runner-"));
  const contractPath = join(dir, "contract.json");
  writeFileSync(contractPath, JSON.stringify(contract));
  const registry = new CodecRegistry();
  registry.register(scalarCodec("int64", { numbers: webNumbers }));
  registry.register(scalarCodec("decimal"));
  registry.register(scalarCodec("json-value"));
  const input = new PassThrough();
  const output = new PassThrough();
  const chunks: Buffer[] = [];
  output.on("data", (c: Buffer) => chunks.push(c));
  const done = runRunner({ registry, contractPath, sessionId: "s.test", maxRecordBytes: 4096, input, output });
  for (const line of lines) {
    input.write(line + "\n");
  }
  input.end();
  await done;
  rmSync(dir, { recursive: true, force: true });
  const text = Buffer.concat(chunks).toString("utf8");
  return text
    .split("\n")
    .filter((l) => l.length > 0)
    .map((l) => JSON.parse(l) as Record<string, unknown>);
}

describe("conformance runner (TypeScript side)", () => {
  it("answers exactly one correlated record per request and keeps number lexemes", async () => {
    const out = await exchange([
      record({ requestId: "r.1", action: "ts-decode-response", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "9007199254740993" }] }),
      record({ requestId: "r.2", action: "ts-encode-request", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "-9223372036854775808" }] }),
      record({ requestId: "r.3", action: "ts-encode-key", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "42" }] }),
      record({ requestId: "r.4", action: "validate-domain", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "1" }] }),
    ]);
    expect(out.map((r) => r["requestId"])).toEqual(["r.1", "r.2", "r.3", "r.4"]);
    expect(out.every((r) => r["sessionId"] === "s.test" && r["kind"] === "success")).toBe(true);
    expect(out[0]!["outputs"]).toEqual([{ kind: "number", text: "9007199254740993" }]);
    expect(out[1]!["outputs"]).toEqual([{ kind: "number", text: "-9223372036854775808" }]);
    expect(out[2]!["outputs"]).toEqual([{ kind: "string", value: "42" }]);
    expect(out[3]!["outputs"]).toEqual([{ kind: "boolean", value: true }]);
  });

  it("reports codec failures with the fixed code set and the failing path", async () => {
    const out = await exchange([
      record({ requestId: "r.1", action: "ts-decode-response", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "9223372036854775808" }] }),
      record({ requestId: "r.2", action: "ts-decode-response", adapterId: "std.int64.codec.r", inputs: [{ kind: "boolean", value: true }] }),
      record({ requestId: "r.3", action: "validate-domain", adapterId: "std.int64.codec.r", inputs: [{ kind: "string", value: "x" }] }),
    ]);
    expect(out.map((r) => [r["kind"], r["code"]])).toEqual([
      ["failure", "codec"],
      ["failure", "codec"],
      ["failure", "codec"],
    ]);
    expect(String(out[0]!["safeMessageId"])).toMatch(/^codec\./);
  });

  it("rejects protocol violations without exiting: session, arity, unknown adapter, malformed record, limit", async () => {
    const out = await exchange([
      JSON.stringify({ format: "tisilia.runner-message", version: "0.3", sessionId: "s.other", requestId: "r.1", kind: "request", action: "compare", adapterId: "std.int64.response", profileId: "p", context: [], inputs: [{ kind: "null" }, { kind: "null" }] }),
      record({ requestId: "r.2", action: "compare", adapterId: "std.int64.response", inputs: [{ kind: "number", text: "1" }] }),
      record({ requestId: "r.3", action: "ts-decode-response", adapterId: "nope", inputs: [{ kind: "number", text: "1" }] }),
      "{not json",
      record({ requestId: "r.5", action: "dotnet-read", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "1" }] }),
      record({ requestId: "r.6", action: "ts-decode-response", adapterId: "std.json-value.codec", inputs: [{ kind: "string", value: "x".repeat(5000) }] }),
      record({ requestId: "r.7", action: "ts-decode-response", adapterId: "std.int64.codec.r", inputs: [{ kind: "number", text: "1" }] }),
    ]);
    expect(out.map((r) => [r["requestId"], r["kind"], r["code"], r["safeMessageId"]])).toEqual([
      ["r.1", "failure", "invalid-input", "protocol.session-mismatch"],
      ["r.2", "failure", "invalid-input", "protocol.input-arity"],
      ["r.3", "failure", "contract", "adapter.not-a-codec"],
      ["unknown", "failure", "invalid-input", "protocol.malformed-json"],
      ["r.5", "failure", "contract", "action.wrong-runner"],
      ["unknown", "failure", "limit", "protocol.record-limit"],
      ["r.7", "success", undefined, undefined],
    ]);
  });

  it("compares through the registered builtin oracles", async () => {
    const out = await exchange([
      record({ requestId: "r.1", action: "compare", adapterId: "std.int64.response", inputs: [{ kind: "number", text: "1" }, { kind: "number", text: "1" }] }),
      record({ requestId: "r.2", action: "compare", adapterId: "std.decimal.response", inputs: [{ kind: "number", text: "1.50" }, { kind: "number", text: "1.5" }] }),
      record({ requestId: "r.3", action: "compare", adapterId: "std.int64.response", inputs: [{ kind: "number", text: "1.0" }, { kind: "number", text: "1" }] }),
      record({ requestId: "r.4", action: "compare", adapterId: "std.json-value.response", inputs: [{ kind: "object", entries: [{ name: "a", value: { kind: "null" } }, { name: "b", value: { kind: "null" } }] }, { kind: "object", entries: [{ name: "b", value: { kind: "null" } }, { name: "a", value: { kind: "null" } }] }] }),
    ]);
    expect(out.map((r) => (r["outputs"] as { value: boolean }[])[0]!.value)).toEqual([true, true, false, false]);
  });

  it("builtin oracles agree with the C# definitions", () => {
    const a = { kind: "object", entries: [{ name: "x", value: { kind: "number", text: "1e2" } }] } as const;
    const b = { kind: "object", entries: [{ name: "x", value: { kind: "number", text: "100" } }] } as const;
    expect(builtinOracle("tisilia.oracle.structural@0.3", a, b)).toBe(false);
    expect(builtinOracle("tisilia.oracle.numeric@0.3", a, b)).toBe(true);
    expect(builtinOracle("tisilia.oracle.numeric@0.3", { kind: "number", text: "-0" }, { kind: "number", text: "0" })).toBe(true);
    expect(builtinOracle("tisilia.oracle.exact-wire@0.3", a, a)).toBe(true);
    expect(builtinOracle("tisilia.oracle.unknown@0.3", a, b)).toBeUndefined();
  });
});
