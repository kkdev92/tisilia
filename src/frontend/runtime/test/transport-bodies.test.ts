import { Readable } from "node:stream";
import { describe, expect, it } from "vitest";
import { send } from "../src/http/transport.js";

// Tisilia requires a Web stream or a native null body. Unbounded arrayBuffer fallback is intentionally unsupported.
function responseLike(body: unknown, text: string, status = 200): Response {
  const bytes = new TextEncoder().encode(text);
  return {
    status,
    type: "basic",
    headers: new Headers({ "content-type": "application/json" }),
    body,
    arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
  } as unknown as Response;
}

const request = { url: new URL("http://api.test/users/1"), method: "GET", headers: [] } as const;

describe("transport with a fetch whose responses have no web stream body", () => {
  it.each([
    ["a fetch that leaves the body undefined", undefined],
    ["a fetch that gives a Node Readable", Readable.from([Buffer.from("ignored")])],
  ])("rejects the non-stream adapter of %s", async (_name, body) => {
    const outcome = await send(request, { fetch: (async () => responseLike(body, '{"id":1}')) as typeof fetch });
    expect(outcome.kind).toBe("transport-failure");
  });

  it("rejects an adapter before it could bypass maxBodyBytes", async () => {
    const outcome = await send(request, { fetch: (async () => responseLike(undefined, '{"id":12345}')) as typeof fetch, limits: { maxBodyBytes: 4 } });
    expect(outcome.kind).toBe("transport-failure");
  });

  it("treats a native null body as empty", async () => {
    const outcome = await send(request, { fetch: async () => new Response(null) });
    expect(outcome.kind).toBe("ok");
    if (outcome.kind === "ok") { expect(outcome.response.body).toEqual(new Uint8Array()); }
  });

  it("reports a failing read as a transport failure instead of throwing", async () => {
    const broken = { ...responseLike(undefined, ""), arrayBuffer: async () => { throw new TypeError("network lost"); } } as unknown as Response;
    const outcome = await send(request, { fetch: (async () => broken) as typeof fetch });
    expect(outcome.kind).toBe("transport-failure");
  });
});
