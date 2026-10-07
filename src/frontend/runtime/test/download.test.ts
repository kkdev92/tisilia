import { describe, expect, it, vi } from "vitest";
import { download, type OperationDescriptor } from "../src/http/client.js";
import { scalarCodec } from "../src/codec/scalars.js";

const operation: OperationDescriptor = {
  id: "file", method: "GET", route: "/file", parameters: [], requestExecution: "browser-allowed", requestHeaderAllowlist: [],
  responses: [
    { caseId: "file.ok", status: 200, body: { kind: "binary", mediaType: "application/octet-stream" }, hydration: "server-only", exposedHeaders: ["content-disposition"] },
    { caseId: "file.error", status: 400, body: { kind: "json", mediaType: "application/json", codec: scalarCodec("string"), nullable: false }, hydration: "browser-safe", exposedHeaders: [] },
  ],
};
const options = { baseUrl: "http://api.test" };
const headers = { "content-type": "application/octet-stream", "content-disposition": 'attachment; filename="report.bin"' };

describe("streaming finite downloads", () => {
  it("delivers bytes incrementally, awaits each write, and returns metadata without a buffer", async () => {
    let release!: () => void;
    const blocked = new Promise<void>(resolve => { release = resolve; });
    let entered!: () => void;
    const firstWrite = new Promise<void>(resolve => { entered = resolve; });
    let reads = 0;
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) { reads++; if (reads <= 3) { controller.enqueue(new Uint8Array([reads, 255])); } else { controller.close(); } },
    }, { highWaterMark: 0 });
    const writes: number[][] = [];
    const sink = async (chunk: Uint8Array) => { writes.push([...chunk]); if (writes.length === 1) { entered(); await blocked; } };
    const pending = download(operation, {}, { ...options, transport: { fetch: async () => new Response(stream, { headers }) } }, sink);
    await firstWrite;
    expect(reads).toBe(1);
    expect(writes).toEqual([[1, 255]]);
    release();
    const result = await pending;
    expect(writes).toEqual([[1, 255], [2, 255], [3, 255]]);
    expect(result).toMatchObject({ kind: "download", file: { bytesWritten: 6, contentType: "application/octet-stream", suggestedFileName: "report.bin" } });
    expect(JSON.stringify(result)).not.toContain('"bytes":');
  });

  it("checks byte limits before writing the exceeding chunk and cancels the reader", async () => {
    const cancel = vi.fn();
    let count = 0;
    const stream = new ReadableStream<Uint8Array>({ pull(controller) { count++; controller.enqueue(new Uint8Array([count, 255])); }, cancel }, { highWaterMark: 0 });
    const sink = vi.fn();
    const result = await download(operation, {}, { ...options, limits: { maxBodyBytes: 3 }, transport: { fetch: async () => new Response(stream, { headers }) } }, sink);
    expect(result.kind).toBe("limit-failure");
    expect(sink).toHaveBeenCalledTimes(1);
    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it.each(["timeout", "cancelled", "throw"] as const)("bounds and sanitizes a sink that ends with %s", async mode => {
    const abort = new AbortController();
    const cancel = vi.fn();
    let sinkSignal: AbortSignal | undefined;
    const stream = new ReadableStream<Uint8Array>({ pull(controller) { controller.enqueue(new Uint8Array([1])); }, cancel }, { highWaterMark: 0 });
    const result = await download(operation, {}, { ...options, signal: abort.signal, limits: { timeoutMs: 30 }, transport: { fetch: async () => new Response(stream, { headers }) } }, async (_chunk, signal) => {
      sinkSignal = signal;
      if (mode === "throw") { throw new Error("SECRET token"); }
      if (mode === "cancelled") { abort.abort(); }
      await new Promise<void>(() => {});
    });
    expect(result.kind).toBe(mode === "throw" ? "transport-failure" : mode);
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(result)).not.toContain("SECRET");
    if (mode !== "throw") { expect(sinkSignal?.aborted).toBe(true); }
  });

  it("decodes declared JSON errors without passing them to the file sink", async () => {
    const sink = vi.fn();
    const result = await download(operation, {}, { ...options, transport: { fetch: async () => new Response('"no file"', { status: 400, headers: { "content-type": "application/json" } }) } }, sink);
    expect(result).toMatchObject({ kind: "response", caseId: "file.error", data: "no file" });
    expect(sink).not.toHaveBeenCalled();
  });

  it.each(["status", "media", "hash"] as const)("refuses %s mismatches before writing", async mismatch => {
    const sink = vi.fn();
    const result = await download(operation, {}, {
      ...options, expectedSemanticHash: "expected", semanticHashHeader: "x-tisilia-contract",
      transport: { fetch: async () => new Response(new Uint8Array([1]), {
        status: mismatch === "status" ? 404 : 200,
        headers: { ...headers, ...(mismatch === "media" ? { "content-type": "text/plain" } : {}), ...(mismatch === "hash" ? { "x-tisilia-contract": "different" } : {}) },
      }) },
    }, sink);
    expect(result.kind).toBe(mismatch === "hash" ? "contract-mismatch" : "unexpected-response");
    expect(sink).not.toHaveBeenCalled();
  });

  it("accepts a native null body as an empty file", async () => {
    const sink = vi.fn();
    const result = await download(operation, {}, { ...options, transport: { fetch: async () => new Response(null, { headers }) } }, sink);
    expect(result).toMatchObject({ kind: "download", file: { bytesWritten: 0 } });
    expect(sink).not.toHaveBeenCalled();
  });
});
