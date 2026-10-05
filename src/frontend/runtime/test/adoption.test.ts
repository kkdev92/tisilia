import { describe, expect, it, vi } from "vitest";
import { buildPlannedUrl, type RouteParameter, type RoutePlan } from "../src/http/routes.js";
import { execute, fetchResponse, prepareRequest, type BufferedFile, type OperationDescriptor } from "../src/http/client.js";
import { suggestedFileName } from "../src/http/filename.js";
import { standardBinder } from "../src/http/binders.js";
import { createHydrationEnvelope, checkHydrationEnvelope } from "../src/hydration.js";
import { scalarCodec } from "../src/codec/scalars.js";

const parameter = (name: string, props: Partial<RouteParameter> = {}): RouteParameter => ({ kind: "parameter", parameterId: name, name, optional: false, catchAll: "none", hasDefault: false, policies: [], ...props });
const plan = (...segments: (string | RouteParameter)[]): RoutePlan => ({ segments: segments.map(p => ({ parts: [typeof p === "string" ? { kind: "literal", value: p } : p] })) });
const url = (p: RoutePlan, entries: readonly (readonly [string, string])[]): string => buildPlannedUrl("https://example.test/api", p, new Map(entries), []).url.href;
const known = new Uint8Array([0, 255, 1, 195, 40]);
const binary: OperationDescriptor = {
  id: "file", method: "GET", route: "/file", routePlan: plan("file"), parameters: [],
  responses: [{ caseId: "file.ok", status: 200, body: { kind: "binary", mediaType: "application/pdf" }, hydration: "server-only", exposedHeaders: ["content-disposition"] }],
  requestExecution: "browser-allowed", requestHeaderAllowlist: [],
};
const fileResponse = (body: BodyInit | null = known, headers: Record<string, string> = {}): Response => new Response(body, { headers: { "content-type": "application/pdf", ...headers } });
const fetchOf = (response: Response): typeof fetch => async () => response;
const options = (response: Response) => ({ baseUrl: "https://example.test", transport: { fetch: fetchOf(response) } });

describe("resolved route plan", () => {
  it("RT02 RT04 RT06 RT34 omits optional/default values without shifting or filling", () => {
    expect(url(plan("items", parameter("id", { optional: true })), [])).toBe("https://example.test/api/items");
    const p = plan("pages", parameter("page", { hasDefault: true, defaultValue: "1" }));
    expect(url(p, [])).toBe("https://example.test/api/pages");
    expect(url(p, [["page", "1"]])).toBe("https://example.test/api/pages/1");
    expect(() => url(plan(parameter("a", { optional: true }), parameter("b", { optional: true })), [["b", "x"]])).toThrow(/intermediate/);
  });
  it("RT07 RT08 RT31 preserves complex separators and rejects only ambiguous values", () => {
    const p: RoutePlan = { segments: [{ parts: [{ kind: "literal", value: ".well-known" }] }, { parts: [parameter("name"), { kind: "separator", value: "." }, parameter("ext", { optional: true })] }] };
    expect(url(p, [["name", "report"]])).toBe("https://example.test/api/.well-known/report");
    expect(url(p, [["name", "report.v1"], ["ext", "pdf"]])).toBe("https://example.test/api/.well-known/report.v1.pdf");
    expect(() => url(p, [["name", "report.v1"]])).toThrow(/separator/);
    expect(() => url(p, [["name", "report"], ["ext", "a.pdf"]])).toThrow(/ambiguous/);
  });
  it("RT09 RT10 RT11 RT12 preserves double-star slashes and limits single-star values", () => {
    const stars = plan("files", parameter("path", { catchAll: "preserve-slashes" }));
    for (const path of ["a/b", "/a", "a//b/", "a"]) { expect(url(stars, [["path", path]])).toBe("https://example.test/api/files/" + path); }
    const star = plan("files", parameter("path", { catchAll: "encode-slashes" }));
    expect(url(star, [["path", "日本 +%"]])).toBe("https://example.test/api/files/%E6%97%A5%E6%9C%AC%20%2B%25");
    expect(() => url(star, [["path", "a/b"]])).toThrow(/slash/);
  });
  it.each([".", "..", "a/../b", "a\\b", "\ud800"])("RT16 RT28 RT30 rejects unsafe path %j", value => {
    expect(() => url(plan("files", parameter("path", { catchAll: "preserve-slashes" })), [["path", value]])).toThrow();
  });
  it("RT14 RT15 RT27 RT29 RT30 keeps literal percent, emoji, query and final identity exact", () => {
    const p = plan(".well-known", parameter("id"));
    const built = buildPlannedUrl("https://example.test/api", p, new Map([["id", "%2e?#+ 😀"]]), [{ name: "q", value: "%2B%20" }]);
    expect(built.url.pathname).toBe("/api/.well-known/%252e%3F%23%2B%20%F0%9F%98%80");
    expect(built.encodedPath).toBe(built.url.pathname);
    expect(built.url.search).toBe("?q=%2B%20");
  });
  it("RT13 keeps undefined, null and empty distinct", () => {
    const op: OperationDescriptor = { ...binary, routePlan: plan(parameter("id", { optional: true })), parameters: [{ id: "id", name: "id", presence: "optional", nullable: false, binder: standardBinder("string", "path"), location: "path", get: a => (a as { id?: unknown }).id }] };
    expect(prepareRequest(op, {}, { baseUrl: "https://example.test" }).url.pathname).toBe("/");
    expect(() => prepareRequest(op, { id: null }, { baseUrl: "https://example.test" })).toThrow(/null/);
    expect(() => prepareRequest(op, { id: "" }, { baseUrl: "https://example.test" })).toThrow(/empty/);
  });
  it("RT28 distinguishes /api and /apix boundaries after URL normalization", () => {
    const p = plan(parameter("id", { catchAll: "preserve-slashes" }));
    const build = (base: string, id: string) => buildPlannedUrl(base, p, new Map([["id", id]]), []).url;
    expect(build("https://example.test/api", "apix/x").pathname).toBe("/api/apix/x");
    expect(build("https://example.test/apix", "api/x").pathname).toBe("/apix/api/x");
    expect(build("https://example.test/api", "%2e%2e/apix").pathname).toBe("/api/%252e%252e/apix");
    for (const id of ["../apix/x", "/../apix/x", "x\\..\\apix"]) expect(() => build("https://example.test/api", id)).toThrow();
  });
});

describe("bounded binary", () => {
  it("BD06 BD11 BD40 preserves arbitrary bytes and derives names only from exposed headers", async () => {
    const response = fileResponse(known, { "content-type": "application/pdf; charset=iso-8859-1", "content-disposition": 'attachment; filename="report.pdf"' });
    const result = await execute(binary, {}, options(response));
    expect(result.kind).toBe("response");
    if (result.kind !== "response" || !("data" in result)) { throw new Error("expected bytes"); }
    expect(result.data).toEqual({ bytes: known, contentType: "application/pdf; charset=iso-8859-1", suggestedFileName: "report.pdf" });
    const hidden: OperationDescriptor = { ...binary, responses: binary.responses.map(r => ({ ...r, exposedHeaders: [] })) };
    const noName = await execute(hidden, {}, options(fileResponse(known, { "content-disposition": "attachment; filename=secret.pdf" })));
    expect(JSON.stringify(noName)).not.toContain("secret.pdf");
  });
  it("BD07 BD10 BD17 supports empty files and application/json as explicit bytes", async () => {
    const empty = await execute(binary, {}, options(fileResponse(null)));
    expect(empty).toMatchObject({ kind: "response", data: { bytes: new Uint8Array() } });
    const jsonFile: OperationDescriptor = { ...binary, responses: binary.responses.map(r => ({ ...r, body: { kind: "binary", mediaType: "application/json" } })) };
    expect(await execute(jsonFile, {}, options(fileResponse(known, { "content-type": "application/json" })))).toMatchObject({ kind: "response", data: { bytes: known } });
  });
  it("BD08 BD12 BD20 dispatches JSON errors and rejects undeclared cases", async () => {
    const mixed: OperationDescriptor = { ...binary, responses: [...binary.responses, { caseId: "problem", status: 416, body: { kind: "json", mediaType: "application/json", codec: scalarCodec("int64"), nullable: false }, hydration: "server-only", exposedHeaders: [] }] };
    expect(await execute(mixed, {}, options(new Response("9007199254740993", { status: 416, headers: { "content-type": "application/json" } })))).toMatchObject({ kind: "response", caseId: "problem", data: 9007199254740993n });
    expect(await execute(binary, {}, options(new Response(null, { status: 304 })))).toMatchObject({ kind: "unexpected-response", reason: "undeclared-status" });
    expect(await execute(binary, {}, options(new Response(known)))).toMatchObject({ kind: "unexpected-response", reason: "undeclared-media" });
  });
  it("BD14 counts bytes independently of absent or incorrect Content-Length", async () => {
    for (const headers of [{}, { "content-length": "1" }, { "content-length": "999999999" }]) {
      const responseHeaders = headers as Record<string, string>;
      expect(await execute(binary, {}, { ...options(fileResponse(known, responseHeaders)), limits: { maxBodyBytes: 5 } })).toMatchObject({ kind: "response", data: { bytes: known } });
      expect(await execute(binary, {}, { ...options(fileResponse(known, responseHeaders)), limits: { maxBodyBytes: 4 } })).toMatchObject({ kind: "limit-failure" });
    }
  });
  it("BD19 exposes partial Content-Range only when the contract allows it", async () => {
    for (const allowed of [true, false]) {
      const partial: OperationDescriptor = { ...binary, responses: binary.responses.map(r => ({ ...r, status: 206, exposedHeaders: allowed ? ["content-range"] : [] })) };
      const result = await execute(partial, {}, options(new Response(known, { status: 206, headers: { "content-type": "application/pdf", "content-range": "bytes 0-4/10" } })));
      expect(result).toMatchObject({ kind: "response", status: 206, data: { bytes: known }, headers: allowed ? [["content-range", "bytes 0-4/10"]] : [] });
    }
  });
  it.each(["throw", "reject", "pending"])("BD13 BD16 BD35 BD38 keeps limit despite %s cleanup", async mode => {
    const cancel = vi.fn(() => { if (mode === "throw") { throw new Error("secondary"); } return mode === "reject" ? Promise.reject(new Error("secondary")) : new Promise<void>(() => {}); });
    const stream = new ReadableStream<Uint8Array>({ start(c) { c.enqueue(known); }, cancel });
    const result = await execute(binary, {}, { ...options(fileResponse(stream)), limits: { maxBodyBytes: 4, timeoutMs: 30 } });
    expect(result.kind).toBe("limit-failure");
    expect(cancel).toHaveBeenCalledOnce();
    expect(stream.locked).toBe(false);
    expect(await execute(binary, {}, { ...options(fileResponse()), limits: { maxBodyBytes: 5 } })).toMatchObject({ kind: "response" });
  });
  it("BD17 BD36 never falls back to unbounded arrayBuffer and classifies bad readers", async () => {
    for (const body of [undefined, {}, { getReader() { throw new Error("locked"); } }, new ReadableStream({ start(c) { c.enqueue("bad"); c.close(); } })]) {
      const arrayBuffer = vi.fn();
      const response = { status: 200, type: "basic", headers: new Headers({ "content-type": "application/pdf" }), body, arrayBuffer } as unknown as Response;
      expect(await execute(binary, {}, options(response))).toMatchObject({ kind: "transport-failure", reason: "read" });
      expect(arrayBuffer).not.toHaveBeenCalled();
    }
  });
  it.each(["opaque", "opaqueredirect", "error"])("BD37 refuses %s responses", async type => {
    const response = { status: 0, type, headers: new Headers(), body: null } as Response;
    expect(await execute(binary, {}, options(response))).toMatchObject({ kind: "transport-failure" });
  });
  it("BD15 BD32 BD33 NI09 ends provider/read waits and never sends after late provider completion", async () => {
    let resolveProvider!: (value: readonly (readonly [string, string])[]) => void;
    const fetch = vi.fn(async () => fileResponse());
    const result = await execute(binary, {}, { baseUrl: "https://example.test", limits: { timeoutMs: 10 }, credentialProvider: () => new Promise(resolve => { resolveProvider = resolve; }), transport: { fetch } });
    expect(result.kind).toBe("timeout");
    resolveProvider([["authorization", "secret"]]);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(fetch).not.toHaveBeenCalled();
    const provider = vi.fn();
    const aborted = AbortSignal.abort();
    expect(await execute(binary, {}, { baseUrl: "https://example.test", signal: aborted, credentialProvider: provider, transport: { fetch } })).toMatchObject({ kind: "cancelled" });
    expect(provider).not.toHaveBeenCalled();
    const hanging = new ReadableStream<Uint8Array>({ pull: () => new Promise<void>(() => {}), cancel: () => new Promise<void>(() => {}) });
    expect(await execute(binary, {}, { ...options(fileResponse(hanging)), limits: { timeoutMs: 10 } })).toMatchObject({ kind: "timeout" });
    expect(hanging.locked).toBe(false);
  });
  it("BD34 shares one clock through provider fetch and decode", async () => {
    let now = 1000;
    const result = await execute(binary, {}, { baseUrl: "https://example.test", limits: { timeoutMs: 100 }, credentialProvider: () => { now += 70; return []; }, transport: { now: () => now, fetch: async () => { now += 31; return fileResponse(); } } });
    expect(result.kind).toBe("timeout");
  });
  it.each(["read", "abort", "limit"] as const)("BD38 keeps the first terminal cause %s despite cleanup abort/rejection", async first => {
    const abort = new AbortController();
    const reader = {
      read: () => {
        if (first === "abort") { abort.abort(); }
        if (first === "limit") { return Promise.resolve({ done: false, value: known }); }
        return Promise.reject(new Error("primary read failure"));
      },
      cancel: vi.fn(() => { abort.abort(); return Promise.reject(new Error("secondary cleanup failure")); }),
      releaseLock: vi.fn(),
    };
    const response = { status: 200, type: "basic", headers: new Headers({ "content-type": "application/pdf" }), body: { getReader: () => reader } } as unknown as Response;
    const result = await execute(binary, {}, { ...options(response), signal: abort.signal, limits: { maxBodyBytes: 4 } });
    expect(result.kind).toBe(first === "read" ? "transport-failure" : first === "abort" ? "cancelled" : "limit-failure");
    expect(reader.cancel).toHaveBeenCalledOnce(); expect(reader.releaseLock).toHaveBeenCalledOnce();
  });
  it("NI09 JSON uses the same provider deadline and BD33 cancels during provider without disclosing credentials", async () => {
    const json: OperationDescriptor = { ...binary, responses: [{ caseId: "json", status: 200, body: { kind: "json", mediaType: "application/json", codec: scalarCodec("int64"), nullable: false }, hydration: "browser-safe", exposedHeaders: [] }] };
    const fetch = vi.fn(async () => new Response("1"));
    expect(await execute(json, {}, { baseUrl: "https://example.test", limits: { timeoutMs: 10 }, credentialProvider: () => new Promise(() => {}), transport: { fetch } })).toMatchObject({ kind: "timeout" });
    const abort = new AbortController();
    setTimeout(() => abort.abort(), 5);
    const result = await execute(json, {}, { baseUrl: "https://example.test", signal: abort.signal, credentialProvider: () => new Promise(() => {}), transport: { fetch } });
    expect(result).toMatchObject({ kind: "cancelled" }); expect(fetch).not.toHaveBeenCalled();
    const failure = await execute(json, {}, { baseUrl: "https://example.test", credentialProvider: () => { throw new Error("Bearer TEST_SECRET"); } });
    expect(JSON.stringify(failure)).not.toContain("TEST_SECRET");
  });
  it("BD27 BD44 makes safe server-only envelopes with no binary payload or refetch mismatch", async () => {
    const raw = await fetchResponse(binary, {}, options(fileResponse(known, { "content-disposition": "attachment; filename=secret.pdf" })));
    const ctx = { semanticHash: "sha256:" + "0".repeat(64), operationId: binary.id, requestIdentity: "sha256:" + "1".repeat(64), scopeNonce: "scope" };
    const envelope = createHydrationEnvelope(binary, raw, ctx);
    expect(envelope).toMatchObject({ kind: "failure", code: "server-only" });
    expect(JSON.stringify(envelope)).not.toMatch(/secret|bytes|fileName|blob:/);
    expect(checkHydrationEnvelope(binary, envelope, ctx)).toBeUndefined();
  });
});

describe("Content-Disposition", () => {
  it.each([
    ["attachment; filename=report.pdf", "report.pdf"],
    ['attachment; filename="a;b.pdf"', "a;b.pdf"],
    ["attachment; filename=literal%20.pdf", "literal%20.pdf"],
    ["attachment; filename=fallback.pdf; filename*=UTF-8''%E6%97%A5%E6%9C%AC.pdf", "日本.pdf"],
    ["attachment; filename=good.pdf; filename*=UTF-8''%FF", "good.pdf"],
    ["attachment; filename=good.pdf; filename*=other''bad", "good.pdf"],
    ["attachment; filename=a; filename=b", undefined],
    ['attachment; filename="../../report.pdf"', "report.pdf"],
    ["attachment; filename=CON.pdf", undefined],
    ['attachment; filename=".."', undefined],
    ['attachment; filename="a\u0000b"', undefined],
  ])("BD21 BD22 BD23 %s", (header, name) => { expect(suggestedFileName(header)).toBe(name); });
});
