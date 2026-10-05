import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { createSSRApp } from "vue";
import { renderToString } from "vue/server-renderer";
import { afterEach, expect, it } from "vitest";
import EmptyMascot from "../src/components/EmptyMascot.vue";
import { prefs, type Theme } from "../src/prefs.js";

afterEach(() => { prefs.theme = "system"; });

it.each(["light", "dark"])("ships the unchanged %s JPEG in source and Vite output", theme => {
  const name = `mascot-chibi-${theme}`;
  const canonical = readFileSync(new URL(`../../../../assets/brand/tisilia/illustrations/${name}.jpg`, import.meta.url));
  const hash = (bytes: Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
  expect(hash(readFileSync(new URL(`../src/assets/brand/tisilia/${name}.jpg`, import.meta.url)))).toBe(hash(canonical));
  const dist = new URL("../dist/assets/", import.meta.url);
  const matches = readdirSync(dist).filter(file => file.startsWith(name + "-") && file.endsWith(".jpg"));
  expect(matches).toHaveLength(1);
  expect(hash(readFileSync(new URL(matches[0]!, dist)))).toBe(hash(canonical));
});

it.each<[Theme, string]>([["system", "(prefers-color-scheme: dark)"], ["light", "not all"], ["dark", "all"]])(
  "respects %s theme without overriding manual preferences with the OS theme", async (theme, media) => {
    prefs.theme = theme;
    const html = await renderToString(createSSRApp(EmptyMascot));
    expect(html).toContain(`media="${media}"`);
    expect(html).toContain("mascot-chibi-dark.jpg");
    expect(html).toContain("mascot-chibi-light.jpg");
    expect(html.match(/<img /g)).toHaveLength(1);
    expect(html).toContain('alt=""');
    expect(html).not.toMatch(/https?:\/\//);
  },
);
