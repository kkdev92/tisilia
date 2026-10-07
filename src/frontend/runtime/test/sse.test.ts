import { describe, expect, it, vi } from "vitest";
import { SseParser } from "../src/http/sse.js";
import { execute, subscribe, type OperationDescriptor } from "../src/http/client.js";
import { scalarCodec } from "../src/codec/scalars.js";
import { createHydrationEnvelope } from "../src/hydration.js";

const encoder = new TextEncoder();
const operation: OperationDescriptor = {
  id: "events", method: "GET", route: "/events", parameters: [], requestExecution: "browser-allowed", requestHeaderAllowlist: [],
  responses: [
    { caseId: "events.ok", status: 200, body: { kind: "sse", mediaType: "text/event-stream", dataFormat: "json", codec: scalarCodec("int64"), nullable: true }, hydration: "server-only", exposedHeaders: [] },
    { caseId: "events.error", status: 400, body: { kind: "json", mediaType: "application/json", codec: scalarCodec("string"), nullable: false }, hydration: "browser-safe", exposedHeaders: [] },
  ],
};
const options = (text: string, headers: Record<string, string> = { "content-type": "text/event-stream" }, status = 200) => ({ baseUrl: "http://api.test", transport: { fetch: async () => new Response(text, { headers, status }) } });

describe("server-sent events", () => {
  it("never puts SSE bytes in a hydration envelope, even with a forged browser-safe descriptor", () => {
    const op = { ...operation, responses: operation.responses.map(r => ({ ...r, hydration: "browser-safe" as const })) };
    const bytes = encoder.encode("data: SECRET\n\n");
    const envelope = createHydrationEnvelope(op, { kind: "raw", caseId: "events.ok", status: 200, mediaType: "text/event-stream", bodyKind: "sse", body: bytes, headers: [], metadata: { status: 200, mediaType: "text/event-stream", bodyBytes: bytes.length, headers: [] } }, { semanticHash: "hash", operationId: "events", scopeNonce: "scope", requestIdentity: "identity" });
    expect(envelope).toMatchObject({ kind: "failure", code: "server-only" }); expect(JSON.stringify(envelope)).not.toContain("SECRET");
  });
  it("parses BOM, UTF-8, CR/LF/CRLF, comments, fields, ID and retry state at every byte boundary", () => {
    const bytes = encoder.encode("\ufeff: comment\rid: first\r\nretry: 9007199254740993\ndata: 日本😀\rdata: two\nevent: update\r\n\r\nid: bad\0id\ndata:\n\nid\nretry: no\nevent:\ndata: tail\n\ndata: unfinished");
    for (let split = 0; split <= bytes.length; split++) {
      const p = new SseParser();
      const events = [...p.push(bytes.subarray(0, split)), ...p.push(bytes.subarray(split))];
      p.finish();
      expect(events).toEqual([
        { data: "日本😀\ntwo", event: "update", id: "first", retryMilliseconds: "9007199254740993" },
        { data: "", event: "message", id: "first", retryMilliseconds: "9007199254740993" },
        { data: "tail", event: "message", id: "", retryMilliseconds: "9007199254740993" },
      ]);
    }
    const p = new SseParser();
    expect([...bytes].flatMap(b => [...p.push(new Uint8Array([b]))])).toHaveLength(3);
    p.finish();
  });

  it("uses the same exact-number decoder for buffered calls and subscriptions, including .NET's empty null", async () => {
    const text = "data: 9007199254740993\n\ndata:\n\n";
    const buffered = await execute(operation, {}, options(text));
    expect(buffered).toMatchObject({ kind: "response", data: [{ data: 9007199254740993n }, { data: null }] });
    const events: unknown[] = [];
    expect(await subscribe(operation, {}, options(text), e => { events.push(e); })).toMatchObject({ kind: "subscription", eventsReceived: 2 });
    expect(events).toEqual("data" in buffered ? buffered.data : undefined);
  });

  it("delivers before EOF and awaits the event handler before reading again", async () => {
    let release!: () => void;
    const blocked = new Promise<void>(r => { release = r; });
    let entered!: () => void;
    const first = new Promise<void>(r => { entered = r; });
    let reads = 0;
    const stream = new ReadableStream<Uint8Array>({ pull(c) { if (++reads <= 2) { c.enqueue(encoder.encode(`data: ${reads}\n\n`)); } else { c.close(); } } }, { highWaterMark: 0 });
    const events: unknown[] = [];
    const pending = subscribe(operation, {}, { baseUrl: "http://api.test", transport: { fetch: async () => new Response(stream, { headers: { "content-type": "text/event-stream" } }) } }, async e => {
      events.push(e.data);
      if (events.length === 1) { entered(); await blocked; }
    });
    await first; expect(events).toEqual([1n]); expect(reads).toBe(1);
    release(); expect(await pending).toMatchObject({ kind: "subscription", eventsReceived: 2 });
    expect(events).toEqual([1n, 2n]);
  });

  it.each(["timeout", "cancelled", "throw"] as const)("bounds handlers and sanitizes %s", async mode => {
    const abort = new AbortController(); const cancel = vi.fn();
    const stream = new ReadableStream<Uint8Array>({ pull(c) { c.enqueue(encoder.encode("data: 1\n\n")); }, cancel }, { highWaterMark: 0 });
    const result = await subscribe(operation, {}, { baseUrl: "http://api.test", signal: abort.signal, limits: { timeoutMs: 30 }, transport: { fetch: async () => new Response(stream, { headers: { "content-type": "text/event-stream" } }) } }, async () => {
      if (mode === "throw") { throw new Error("SECRET token"); }
      if (mode === "cancelled") { abort.abort(); }
      await new Promise<void>(() => {});
    });
    expect(result.kind).toBe(mode === "throw" ? "transport-failure" : mode);
    expect(JSON.stringify(result)).not.toContain("SECRET"); expect(cancel).toHaveBeenCalledOnce();
  });

  it("classifies malformed event data, UTF-8, and JSON/connection limits", async () => {
    expect(await subscribe(operation, {}, options("data: SECRET\n\n"), () => {})).toMatchObject({ kind: "codec-failure", code: "unexpected-token" });
    expect(await subscribe(operation, {}, { ...options("data: 1\n\n"), limits: { maxBodyBytes: 3 } }, () => {})).toMatchObject({ kind: "limit-failure", limit: "maxBodyBytes" });
    expect(await subscribe(operation, {}, { ...options("data: 123\n\n"), limits: { maxNumberCharacters: 2 } }, () => {})).toMatchObject({ kind: "limit-failure", limit: "maxNumberCharacters" });
    const truncated = { baseUrl: "http://api.test", transport: { fetch: async () => new Response(new Uint8Array([0xc3]), { headers: { "content-type": "text/event-stream" } }) } };
    expect(await subscribe(operation, {}, truncated, () => {})).toMatchObject({ kind: "codec-failure", code: "invalid-utf8" });
    expect(await execute(operation, {}, truncated)).toMatchObject({ kind: "codec-failure", code: "invalid-utf8" });
  });

  it("checks status, media, charset, and contract guard before any handler receives data; decodes errors", async () => {
    const sink = vi.fn();
    expect(await subscribe(operation, {}, options('"no"', { "content-type": "application/json" }, 400), sink)).toMatchObject({ kind: "response", data: "no" });
    expect(await subscribe(operation, {}, options("data: 1\n\n", { "content-type": "text/event-stream; charset=latin1" }), sink)).toMatchObject({ kind: "codec-failure", code: "charset" });
    expect(await subscribe(operation, {}, options("data: 1\n\n", undefined, 201), sink)).toMatchObject({ kind: "unexpected-response" });
    expect(await subscribe(operation, {}, { ...options("data: 1\n\n", { "content-type": "text/event-stream", "x-contract": "other" } as Record<string, string>), expectedSemanticHash: "expected", semanticHashHeader: "x-contract" }, sink)).toMatchObject({ kind: "contract-mismatch" });
    expect(sink).not.toHaveBeenCalled();
  });
});
