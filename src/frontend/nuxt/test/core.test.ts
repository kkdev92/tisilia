import { describe, expect, it } from "vitest";
import { createHydrationEnvelope, prepareRequest, scalarCodec, webNumbers, xmlElementCodec, xmlTextCodec, type OperationDescriptor, type PreparedRequest, type RawOutcome } from "@kkdev92/tisilia-runtime";
import { asyncDataKeyOf, decodeIdentityKey, envelopeMatchesScope, hiddenGuardHeader, hydrate, identityOf, identityRecordOf, operationHeaders, partitionForwardedHeaders } from "../src/runtime/core.js";

const semanticHash = "sha256:" + "a".repeat(64);
const int64 = scalarCodec("int64", { numbers: webNumbers });
const operation: OperationDescriptor = {
  id: "counter.get",
  method: "GET",
  route: "/counter",
  profileId: "p",
  parameters: [],
  responses: [
    { caseId: "counter.get.ok", status: 200, body: { kind: "json", mediaType: "application/json", codec: int64, nullable: false }, hydration: "browser-safe", exposedHeaders: ["etag"] },
    { caseId: "counter.get.secret", status: 202, body: { kind: "json", mediaType: "application/json", codec: int64, nullable: false }, hydration: "server-only", exposedHeaders: [] },
  ],
  requestExecution: "browser-allowed",
  requestHeaderAllowlist: ["accept-language"],
};
const prepared: PreparedRequest = { url: new URL("http://api.test/counter"), encodedPath: "/counter", queryEntries: [], method: "GET", headers: [["accept-language", "ja"]], bodyText: undefined, bodyBytes: undefined };
const options = { baseUrl: "http://api.test" };

it("binds raw upload identities to exact bytes and distinguishes empty from omitted bodies", async () => {
  const upload: OperationDescriptor = { ...operation, method: "POST", requestBody: { kind: "binary", mediaType: "application/octet-stream", presence: "optional", get: args => args } };
  const record = (bytes?: Uint8Array) => identityRecordOf(upload, { ...prepared, method: "POST", bodyBytes: bytes }, semanticHash, "scope");
  const a = record(new Uint8Array([0, 255]));
  const b = record(new Uint8Array([0, 254]));
  expect(a.bodyKind).toBe("binary");
  expect(a.bodyText).toBe("AP8=");
  expect(asyncDataKeyOf(a)).not.toBe(asyncDataKeyOf(b));
  expect(await identityOf(a, false, undefined)).not.toBe(await identityOf(b, false, undefined));
  expect(asyncDataKeyOf(record(new Uint8Array()))).not.toBe(asyncDataKeyOf(record()));
  // a streamed body's bytes are not known before it is sent: it has no identity, rather than the identity of an omitted body
  const streamed = prepareRequest(upload, new ReadableStream<Uint8Array>({ start(c) { c.enqueue(new Uint8Array([1])); c.close(); } }), options);
  expect(() => identityRecordOf(upload, streamed, semanticHash, "scope")).toThrow(/no request identity/);
});

it("identifies an XML body by the document it sends", async () => {
  const name = xmlTextCodec({ id: "std.string.xml-string", typeId: "std.string", scalar: "string", grammar: "xml-string" });
  const item = xmlElementCodec({ id: "item", typeId: "item", attributes: [], elements: [{ property: "Name", name: "Name", codec: name, presence: "optional", wirePresence: "optional", nullable: false }], request: true, response: false });
  const post: OperationDescriptor = { ...operation, method: "POST", requestBody: { kind: "xml", mediaType: "application/xml", codec: item, root: { name: "Item" }, presence: "required", maxDepth: 32, get: args => (args as { body: unknown }).body } };
  const record = (value: string) => identityRecordOf(post, prepareRequest(post, { body: { Name: value } }, options), semanticHash, "scope");
  expect(record("a").bodyKind).toBe("binary");
  expect(new TextDecoder().decode(Uint8Array.from(atob(record("a").bodyText), c => c.charCodeAt(0)))).toBe("<Item><Name>a</Name></Item>");
  expect(asyncDataKeyOf(record("a"))).not.toBe(asyncDataKeyOf(record("b")));
});

function raw(status: number, caseId: string, body: string): RawOutcome {
  const bytes = new TextEncoder().encode(body);
  return { kind: "raw", caseId, status, mediaType: "application/json", headers: [["etag", "v1"]], bodyKind: "json", body: bytes, metadata: { status, mediaType: "application/json", bodyBytes: bytes.byteLength, headers: [] } };
}

describe("header forwarding", () => {
  it("forwards only allowlisted headers, splits credentials and never copies hop-by-hop headers", () => {
    const forwarded = partitionForwardedHeaders(
      { Authorization: "Bearer t", cookie: "s=1", "accept-language": "ja", host: "evil", connection: "keep-alive", "x-custom": "1", "content-length": "5" },
      ["authorization", "cookie", "accept-language", "host", "connection", "content-length"],
    );
    expect(forwarded.credentials).toEqual([
      ["authorization", "Bearer t"],
      ["cookie", "s=1"],
    ]);
    expect(forwarded.other).toEqual([["accept-language", "ja"]]);
    expect(operationHeaders(forwarded, operation)).toEqual([["accept-language", "ja"]]);
    expect(operationHeaders(forwarded, { ...operation, requestHeaderAllowlist: [] })).toEqual([]);
  });
});

describe("request identity", () => {
  it("identifies deterministic multipart bytes and distinguishes changed file content", () => {
    const form: OperationDescriptor = { ...operation, method: "POST", requestBody: { kind: "form", mediaType: "multipart/form-data", presence: "required", get: a => a, fields: [{ name: "file", kind: "file", presence: "required", repeated: false }] } };
    const record = (byte: number) => identityRecordOf(form, prepareRequest(form, { file: { fileName: "file.bin", bytes: new Uint8Array([byte]) } }, options), semanticHash, "scope");
    expect(record(0)).toEqual(record(0)); expect(record(0).bodyKind).toBe("binary");
    expect(asyncDataKeyOf(record(0))).not.toBe(asyncDataKeyOf(record(255)));
  });
  it("is sha256 without credentials, keyed hmac with credentials, and the async-data key ignores credentials", async () => {
    const record = identityRecordOf(operation, prepared, semanticHash, "nonce-0123456789abcdef");
    expect(record.selectedHeaderEntries).toEqual([{ name: "accept-language", value: "ja" }]);
    expect(record.bodyKind).toBe("none");
    const plain = await identityOf(record, false, undefined);
    expect(plain).toMatch(/^rid:sha256:[a-f0-9]{64}$/);
    const key = new Uint8Array(32).fill(7);
    const keyed = await identityOf(record, true, key);
    expect(keyed).toMatch(/^rid:hmac-sha256:[a-f0-9]{64}$/);
    expect(keyed.slice(16)).not.toBe(plain.slice(11));
    await expect(identityOf(record, true, undefined)).rejects.toThrow();
    const k1 = asyncDataKeyOf(record);
    expect(k1).toMatch(/^tisilia:counter\.get:[a-f0-9]{16}$/);
    expect(asyncDataKeyOf(identityRecordOf(operation, prepared, semanticHash, "other-nonce-000000000"))).toBe(k1);
    expect(asyncDataKeyOf(identityRecordOf(operation, { ...prepared, encodedPath: "/counter2" }, semanticHash, "nonce-0123456789abcdef"))).not.toBe(k1);
  });

  it("decodes configured identity keys and rejects short ones", () => {
    expect(decodeIdentityKey("")).toBeUndefined();
    expect(decodeIdentityKey(Buffer.alloc(32, 1).toString("base64"))?.byteLength).toBe(32);
    expect(decodeIdentityKey("x".repeat(40))?.byteLength).toBe(40);
    expect(() => decodeIdentityKey("short")).toThrow();
  });
});

describe("hydration", () => {
  const ctx = { semanticHash, operationId: "counter.get", requestIdentity: "rid:sha256:" + "b".repeat(64), scopeNonce: "nonce-0123456789abcdef" };
  const expected = { semanticHash, operationId: "counter.get", scopeNonce: "nonce-0123456789abcdef" };

  it("decodes a browser-safe json envelope with the case codec and keeps exposed headers", () => {
    const envelope = createHydrationEnvelope(operation, raw(200, "counter.get.ok", "9007199254740993"), ctx);
    expect(envelope.kind).toBe("json");
    const result = hydrate(operation, JSON.parse(JSON.stringify(envelope)), expected, options);
    expect(result.kind).toBe("response");
    if (result.kind === "response") {
      expect(result.caseId).toBe("counter.get.ok");
      expect((result as { data?: unknown }).data).toBe(9007199254740993n);
      expect(result.headers).toEqual([["etag", "v1"]]);
    }
  });

  it("turns server-only cases into failure/server-only without a body and surfaces failures with fixed codes", () => {
    const secret = createHydrationEnvelope(operation, raw(202, "counter.get.secret", "42"), ctx);
    expect(secret).toMatchObject({ kind: "failure", code: "server-only" });
    expect(JSON.stringify(secret)).not.toContain("42");
    const hydrated = hydrate(operation, secret, expected, options);
    expect(hydrated).toMatchObject({ kind: "hydration-failure", code: "server-only" });
    const timeout = createHydrationEnvelope(operation, { kind: "timeout", operationId: "counter.get", timeoutMs: 1 }, ctx);
    expect(hydrate(operation, timeout, expected, options)).toMatchObject({ kind: "hydration-failure", code: "timeout" });
  });

  it("rejects envelopes of another scope, contract or operation and malformed payloads", () => {
    const envelope = createHydrationEnvelope(operation, raw(200, "counter.get.ok", "1"), ctx);
    expect(hydrate(operation, envelope, { ...expected, scopeNonce: "different-nonce-000000" }, options)).toMatchObject({ kind: "hydration-mismatch", mismatch: "scope" });
    expect(hydrate(operation, envelope, { ...expected, semanticHash: "sha256:" + "c".repeat(64) }, options)).toMatchObject({ kind: "hydration-mismatch", mismatch: "semantic-hash" });
    expect(hydrate(operation, { ...envelope, status: 201 }, expected, options)).toMatchObject({ kind: "hydration-mismatch", mismatch: "status" });
    expect(hydrate(operation, { ...envelope, extra: 1 }, expected, options)).toMatchObject({ kind: "hydration-mismatch", mismatch: "schema" });
    expect(hydrate(operation, "garbage", expected, options)).toMatchObject({ kind: "hydration-mismatch", mismatch: "schema" });
    expect(envelopeMatchesScope(envelope, expected)).toBe(true);
    expect(envelopeMatchesScope(envelope, { ...expected, scopeNonce: "different-nonce-000000" })).toBe(false);
    expect(envelopeMatchesScope(null, expected)).toBe(false);
  });
});

describe("contract guard on another origin", () => {
  it("names a guard header the browser could not see, and nothing else", () => {
    // what the browser shows of a cross-origin response whose CORS policy does not expose the header
    const hidden = hiddenGuardHeader(raw(200, "counter.get.ok", "1"), "x-tisilia-contract");
    expect(hidden).toContain("'x-tisilia-contract'");
    expect(hidden).toContain('WithExposedHeaders("x-tisilia-contract")');
    const seen = raw(200, "counter.get.ok", "1");
    const exposed: RawOutcome = seen.kind === "raw" ? { ...seen, metadata: { ...seen.metadata, headers: [["x-tisilia-contract", semanticHash]] } } : seen;
    expect(hiddenGuardHeader(exposed, "X-Tisilia-Contract")).toBeUndefined();
    // no guard configured, or no response to look at
    expect(hiddenGuardHeader(raw(200, "counter.get.ok", "1"), undefined)).toBeUndefined();
    expect(hiddenGuardHeader({ kind: "timeout", operationId: "counter.get", timeoutMs: 1 }, "x-tisilia-contract")).toBeUndefined();
  });
});
