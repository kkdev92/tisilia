import { useRequestHeaders, useRuntimeConfig } from "nuxt/app";
import type { ClientOptions, OperationDescriptor } from "@kkdev92/tisilia-runtime";
import { decodeIdentityKey, noForwardedHeaders, operationHeaders, partitionForwardedHeaders, type ForwardedHeaders } from "../core.js";

interface TisiliaRuntimeConfigView {
  public: { tisilia?: { baseUrl?: string; semanticHashHeader?: string } };
  tisilia?: { serverBaseUrl?: string; forwardHeaders?: string[]; identityKey?: string };
}

export interface TisiliaRequestScope {
  /** The one allowed API origin for this render (server: private serverBaseUrl or the public baseUrl; browser: public baseUrl). */
  readonly baseUrl: string;
  readonly server: boolean;
  /** Allowlisted incoming headers (SSR only), split into credentials and other headers. */
  readonly forwarded: ForwardedHeaders;
  readonly semanticHashHeader: string | undefined;
  /** Server-only HMAC key for request identities of requests that carry forwarded credentials. */
  readonly identityKey: Uint8Array | undefined;
}

let processIdentityKey: Uint8Array<ArrayBuffer> | undefined;

function serverIdentityKey(configured: string | undefined): Uint8Array {
  const decoded = decodeIdentityKey(configured ?? "");
  if (decoded !== undefined) {
    return decoded;
  }
  if (processIdentityKey === undefined) {
    const key = new Uint8Array(new ArrayBuffer(32));
    crypto.getRandomValues(key);
    processIdentityKey = key;
  }
  return processIdentityKey;
}

/** The request-scoped facts every Tisilia call in this render shares. Never cached across requests. */
export function useTisiliaRequestScope(): TisiliaRequestScope {
  const config = useRuntimeConfig() as unknown as TisiliaRuntimeConfigView;
  const server = import.meta.server;
  const publicBase = config.public.tisilia?.baseUrl ?? "";
  const baseUrl = server && (config.tisilia?.serverBaseUrl ?? "").length > 0 ? config.tisilia!.serverBaseUrl! : publicBase;
  if (baseUrl.length === 0) {
    throw new Error("@kkdev92/tisilia-nuxt: runtimeConfig.public.tisilia.baseUrl (NUXT_PUBLIC_TISILIA_BASE_URL) is not configured");
  }
  const allowlist = server ? (config.tisilia?.forwardHeaders ?? []) : [];
  const forwarded = server && allowlist.length > 0 ? partitionForwardedHeaders(useRequestHeaders(allowlist), allowlist) : noForwardedHeaders;
  const header = config.public.tisilia?.semanticHashHeader ?? "";
  return {
    baseUrl,
    server,
    forwarded,
    semanticHashHeader: header.length > 0 ? header : undefined,
    identityKey: server ? serverIdentityKey(config.tisilia?.identityKey) : undefined,
  };
}

/** Client options for one operation in the current request scope (per-operation header allowlist applied). */
export function clientOptionsFor(scope: TisiliaRequestScope, operation: OperationDescriptor | undefined, overrides: Partial<ClientOptions> = {}): ClientOptions {
  const headers = operation === undefined ? [] : operationHeaders(scope.forwarded, operation);
  const credentials = scope.forwarded.credentials;
  return {
    baseUrl: scope.baseUrl,
    credentials: "same-origin",
    ...(headers.length > 0 ? { headers } : {}),
    ...(credentials.length > 0 ? { credentialProvider: () => credentials } : {}),
    ...(scope.semanticHashHeader !== undefined ? { semanticHashHeader: scope.semanticHashHeader } : {}),
    ...overrides,
  };
}

/**
 * A request-scoped generated client for imperative calls (mutations, event handlers). The factory is the generated
 * `create<ApiId>Client`; the client is built per call site, never stored globally with a user's credentials.
 */
export function useTisiliaClient<TClient>(create: (options: ClientOptions) => TClient, overrides: Partial<ClientOptions> = {}): TClient {
  return create(clientOptionsFor(useTisiliaRequestScope(), undefined, overrides));
}
