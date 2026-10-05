// Observational Node probe, not a universal process-memory guarantee. Run after npm run build.
import assert from "node:assert/strict";
import { writeFile, mkdir } from "node:fs/promises";
import { execute } from "../src/frontend/runtime/dist/index.js";

const op = { id: "memory.file", method: "GET", route: "/file", parameters: [], responses: [{ caseId: "file", status: 200, body: { kind: "binary", mediaType: "application/pdf" }, hydration: "server-only", exposedHeaders: [] }], requestExecution: "browser-allowed", requestHeaderAllowlist: [] };
const M = 16 * 1024 * 1024;
const measurements = [];
for (const size of [0, 1024 * 1024, 8 * 1024 * 1024, M, M + 1]) {
  globalThis.gc?.();
  const before = process.memoryUsage(); let peak = { ...before }; let emitted = 0, chunks = 0, cancelled = false;
  const sample = () => { const now = process.memoryUsage(); for (const key of Object.keys(now)) peak[key] = Math.max(peak[key], now[key]); };
  const stream = new ReadableStream({
    pull(controller) {
      if (emitted === size) { controller.close(); return; }
      const count = Math.min(65536, size - emitted); const chunk = new Uint8Array(count);
      for (let i = 0; i < count; i++) chunk[i] = (emitted + i) % 251;
      emitted += count; chunks++; controller.enqueue(chunk); sample();
    },
    cancel() { cancelled = true; },
  });
  const start = performance.now();
  const result = await execute(op, {}, { baseUrl: "https://memory.invalid", limits: { maxBodyBytes: M }, transport: { fetch: async () => new Response(stream, { headers: { "content-type": "application/pdf" } }) } });
  sample();
  assert.equal(result.kind, size <= M ? "response" : "limit-failure");
  if (result.kind === "response") {
    assert.equal(result.data.bytes.length, size);
    for (let i = 0; i < size; i++) assert.equal(result.data.bytes[i], i % 251);
  }
  // The stream may already be closed on the last oversized chunk, in which case cancel's underlying hook need not run.
  assert.equal(stream.locked, false);
  measurements.push({ size, maxBodyBytes: M, chunks, emitted, cancelled, result: result.kind, elapsedMs: Math.round((performance.now() - start) * 100) / 100, before, sampledPeak: peak, after: process.memoryUsage() });
}
const report = { node: process.version, platform: process.platform, architecture: process.arch, note: "Memory sampled at each pull and after execute. Sampling can miss peaks. RSS includes Node/Fetch; no fixed RSS guarantee. Transport retains at most M accepted bytes, may receive an additional oversized chunk, allocates one final joined buffer for multiple chunks, and does not join on overflow.", measurements };
await mkdir(new URL("../artifacts/adoption-compatibility/", import.meta.url), { recursive: true });
await writeFile(new URL("../artifacts/adoption-compatibility/memory-results.json", import.meta.url), JSON.stringify(report, null, 2) + "\n");
console.log(JSON.stringify(report, null, 2));
