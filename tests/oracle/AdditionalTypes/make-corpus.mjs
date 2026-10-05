// Corpora for IPNetwork, Index, Range, Complex and JsonScalar from the module's own verdicts: [kind, UTF-16 code units, module accepts, extra].
// Run: node tests/oracle/AdditionalTypes/make-corpus.mjs [out.json]   (default: <tmp>/tisilia-additional-corpus.json)
import { writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const m = await import(new URL("../../../src/backend/Tisilia.Generator/Additional/tisilia-additional.js", import.meta.url).href);
const ctx = { path: "" };
const accepts = (rule, v) => {
  try {
    rule(v, ctx);
    return true;
  } catch {
    return false;
  }
};
let seed = 20261003;
const rand = () => {
  seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const int = (n) => Math.floor(rand() * n);
const pick = (list) => list[int(list.length)];
const units = (s) => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i));
const corpus = [];

// ---------------------------------------------------------------- IPNetwork candidates
const v4 = () => [int(256), int(256), int(256), int(256)].join(".");
const hex = () => int(0x10000).toString(16);
const v6words = () => Array.from({ length: 8 }, () => (rand() < 0.5 ? 0 : int(0x10000)));
const networks = new Set(["10.0.0.0/8", "10.0.0.1/8", "0.0.0.0/0", "1.0.0.0/0", "::/0", "::%2/0", "fe80::%2/64", "fe80::1%2/64", "::ffff:10.0.0.0/104", "2001:db8::/32", "2001:DB8::/32", "2001:0db8::/32",
  "10.0.0.0/08", "10.0.0.0/33", "::/129", "10.0.0.0", "/8", "10.0.0.0/", "10.0.0.0/-1", " 10.0.0.0/8", "10.0.0.0/8 ", "1/8", "0x0a.0.0.0/8", "010.0.0.0/8", "::0/0", "0:0:0:0:0:0:0:0/0", "255.255.255.255/32", "255.255.255.254/31"]);
for (let i = 0; i < 4000; i++) {
  const prefix4 = int(34);
  networks.add(`${v4()}/${prefix4}`);
  const words = v6words();
  const prefix6 = int(130);
  networks.add(`${m.ipv6Text(words, rand() < 0.1 ? 1 + int(9) : 0)}/${prefix6}`);
  // canonical by construction: clear host bits
  const p = int(129);
  const masked = words.map((w, k) => {
    const lo = k * 16;
    if (p <= lo) return 0;
    if (p >= lo + 16) return w;
    return w & (0xffff << (16 - (p - lo))) & 0xffff;
  });
  networks.add(`${m.ipv6Text(masked)}/${p}`);
  const q = int(33);
  const a = [int(256), int(256), int(256), int(256)];
  const n = ((a[0] << 24) | (a[1] << 16) | (a[2] << 8) | a[3]) >>> 0;
  const mask = q === 0 ? 0 : (0xffffffff << (32 - q)) >>> 0;
  const c = (n & mask) >>> 0;
  networks.add(`${c >>> 24}.${(c >>> 16) & 255}.${(c >>> 8) & 255}.${c & 255}/${q}`);
}
for (const t of networks) corpus.push(["ipnetwork", units(t), accepts(m.ipNetworkDomainRule, t)]);

// ---------------------------------------------------------------- Index / Range candidates
const indexTexts = new Set(["0", "^0", "1", "^1", "2147483647", "^2147483647", "2147483648", "^2147483648", "-1", "^-1", "01", "^01", "", "^", " 1", "1 ", "+1", "^^1", "1.0", "１"]);
for (let i = 0; i < 1000; i++) indexTexts.add((rand() < 0.5 ? "^" : "") + String(rand() < 0.3 ? int(2147483647) : int(1000)));
for (const t of indexTexts) corpus.push(["index", units(t), accepts(m.indexDomainRule, t)]);
const rangeTexts = new Set(["1..^2", "0..^0", "..", "..5", "3..", "0..5", "^3..^1", "1..2..3", "1...2", "1 ..2", "2147483647..^2147483647", "2147483648..0", "-1..2", "1..^-2", "01..2"]);
const idx = [...indexTexts];
for (let i = 0; i < 1500; i++) rangeTexts.add(pick(idx) + ".." + pick(idx));
for (const t of rangeTexts) corpus.push(["range", units(t), accepts(m.rangeDomainRule, t)]);

// ---------------------------------------------------------------- Complex: the request wire the module writes for random doubles
const view = new DataView(new ArrayBuffer(8));
const randomDouble = () => {
  for (;;) {
    view.setUint32(0, int(0x100000000));
    view.setUint32(4, int(0x100000000));
    const d = view.getFloat64(0);
    if (Number.isFinite(d)) return d;
  }
};
const specials = [0, -0, 5e-324, -5e-324, 2.2250738585072014e-308, 1.7976931348623157e308, -1.7976931348623157e308, 0.1, 0.3, 1e21, 1e-7, 123456789012345680000, 9007199254740993, 1 / 3, Math.PI, -2.5];
const doubles = [...specials];
for (let i = 0; i < 6000; i++) doubles.push(rand() < 0.5 ? randomDouble() : (rand() - 0.5) * 10 ** (int(40) - 20));
for (let i = 0; i < doubles.length; i += 1) {
  const value = { real: doubles[i], imaginary: doubles[(i * 7 + 3) % doubles.length] };
  const wire = m.complexRequestEncode.encodeRequest(value, ctx);
  const text = "{" + wire.entries.map((e) => JSON.stringify(e.name) + ":" + e.value.text).join(",") + "}";
  corpus.push(["complex", units(text), true, [view.setFloat64(0, value.real) ?? view.getBigUint64(0).toString(), view.setFloat64(0, value.imaginary) ?? view.getBigUint64(0).toString()]]);
}

// ---------------------------------------------------------------- JsonScalar: JSON texts of scalars (and of things a JsonValue is not)
const numberLexeme = () => {
  const sign = rand() < 0.3 ? "-" : "";
  const intPart = rand() < 0.2 ? "0" : String(1 + int(9)) + Array.from({ length: int(25) }, () => int(10)).join("");
  const frac = rand() < 0.5 ? "." + Array.from({ length: 1 + int(20) }, () => int(10)).join("") : "";
  const exp = rand() < 0.4 ? pick(["e", "E"]) + pick(["", "+", "-"]) + String(int(500)) : "";
  return sign + intPart + frac + exp;
};
const scalars = new Set(["1.50", "-0", "0", "1E+400", "-1e-400", "123456789012345678901234567890", "true", "false", "\"x\"", "\"\"", "\"\\u00e9\"", "\"\\ud83d\\ude00\"", "\"\\u2028\"", "\"a\\\"b\\\\c\"", "{}", "[]", "null", "[1]", "{\"a\":1}"]);
for (let i = 0; i < 3000; i++) scalars.add(numberLexeme());
for (const t of scalars) {
  let accepted;
  try {
    const v = JSON.parse(t);
    accepted = v !== null && typeof v !== "object";
  } catch {
    accepted = false;
  }
  corpus.push(["jsonscalar", units(t), accepted]);
}

const out = process.argv[2] ?? join(tmpdir(), "tisilia-additional-corpus.json");
writeFileSync(out, JSON.stringify(corpus));
console.log(`wrote ${corpus.length} entries to ${out}`);
const counts = {};
for (const [k, , ok] of corpus) counts[k + (ok ? "+" : "-")] = (counts[k + (ok ? "+" : "-")] ?? 0) + 1;
console.log(counts);
