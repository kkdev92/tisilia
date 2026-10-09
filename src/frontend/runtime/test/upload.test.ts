import { describe, expect, it, vi } from "vitest";
import { execute, prepareRequest, type OperationDescriptor } from "../src/http/client.js";
import { supportsRequestStreams } from "../src/http/transport.js";

const operation: OperationDescriptor = {
  id: "upload", method: "POST", route: "/upload", parameters: [],
  requestBody: { kind: "binary", mediaType: "application/octet-stream", presence: "required", get: args => (args as { body?: unknown }).body },
  responses: [{ caseId: "uploaded", status: 204, body: { kind: "none" }, hydration: "browser-safe", exposedHeaders: [] }],
  requestExecution: "browser-allowed", requestHeaderAllowlist: [],
};
const options = { baseUrl: "http://api.test" };

describe("finite raw uploads", () => {
  it("snapshots only the selected view, without text or base64 encoding", () => {
    const input = new Uint8Array([99, 0, 255, 195, 40, 88]);
    const prepared = prepareRequest(operation, { body: input.subarray(1, 5) }, options);
    input.fill(7);
    expect(prepared.bodyBytes).toEqual(new Uint8Array([0, 255, 195, 40]));
    expect(prepared.bodyText).toBeUndefined();
    expect(prepared.headers).toEqual([["content-type", "application/octet-stream"]]);
  });

  it("distinguishes empty, omitted and invalid bodies", () => {
    expect(prepareRequest(operation, { body: new Uint8Array() }, options).bodyBytes).toHaveLength(0);
    for (const body of [undefined, null, "AAAA", [0, 1], new ArrayBuffer(1)]) {
      expect(() => prepareRequest(operation, { body }, options)).toThrow();
    }
    const optional = { ...operation, requestBody: { ...operation.requestBody!, presence: "optional" as const } };
    expect(prepareRequest(optional, {}, options).bodyBytes).toBeUndefined();
    expect(prepareRequest(optional, {}, options).headers).toEqual([]);
    expect(() => prepareRequest({ ...operation, method: "GET" }, { body: new Uint8Array() }, options)).toThrow();
  });

  it("enforces byte limits before credentials or fetch and allows the exact boundary", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(null, { status: 204 }));
    const credentialProvider = vi.fn(() => []);
    const config = { ...options, limits: { maxBodyBytes: 2 }, credentialProvider, transport: { fetch } };
    expect((await execute(operation, { body: new Uint8Array(3) }, config)).kind).toBe("limit-failure");
    expect(fetch).not.toHaveBeenCalled();
    expect(credentialProvider).not.toHaveBeenCalled();
    expect((await execute(operation, { body: new Uint8Array([0, 255]) }, config)).kind).toBe("response");
    expect(fetch.mock.calls[0]?.[1]?.body).toEqual(new Uint8Array([0, 255]));
  });

  it("keeps bytes stable across an asynchronous credential provider", async () => {
    const body = new Uint8Array([0, 255]);
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(null, { status: 204 }));
    await execute(operation, { body }, { ...options, transport: { fetch }, credentialProvider: async () => { body.fill(8); return []; } });
    expect(fetch.mock.calls[0]?.[1]?.body).toEqual(new Uint8Array([0, 255]));
  });
});

describe("streamed raw uploads", () => {
  const chunks = (...parts: number[][]) => new ReadableStream<Uint8Array>({ start(c) { for (const part of parts) { c.enqueue(new Uint8Array(part)); } c.close(); } });
  const read = async (body: unknown) => new Uint8Array(await new Response(body as ReadableStream<Uint8Array>).arrayBuffer());

  it("sends a stream as it is read, with duplex half, where fetch streams request bodies", async () => {
    expect(supportsRequestStreams()).toBe(true); // Node's fetch
    const prepared = prepareRequest(operation, { body: chunks([0, 1], [255]) }, options);
    expect(prepared.bodyBytes).toBeUndefined();
    expect(prepared.bodyText).toBeUndefined();
    expect(prepared.bodyStream).toBeInstanceOf(ReadableStream);
    let sent: Uint8Array | undefined;
    const fetch = vi.fn<typeof globalThis.fetch>(async (_url, init) => {
      expect((init as RequestInit & { duplex?: string }).duplex).toBe("half");
      sent = await read(init?.body);
      return new Response(null, { status: 204 });
    });
    expect((await execute(operation, { body: chunks([0, 1], [255]) }, { ...options, transport: { fetch } })).kind).toBe("response");
    expect(sent).toEqual(new Uint8Array([0, 1, 255]));
  });

  it("fails a stream that goes past maxBodyBytes while it is sent, and one whose source fails", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async (_url, init) => { await read(init?.body); return new Response(null, { status: 204 }); });
    const config = { ...options, limits: { maxBodyBytes: 2 }, transport: { fetch } };
    expect(await execute(operation, { body: chunks([0, 1], [2]) }, config)).toEqual({ kind: "limit-failure", operationId: "upload", limit: "maxBodyBytes" });
    expect((await execute(operation, { body: chunks([0], [1]) }, config)).kind).toBe("response");
    const failing = new ReadableStream<Uint8Array>({ pull(c) { c.error(new Error("disk gone: /private/path")); } });
    const failed = await execute(operation, { body: failing }, config);
    expect(failed).toEqual({ kind: "transport-failure", operationId: "upload", reason: "network", message: "the request body stream failed" });
    const notBytes = new ReadableStream({ start(c) { c.enqueue("text"); c.close(); } });
    expect((await execute(operation, { body: notBytes }, config)).kind).toBe("transport-failure");
  });

  it("refuses a stream where fetch would send it as text or nothing, and one already being read", () => {
    const platform = globalThis.Request;
    // Firefox and Safari: the stream becomes text, with a Content-Type, and duplex is never read
    globalThis.Request = class extends platform {
      constructor(input: RequestInfo | URL, init?: RequestInit) { super(input, init === undefined ? undefined : { method: init.method ?? "GET", body: "[object ReadableStream]" }); }
    } as typeof Request;
    try {
      expect(supportsRequestStreams()).toBe(false);
      expect(() => prepareRequest(operation, { body: chunks([0]) }, options)).toThrow(/only Node's fetch streams a request body/);
    } finally {
      globalThis.Request = platform;
    }
    // a browser: WebKit passes the Fetch check yet sends an empty body, so outside Node a stream is never sent
    const node = Object.getOwnPropertyDescriptor(globalThis, "process")!;
    Object.defineProperty(globalThis, "process", { value: undefined, configurable: true, writable: true });
    try {
      expect(supportsRequestStreams()).toBe(false);
    } finally {
      Object.defineProperty(globalThis, "process", node);
    }
    expect(supportsRequestStreams()).toBe(true);
    const locked = chunks([0]);
    locked.getReader();
    expect(() => prepareRequest(operation, { body: locked }, options)).toThrow(/already being read/);
    expect(() => prepareRequest({ ...operation, method: "GET" }, { body: chunks([0]) }, options)).toThrow();
  });
});
