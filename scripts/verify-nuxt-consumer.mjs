import assert from "node:assert/strict";
import { cp, mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { spawn } from "node:child_process";
import { chromium } from "@playwright/test";
const consumer = resolve(process.argv[2]);
const apiUrl = process.argv[3];
const app = resolve(consumer, "nuxt");
await mkdir(app, { recursive: true });
await cp(resolve(import.meta.dirname, "../tests/fixtures/AdoptionNuxt"), app, { recursive: true });
// The app is outside the repository and resolves only its packed runtime/module and its generated client.
const env = { ...process.env, NUXT_PUBLIC_TISILIA_BASE_URL: apiUrl, NUXT_TISILIA_SERVER_BASE_URL: apiUrl, NITRO_HOST: "127.0.0.1", NITRO_PORT: "4181", NUXT_TELEMETRY_DISABLED: "1" };
const build = spawn(process.execPath, [resolve(consumer, "node_modules/nuxt/bin/nuxt.mjs"), "build"], { cwd: app, env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
let log = ""; build.stdout.on("data", b => { log += b; }); build.stderr.on("data", b => { log += b; });
const exit = await new Promise((r, reject) => { build.on("error", reject); build.on("close", r); });
await writeFile(resolve(consumer, "nuxt-build.log"), log);
assert.equal(exit, 0, log);
const server = spawn(process.execPath, [resolve(app, ".output/server/index.mjs")], { cwd: app, env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
server.stdout.on("data", b => { log += b; }); server.stderr.on("data", b => { log += b; });
let browser;
try {
  let ready = false;
  for (let i = 0; i < 180; i++) { try { const response = await fetch("http://127.0.0.1:4181/__ready"); if (response.status < 500) { ready = true; break; } } catch { } await new Promise(r => setTimeout(r, 250)); }
  assert(ready, log);
  browser = await chromium.launch({ headless: true }); const page = await browser.newPage();
  const errors = []; page.on("pageerror", e => errors.push(e.message));
  const count = async () => (await (await fetch(apiUrl + "/test-counts")).json()).downloads;
  const before = await count();
  const response = await page.goto("http://127.0.0.1:4181");
  assert.equal(response.headers()["x-ssr-file-size"], "5");
  const html = await response.text();
  assert(!html.includes("packed.pdf") && !html.includes("suggestedFileName") && !html.includes("0,255,1,195,40"), "binary leaked to payload");
  await page.waitForSelector('main[data-mounted="true"]');
  assert.equal(await page.locator("#binary").textContent(), "hydration-failure:server-only");
  assert.equal(await page.locator("#json").textContent(), "9007199254740993");
  assert.equal(await count(), before + 1, "hydration must not refetch a server-only envelope");
  await page.click("#refresh");
  await page.waitForTimeout(300);
  assert.equal(await count(), before + 2, "explicit refresh must fetch once");
  assert.equal(await page.locator("#binary").textContent(), "hydration-failure:server-only");
  await page.click("#download"); await page.waitForFunction(() => document.querySelector("#bytes")?.textContent === "5");
  assert.equal(await count(), before + 3); assert.deepEqual(errors, []);
  console.log(JSON.stringify({ consumer: "packed Nuxt", nuxt: "4.5.2", chromium: browser.version(), ssr: "binary serverResult, safe envelope", hydration: "no refetch", refresh: "one fetch", imperative: "5 exact bytes", json: "9007199254740993" }));
} finally {
  await browser?.close(); server.kill(); await writeFile(resolve(consumer, "nuxt-run.log"), log);
}
