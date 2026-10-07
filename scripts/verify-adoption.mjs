import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { resolve, extname } from "node:path";
import { pathToFileURL } from "node:url";
import { build } from "esbuild";
import { chromium, firefox, webkit } from "@playwright/test";
import { runHttpCases } from "../tests/http/adoption-cases.mjs";

const root = resolve(import.meta.dirname, "..");
const out = resolve(root, "artifacts/adoption-compatibility");
const cli = resolve(root, "src/backend/Tisilia.Tool/bin/Debug/net10.0/Tisilia.Tool.dll");
const project = resolve(root, "tests/fixtures/AdoptionApi");
const fixture = resolve(project, "bin/Debug/net10.0/AdoptionApi.dll");
const api = "http://127.0.0.1:4178";
const baseUrl = api + "/base";
const processes = [];
const report = { node: process.version, os: process.platform, architecture: process.arch, runs: [], browsers: {}, diagnostics: [] };
await mkdir(out, { recursive: true });
function processRun(command, args, env = {}) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, { cwd: root, windowsHide: true, env: { ...process.env, MSBUILDDISABLENODEREUSE: "1", ...env }, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "", stderr = "";
    child.stdout.on("data", b => { stdout += b; }); child.stderr.on("data", b => { stderr += b; }); child.on("error", reject);
    child.on("close", code => resolveRun({ code, stdout, stderr }));
  });
}
async function command(args, expected = 0, env = {}) {
  const result = await processRun("dotnet", [cli, ...args], env);
  assert.equal(result.code, expected, `CLI ${args[0]}: ${result.stderr}\n${result.stdout}`); return result;
}
async function start(mode, port) {
  const child = spawn("dotnet", [fixture], { cwd: project, windowsHide: true, env: { ...process.env, ADOPTION_MODE: mode, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: `http://127.0.0.1:${port}`, TISILIA_EXPORT_OUTPUT: "", TISILIA_DOCTOR_OUTPUT: "" }, stdio: ["ignore", "pipe", "pipe"] });
  processes.push(child); let logs = ""; child.stdout.on("data", b => { logs += b; }); child.stderr.on("data", b => { logs += b; });
  for (let i = 0; i < 200; i++) {
    if (child.exitCode !== null) throw new Error(`fixture ${mode} exited: ${logs}`);
    try { if ((await fetch(`http://127.0.0.1:${port}/counts`)).ok) return child; } catch { }
    await new Promise(r => setTimeout(r, 50));
  }
  throw new Error(`fixture ${mode} did not start: ${logs}`);
}
let startupProbes = 0;
const server = createServer(async (req, res) => {
  if (req.url === "/startup") { startupProbes++; res.end("ok"); return; }
  if (req.url === "/") { res.setHeader("content-type", "text/html"); res.end("<!doctype html><title>Adoption tests</title><button id='save'>Save</button><button id='dispose'>Dispose</button>"); return; }
  try {
    const path = resolve(root, "." + new URL(req.url, "http://fixture.test").pathname);
    if (!path.startsWith(root + (process.platform === "win32" ? "\\" : "/"))) throw new Error("outside root");
    res.setHeader("content-type", extname(path) === ".json" ? "application/json" : "text/javascript"); res.end(await readFile(path));
  } catch { res.writeHead(404); res.end(); }
});
try {
  await new Promise((r, reject) => { server.once("error", reject); server.listen(4179, "127.0.0.1", r); });
  await command(["export", "--project", project, "--allow-execute-project", "--no-build", "--output", resolve(out, "adoption.contract.json")]);
  // init writes only when the config is absent; a deterministic config keeps repeated verification reviewable.
  await writeFile(resolve(out, "tisilia.config.json"), JSON.stringify({ format: "tisilia.config", version: "0.1", apiId: "adoption-api", contract: "adoption.contract.json", output: "generated", target: { typescriptMinimumMajor: 6, ecmaScript: "ES2022", moduleMode: "bundler" }, selection: "explicit", coveragePolicy: "development", modules: [], portableProjects: [], limits: { maxBodyBytes: 16777216, maxDepth: 64, maxTokens: 1000000, maxNumberCharacters: 4096, timeoutMs: 30000, maxDiagnosticBytes: 262144 }, nuxt: { enabled: false, hydration: "browser-safe-only", sharedCache: false } }, null, 2));
  await command(["generate", "--config", resolve(out, "tisilia.config.json")]);
  await command(["check", "--config", resolve(out, "tisilia.config.json")]);
  const bundle = async (entry, file) => build({ entryPoints: [resolve(root, entry)], outfile: resolve(out, file), bundle: true, format: "esm", platform: "browser", target: "es2022", logLevel: "silent" });
  await bundle("src/frontend/explorer/src/snippets.ts", "snippets.mjs");
  const { clientSnippet } = await import(pathToFileURL(resolve(out, "snippets.mjs")));
  const uploadSnippet = clientSnippet({ apiId: "adoption-api", operationId: "upload.stream", baseUrl, credentials: [], reveal: true, args: { body: new Uint8Array([0, 255, 195, 40]) } }).replace("./api/index.js", "./generated/index.js");
  await writeFile(resolve(out, "upload-snippet.ts"), uploadSnippet);
  const formSnippet = clientSnippet({ apiId: "adoption-api", operationId: "forms.files", baseUrl, credentials: [], reveal: true, args: { body: { files: [{ fileName: "sample.bin", bytes: new Uint8Array([0, 255]) }] } } }).replace("./api/index.js", "./generated/index.js");
  await writeFile(resolve(out, "form-snippet.ts"), formSnippet);
  const complexFormSnippet = clientSnippet({ document: JSON.parse(await readFile(resolve(out, "adoption.contract.json"), "utf8")), apiId: "adoption-api", operationId: "forms.order", baseUrl, credentials: [], reveal: true, args: { body: { Lines: [{ Id: 9007199254740993n, Details: { Label: "日本", Tags: ["one"], Notes: [{ Text: "note" }] } }] } } }).replace("./api/index.js", "./generated/index.js");
  await writeFile(resolve(out, "complex-form-snippet.ts"), complexFormSnippet);
  const dateSnippet = clientSnippet({ apiId: "adoption-api", operationId: "datetime.query", baseUrl, credentials: [], reveal: true, args: { at: { kind: "datetime-local-wire", ticks: 639263774451234567n, offsetMinutes: 540 } } }).replace("./api/index.js", "./generated/index.js");
  await writeFile(resolve(out, "datetime-snippet.ts"), dateSnippet);
  await writeFile(resolve(out, "tsconfig.json"), JSON.stringify({ compilerOptions: { target: "ES2022", module: "ESNext", moduleResolution: "Bundler", strict: true, exactOptionalPropertyTypes: true, noUncheckedIndexedAccess: true, skipLibCheck: true, noEmit: true }, include: ["generated/**/*.ts", "upload-snippet.ts", "form-snippet.ts", "complex-form-snippet.ts", "datetime-snippet.ts"] }));
  const checked = await processRun(process.execPath, [resolve(root, "node_modules/typescript/bin/tsc"), "-p", resolve(out, "tsconfig.json")]);
  assert.equal(checked.code, 0, checked.stdout + checked.stderr);
  await bundle("artifacts/adoption-compatibility/generated/index.ts", "generated.mjs");
  await bundle("src/frontend/explorer/src/download.ts", "download.mjs");
  const runtime = await import("../src/frontend/runtime/dist/index.js");
  const generated = await import(pathToFileURL(resolve(out, "generated.mjs")));
  const document = JSON.parse(await readFile(resolve(out, "adoption.contract.json"), "utf8"));
  const interpreted = runtime.createContractRegistry(document);
  const operations = new Map(Object.values(generated).filter(x => x && typeof x === "object" && "routePlan" in x).map(x => [x.id, x]));
  assert.equal(operations.size, 58);
  await start("valid", 4178); await start("before", 4180);
  for (const path of ["/v1/optional", "/v1/default", "/v1/stars/a//b/", "/v1/file", "/v1/async", "/v1/auth", "/v1/sse", "/v1/preserve"]) {
    const before = await fetch("http://127.0.0.1:4180/base" + path, { redirect: "manual" });
    const after = await fetch(baseUrl + path, { redirect: "manual" });
    assert.equal(after.status, before.status, path);
    for (const h of ["content-type", "content-disposition", "access-control-allow-origin"]) assert.equal(after.headers.get(h), before.headers.get(h), `${path} ${h}`);
    // Cookie redirects contain this fixture's origin. Only that known port differs; retain the entire destination path/query.
    if (before.headers.has("location")) {
      const a = new URL(after.headers.get("location")); const b = new URL(before.headers.get("location"));
      assert.equal(a.origin, api); assert.equal(b.origin, "http://127.0.0.1:4180");
      assert.equal(a.pathname + a.search + a.hash, b.pathname + b.search + b.hash);
    } else assert.equal(after.headers.get("location"), null);
    assert.deepEqual(new Uint8Array(await after.arrayBuffer()), new Uint8Array(await before.arrayBuffer()), path);
  }
  for (const origin of ["http://127.0.0.1:4180/base", baseUrl]) {
    const denied = await fetch(origin + "/v1/form", { method: "POST", body: new URLSearchParams({ value: "hello" }) }); assert.equal(denied.status, 400);
    const token = await fetch(origin + "/v1/csrf"); const cookie = token.headers.getSetCookie().map(c => c.split(";")[0]).join("; ");
    const form = new FormData(); form.append("value", "hello 日本");
    const accepted = await fetch(origin + "/v1/form", { method: "POST", headers: { cookie, RequestVerificationToken: await token.text() }, body: form });
    assert.equal(accepted.status, 200); assert.equal(await accepted.text(), "hello 日本");
    for (const browserOrigin of ["http://127.0.0.1:4179", "https://denied.invalid"]) {
      const cors = await fetch(origin + "/v1/file", { headers: { origin: browserOrigin } });
      assert.equal(cors.headers.get("access-control-allow-origin"), browserOrigin.includes("4179") ? browserOrigin : null);
    }
  }
  for (const [name, ops, client] of [["generated", operations, generated.createAdoptionApiClient({ baseUrl })], ["interpreter", interpreted.operations, undefined]]) {
    report.runs.push({ host: "Node", surface: name, cases: await runHttpCases(runtime, ops, client, baseUrl) });
  }
  for (const [mode, expected] of [["valid", 0], ["diagnostics", 3], ["excluded", 6], ["provider-failure", 6], ["startup-failure", 6], ["preserve", 0]]) {
    const output = resolve(out, `doctor-${mode}.json`);
    const result = await command(["doctor", "--project", project, "--no-build", "--allow-execute-project", "--format", "json", "--output", output], expected, { ADOPTION_MODE: mode });
    assert(!result.stdout.includes("TEST_SECRET") && !result.stderr.includes("TEST_SECRET"));
    const doctor = JSON.parse(result.stdout); assert.equal(doctor.format, "tisilia.doctor-report"); assert.equal(doctor.exitCode, expected);
    assert.deepEqual(doctor, JSON.parse(await readFile(output, "utf8")));
    if (mode === "diagnostics") assert(doctor.causes.some(c => c.operationIds.includes("bad.date.one") && c.operationIds.includes("bad.date.two")));
    report.diagnostics.push({ mode, code: expected, selected: doctor.selectedCount, analyzed: doctor.analyzedCount });
    if (mode === "diagnostics") {
      const human = await command(["doctor", "--project", project, "--no-build", "--allow-execute-project", "--output", resolve(out, "doctor-human.json")], expected, { ADOPTION_MODE: mode });
      assert(human.stdout.includes(`selected ${doctor.selectedCount}, analyzed ${doctor.analyzedCount}, unanalyzed ${doctor.unanalyzedCount}`));
      assert(human.stdout.includes("HTTP unobserved"));
      for (const cause of doctor.causes) { assert(human.stdout.includes(cause.message)); for (const id of cause.operationIds) assert(human.stdout.includes(id)); }
    }
  }
  const probe = { ADOPTION_MODE: "valid", ADOPTION_STARTUP_PROBE: "http://127.0.0.1:4179/startup" };
  await command(["doctor", "--project", project], 7, probe); assert.equal(startupProbes, 0);
  await command(["doctor", "--project", project, "--no-build", "--allow-execute-project", "--output", resolve(out, "doctor-probe.json")], 0, probe); assert.equal(startupProbes, 1);
  const preserved = resolve(out, "preserved.contract.json"); await writeFile(preserved, "known-good");
  await command(["export", "--project", project, "--no-build", "--allow-execute-project", "--output", preserved], 3, { ADOPTION_MODE: "diagnostics" }); assert.equal(await readFile(preserved, "utf8"), "known-good");
  for (const [name, engine] of Object.entries({ chromium, firefox, webkit })) {
    const browser = await engine.launch({ headless: true });
    try {
      report.browsers[name] = browser.version();
      const page = await browser.newPage(); await page.goto("http://127.0.0.1:4179");
      for (const surface of ["generated", "interpreter"]) {
        const cases = await page.evaluate(async ({ baseUrl, surface }) => {
          const runtime = await import("/src/frontend/runtime/dist/index.js");
          const generated = await import("/artifacts/adoption-compatibility/generated.mjs");
          const { runHttpCases } = await import("/tests/http/adoption-cases.mjs");
          const doc = await (await fetch("/artifacts/adoption-compatibility/adoption.contract.json")).json();
          const operations = surface === "interpreter" ? runtime.createContractRegistry(doc).operations : new Map(Object.values(generated).filter(x => x && typeof x === "object" && "routePlan" in x).map(x => [x.id, x]));
          return runHttpCases(runtime, operations, surface === "generated" ? generated.createAdoptionApiClient({ baseUrl }) : undefined, baseUrl, true);
        }, { baseUrl, surface });
        report.runs.push({ host: name, surface, cases });
      }
      await page.evaluate(async baseUrl => {
        const { createAdoptionApiClient } = await import("/artifacts/adoption-compatibility/generated.mjs");
        const { DownloadLease } = await import("/artifacts/adoption-compatibility/download.mjs");
        const file = (await createAdoptionApiClient({ baseUrl }).filePost()).data;
        const lease = new DownloadLease();
        window.__urls = []; const original = URL.createObjectURL.bind(URL); URL.createObjectURL = blob => { const url = original(blob); window.__urls.push(url); return url; };
        document.querySelector("#save").onclick = () => lease.save(file, true);
        document.querySelector("#dispose").onclick = () => lease.dispose();
      }, baseUrl);
      const postCount = (await (await fetch(api + "/counts")).json()).posts;
      for (let i = 0; i < 2; i++) {
        const downloaded = page.waitForEvent("download"); await page.click("#save"); const download = await downloaded;
        assert.equal(download.suggestedFilename(), "report.html"); assert.deepEqual([...await readFile(await download.path())], [0, 255, 1, 195, 40]);
      }
      assert.equal((await (await fetch(api + "/counts")).json()).posts, postCount);
      assert.equal(await page.evaluate(() => window.__urls.length), 1);
      await page.click("#dispose");
      assert.equal(await page.evaluate(async () => { try { await fetch(window.__urls[0]); return false; } catch { return true; } }), true);
      report.runs.push({ host: name, surface: "Explorer DownloadLease", cases: ["explicit download byte equality", "repeated saves reuse URL without POST", "dispose revokes URL"] });
      if (name === "chromium") {
        await page.addInitScript(() => {
          localStorage.setItem("tisilia-explorer.locale", "en");
          window.__created = []; window.__revoked = [];
          const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
          URL.createObjectURL = value => { const url = create(value); window.__created.push(url); return url; };
          URL.revokeObjectURL = url => { window.__revoked.push(url); revoke(url); };
        });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/file.post");
        const post = page.locator('[id="operation-file.post"]');
        await post.getByRole("button", { name: "Execute", exact: true }).click();
        const save = post.getByRole("button", { name: "Save received file", exact: true });
        await save.waitFor(); assert(await save.isDisabled());
        assert.equal(await post.locator("iframe,object,embed").count(), 0);
        assert(!(await post.locator(".result").textContent()).includes("report.html"));
        await post.locator(".result-body").getByRole("button", { name: "Show values", exact: true }).click();
        assert(await save.isEnabled());
        page.on("dialog", dialog => dialog.accept());
        const postsBeforeSave = (await (await fetch(api + "/counts")).json()).posts;
        const event = page.waitForEvent("download"); await save.click(); const download = await event;
        assert.equal(download.suggestedFilename(), "report.html"); assert.deepEqual([...await readFile(await download.path())], [0, 255, 1, 195, 40]);
        assert.equal((await (await fetch(api + "/counts")).json()).posts, postsBeforeSave);
        await post.locator(".result-body").getByRole("button", { name: "Hide values", exact: true }).click();
        assert(await save.isDisabled()); assert.equal(await page.evaluate(() => window.__revoked.length), 1);
        await post.locator(".result-body").getByRole("button", { name: "Show values", exact: true }).click();
        const again = page.waitForEvent("download"); await save.click(); await again;
        await post.getByRole("button", { name: "Execute", exact: true }).click();
        await page.waitForFunction(() => window.__revoked.length === 2);
        // Collapsing the operation destroys LiveResponse and its lease.
        const third = page.waitForEvent("download"); await save.click(); await third;
        await post.locator(".op-toggle").click(); await page.waitForFunction(() => window.__revoked.length === 3);
        await page.getByRole("button", { name: "Authorize", exact: true }).click();
        await page.getByRole("dialog").locator('input[type="password"]').fill("synthetic-test-token");
        await page.getByRole("dialog").getByRole("button", { name: "Authorize", exact: true }).click();
        await post.locator(".op-toggle").click();
        const last = page.waitForEvent("download"); await save.click(); await last;
        await page.getByRole("button", { name: "Authorized", exact: true }).click();
        await page.getByRole("dialog").getByRole("button", { name: "Sign out", exact: true }).click();
        await page.waitForFunction(() => window.__revoked.length === 4);
        assert.equal(await post.locator(".result").count(), 0);
        report.runs.push({ host: name, surface: "Explorer UI", cases: ["masked save disabled and filename hidden", "explicit reveal and confirmation", "HTML downloaded without inline rendering", "POST not repeated by save", "hide/replacement/unmount revoke", "sign out clears result and revokes URL"] });
        for (const [id, filename] of [["file.get", "report.pdf"], ["file.hidden", "report.svg"]]) {
          await page.goto(baseUrl + "/__tisilia/index.html#/op/" + id);
          // Hash navigation keeps the same SPA and disclosure state; reload to test the initial masked state.
          await page.reload();
          const operation = page.locator(`[id="operation-${id}"]`);
          await operation.getByRole("button", { name: "Execute", exact: true }).click();
          const save = operation.getByRole("button", { name: "Save received file", exact: true }); await save.waitFor();
          assert(await save.isDisabled());
          await operation.locator(".result-body").getByRole("button", { name: "Show values", exact: true }).click();
          assert.equal(await operation.locator("iframe,object,embed").count(), 0);
          const saving = page.waitForEvent("download"); await save.click(); const download = await saving;
          assert.equal(download.suggestedFilename(), filename); assert.deepEqual([...await readFile(await download.path())], [0, 255, 1, 195, 40]);
        }
        report.runs.push({ host: name, surface: "Explorer UI PDF/SVG", cases: ["PDF explicit download without preview", "SVG explicit download without preview"] });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/upload.stream");
        await page.reload();
        const upload = page.locator('[id="operation-upload.stream"]');
        await upload.getByLabel("Upload file", { exact: true }).setInputFiles({ name: "test.bin", mimeType: "application/octet-stream", buffer: Buffer.from([0, 255, 1, 195, 40]) });
        await upload.locator(".request-line.ok").waitFor();
        const sent = page.waitForRequest(request => request.method() === "POST" && request.url().endsWith("/v1/upload"));
        await upload.getByRole("button", { name: "Execute", exact: true }).click();
        const request = await sent;
        assert.deepEqual([...request.postDataBuffer()], [0, 255, 1, 195, 40]);
        assert.equal(request.headers()["content-type"], "application/octet-stream");
        await upload.getByRole("button", { name: "Save received file", exact: true }).waitFor();
        await upload.screenshot({ path: resolve(out, "explorer-upload.png") });
        await upload.getByRole("button", { name: "Reset", exact: true }).click();
        assert.equal(await upload.locator('input[type="file"]').evaluate(input => input.files.length), 0);
        await upload.getByRole("button", { name: "Execute", exact: true }).click();
        await upload.locator(".request-line.invalid").waitFor();
        report.runs.push({ host: name, surface: "Explorer upload", cases: ["file selection sends exact bytes and declared media", "reset clears file and bytes", "required upload cannot be omitted"] });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/forms.values"); await page.reload();
        const form = page.locator('[id="operation-forms.values"]');
        for (const [field, value] of [["value", "日本😀"], ["id", "9007199254740993"], ["amount", "1234567890.123456789"], ["tags", "1\n2"]]) {
          await form.locator(`[id="form-forms.values-${field}"]`).fill(value);
        }
        await form.locator(".request-line.ok").waitFor();
        const formResponse = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith("/v1/form-values"));
        await form.getByRole("button", { name: "Execute", exact: true }).click();
        const formReceived = await formResponse; assert.equal(formReceived.status(), 200);
        assert.equal(new URLSearchParams(formReceived.request().postData()).get("id"), "9007199254740993");
        await form.locator(".result").waitFor(); await form.screenshot({ path: resolve(out, "explorer-form.png") });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/forms.files"); await page.reload();
        const files = page.locator('[id="operation-forms.files"]');
        await files.locator('input[data-form-file]').setInputFiles([{ name: "first.bin", mimeType: "application/octet-stream", buffer: Buffer.from([0, 255]) }, { name: "empty.bin", mimeType: "application/octet-stream", buffer: Buffer.alloc(0) }]);
        await files.locator(".request-line.ok").waitFor();
        const fileResponse = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith("/v1/form-files"));
        await files.getByRole("button", { name: "Execute", exact: true }).click();
        const filesReceived = await fileResponse; assert.equal(filesReceived.status(), 200);
        assert.deepEqual(await filesReceived.json(), [{ name: "first.bin", bytes: "AP8=" }, { name: "empty.bin", bytes: "" }]);
        await files.getByRole("button", { name: "Reset", exact: true }).click();
        assert.equal(await files.locator('input[data-form-file]').evaluate(input => input.files.length), 0);
        report.runs.push({ host: name, surface: "Explorer forms", cases: ["exact scalar and repeated input", "multiple file selection including empty file", "reset clears form files"] });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/forms.nested"); await page.reload();
        const nestedForm = page.locator('[id="operation-forms.nested"]');
        for (const [field, value] of [["title_text", "日本😀"], ["Details.Id", "9007199254740993"], ["Details.Tags", "one\ntwo"], ["Mode", "9007199254740993"]]) {
          await nestedForm.locator(`[id="form-forms.nested-${field}"]`).fill(value);
        }
        await nestedForm.locator(".request-line.ok").waitFor();
        const nestedResponse = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith("/v1/form-nested"));
        await nestedForm.getByRole("button", { name: "Execute", exact: true }).click();
        const nestedReceived = await nestedResponse; assert.equal(nestedReceived.status(), 200);
        const nestedWire = await new Response(nestedReceived.request().postDataBuffer(), { headers: { "content-type": nestedReceived.request().headers()["content-type"] } }).formData();
        assert.equal(nestedWire.get("Details.Id"), "9007199254740993"); assert.equal(nestedWire.get("Details.Tags[1]"), "two"); assert.equal(nestedWire.get("Mode"), "Large");
        await nestedForm.locator(".result").waitFor(); await nestedForm.screenshot({ path: resolve(out, "explorer-nested-form.png") });
        report.runs.push({ host: name, surface: "Explorer nested forms", cases: ["nested names and indexed collection", "exact enum domain writes CLR name"] });
        await page.goto(baseUrl + "/__tisilia/index.html#/op/forms.order"); await page.reload();
        const order = page.locator('[id="operation-forms.order"]');
        await order.getByRole("button", { name: "Add item: Lines", exact: true }).click();
        await order.getByRole("button", { name: "Add item: Lines[0].Details.Notes", exact: true }).click();
        for (const [field, value] of [["Lines[0].Id", "9007199254740993"], ["Lines[0].Details.Label", "日本😀"], ["Lines[0].Details.Tags", "one\ntwo"], ["Lines[0].Details.Notes[0].Text", "note"]]) {
          await order.locator(`[id="form-forms.order-${field}"]`).fill(value);
        }
        await order.locator(".request-line.ok").waitFor();
        assert.equal(await order.locator('[id="form-forms.order-Lines[0].Details.Notes[0].Text"]').evaluate(input => input.getBoundingClientRect().width >= 160), true, "deep form inputs remain readable");
        const orderResponse = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith("/v1/form-order"));
        await order.getByRole("button", { name: "Execute", exact: true }).click();
        const orderReceived = await orderResponse; assert.equal(orderReceived.status(), 200);
        const orderWire = await new Response(orderReceived.request().postDataBuffer(), { headers: { "content-type": orderReceived.request().headers()["content-type"] } }).formData();
        assert.equal(orderWire.get("Lines[0].Id"), "9007199254740993"); assert.equal(orderWire.get("Lines[0].Details.Notes[0].Text"), "note");
        await order.locator(".result").waitFor(); await order.screenshot({ path: resolve(out, "explorer-complex-form.png") });
        await order.getByRole("button", { name: "Remove last item: Lines", exact: true }).click();
        await order.getByRole("button", { name: "Add item: Lines", exact: true }).click();
        assert.equal(await order.locator('[id="form-forms.order-Lines[0].Id"]').inputValue(), "");
        report.runs.push({ host: name, surface: "Explorer complex forms", cases: ["constructor models and nested object collections", "removing rows clears values"] });

      }
    } finally { await browser.close(); }
  }
  await writeFile(resolve(out, "http-browser-results.json"), JSON.stringify(report, null, 2) + "\n");
  console.log(JSON.stringify({ ...report, runs: report.runs.map(r => ({ ...r, cases: r.cases.length })) }, null, 2));
} finally {
  for (const child of processes) child.kill();
  server.closeAllConnections(); await new Promise(r => server.close(r));
}
