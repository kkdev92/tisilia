// What ComplexJsonConverter wrote (complex-out.json from check-corpus.cs), decoded by the module, must be the original doubles bit for bit.
// Run: node tests/oracle/AdditionalTypes/complex-decode.mjs [complex-out.json]   (default: <tmp>/complex-out.json)
import { readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const m = await import(new URL("../../../src/backend/Tisilia.Generator/Additional/tisilia-additional.js", import.meta.url).href);
const runtime = await import(new URL("../../../src/frontend/runtime/dist/index.js", import.meta.url).href);
const rows = JSON.parse(readFileSync(process.argv[2] ?? join(tmpdir(), "complex-out.json"), "utf8"));
const view = new DataView(new ArrayBuffer(8));
const bits = (d) => {
  view.setFloat64(0, d);
  return view.getBigUint64(0).toString();
};
let bad = 0;
for (const [json, real, imaginary] of rows) {
  const value = m.complexResponseDecode.decodeResponse(runtime.parseJson(json), { path: "" });
  if (bits(value.real) !== real || bits(value.imaginary) !== imaginary) {
    if (bad++ < 10) console.log("MISMATCH", json, value);
  }
}
console.log(`${rows.length} server writes decoded, ${bad} mismatches; e.g. ${rows.slice(0, 3).map((r) => r[0]).join("  ")}`);
