// The expected values are fixture data, independent of the runtime's URL writer and codec implementation.
const known = [0, 255, 1, 195, 40];
function check(condition, message) { if (!condition) throw new Error(message); }
function equal(actual, expected, label) { check(JSON.stringify(actual) === JSON.stringify(expected), `${label}: ${JSON.stringify(actual)} != ${JSON.stringify(expected)}`); }
export async function runHttpCases(runtime, operations, client, baseUrl, browser = false) {
  const results = [];
  const call = async (id, args = {}, overrides = {}) => {
    const operation = operations.get(id);
    check(operation, `missing operation ${id}`);
    const prepared = runtime.prepareRequest(operation, args, { baseUrl });
    equal(prepared.encodedPath, prepared.url.pathname, `${id} canonical path`);
    const name = id.replace(/[.-]([a-z])/g, (_, c) => c.toUpperCase());
    const result = client ? await client[name](args, overrides) : await runtime.execute(operation, args, { baseUrl, ...overrides });
    return result;
  };
  const routes = [
    ["route.required", { id: "ef5695ac-9d1a-4cdd-8fef-596aac4b8599" }, "ef5695ac-9d1a-4cdd-8fef-596aac4b8599", null],
    ["route.optional", {}, null, null], ["route.optional", { id: "abc" }, "abc", null],
    ["route.default", {}, "1", null], ["route.default", { page: 1 }, "1", null], ["route.default", { page: 2 }, "2", null],
    ["route.complex", { filename: "report" }, "report", null],
    ["route.complex", { filename: "report.v1", ext: "pdf" }, "report.v1", "pdf"],
    ["route.star", { path: "file" }, "file", null], ["route.stars", { path: "a/b" }, "a/b", null],
    ...["/a", "a//b/", "a b/日本😀/%2e/%252e/a+b", ".well-known", "https://example?x#fragment"].map(path => ["route.stars", { path }, path, null]),
    ["route.incoming", { id: "xyz" }, "xyz", null], ["route.name", { 名前: "日本😀" }, "日本😀", null], ["route.casing", { id: "case" }, "case", null],
  ];
  for (const [id, args, first, second] of routes) {
    const result = await call(id, args);
    equal(result.kind, "response", `${id} result`);
    equal(result.data, { first, second, pathBase: "/base" }, id);
    results.push(`${id}:${JSON.stringify(args)}`);
  }
  for (const [id, args] of [["route.star", { path: "a/b" }], ["route.stars", { path: "a/../b" }], ["route.optional", { id: null }], ["route.optional", { id: "" }], ["route.optional", { id: "\ud800" }], ["route.complex", { filename: "a", ext: "b.c" }]]) {
    let rejected = false;
    try { runtime.prepareRequest(operations.get(id), args, { baseUrl }); } catch { rejected = true; }
    check(rejected, `${id} unsafe input must fail before fetch`); results.push(`${id}:rejected`);
  }
  for (const id of ["file.get", "file.stream", "file.concrete", "file.json", "file.hidden", "mvc.file", "mvc.stream"]) {
    const result = await call(id); equal(result.kind, "response", id);
    equal([...result.data.bytes], known, id);
    if (id === "file.get") equal(result.data.suggestedFileName, "report.pdf", "CORS exposed filename");
    if (id === "file.hidden") equal(result.data.suggestedFileName, browser ? undefined : "report.svg", "CORS hidden filename");
    results.push(id);
  }
  equal([...(await call("file.empty")).data.bytes], [], "empty actual file"); results.push("file.empty");
  for (const status of [200, 400, 404, 412, 416]) {
    const result = await call("file.union", { status }); equal(result.kind, "response", `union ${status}`); equal(result.status, status, "status");
    if (status === 200) equal([...result.data.bytes], known, "union file");
    else check(result.data.id === 9007199254740993n, "JSON error integer precision");
    results.push(`file.union:${status}`);
  }
  for (const id of ["file.head", "file.not-modified", "file.none"]) { const result = await call(id); equal(result.kind, "response", id); check(!("data" in result), "bodyless has no file"); results.push(id); }
  const partial = await call("file.partial"); equal(partial.status, 206, "partial stays partial"); equal([...partial.data.bytes], known, "partial bytes"); results.push("file.partial");
  equal((await call("file.get", {}, { limits: { maxBodyBytes: 5 } })).kind, "response", "exact limit");
  equal((await call("file.get", {}, { limits: { maxBodyBytes: 4 } })).kind, "limit-failure", "limit + 1");
  const gzip = await call("file.gzip", {}, { limits: { maxBodyBytes: 65536 } }); equal(gzip.kind, "response", "gzip");
  equal([...gzip.data.bytes], Array.from({ length: 65536 }, (_, i) => i % 251), "decompressed known bytes");
  equal((await call("file.gzip", {}, { limits: { maxBodyBytes: 65535 } })).kind, "limit-failure", "gzip expanded limit"); results.push("bounded-gzip");
  // Both timers share the browser's scheduling delays. A wall-clock assertion also counts a paused or busy CI browser,
  // whereas this watchdog still fails if cleanup waits for the five-second response or never completes.
  let watchdog;
  try {
    const timed = await Promise.race([
      call("file.slow", {}, { limits: { timeoutMs: 150 }, credentialProvider: async () => { await new Promise(r => setTimeout(r, 50)); return []; } }),
      new Promise((_, reject) => { watchdog = setTimeout(() => reject(new Error("deadline cleanup blocked")), 1500); }),
    ]);
    equal(timed.kind, "timeout", "common provider/read timeout"); results.push("timeout");
  } finally { clearTimeout(watchdog); }
  const abort = new AbortController(); setTimeout(() => abort.abort(), 50);
  equal((await call("file.slow", {}, { signal: abort.signal })).kind, "cancelled", "cancel read"); results.push("cancel");
  const redirected = await runtime.send({ url: new URL(baseUrl + "/v1/redirect"), method: "GET", headers: [] });
  check(redirected.kind !== "ok", "redirect must not follow"); results.push("redirect");
  const bytes = await call("json.bytes"); equal([...bytes.data], known, "JSON base64");
  const asyncJson = await call("json.async"); check(asyncJson.data[0] === 9007199254740993n && asyncJson.data[1] === 9223372036854775807n, "finite async JSON precision"); results.push("json-regression");
  return results;
}
