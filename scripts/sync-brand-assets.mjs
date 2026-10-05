// Repository maintenance only; published Explorer builds use their own shipped copies.
import { copyFileSync, mkdirSync, readFileSync, readdirSync } from "node:fs";
import { createHash } from "node:crypto";
import assert from "node:assert/strict";

export const explorerBrandFiles = ["mascot-chibi-light.jpg", "mascot-chibi-dark.jpg"];
const source = new URL("../assets/brand/tisilia/illustrations/", import.meta.url);
const destination = new URL("../src/frontend/explorer/src/assets/brand/tisilia/", import.meta.url);
const check = process.argv.includes("--check");
// Validate the shared NuGet icon independently of any image library or ignored docs helper.
const icon = readFileSync(new URL("../assets/brand/tisilia/icons/nuget-icon.jpg", import.meta.url));
const manifest = JSON.parse(readFileSync(new URL("../assets/brand/tisilia/manifest.json", import.meta.url), "utf8"));
assert.equal(createHash("sha256").update(icon).digest("hex"), manifest.assets.find(asset => asset.path === "icons/nuget-icon.jpg").sha256);
assert.ok(icon.length < 1_000_000);
assert.equal(icon.readUInt16BE(0), 0xffd8, "NuGet icon must be JPEG");
let dimensions;
for (let offset = 2; offset < icon.length;) {
  assert.equal(icon[offset++], 0xff);
  const marker = icon[offset++];
  if (marker === 0xda || marker === 0xd9) break;
  const length = icon.readUInt16BE(offset);
  assert.ok(length >= 2);
  if ([0xc0, 0xc1, 0xc2].includes(marker)) {
    dimensions = [icon.readUInt16BE(offset + 5), icon.readUInt16BE(offset + 3)];
    break;
  }
  offset += length;
}
assert.deepEqual(dimensions, [128, 128], "NuGet icon dimensions");
if (!check) mkdirSync(destination, { recursive: true });
for (const name of explorerBrandFiles) {
  if (!check) copyFileSync(new URL(name, source), new URL(name, destination));
  const hash = (url) => createHash("sha256").update(readFileSync(url)).digest("hex");
  assert.equal(hash(new URL(name, destination)), hash(new URL(name, source)), name);
}
assert.deepEqual(readdirSync(destination).sort(), [...explorerBrandFiles].sort());
const policy = new URL("../assets/brand/tisilia/BRAND-ASSET-POLICY.md", import.meta.url);
const publicDirectory = new URL("../src/frontend/explorer/public/", import.meta.url);
const policyCopy = new URL("BRAND-ASSET-POLICY.md", publicDirectory);
if (!check) {
  mkdirSync(publicDirectory, { recursive: true });
  copyFileSync(policy, policyCopy);
}
assert.deepEqual(readFileSync(policyCopy), readFileSync(policy), "Explorer brand policy must match its canonical copy");
console.log(`PASS: ${explorerBrandFiles.length} Explorer JPEG copies match their canonical assets.`);
console.log("PASS: Explorer brand policy matches its canonical copy.");
