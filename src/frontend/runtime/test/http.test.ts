import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { scalarCodec, webNumbers } from "../src/codec/scalars.js";
import { objectCodec } from "../src/codec/structural.js";
import { standardBinder } from "../src/http/binders.js";
import { execute, prepareRequest, type OperationDescriptor } from "../src/http/client.js";
import { buildUrl, encodePathSegment, encodeQueryComponent, validateHeaderValue } from "../src/http/url.js";
import { parseMediaType } from "../src/http/mediaType.js";
import { computeRequestIdentity, createRequestIdentityRecord, requestIdentityBytes } from "../src/identity.js";
import { parseHydrationEnvelope } from "../src/envelope.js";
import { createHydrationEnvelope } from "../src/hydration.js";
import { resolveLimits } from "../src/json/limits.js";
import { parseJson } from "../src/json/parser.js";
import { CodecError } from "../src/codec/errors.js";

let server: Server;
let baseUrl: string;
const id = "550e8400-e29b-41d4-a716-446655440000";

const user = objectCodec<{ id: string; revision: bigint; nickname?: string | null }>({
  id: "demo.UserResponse.codec",
  typeId: "demo.UserResponse",
  properties: [
    { name: "id", codec: scalarCodec("guid"), presence: "required", nullable: false },
    { name: "revision", codec: scalarCodec("int64", { numbers: webNumbers }), presence: "required", nullable: false },
    { name: "nickname", codec: scalarCodec("string"), presence: "optional", nullable: true },
  ],
  nameMatching: "ordinal-ignore-case",
  duplicates: "last-wins",
  readAdditional: "ignore",
  writeAdditional: "ignore",
  request: true,
  response: true,
});

const problem = objectCodec<{ title?: string | null; status?: number | null }>({
  id: "demo.Problem.codec",
  typeId: "demo.Problem",
  properties: [
    { name: "title", codec: scalarCodec("string"), presence: "optional", nullable: true },
    { name: "status", codec: scalarCodec("int32", { numbers: webNumbers }), presence: "optional", nullable: true },
  ],
  nameMatching: "ordinal-ignore-case",
  duplicates: "last-wins",
  readAdditional: "ignore",
  writeAdditional: "ignore",
  request: false,
  response: true,
});

const getUser: OperationDescriptor = {
  id: "users.get",
  method: "GET",
  route: "/users/{id:guid}",
  profileId: "web",
  parameters: [
    { name: "id", location: "path", binder: standardBinder("guid", "path"), presence: "required", nullable: false, get: (a) => (a as { id: string }).id },
    { name: "verbose", location: "query", binder: standardBinder("boolean", "query"), presence: "optional", nullable: false, get: (a) => (a as { verbose?: boolean }).verbose },
    { name: "tags", location: "query", binder: standardBinder("string", "query", { cardinality: "repeated" }), presence: "optional", nullable: false, get: (a) => (a as { tags?: string[] }).tags },
    { name: "mode", location: "query", binder: standardBinder("string", "query"), presence: "optional", nullable: false, get: (a) => (a as { mode?: string }).mode },
  ],
  responses: [
    { caseId: "users.get.ok", status: 200, body: { kind: "json", mediaType: "application/json", codec: user, nullable: false }, hydration: "browser-safe", exposedHeaders: ["etag"] },
    { caseId: "users.get.not-found", status: 404, body: { kind: "json", mediaType: "application/problem+json", codec: problem, nullable: false }, hydration: "server-only", exposedHeaders: [] },
    { caseId: "users.get.gone", status: 410, body: { kind: "text", mediaType: "text/plain" }, hydration: "server-only", exposedHeaders: [] },
    { caseId: "users.get.no-content", status: 204, body: { kind: "none" }, hydration: "browser-safe", exposedHeaders: [] },
  ],
  requestExecution: "browser-allowed",
  requestHeaderAllowlist: ["accept-language"],
};

const putUser: OperationDescriptor = {
  ...getUser,
  id: "users.put",
  method: "PUT",
  requestBody: { mediaType: "application/json", codec: user, presence: "required", nullable: false, get: (a) => (a as { body: unknown }).body },
};

beforeAll(async () => {
  server = createServer((req, res) => {
    const url = new URL(req.url!, "http://localhost");
    const mode = url.searchParams.get("mode") ?? "ok";
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => chunks.push(c));
    req.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      switch (mode) {
        case "ok":
          res.writeHead(200, { "content-type": "application/json; charset=utf-8", etag: '"v1"', "x-secret": "hide" });
          res.end('{"id":"550E8400-E29B-41D4-A716-446655440000","revision":9007199254740993,"nickname":null,"extra":true}');
          return;
        case "echo":
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ id: "550e8400-e29b-41d4-a716-446655440000", revision: 1, nickname: body + "|" + (req.headers["content-type"] ?? "") + "|" + (req.headers["accept-language"] ?? "") + "|" + req.url }));
          return;
        case "notfound":
          res.writeHead(404, { "content-type": "application/problem+json" });
          res.end('{"title":"nope","status":404,"traceId":"x"}');
          return;
        case "gone":
          res.writeHead(410, { "content-type": "text/plain; charset=utf-8" });
          res.end("it is gone é");
          return;
        case "nocontent":
          res.writeHead(204);
          res.end();
          return;
        case "badjson":
          res.writeHead(200, { "content-type": "application/json" });
          res.end('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":');
          return;
        case "badshape":
          res.writeHead(200, { "content-type": "application/json" });
          res.end('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":"abc"}');
          return;
        case "html":
          res.writeHead(200, { "content-type": "text/html" });
          res.end("<html>");
          return;
        case "teapot":
          res.writeHead(418, { "content-type": "application/json" });
          res.end("{}");
          return;
        case "redirect":
          res.writeHead(302, { location: "/users/550e8400-e29b-41d4-a716-446655440000" });
          res.end();
          return;
        case "big":
          res.writeHead(200, { "content-type": "application/json" });
          res.end('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":1,"nickname":"' + "x".repeat(5000) + '"}');
          return;
        case "empty":
          res.writeHead(200, { "content-type": "application/json" });
          res.end("");
          return;
        case "null":
          res.writeHead(200, { "content-type": "application/json" });
          res.end("null");
          return;
        case "latin":
          res.writeHead(200, { "content-type": "application/json; charset=iso-8859-1" });
          res.end("{}");
          return;
        case "slow":
          setTimeout(() => {
            res.writeHead(200, { "content-type": "application/json" });
            res.end("{}");
          }, 500);
          return;
        default:
          res.writeHead(500);
          res.end();
      }
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

describe("url building", () => {
  it("percent-encodes path segments and query components", () => {
    expect(encodePathSegment("a b+c/é".replace("/", ""))).toBe("a%20b%2Bc%C3%A9");
    expect(() => encodePathSegment("a/b")).toThrow(CodecError);
    expect(() => encodePathSegment("")).toThrow(CodecError);
    expect(encodeQueryComponent("a b+c&d=é")).toBe("a%20b%2Bc%26d%3D%C3%A9");
    const built = buildUrl("https://api.example.com/base/", "/users/{id:guid}", new Map([["id", "x"]]), [{ name: "q", value: "1" }, { name: "q", value: "2" }]);
    expect(built.url.toString()).toBe("https://api.example.com/base/users/x?q=1&q=2");
    expect(built.encodedPath).toBe("/base/users/x");
    expect(() => buildUrl("https://user:pw@api.example.com", "/x", new Map(), [])).toThrow(CodecError);
    expect(() => buildUrl("ftp://api.example.com", "/x", new Map(), [])).toThrow(CodecError);
    expect(() => buildUrl("https://api.example.com", "/x?y=1", new Map(), [])).toThrow(CodecError);
    expect(() => validateHeaderValue("a\r\nb")).toThrow(CodecError);
  });

  it("reads route templates as ASP.NET Core does: escaped braces, constraints holding '?' or '/'", () => {
    const build = (route: string, values: [string, string][] = []) => buildUrl("https://api.example.com", route, new Map(values), []);
    // the inline regex constraint of the ASP.NET Core routing documentation: `{{3}}` belongs to the regex, not a parameter '3'
    expect(build("/ssn/{message:regex(^\\d{{3}}-\\d{{2}}-\\d{{4}}$)}", [["message", "123-45-6789"]]).encodedPath).toBe("/ssn/123-45-6789");
    // escaped braces in literal text are literal braces, sent percent-encoded (the server matches the decoded path)
    const literal = build("/braces/{{literal}}/{id:int}", [["id", "42"]]);
    expect(literal.encodedPath).toBe("/braces/%7Bliteral%7D/42");
    expect(literal.url.pathname).toBe("/braces/%7Bliteral%7D/42");
    expect(build("/v/{v:regex(^\\d+(\\.\\d+)?$)}", [["v", "1.2"]]).encodedPath).toBe("/v/1.2");
    expect(build("/u/{u:regex(^https?://x#y$)}", [["u", "a"]]).encodedPath).toBe("/u/a");
    expect(build("/c/{c:regex(([}}])\\w+)}", [["c", "b"]]).encodedPath).toBe("/c/b");
    expect(build("/p/{id:int:min(1)}/{name:alpha}", [["id", "1"], ["name", "ab"]]).encodedPath).toBe("/p/1/ab");
    expect(() => build("/v/{v:regex(^\\d{3}$)}", [["v", "123"]])).toThrow(/unescaped/);
    expect(() => build("/v/{v", [["v", "1"]])).toThrow(/unterminated/);
    expect(() => build("/v/}x")).toThrow(/unbalanced/);
    expect(() => build("/v/a?b")).toThrow(/without query or fragment/);
    expect(() => build("/v/a#b")).toThrow(/without query or fragment/);
    expect(() => build("/ssn/{message:regex(^\\d{{3}}$)}")).toThrow(/'message' has no value/);
  });

  it("parses media types case-insensitively", () => {
    expect(parseMediaType("Application/JSON; Charset=UTF-8")?.essence).toBe("application/json");
    expect(parseMediaType("Application/JSON; Charset=UTF-8")?.charset).toBe("utf-8");
    expect(parseMediaType("application/vnd.x+json")?.essence).toBe("application/vnd.x+json");
    expect(parseMediaType("garbage")).toBeUndefined();
  });

  it("parses media type parameters as RFC 9110 defines them", () => {
    // a quoted-string may hold ";" and quoted-pairs; empty parameters and OWS around ";" are allowed
    const quoted = parseMediaType('application/json; profile="a;b"; charset=utf-8');
    expect(quoted?.essence).toBe("application/json");
    expect(quoted?.charset).toBe("utf-8");
    expect(quoted?.parameters.get("profile")).toBe("a;b");
    expect(parseMediaType('text/plain; title="say \\"hi\\" \\\\ bye"')?.parameters.get("title")).toBe('say "hi" \\ bye');
    expect(parseMediaType("text/plain;")?.essence).toBe("text/plain");
    expect(parseMediaType("text/plain ;; charset=UTF-8 ; ")?.charset).toBe("utf-8");
    expect(parseMediaType('text/plain; charset="utf-8"')?.charset).toBe("utf-8");
    expect(parseMediaType("text/plain; charset=utf-8; charset=latin1")?.charset).toBe("utf-8");
    // no whitespace around "=", no whitespace inside type/subtype, terminated quotes, token names and values
    expect(parseMediaType("text/plain; charset = utf-8")).toBeUndefined();
    expect(parseMediaType("text/plain; charset= utf-8")).toBeUndefined();
    expect(parseMediaType("text / plain")).toBeUndefined();
    expect(parseMediaType('text/plain; title="open')).toBeUndefined();
    expect(parseMediaType("text/plain; =utf-8")).toBeUndefined();
    expect(parseMediaType("text/plain; charset=")).toBeUndefined();
    expect(parseMediaType("text/plain; charset=utf 8")).toBeUndefined();
    expect(parseMediaType("text/plain charset=utf-8")).toBeUndefined();
  });
});

describe("execute", () => {
  it("decodes a declared JSON case, exposes only declared headers and normalizes the guid", async () => {
    const result = await execute(getUser, { id, verbose: true }, { baseUrl });
    expect(result.kind).toBe("response");
    if (result.kind === "response" && "data" in result) {
      expect(result.caseId).toBe("users.get.ok");
      expect(result.data).toEqual({ id: "550e8400-e29b-41d4-a716-446655440000", revision: 9007199254740993n, nickname: null });
      expect(result.headers).toEqual([["etag", '"v1"']]);
    }
  });

  it("sends the encoded body, content-type, allowlisted headers and repeated query in order", async () => {
    const result = await execute(putUser, { id, mode: "echo", body: { id, revision: 9007199254740993n }, tags: ["b a", "c"] }, { baseUrl, headers: [["accept-language", "ja"]] });
    expect(result.kind).toBe("response");
    if (result.kind === "response" && "data" in result) {
      expect((result.data as { nickname: string }).nickname).toBe('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":9007199254740993}|application/json|ja|/users/550e8400-e29b-41d4-a716-446655440000?tags=b%20a&tags=c&mode=echo');
    }
    await expect(execute(putUser, { id, mode: "echo", body: {} }, { baseUrl, headers: [["x-custom", "1"]] })).resolves.toMatchObject({ kind: "transport-failure", reason: "request-encoding" });
    await expect(execute(putUser, { id, mode: "echo", body: {} }, { baseUrl, headers: [["authorization", "Bearer x"]] })).resolves.toMatchObject({ kind: "transport-failure", reason: "request-encoding" });
  });

  it("checks credential headers like any other argument instead of throwing", async () => {
    // a name that is no token or a value with a line break never reaches fetch, and the value never reaches the message
    const badName = await execute(getUser, { id, verbose: true }, { baseUrl, credentialProvider: () => [["bad name", "x"]] });
    expect(badName).toMatchObject({ kind: "transport-failure", reason: "request-encoding" });
    const badValue = await execute(getUser, { id, verbose: true }, { baseUrl, credentialProvider: () => [["authorization", "Bearer secret\r\nx: y"]] });
    expect(badValue).toMatchObject({ kind: "transport-failure", reason: "request-encoding" });
    expect(JSON.stringify(badValue)).not.toContain("secret");
    // a forwarded cookie is sent: the Nuxt module passes it during SSR (browsers drop forbidden headers themselves)
    await expect(execute(getUser, { id, verbose: true }, { baseUrl, credentialProvider: () => [["Cookie", "a=b"]] })).resolves.toMatchObject({ kind: "response" });
  });

  it("classifies problem+json, text and bodyless cases by status and media type", async () => {
    await expect(execute(getUser, { id, mode: "notfound" }, { baseUrl })).resolves.toMatchObject({ kind: "response", caseId: "users.get.not-found", data: { title: "nope", status: 404 } });
    await expect(execute(getUser, { id, mode: "gone" }, { baseUrl })).resolves.toMatchObject({ kind: "response", caseId: "users.get.gone", data: "it is gone é" });
    const noContent = await execute(getUser, { id, mode: "nocontent" }, { baseUrl });
    expect(noContent).toMatchObject({ kind: "response", caseId: "users.get.no-content", status: 204 });
    expect("data" in noContent).toBe(false);
  });

  it("distinguishes codec-failure from unexpected-response", async () => {
    await expect(execute(getUser, { id, mode: "badjson" }, { baseUrl })).resolves.toMatchObject({ kind: "codec-failure", caseId: "users.get.ok", code: "unexpected-end" });
    await expect(execute(getUser, { id, mode: "badshape" }, { baseUrl })).resolves.toMatchObject({ kind: "codec-failure", caseId: "users.get.ok", code: "type-mismatch", path: "/revision" });
    await expect(execute(getUser, { id, mode: "html" }, { baseUrl })).resolves.toMatchObject({ kind: "unexpected-response", reason: "undeclared-media" });
    await expect(execute(getUser, { id, mode: "teapot" }, { baseUrl })).resolves.toMatchObject({ kind: "unexpected-response", reason: "undeclared-status" });
    await expect(execute(getUser, { id, mode: "empty" }, { baseUrl })).resolves.toMatchObject({ kind: "codec-failure", code: "empty-body" });
    await expect(execute(getUser, { id, mode: "null" }, { baseUrl })).resolves.toMatchObject({ kind: "codec-failure", code: "null-not-allowed" });
    await expect(execute(getUser, { id, mode: "latin" }, { baseUrl })).resolves.toMatchObject({ kind: "codec-failure", code: "charset" });
    const failure = await execute(getUser, { id, mode: "badshape" }, { baseUrl });
    expect("rawBody" in failure).toBe(false);
    const retained = await execute(getUser, { id, mode: "badshape" }, { baseUrl, retainRawBody: true });
    expect((retained as { rawBody?: Uint8Array }).rawBody?.byteLength).toBeGreaterThan(0);
  });

  it("never follows redirects, enforces body limits and timeouts, and reports cancellation", async () => {
    await expect(execute(getUser, { id, mode: "redirect" }, { baseUrl })).resolves.toMatchObject({ kind: "transport-failure", reason: "redirect" });
    await expect(execute(getUser, { id, mode: "big" }, { baseUrl, limits: { maxBodyBytes: 1000 } })).resolves.toMatchObject({ kind: "limit-failure", limit: "maxBodyBytes" });
    await expect(execute(getUser, { id, mode: "slow" }, { baseUrl, limits: { timeoutMs: 50 } })).resolves.toMatchObject({ kind: "timeout" });
    const controller = new AbortController();
    const pending = execute(getUser, { id, mode: "slow" }, { baseUrl, signal: controller.signal });
    controller.abort();
    await expect(pending).resolves.toMatchObject({ kind: "cancelled" });
    // a signal aborted before the call: cancelled without a request
    await expect(execute(getUser, { id }, { baseUrl, signal: AbortSignal.abort() })).resolves.toMatchObject({ kind: "cancelled" });
    await expect(execute(getUser, { id }, { baseUrl: "http://127.0.0.1:9" })).resolves.toMatchObject({ kind: "transport-failure", reason: "network" });
  });

  it("refuses limits that would silently disable a check instead of applying them", async () => {
    // NaN compares false against every size: before limits were validated, maxBodyBytes: NaN switched the body limit off
    await expect(execute(getUser, { id, mode: "big" }, { baseUrl, limits: { maxBodyBytes: Number.NaN } })).rejects.toThrow(RangeError);
    await expect(execute(getUser, { id }, { baseUrl, limits: { maxDepth: 0 } })).rejects.toThrow(/maxDepth/);
    expect(() => resolveLimits({ timeoutMs: -1 })).toThrow(RangeError);
    expect(() => parseJson("[]", { limits: { maxTokens: 1.5 } })).toThrow(RangeError);
    // an undefined entry keeps its default
    expect(resolveLimits({ maxDepth: undefined as unknown as number, timeoutMs: 1000 })).toMatchObject({ maxDepth: 64, timeoutMs: 1000 });
  });

  it("detects a contract mismatch only when opted in", async () => {
    const opts = { baseUrl, expectedSemanticHash: "sha256:" + "a".repeat(64), semanticHashHeader: "x-secret" };
    await expect(execute(getUser, { id }, opts)).resolves.toMatchObject({ kind: "contract-mismatch", actualSemanticHash: "hide" });
    await expect(execute(getUser, { id }, { baseUrl })).resolves.toMatchObject({ kind: "response" });
  });

  it("prepareRequest produces the same bytes the client sends and rejects GET bodies", () => {
    const prepared = prepareRequest(putUser, { id, mode: "echo", body: { id: "550e8400-e29b-41d4-a716-446655440000", revision: 1n } }, { baseUrl });
    expect(prepared.bodyText).toBe('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":1}');
    expect(prepared.headers).toEqual([["content-type", "application/json"]]);
    expect(() => prepareRequest({ ...getUser, requestBody: putUser.requestBody! }, { id, mode: "echo", body: {} }, { baseUrl })).toThrow(CodecError);
    expect(() => prepareRequest(getUser, { id: "not a guid" }, { baseUrl })).toThrow(CodecError);
    expect(() => prepareRequest(getUser, {}, { baseUrl })).toThrow(/required/);
  });

  it("applies the depth and byte limits to request bodies, including the server profile's MaxDepth", async () => {
    type Node = { child?: Node | null };
    const node: ReturnType<typeof objectCodec<Node>> = objectCodec<Node>({
      id: "demo.Node.codec",
      typeId: "demo.Node",
      properties: [{ name: "child", codec: () => node, presence: "optional", nullable: true }],
      nameMatching: "ordinal-ignore-case",
      duplicates: "last-wins",
      readAdditional: "ignore",
      writeAdditional: "ignore",
      request: true,
      response: true,
    });
    const postNode: OperationDescriptor = { ...putUser, id: "nodes.post", requestBody: { mediaType: "application/json", codec: node, presence: "required", nullable: false, get: (a) => (a as { body: unknown }).body, maxDepth: 3 } };
    const nested = (n: number): Node => (n === 1 ? {} : { child: nested(n - 1) });
    expect(prepareRequest(postNode, { id, body: nested(3) }, { baseUrl }).bodyText).toBe('{"child":{"child":{}}}');
    expect(() => prepareRequest(postNode, { id, body: nested(4) }, { baseUrl })).toThrow(/nests 4 containers; the limit is 3/);
    expect(() => prepareRequest(postNode, { id, body: nested(3) }, { baseUrl, limits: { maxDepth: 2 } })).toThrow(/the limit is 2/);
    await expect(execute(postNode, { id, mode: "echo", body: nested(4) }, { baseUrl })).resolves.toMatchObject({ kind: "limit-failure", operationId: "nodes.post", limit: "maxDepth" });
    await expect(execute(postNode, { id, mode: "echo", body: nested(3) }, { baseUrl, limits: { maxBodyBytes: 10 } })).resolves.toMatchObject({ kind: "limit-failure", limit: "maxBodyBytes" });
  });
});

describe("request identity and envelopes", () => {
  it("computes rid:sha256 over the prefixed JCS record", async () => {
    const record = createRequestIdentityRecord({
      operationId: "users.get",
      method: "GET",
      encodedPath: "/users/550e8400-e29b-41d4-a716-446655440000",
      queryEntries: [],
      selectedHeaderEntries: [{ name: "Accept-Language", value: "ja" }],
      bodyKind: "none",
      bodyText: "ignored",
      semanticHash: "sha256:" + "a".repeat(64),
      scopeNonce: "0123456789abcdef0123456789abcdef",
    });
    const bytes = requestIdentityBytes(record);
    const text = new TextDecoder().decode(bytes);
    expect(text.startsWith("TISILIA-REQUEST/0.3\n{")).toBe(true);
    expect(text).toContain('"selectedHeaderEntries":[{"name":"accept-language","value":"ja"}]');
    expect(text).toContain('"bodyText":""');
    const rid = await computeRequestIdentity(record);
    expect(rid).toMatch(/^rid:sha256:[a-f0-9]{64}$/);
    expect(await computeRequestIdentity(record)).toBe(rid);
  });

  it("validates envelopes fail-closed", () => {
    const base = { format: "tisilia.hydration-envelope", version: "0.3", semanticHash: "sha256:" + "a".repeat(64), operationId: "users.get", requestIdentity: "rid:sha256:" + "b".repeat(64), scopeNonce: "0123456789abcdef" };
    expect(parseHydrationEnvelope({ ...base, kind: "bodyless", responseCaseId: "users.get.no-content", status: 204, headers: [] }).kind).toBe("bodyless");
    expect(() => parseHydrationEnvelope({ ...base, kind: "bodyless", responseCaseId: "x", status: 204, headers: [], bodyText: "null" })).toThrow(/unknown envelope field/);
    expect(() => parseHydrationEnvelope({ ...base, kind: "json", responseCaseId: "x", status: 200, mediaType: "application/json", bodyText: "null", headers: [] })).not.toThrow();
    expect(() => parseHydrationEnvelope({ ...base, kind: "failure", code: "server-only", safeMessageId: "hydration.server-only" })).not.toThrow();
    expect(() => parseHydrationEnvelope({ ...base, kind: "failure", code: "nope", safeMessageId: "x" })).toThrow(/failure code/);
    expect(() => parseHydrationEnvelope({ ...base, requestIdentity: "rid:md5:x", kind: "bodyless", responseCaseId: "x", status: 204, headers: [] })).toThrow(/request identity/);
  });

  it("turns a body that is not UTF-8 into a codec failure envelope instead of failing the SSR render", () => {
    const context = { semanticHash: "sha256:" + "a".repeat(64), operationId: getUser.id, requestIdentity: "rid:sha256:" + "b".repeat(64), scopeNonce: "0123456789abcdef" };
    const raw = { kind: "raw" as const, caseId: "users.get.ok", status: 200, mediaType: "application/json", headers: [], bodyKind: "json" as const, metadata: { status: 200, mediaType: "application/json", bodyBytes: 3, headers: [] } };
    expect(createHydrationEnvelope(getUser, { ...raw, body: Uint8Array.of(0x22, 0xff, 0x22) }, context)).toMatchObject({ kind: "failure", code: "codec", safeMessageId: "codec.invalid-utf8" });
    expect(createHydrationEnvelope(getUser, { ...raw, body: Uint8Array.of(0xef, 0xbb, 0xbf, 0x7b, 0x7d) }, context)).toMatchObject({ kind: "failure", code: "codec", safeMessageId: "codec.bom" });
    expect(createHydrationEnvelope(getUser, { ...raw, body: new TextEncoder().encode("{}") }, context)).toMatchObject({ kind: "json", bodyText: "{}" });
  });
});
