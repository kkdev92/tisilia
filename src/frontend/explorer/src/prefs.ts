// The page's display preferences — its language and its theme — and the only code of the Explorer that touches browser storage: two
// keys, each holding one value of a fixed list. Credentials, drafts and responses never come here: they live in memory and are gone
// on reload; the source guard in explorer.test.ts keeps every other module away from storage.
import { reactive } from "vue";

export type Locale = "en" | "ja";
export type Theme = "system" | "light" | "dark";

export const locales: readonly Locale[] = ["ja", "en"];
export const themes: readonly Theme[] = ["system", "light", "dark"];
export const storageKeys = { locale: "tisilia-explorer.locale", theme: "tisilia-explorer.theme" } as const;

/** The part of the Web Storage API the preferences use. */
export type PreferenceStorage = Pick<Storage, "getItem" | "setItem">;

/** The page's local storage, or undefined where the browser refuses it (SecurityError: an opaque origin, storage blocked by the user). */
function pageStorage(): PreferenceStorage | undefined {
  try {
    return globalThis.localStorage ?? undefined;
  } catch {
    return undefined;
  }
}

function read<T extends string>(storage: PreferenceStorage | undefined, key: string, allowed: readonly T[]): T | undefined {
  try {
    const value = storage?.getItem(key);
    return allowed.find((a) => a === value);
  } catch {
    return undefined;
  }
}

function write(storage: PreferenceStorage | undefined, key: string, value: string): void {
  try {
    storage?.setItem(key, value);
  } catch {
    // storage full or refused: the choice holds for this page
  }
}

/** The language the browser prefers among the Explorer's: the first entry of navigator.languages that is Japanese or English. */
export function preferredLocale(languages: readonly string[]): Locale {
  for (const tag of languages) {
    const primary = tag.trim().toLowerCase().split("-")[0];
    if (primary === "ja" || primary === "en") {
      return primary;
    }
  }
  return "en";
}

/** The choices stored by an earlier visit, else the browser's language and the system's theme. */
export function initialPreferences(storage: PreferenceStorage | undefined, languages: readonly string[]): { locale: Locale; theme: Theme } {
  return { locale: read(storage, storageKeys.locale, locales) ?? preferredLocale(languages), theme: read(storage, storageKeys.theme, themes) ?? "system" };
}

function browserLanguages(): readonly string[] {
  if (typeof navigator === "undefined") {
    return [];
  }
  return navigator.languages !== undefined && navigator.languages.length > 0 ? navigator.languages : navigator.language !== undefined ? [navigator.language] : [];
}

export const prefs = reactive(initialPreferences(pageStorage(), browserLanguages()));

export function setLocale(locale: Locale): void {
  prefs.locale = locale;
  write(pageStorage(), storageKeys.locale, locale);
}

export function setTheme(theme: Theme): void {
  prefs.theme = theme;
  write(pageStorage(), storageKeys.theme, theme);
}

/** The document follows the preferences: `lang` (WCAG 3.1.1: the page's language is programmatically determined) and the theme. */
export function applyPreferences(root: HTMLElement): void {
  root.lang = prefs.locale;
  root.dataset["theme"] = prefs.theme;
}
