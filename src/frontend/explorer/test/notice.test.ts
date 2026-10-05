import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const read = (path: string): string => readFileSync(new URL(path, import.meta.url), "utf8");
const normalize = (text: string): string => text.replace(/\s+/g, " ").trim();
const sources = JSON.parse(read("../third-party-sources.json")) as {
  name: string; source: string; license: string; copyright: string; usage: string;
}[];
// Read the project's MIT terms independently so a truncated permission or warranty clause fails the checks too.
const license = read("../../../../LICENSE");
const mitTerms = normalize(license.slice(license.indexOf("Permission is hereby granted")));

// The build's list covers bundled packages; source metadata covers material copied or adapted directly into the sources.
// Both the NuGet NOTICE and the npm source package's NOTICE must cover the Explorer.
describe.each([
  ["root NOTICE", "../../../../NOTICE"],
  ["Explorer npm NOTICE", "../NOTICE"],
])("third-party notices in %s", (_name, path) => {
  const notice = read(path);

  it("names every bundled dependency with its expected license", () => {
    const list = fileURLToPath(new URL("../dist/third-party-licenses.json", import.meta.url));
    expect(existsSync(list), "the list is written by the build: run `npm run build` first").toBe(true);
    const bundled = JSON.parse(readFileSync(list, "utf8")) as {
      name: string; version: string; identifier?: string; text?: string;
    }[];

    expect(bundled.length).toBeGreaterThan(0);
    for (const { name, identifier, text } of bundled) {
      expect(notice, `${name} is bundled into the page but NOTICE does not name it`).toContain(name);
      expect(identifier, `${name} carries no license identifier`).toBe("MIT");
      expect(text, `${name} carries no license text`).toBeTruthy();
      expect(normalize(notice), `${name}'s license text is missing from NOTICE`).toContain(normalize(text ?? ""));
    }
  });

  it("covers every vendored source with copyright and full license terms", () => {
    expect(sources.length).toBeGreaterThan(0);
    for (const source of sources) {
      expect(source.name.trim()).not.toBe("");
      expect(source.copyright.trim()).not.toBe("");
      expect(source.source).toMatch(/^https:\/\//);
      expect(source.usage.trim()).not.toBe("");
      expect(source.license, `${source.name} has an unexpected license`).toBe("MIT");
      // Check the source's own section: another material's MIT text must not hide an incomplete notice.
      const section = notice.split(/-{80}\r?\n(?=\S)/).find(part => part.startsWith(source.name));
      expect(section, `${source.name} has no NOTICE section`).toBeDefined();
      expect(section).toContain(source.copyright);
      expect(section).toContain("The MIT License (MIT)");
      expect(normalize(section ?? "")).toContain(mitTerms);
    }
  });
});

describe("Explorer source distribution", () => {
  it("ships the canonical brand policy with the source artwork and the built page", () => {
    const policy = read("../../../../assets/brand/tisilia/BRAND-ASSET-POLICY.md");
    expect(read("../public/BRAND-ASSET-POLICY.md")).toBe(policy);
    expect(read("../dist/BRAND-ASSET-POLICY.md")).toBe(policy);
    const pkg = JSON.parse(read("../package.json")) as { files: string[]; license: string };
    expect(pkg.files).toContain("public");
    expect(pkg.license).toBe("MIT");
  });

  it("includes notices and vendored source metadata in the npm package", () => {
    const pkg = JSON.parse(read("../package.json")) as { files: string[] };
    expect(pkg.files).toEqual(expect.arrayContaining(["NOTICE", "third-party-sources.json"]));
    expect(read("../NOTICE")).toContain("Vue");
    expect(sources.map(source => source.name)).toContain("Feather Icons");
  });
});
