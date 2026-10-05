// Framework-free core of the Nuxt adapter: header forwarding, request identity, hydration checks.
// The Vue/Nuxt composables are thin wrappers so that this logic is unit-testable without a Nuxt runtime.
import type { ClientOptions, EnvelopeCheck, EnvelopeFailureCode, EnvelopeMismatch, HydrationEnvelope, OperationDescriptor, OperationResult, PreparedRequest, RawOutcome, RequestIdentityRecord } from "@kkdev92/tisilia-runtime";
import { checkHydrationEnvelope, computeRequestIdentity, computeRequestIdentityHmac, createRequestIdentityRecord, decodeResponse, EnvelopeError, parseHydrationEnvelope, rawFromEnvelope } from "@kkdev92/tisilia-runtime";

/** Headers that carry credentials: forwarded only through the credential provider, never through plain headers. */
export const credentialHeaderNames: readonly string[] = ["authorization", "cookie", "proxy-authorization"];

/** Hop-by-hop and connection-level headers are never copied to the API origin, even when allowlisted. */
export const neverForwardedHeaderNames: readonly string[] = ["connection", "keep-alive", "transfer-encoding", "upgrade", "expect", "host", "te", "trailer", "proxy-connection", "content-length", "content-type", "accept-encoding"];

export interface ForwardedHeaders {
  readonly credentials: readonly (readonly [string, string])[];
  readonly other: readonly (readonly [string, string])[];
}

export const noForwardedHeaders: ForwardedHeaders = Object.freeze({ credentials: [], other: [] });

/** Splits the incoming SSR request headers that the module allowlist admits into credentials and other headers. */
export function partitionForwardedHeaders(incoming: Readonly<Record<string, string | undefined>>, allowlist: readonly string[]): ForwardedHeaders {
  const allowed = new Set(allowlist.map((h) => h.toLowerCase()));
  const credentials: (readonly [string, string])[] = [];
  const other: (readonly [string, string])[] = [];
  for (const [rawName, value] of Object.entries(incoming)) {
    const name = rawName.toLowerCase();
    if (value === undefined || !allowed.has(name) || neverForwardedHeaderNames.includes(name)) {
      continue;
    }
    if (credentialHeaderNames.includes(name)) {
      credentials.push([name, value]);
    } else {
      other.push([name, value]);
    }
  }
  return { credentials, other };
}

/** Non-credential forwarded headers an operation accepts (its own allowlist decides). */
export function operationHeaders(forwarded: ForwardedHeaders, operation: OperationDescriptor): readonly (readonly [string, string])[] {
  const allow = new Set(operation.requestHeaderAllowlist.map((h) => h.toLowerCase()));
  return forwarded.other.filter(([name]) => allow.has(name));
}

/** The identity record of a prepared request; credentials are never part of it. */
export function identityRecordOf(operation: OperationDescriptor, prepared: PreparedRequest, semanticHash: string, scopeNonce: string): RequestIdentityRecord {
  return createRequestIdentityRecord({
    operationId: operation.id,
    method: prepared.method,
    encodedPath: prepared.encodedPath,
    queryEntries: prepared.queryEntries.map((q) => ({ name: q.name, value: q.value })),
    selectedHeaderEntries: prepared.headers.map(([name, value]) => ({ name, value })),
    bodyKind: prepared.bodyText === undefined ? "none" : "json",
    bodyText: prepared.bodyText ?? "",
    semanticHash,
    scopeNonce,
  });
}

/**
 * `rid:sha256:` for requests without secrets; when credentials were forwarded on the server the identity is keyed
 * with a scope-private key (`rid:hmac-sha256:`) so that low-entropy secrets cannot be guessed from it.
 */
export async function identityOf(record: RequestIdentityRecord, credentialsForwarded: boolean, serverKey: Uint8Array | undefined): Promise<string> {
  if (credentialsForwarded) {
    if (serverKey === undefined) {
      throw new Error("a server-side identity key is required when credentials are forwarded");
    }
    return computeRequestIdentityHmac(record, serverKey);
  }
  return computeRequestIdentity(record);
}

/** Stable synchronous key for useAsyncData: the canonical request without credentials (FNV-1a 64 of the record text). */
export function asyncDataKeyOf(record: RequestIdentityRecord): string {
  const text = JSON.stringify([record.method, record.encodedPath, record.queryEntries, record.selectedHeaderEntries, record.bodyKind, record.bodyText, record.semanticHash]);
  let hash = 0xcbf29ce484222325n;
  for (const byte of new TextEncoder().encode(text)) {
    hash ^= BigInt(byte);
    hash = (hash * 0x100000001b3n) & 0xffffffffffffffffn;
  }
  return "tisilia:" + record.operationId + ":" + hash.toString(16).padStart(16, "0");
}

export interface HydrationFailure {
  readonly kind: "hydration-failure";
  readonly operationId: string;
  readonly code: EnvelopeFailureCode;
  readonly safeMessageId: string;
}

export interface HydrationMismatch {
  readonly kind: "hydration-mismatch";
  readonly operationId: string;
  readonly mismatch: EnvelopeMismatch | "schema";
}

export type HydratedResult<T extends OperationResult = OperationResult> = T | HydrationFailure | HydrationMismatch;

/**
 * Browser-side hydration: schema, contract hash, operation, scope and case are checked before the
 * body text goes through the same case decoder the server used (SV47). Failure envelopes surface with their fixed code
 * only, so a server-only case never exposes a raw body (SV35).
 */
export function hydrate<T extends OperationResult = OperationResult>(operation: OperationDescriptor, value: unknown, expected: EnvelopeCheck, options: ClientOptions): HydratedResult<T> {
  let envelope: HydrationEnvelope;
  try {
    envelope = parseHydrationEnvelope(value);
  } catch (error) {
    if (error instanceof EnvelopeError) {
      return { kind: "hydration-mismatch", operationId: operation.id, mismatch: "schema" };
    }
    throw error;
  }
  const mismatch = checkHydrationEnvelope(operation, envelope, expected);
  if (mismatch !== undefined) {
    return { kind: "hydration-mismatch", operationId: operation.id, mismatch };
  }
  if (envelope.kind === "failure") {
    return { kind: "hydration-failure", operationId: operation.id, code: envelope.code, safeMessageId: envelope.safeMessageId };
  }
  return decodeResponse(operation, rawFromEnvelope(envelope), options) as T;
}

/**
 * The contract guard compares the hash a response names in `header`. On another origin the browser shows a response header only
 * when CORS lists it in Access-Control-Expose-Headers, so the guard finds nothing to compare and lets every response through
 * (the CORS setting is the application's). The message for a decoded response that lacks the header, else
 * undefined; the runtime cannot tell a hidden header from a missing one, so this is a development hint, not a failure.
 */
export function hiddenGuardHeader(raw: RawOutcome, header: string | undefined): string | undefined {
  if (header === undefined || raw.kind !== "raw" || raw.metadata.headers.some(([name]) => name === header.toLowerCase())) {
    return undefined;
  }
  return `[tisilia] the contract guard found no '${header}' header on ${raw.caseId}: when the API is on another origin, CORS must expose it `
    + `(Access-Control-Expose-Headers; ASP.NET Core: WithExposedHeaders("${header}")), otherwise the guard does not run in the browser`;
}

/** Whether a cached envelope may be reused in the current scope (scope changes discard everything; SV47 scope/contract/operation correspondence). */
export function envelopeMatchesScope(value: unknown, expected: EnvelopeCheck): value is HydrationEnvelope {
  try {
    const envelope = parseHydrationEnvelope(value);
    return envelope.semanticHash === expected.semanticHash && envelope.operationId === expected.operationId && envelope.scopeNonce === expected.scopeNonce;
  } catch {
    return false;
  }
}

/**
 * Decodes the configured key: base64 of ≥32 random bytes (recommended, `openssl rand -base64 32`), otherwise the
 * UTF-8 bytes of the text; at least 256 bits are required either way.
 */
export function decodeIdentityKey(configured: string): Uint8Array | undefined {
  if (configured.length === 0) {
    return undefined;
  }
  if (/^[A-Za-z0-9+/]+=*$/.test(configured) && configured.length % 4 === 0) {
    const binary = atob(configured);
    const decoded = Uint8Array.from(binary, (c) => c.charCodeAt(0));
    if (decoded.byteLength >= 32) {
      return decoded;
    }
  }
  const bytes = new TextEncoder().encode(configured);
  if (bytes.byteLength < 32) {
    throw new Error("runtimeConfig.tisilia.identityKey must hold at least 32 bytes");
  }
  return bytes;
}
