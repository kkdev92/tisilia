import { describe, expect, it } from "vitest";
import { scalarCodec } from "../src/codec/scalars.js";
import { xmlTextCodec } from "../src/codec/xml.js";
import { decodeResponse, download, executeWithRaw, subscribe, type OperationDescriptor, type RawResponse, type ResponseBodyDescriptor } from "../src/http/client.js";
import { createHydrationEnvelope, rawFromEnvelope } from "../src/hydration.js";

// These descriptors deliberately bypass contract validation, as a hand-written runtime consumer can.
const credentialHeaders = ["Authorization", "Proxy-Authorization", "Cookie", "Cookie2", "Set-Cookie", "Set-Cookie2"];
const exposedHeaders = [...credentialHeaders, "ETag", "X-Token-Count"];
const receivedHeaders: (readonly [string, string])[] = [
  ...credentialHeaders.map(name => [name, "CREDENTIAL_VALUE"] as const),
  ["ETag", '"v1"'], ["X-Token-Count", "3"], ["X-Undeclared", "PRIVATE_VALUE"],
];
const expectedHeaders = [["etag", '"v1"'], ["x-token-count", "3"]];
const context = { semanticHash: "sha256:" + "a".repeat(64), operationId: "headers", requestIdentity: "rid:sha256:" + "b".repeat(64), scopeNonce: "0123456789abcdef" };

function operation(body: ResponseBodyDescriptor): OperationDescriptor {
  return {
    id: "headers", method: "GET", route: "/", parameters: [], requestExecution: "browser-allowed", requestHeaderAllowlist: [],
    responses: [{ caseId: "headers.ok", status: body.kind === "none" ? 204 : 200, body, hydration: "browser-safe", exposedHeaders }],
  };
}

const cases: readonly { name: string; body: ResponseBodyDescriptor; text: string | null }[] = [
  { name: "JSON", body: { kind: "json", mediaType: "application/json", codec: scalarCodec("string"), nullable: false }, text: '"ok"' },
  { name: "text", body: { kind: "text", mediaType: "text/plain" }, text: "ok" },
  { name: "XML", body: { kind: "xml", mediaType: "application/xml", root: { name: "value" }, codec: xmlTextCodec({ id: "text", typeId: "std.string", scalar: "string", grammar: "xml-string" }), nullable: false }, text: "<value>ok</value>" },
  { name: "bodyless", body: { kind: "none" }, text: null },
  { name: "binary", body: { kind: "binary", mediaType: "application/octet-stream" }, text: "ok" },
  { name: "SSE", body: { kind: "sse", mediaType: "text/event-stream", dataFormat: "text", codec: scalarCodec("string"), nullable: false }, text: "data: ok\n\n" },
];

function raw(body: ResponseBodyDescriptor, text: string | null): RawResponse {
  const bytes = new TextEncoder().encode(text ?? "");
  const status = body.kind === "none" ? 204 : 200;
  const mediaType = body.kind === "none" ? undefined : body.mediaType;
  return { kind: "raw", caseId: "headers.ok", status, mediaType, headers: receivedHeaders, bodyKind: body.kind, body: bytes, metadata: { status, mediaType, bodyBytes: bytes.length, headers: receivedHeaders } };
}

function options(body: ResponseBodyDescriptor, text: string | null) {
  const headers = new Headers(receivedHeaders.map(([name, value]): [string, string] => [name, value]));
  if (body.kind !== "none") { headers.set("content-type", body.mediaType); }
  return { baseUrl: "http://api.test", transport: { fetch: async () => new Response(text, { status: body.kind === "none" ? 204 : 200, headers }) } };
}

describe("response header boundary", () => {
  it("compares generated header casing independently of the declaration's casing", () => {
    const body: ResponseBodyDescriptor = { kind: "none" };
    for (let seed = 0; seed < 64; seed++) {
      const headers = receivedHeaders.map(([name, value]): readonly [string, string] => [
        [...name].map((letter, i) => (seed & (1 << (i % 6))) !== 0 ? letter.toUpperCase() : letter.toLowerCase()).join(""), value,
      ]);
      expect(decodeResponse(operation(body), { ...raw(body, null), headers }, { baseUrl: "http://api.test" })).toMatchObject({ kind: "response", headers: expectedHeaders });
      expect(createHydrationEnvelope(operation(body), { ...raw(body, null), headers }, context)).toMatchObject({ headers: expectedHeaders.map(([name, value]) => ({ name, value })) });
    }
  });

  it.each(cases)("does not expose credentials in fetched $name responses, even when declared", async ({ body, text }) => {
    const outcome = await executeWithRaw(operation(body), {}, options(body, text));
    expect(outcome.raw).toMatchObject({ kind: "raw", headers: expectedHeaders });
    expect(outcome.result).toMatchObject({ kind: "response", headers: expectedHeaders });
    expect(JSON.stringify(outcome.result)).not.toContain("CREDENTIAL_VALUE");
  });

  it.each(cases)("rechecks manually supplied raw $name headers before decoding", ({ body, text }) => {
    const result = decodeResponse(operation(body), raw(body, text), { baseUrl: "http://api.test" });
    expect(result).toMatchObject({ kind: "response", headers: expectedHeaders });
    expect(JSON.stringify(result)).not.toContain("PRIVATE_VALUE");
    expect(JSON.stringify(result)).not.toContain("CREDENTIAL_VALUE");
  });

  it.each(cases.filter(c => c.body.kind === "json" || c.body.kind === "text" || c.body.kind === "none"))("rechecks raw $name headers before Nuxt hydration", ({ body, text }) => {
    const op = operation(body);
    const envelope = createHydrationEnvelope(op, raw(body, text), context);
    expect(envelope).toMatchObject({ headers: expectedHeaders.map(([name, value]) => ({ name, value })) });
    expect(JSON.stringify(envelope)).not.toContain("PRIVATE_VALUE");
    expect(JSON.stringify(envelope)).not.toContain("CREDENTIAL_VALUE");
    if (envelope.kind !== "failure") {
      expect(decodeResponse(op, rawFromEnvelope(envelope), { baseUrl: "http://api.test" })).toMatchObject({ kind: "response", headers: expectedHeaders });
    }
  });

  it("keeps the same header boundary for streamed downloads and subscriptions", async () => {
    const binary = cases.find(c => c.name === "binary")!;
    const file = await download(operation(binary.body), {}, options(binary.body, binary.text), () => { });
    expect(file).toMatchObject({ kind: "download", headers: expectedHeaders });
    const events = cases.find(c => c.name === "SSE")!;
    const subscription = await subscribe(operation(events.body), {}, options(events.body, events.text), () => { });
    expect(subscription).toMatchObject({ kind: "subscription", headers: expectedHeaders });
  });
});
