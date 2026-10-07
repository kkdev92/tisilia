import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it } from "vitest";
import { credentialProblem, describeCredential } from "../src/auth.js";
import { formatNumber, messageCatalogs, t } from "../src/i18n.js";
import { initialPreferences, preferredLocale, prefs, storageKeys, type PreferenceStorage } from "../src/prefs.js";

afterEach(() => {
  prefs.locale = "en";
});

describe("display preferences: language and theme", () => {
  it("take the first of the browser's languages the Explorer has", () => {
    expect(preferredLocale(["ja-JP", "en-US"])).toBe("ja");
    expect(preferredLocale(["fr-FR", "ja"])).toBe("ja");
    expect(preferredLocale(["en-GB", "ja"])).toBe("en");
    expect(preferredLocale(["fr", "de"])).toBe("en");
    expect(preferredLocale([])).toBe("en");
  });

  it("keep a stored choice only when it is a known value, and do without a storage that throws", () => {
    const stored = (entries: Record<string, string>): PreferenceStorage => ({ getItem: (key) => entries[key] ?? null, setItem: () => undefined });
    expect(initialPreferences(stored({ [storageKeys.locale]: "en", [storageKeys.theme]: "dark" }), ["ja"])).toEqual({ locale: "en", theme: "dark" });
    expect(initialPreferences(stored({ [storageKeys.locale]: "fr", [storageKeys.theme]: "<script>" }), ["ja"])).toEqual({ locale: "ja", theme: "system" });
    // localStorage throws SecurityError where the user blocks storage (MDN Window.localStorage)
    const refusing: PreferenceStorage = {
      getItem: () => {
        throw new DOMException("denied", "SecurityError");
      },
      setItem: () => {
        throw new DOMException("full", "QuotaExceededError");
      },
    };
    expect(initialPreferences(refusing, ["en-US"])).toEqual({ locale: "en", theme: "system" });
    expect(initialPreferences(undefined, ["ja-JP"])).toEqual({ locale: "ja", theme: "system" });
  });
});

describe("the Explorer's words in Japanese and English", () => {
  it("has every message in both languages, none of them empty", () => {
    const keys = (o: object): string[] => Object.keys(o).sort();
    expect(keys(messageCatalogs.ja)).toEqual(keys(messageCatalogs.en));
    for (const [locale, catalog] of Object.entries(messageCatalogs)) {
      for (const [key, value] of Object.entries(catalog)) {
        const sample: unknown =
          key === "fieldError" ? catalog.fieldError({ message: "range at /x: too large", code: "range" }) : key === "hint" ? catalog.hint("e.g. 1") : typeof value === "function" ? (value as (...args: unknown[]) => unknown)(2, "x", "y") : value;
        const texts = typeof sample === "string" ? [sample] : Object.values(sample as object).map(String);
        expect(texts.join(""), `${locale}.${key}`).not.toBe("");
      }
    }
  });

  it("words input errors by their kind and keeps the codec's detail", () => {
    prefs.locale = "ja";
    expect(t().fieldError({ message: "required", code: "required" })).toBe("必須です — 値を入力してください");
    expect(t().fieldError({ message: "range at /body/count: value is outside the int32 range", code: "range" })).toBe("範囲外の値です（value is outside the int32 range）");
    expect(t().fieldError({ message: "invalid JSON: unexpected end of input", code: "invalid-json" })).toBe("JSON として読めません（unexpected end of input）");
    expect(t().fieldError({ message: "something the page does not know" })).toBe("something the page does not know");
    expect(t().fieldError({ message: "form collection requires 1–1024 items", code: "form-items" })).toBe("項目を 1〜1,024 件追加してください");
    prefs.locale = "en";
    expect(t().fieldError({ message: "required", code: "required" })).toBe("Required — enter a value");
    expect(t().fieldError({ message: "range at /body/count: value is outside the int32 range", code: "range" })).toBe("value is outside the int32 range");
    expect(t().fieldError({ message: "form collection requires 1–1024 items", code: "form-items" })).toBe("Add 1 to 1,024 items");
  });

  it("words the format hints of the inputs", () => {
    prefs.locale = "ja";
    const hint = t().hint;
    expect(hint("e.g. 550e8400-e29b-41d4-a716-446655440000")).toBe("例: 550e8400-e29b-41d4-a716-446655440000");
    expect(hint("yyyy-MM-dd, e.g. 2026-10-02")).toBe("yyyy-MM-dd、例: 2026-10-02");
    expect(hint("local time with offset, e.g. 2026-10-02T13:45:30+09:00")).toBe("オフセット付きのローカル時刻、例: 2026-10-02T13:45:30+09:00");
    expect(hint("e.g. 192.0.2.1 or 2001:db8::1")).toBe("例: 192.0.2.1 または 2001:db8::1");
    expect(hint("int32 · -2147483648 … 2147483647")).toBe("int32 · -2147483648 … 2147483647");
    expect(hint("one member")).toBe("メンバーを 1 つ");
    // every fixed hint forms.ts writes reads without English words
    const forms = readFileSync(fileURLToPath(new URL("../src/forms.ts", import.meta.url)), "utf8");
    const hints = [...forms.matchAll(/hint: "([^"]+)"/g), ...forms.matchAll(/hint: [^"]*\? "([^"]+)" : "([^"]+)"/g)].flatMap((m) => m.slice(1));
    expect(hints.length).toBeGreaterThan(20);
    for (const h of hints) {
      expect(hint(h), h).not.toMatch(/\be\.g\.|\b(or|one|of|any|size|local|time|with|offset|character|member|members|text|decimal|integer|number)\b/);
    }
    prefs.locale = "en";
    expect(t().hint("e.g. 1.5")).toBe("e.g. 1.5");
  });

  it("words credentials and what keeps them from a browser", () => {
    prefs.locale = "ja";
    expect(describeCredential({ kind: "bearer", token: "secret" })).toBe("Bearer トークン");
    expect(describeCredential({ kind: "api-key", header: "X-API-Key", value: "secret" })).toBe("API キー · X-API-Key");
    expect(credentialProblem({ kind: "header", name: "Cookie", value: "a=b" })).toMatch(/ブラウザーが管理します/);
    expect(credentialProblem({ kind: "basic", username: "a:b", password: "x" })).toMatch(/RFC 7617/);
  });

  it("groups numbers the way the language writes them", () => {
    prefs.locale = "ja";
    expect(formatNumber(1234567)).toBe("1,234,567");
    expect(t().operationCount(74)).toBe("操作 74 件");
    prefs.locale = "en";
    expect(t().operationCount(1)).toBe("1 operation");
    expect(t().operationCount(1234)).toBe("1,234 operations");
  });

  it("leaves no words of its own in the templates: they come from the catalog", () => {
    const dir = fileURLToPath(new URL("../src/", import.meta.url));
    const files = (readdirSync(dir, { recursive: true }) as string[]).filter((f) => f.endsWith(".vue"));
    // names and literals that read the same in both languages
    const allowed = new Set(["Bearer", "Basic", "Ctrl", "Enter", "JSON", "curl", "fetch", "null", "true", "false", "X-API-Key", "X-Tenant", "HTTP", "ms"]);
    for (const file of files) {
      const source = readFileSync(dir + file, "utf8");
      const template = source.slice(source.indexOf("<template>"), source.lastIndexOf("</template>"));
      // literal attributes a reader sees
      for (const m of template.matchAll(/\s(?:title|aria-label|placeholder|alt)="([^"]*)"/g)) {
        // A decorative image deliberately has no accessible words to translate.
        if (m[0].trim() === 'alt=""') continue;
        expect(allowed.has(m[1]!), `${file}: ${m[0].trim()}`).toBe(true);
      }
      // text between tags, interpolations left out
      // a tag ends at a ">" outside its quoted attribute values (v-if="count > 0")
      const text = template.replace(/<!--[\s\S]*?-->/g, "").replace(/\{\{[\s\S]*?\}\}/g, " ").replace(/<(?:[^>"']|"[^"]*"|'[^']*')*>/g, "\n");
      for (const word of text.match(/[A-Za-z][A-Za-z-]+/g) ?? []) {
        expect(allowed.has(word), `${file}: "${word}"`).toBe(true);
      }
    }
  });
});
