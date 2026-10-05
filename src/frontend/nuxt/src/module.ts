import { addImports, addPlugin, createResolver, defineNuxtModule } from "@nuxt/kit";
import { defu } from "defu";

/**
 * Module options. Only `browser-safe-only` hydration and `sharedCache: false` exist; other
 * values are rejected at build time rather than silently downgraded.
 */
export interface ModuleOptions {
  /** Browser base origin of the API (public runtime config `tisilia.baseUrl`, env `NUXT_PUBLIC_TISILIA_BASE_URL`). */
  baseUrl?: string;
  /** Server-only base origin used during SSR (private runtime config `tisilia.serverBaseUrl`, env `NUXT_TISILIA_SERVER_BASE_URL`). */
  serverBaseUrl?: string;
  /** Incoming request headers forwarded to the API during SSR; credentials go through the credential provider. */
  forwardHeaders?: string[];
  /** Response header carrying the server's semantic hash for the opt-in contract guard. */
  semanticHashHeader?: string;
  hydration?: "browser-safe-only";
  sharedCache?: false;
}

// Type aliases, not interfaces: nuxt.config's `runtimeConfig` is typed `Overrideable<RuntimeConfig>` (@nuxt/schema 4.5.2), which
// makes the members of a nested object optional only when it `extends Record<string, unknown>` — an object type alias does, an
// interface (no implicit index signature) does not, and an application setting only `baseUrl` would fail to type-check.
// The module fills every member it does not get (defu below), so the resolved config always has them.
export type PublicTisiliaRuntimeConfig = {
  baseUrl: string;
  semanticHashHeader: string;
};

export type PrivateTisiliaRuntimeConfig = {
  serverBaseUrl: string;
  forwardHeaders: string[];
  /** Scope-private key (base64 or ≥32 UTF-8 bytes) for `rid:hmac-sha256:` identities; a per-process random key is used when empty. */
  identityKey: string;
};

export default defineNuxtModule<ModuleOptions>({
  meta: {
    name: "@kkdev92/tisilia-nuxt",
    configKey: "tisilia",
    compatibility: { nuxt: ">=4.5.0" },
  },
  defaults: {
    forwardHeaders: [],
    hydration: "browser-safe-only",
    sharedCache: false,
  },
  setup(options, nuxt) {
    if (options.hydration !== "browser-safe-only") {
      throw new Error("@kkdev92/tisilia-nuxt: the only hydration mode is 'browser-safe-only'");
    }
    if (options.sharedCache !== false) {
      throw new Error("@kkdev92/tisilia-nuxt: sharedCache must be false; shared caching of payloads is not a supported policy");
    }
    const resolver = createResolver(import.meta.url);
    const publicConfig: PublicTisiliaRuntimeConfig = { baseUrl: options.baseUrl ?? "", semanticHashHeader: options.semanticHashHeader ?? "" };
    const privateConfig: PrivateTisiliaRuntimeConfig = { serverBaseUrl: options.serverBaseUrl ?? "", forwardHeaders: (options.forwardHeaders ?? []).map((h) => h.toLowerCase()), identityKey: "" };
    nuxt.options.runtimeConfig.public["tisilia"] = defu(nuxt.options.runtimeConfig.public["tisilia"] as Partial<PublicTisiliaRuntimeConfig> | undefined, publicConfig);
    nuxt.options.runtimeConfig["tisilia"] = defu(nuxt.options.runtimeConfig["tisilia"] as Partial<PrivateTisiliaRuntimeConfig> | undefined, privateConfig);
    nuxt.options.build.transpile.push(resolver.resolve("./runtime"));
    addPlugin({ src: resolver.resolve("./runtime/plugin"), mode: "all" });
    addImports([
      { name: "useTisiliaScope", from: resolver.resolve("./runtime/composables/scope") },
      { name: "useTisiliaRequestScope", from: resolver.resolve("./runtime/composables/client") },
      { name: "useTisiliaClient", from: resolver.resolve("./runtime/composables/client") },
      { name: "useTisiliaOperation", from: resolver.resolve("./runtime/composables/operation") },
    ]);
  },
});

declare module "@nuxt/schema" {
  interface NuxtConfig {
    tisilia?: ModuleOptions;
  }
  interface NuxtOptions {
    tisilia?: ModuleOptions;
  }
  interface PublicRuntimeConfig {
    tisilia: PublicTisiliaRuntimeConfig;
  }
  interface RuntimeConfig {
    tisilia: PrivateTisiliaRuntimeConfig;
  }
}
