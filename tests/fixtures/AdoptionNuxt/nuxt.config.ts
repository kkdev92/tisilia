import { defineNuxtConfig } from "nuxt/config";
export default defineNuxtConfig({
  modules: ["@kkdev92/tisilia-nuxt"],
  devtools: { enabled: false },
  compatibilityDate: "2026-10-05",
  tisilia: { forwardHeaders: [], sharedCache: false, hydration: "browser-safe-only" },
});
