import { describe, expect, it, vi } from "vitest";
import { execute, prepareRequest, type OperationDescriptor } from "../src/http/client.js";

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
