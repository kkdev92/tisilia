// The expected values are fixture data, independent of the runtime's URL writer and codec implementation.
const known = [0, 255, 1, 195, 40];
function check(condition, message) { if (!condition) throw new Error(message); }
function equal(actual, expected, label) { check(JSON.stringify(actual) === JSON.stringify(expected), `${label}: ${JSON.stringify(actual)} != ${JSON.stringify(expected)}`); }
export async function runHttpCases(runtime, operations, client, baseUrl, browser = false) {
  const results = [];
  const lines = [
    { Id: 9007199254740993n, Details: { Label: "日本😀", Tags: ["one", "two"], Notes: [{ Text: "note 1" }, { Text: "note 2" }] } },
    { Id: -9007199254740993n, Details: { Label: "second", Tags: ["three"], Notes: [{ Text: "note 3" }] } },
  ];
  for (const [id, method, body] of [["forms.order", "formsOrder", { Lines: lines }], ["forms.lines", "formsLines", { values: lines }], ["forms.list", "formsList", { values: [9007199254740993n, -9007199254740993n] }]]) {
    const result = client ? await client[method]({ body }) : await runtime.execute(operations.get(id), { body }, { baseUrl });
    equal(result.kind, "response", id);
    if (id === "forms.list") { check(result.data[0] === 9007199254740993n && result.data[1] === -9007199254740993n, "root scalar list"); }
    else {
      const actual = id === "forms.order" ? result.data.lines : result.data;
      check(actual.length === 2 && actual[0].id === 9007199254740993n && actual[1].id === -9007199254740993n, "complex list precision");
      equal(actual[0].details, { label: "日本😀", tags: ["one", "two"], notes: [{ text: "note 1" }, { text: "note 2" }] }, "nested lists");
    }
    results.push(id);
  }
  const attachmentBody = { Items: [{ Label: "日本", File: { fileName: "first.bin", bytes: new Uint8Array([0, 255]) } }, { Label: "empty", File: { fileName: "empty.bin", bytes: new Uint8Array() } }] };
  const attached = client ? await client.formsAttachments({ body: attachmentBody }) : await runtime.execute(operations.get("forms.attachments"), { body: attachmentBody }, { baseUrl });
  equal(attached.kind, "response", "complex file list"); check(attached.data[0].length === 2n && attached.data[1].length === 0n, "file lengths");
  equal(attached.data.map(x => x.fileName), ["first.bin", "empty.bin"], "complex file names"); results.push("forms.attachments");
  const dateTexts = ["2026-09-30T06:04:05.1234567Z", "2026-09-30T06:04:05.1234567", "2026-09-30T15:04:05.1234567+09:00", "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999"];
  for (const text of dateTexts) {
    const value = runtime.parseDateTime(text);
    const body = { at: value, optional: null, items: [value], keys: new Map([[value, "exact"]]) };
    const result = client ? await client.datetimeEcho({ body }) : await runtime.execute(operations.get("datetime.echo"), { body }, { baseUrl });
    equal(result.kind, "response", "mixed DateTime " + text);
    const actual = result.data.at;
    equal(actual.kind, value.kind, "DateTime Kind");
    if (value.kind === "datetime-local-wire") {
      check(actual.ticks - BigInt(actual.offsetMinutes) * 600000000n === value.ticks - BigInt(value.offsetMinutes) * 600000000n, "local DateTime instant");
    } else check(actual.ticks === value.ticks, "DateTime exact ticks");
    check(result.data.items[0].ticks === actual.ticks && result.data.optional === null, "nested and nullable DateTime");
    check([...result.data.keys.keys()][0].ticks === actual.ticks, "DateTime key normalized by server zone");
    results.push("datetime.echo:" + text);
  }
  const dateValues = dateTexts.slice(0, 3).map(text => runtime.parseDateTime(text));
  const observed = client ? await client.datetimeObserve({ body: dateValues }) : await runtime.execute(operations.get("datetime.observe"), { body: dateValues }, { baseUrl });
  equal(observed.kind, "response", "DateTime observed");
  equal(observed.data.map(v => v.kind), ["Utc", "Unspecified", "Local"], "CLR Kinds");
  for (let i = 0; i < 3; i++) {
    const value = observed.data[i]; check(value.value.ticks === value.ticks, "independent CLR tick observation");
    if (i === 2) check(value.ticks - value.offsetTicks === dateValues[i].ticks - 540n * 600000000n, "independent CLR local offset");
  }
  results.push("datetime.observe");
  for (const id of ["datetime.query", "datetime.route", "datetime.header"]) {
    for (const text of [...dateTexts, "2026-09-30T06:04:05.1234567+00:00"]) {
      const at = runtime.parseDateTime(text);
      const method = id.replace(/\.([a-z])/g, (_, c) => c.toUpperCase());
      const result = client ? await client[method]({ at }) : await runtime.execute(operations.get(id), { at }, { baseUrl });
      equal(result.kind, "response", id);
      const expectedKind = at.kind === "datetime-unspecified" ? "Unspecified" : "Utc";
      equal(result.data.kind, expectedKind, id + " AdjustToUniversal");
      check(result.data.ticks === at.ticks - (at.kind === "datetime-local-wire" ? BigInt(at.offsetMinutes) * 600000000n : 0n), id + " exact bound ticks");
    }
    results.push(id);
  }
  for (const text of dateTexts) {
    const value = runtime.parseDateTime(text);
    const args = { path: value, query: value, header: value };
    const result = client ? await client.datetimeMvc(args) : await runtime.execute(operations.get("datetime.mvc"), args, { baseUrl });
    equal(result.kind, "response", "MVC DateTime");
    for (const observed of result.data) {
      equal(observed.kind, value.kind === "datetime-unspecified" ? "Unspecified" : "Utc", "MVC DateTime Kind");
      check(observed.ticks === value.ticks - (value.kind === "datetime-local-wire" ? BigInt(value.offsetMinutes) * 600000000n : 0n), "MVC DateTime ticks");
    }
  }
  results.push("datetime.mvc");
  for (const id of ["datetime.form", "datetime.form-model"]) {
    for (const text of dateTexts.slice(0, 3)) {
      const value = runtime.parseDateTime(text);
      const model = id === "datetime.form-model";
      const args = { body: model ? { At: value } : { at: value } };
      const method = model ? "datetimeFormModel" : "datetimeForm";
      const result = client ? await client[method](args) : await runtime.execute(operations.get(id), args, { baseUrl });
      equal(result.kind, "response", id);
      equal(result.data.kind, value.kind === "datetime-unspecified" ? "Unspecified" : model ? "Local" : "Utc", id + " binder Kind");
      const expectedTicks = value.ticks - (value.kind === "datetime-local-wire" ? BigInt(value.offsetMinutes) * 600000000n : 0n);
      check(result.data.ticks - (value.kind !== "datetime-unspecified" && model ? result.data.offsetTicks : 0n) === expectedTicks, id + " binder precision");
    }
    results.push(id);
  }
  const customJson = client ? await client.jsonCustom({ body: { largeNumber: 9007199254740993n } }) : await runtime.execute(operations.get("json.custom"), { body: { largeNumber: 9007199254740993n } }, { baseUrl });
  equal(customJson.kind, "response", "endpoint JSON options"); check(customJson.data.large_number === 9007199254740993n, "request and response JSON profiles are distinct"); results.push("json.custom");
  const mvcJson = client ? await client.mvcJson({ body: { largeNumber: 9007199254740993n } }) : await runtime.execute(operations.get("mvc.json"), { body: { largeNumber: 9007199254740993n } }, { baseUrl });
  equal(mvcJson.kind, "response", "MVC declared JSON"); check(mvcJson.data.large_number === 9007199254740993n, "MVC response options"); results.push("mvc.json");
  const nestedArgs = { body: { "title_text": "日本😀", "Details.Id": 9007199254740993n, "Details.Tags": ["one", "日本😀"], Mode: 9007199254740993n } };
  const nested = client ? await client.formsNested(nestedArgs) : await runtime.execute(operations.get("forms.nested"), nestedArgs, { baseUrl });
  equal(nested.kind, "response", "nested form"); equal(nested.data.title, "日本😀", "form DataMember name"); check(nested.data.id === 9007199254740993n && nested.data.mode === 9007199254740993n, "nested exact integers"); equal(nested.data.tags, ["one", "日本😀"], "indexed strings"); results.push("forms.nested");
  for (const id of ["forms.enum", "mvc.enum"]) {
    const method = id === "forms.enum" ? "formsEnum" : "mvcEnum";
    const args = { body: { mode: 9007199254740993n } };
    const result = client ? await client[method](args) : await runtime.execute(operations.get(id), args, { baseUrl });
    equal(result.kind, "response", id); check(result.data === 9007199254740993n, "enum long precision"); results.push(id);
  }
  equal((await runtime.execute(operations.get("mvc.enum"), { body: { mode: 2n } }, { baseUrl })).kind, "transport-failure", "MVC undefined enum refused");
  const formBody = { value: "日本😀\r\n+&=", id: 9007199254740993n, amount: { kind: "decimal", sign: 1, coefficient: 1234567890123456789n, scale: 9 }, tags: [1, 2] };
  for (const id of ["forms.values", "forms.multipart"]) {
    const method = id === "forms.values" ? "formsValues" : "formsMultipart";
    const result = client ? await client[method]({ body: formBody }) : await runtime.execute(operations.get(id), { body: formBody }, { baseUrl });
    equal(result.kind, "response", id);
    check(result.data.id === formBody.id, "form exact int64"); equal(result.data.value, formBody.value, "form Unicode and newlines"); equal(result.data.tags, [1, 2], "form repetitions");
    check(runtime.formatDecimal(result.data.amount) === "1234567890.123456789", "form exact decimal"); results.push(id);
  }
  for (const id of ["forms.file", "forms.files", "mvc.upload"]) {
    const file = { fileName: "日本😀.bin", bytes: new Uint8Array([99, ...known, 88]).subarray(1, 6) };
    const args = { body: id !== "forms.files" ? { file } : { files: [file, { fileName: "empty.bin", bytes: new Uint8Array() }] } };
    const method = id === "forms.file" ? "formsFile" : id === "mvc.upload" ? "mvcUpload" : "formsFiles";
    const result = client ? await client[method](args) : await runtime.execute(operations.get(id), args, { baseUrl });
    equal(result.kind, "response", id); const files = id !== "forms.files" ? [result.data] : result.data;
    equal(files[0].name, file.fileName, "multipart Unicode filename"); equal([...files[0].bytes], known, "multipart exact bytes");
    if (files.length > 1) { equal([...files[1].bytes], [], "multipart empty file"); }
    results.push(id);
  }
  const mvcForm = await runtime.execute(operations.get("mvc.form"), { body: { value: "日本😀" } }, { baseUrl });
  equal(mvcForm.kind, "response", "MVC form"); equal(mvcForm.data, "日本😀", "MVC form string");
  equal((await runtime.execute(operations.get("mvc.form"), { body: { value: " " } }, { baseUrl })).kind, "transport-failure", "MVC blank-to-null rejected"); results.push("mvc.form");
  const tokenResponse = await fetch(baseUrl + "/v1/csrf", { credentials: "include" });
  const token = await tokenResponse.text();
  const cookie = browser ? undefined : tokenResponse.headers.get("set-cookie")?.split(";")[0];
  const secureOptions = { baseUrl, credentials: "include", credentialProvider: () => [["RequestVerificationToken", token], ...(cookie ? [["cookie", cookie]] : [])] };
  const secured = await runtime.execute(operations.get("forms.secure"), { body: { value: "CSRF checked" } }, secureOptions);
  equal(secured.kind, "response", "form CSRF"); equal(secured.data, "CSRF checked", "form CSRF data"); results.push("forms.csrf");
  for (const id of ["events.json", "events.text"]) {
    const events = [];
    const name = id === "events.json" ? "eventsJson" : "eventsText";
    const result = client ? await client[name + "Subscribe"](e => { events.push(e); }) : await runtime.subscribe(operations.get(id), {}, { baseUrl }, e => { events.push(e); });
    equal(result.kind, "subscription", id);
    equal(result.eventsReceived, 2, id + " count");
    if (id === "events.json") { check(events[0].data === 9007199254740993n && events[1].data === 9223372036854775807n, "SSE exact integers"); }
    else { equal(events, [{ data: "日本😀\nsecond line", event: "update", id: "event-1", retryMilliseconds: "123" }, { data: "", event: "message", id: "event-1", retryMilliseconds: "123" }], "SSE text framing"); }
    results.push(id);
  }
  const abortEvents = new AbortController(); let received = 0;
  const stopped = await runtime.subscribe(operations.get("events.slow"), {}, { baseUrl, signal: abortEvents.signal }, e => { check(e.data === 9007199254740993n, "first live event"); received++; abortEvents.abort(); });
  equal(stopped.kind, "cancelled", "SSE live cancellation"); equal(received, 1, "SSE received before EOF"); results.push("events.cancel");
  const call = async (id, args = {}, overrides = {}) => {
    const operation = operations.get(id);
    check(operation, `missing operation ${id}`);
    const prepared = runtime.prepareRequest(operation, args, { baseUrl });
    equal(prepared.encodedPath, prepared.url.pathname, `${id} canonical path`);
    const name = id.replace(/[.-]([a-z])/g, (_, c) => c.toUpperCase());
    const result = client ? await client[name](args, overrides) : await runtime.execute(operation, args, { baseUrl, ...overrides });
    return result;
  };
  for (const id of ["upload.stream", "upload.pipe", "upload.optional"]) {
    for (const bytes of [new Uint8Array(), new Uint8Array(known), new Uint8Array([99, ...known, 88]).subarray(1, 6)]) {
      const result = await call(id, { body: bytes });
      equal(result.kind, "response", id);
      equal([...result.data.bytes], [...bytes], id + " exact bytes");
    }
    equal((await call(id, { body: new Uint8Array(known) }, { limits: { maxBodyBytes: 4 } })).kind, "limit-failure", id + " limit");
    results.push(id);
  }
  equal([...(await call("upload.optional")).data.bytes], [], "omitted optional raw body");
  for (const id of ["file.get", "file.empty", "file.gzip", "file.partial"]) {
    const chunks = [];
    const result = await runtime.download(operations.get(id), {}, { baseUrl }, async chunk => { chunks.push(...chunk); });
    equal(result.kind, "download", id + " streaming");
    equal(chunks, id === "file.gzip" ? Array.from({ length: 65536 }, (_, i) => i % 251) : id === "file.empty" ? [] : known, id + " streaming bytes");
    equal(result.file.bytesWritten, chunks.length, id + " size");
    results.push(id + ":streaming");
  }
  let errorWrites = 0;
  const jsonError = await runtime.download(operations.get("file.union"), { status: 400 }, { baseUrl }, () => { errorWrites++; });
  equal(jsonError.kind, "response", "streaming JSON error");
  check(jsonError.data.id === 9007199254740993n, "streaming error precision"); equal(errorWrites, 0, "no error bytes written");
  const routes = [
    ["route.required", { id: "ef5695ac-9d1a-4cdd-8fef-596aac4b8599" }, "ef5695ac-9d1a-4cdd-8fef-596aac4b8599", null],
    ["route.transform", { id: "MiXeD日本" }, "MiXeD日本", null],
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
