// Compile-time checks, run by `npm run typecheck` (no runtime part). An application's nuxt.config sets only the runtime config
// it needs — the module fills the rest — while the resolved config the app reads has every member. Interfaces broke the first
// half: nuxt.config's runtimeConfig is Overrideable<RuntimeConfig> (@nuxt/schema 4.5.2), which makes nested members optional only
// for object types that extend Record<string, unknown>, and an interface does not.
import type { NuxtConfig, PublicRuntimeConfig, RuntimeConfig } from "@nuxt/schema";
import "../src/module.js";

export const partialConfig: NuxtConfig = {
  runtimeConfig: {
    public: { tisilia: { baseUrl: "http://127.0.0.1:5000" } },
    tisilia: { serverBaseUrl: "http://api.internal" },
  },
};

export type ResolvedBaseUrl = PublicRuntimeConfig["tisilia"]["baseUrl"] extends string ? true : never;
export type ResolvedIdentityKey = RuntimeConfig["tisilia"]["identityKey"] extends string ? true : never;
export const resolved: [ResolvedBaseUrl, ResolvedIdentityKey] = [true, true];
