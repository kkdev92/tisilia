import { canonicalize } from "./json/jcs.js";
import { fromNative, type JsonValue } from "./json/ast.js";
import { sha256 } from "./sha256.js";

/**
 * Request identity: JCS(record) prefixed with `TISILIA-REQUEST/0.3\n`, hashed with SHA-256, written as
 * `rid:sha256:<64hex>`. Credentials never enter the record; the HMAC variant (secrets on the server) is computed
 * server-side only and is distinguishable by its `rid:hmac-sha256:` prefix.
 */
export const requestHashPrefix = "TISILIA-REQUEST/0.3\n";

export interface RequestIdentityRecord {
  readonly format: "tisilia.request-identity-record";
  readonly version: "0.3";
  readonly operationId: string;
  readonly method: string;
  readonly encodedPath: string;
  readonly queryEntries: readonly { readonly name: string; readonly value: string }[];
  readonly selectedHeaderEntries: readonly { readonly name: string; readonly value: string }[];
  readonly bodyKind: "none" | "json";
  readonly bodyText: string;
  readonly semanticHash: string;
  readonly scopeNonce: string;
}

export function createRequestIdentityRecord(input: Omit<RequestIdentityRecord, "format" | "version">): RequestIdentityRecord {
  return {
    format: "tisilia.request-identity-record",
    version: "0.3",
    operationId: input.operationId,
    method: input.method,
    encodedPath: input.encodedPath,
    queryEntries: input.queryEntries.map((e) => ({ name: e.name, value: e.value })),
    selectedHeaderEntries: input.selectedHeaderEntries.map((e) => ({ name: e.name.toLowerCase(), value: e.value })),
    bodyKind: input.bodyKind,
    bodyText: input.bodyKind === "none" ? "" : input.bodyText,
    semanticHash: input.semanticHash,
    scopeNonce: input.scopeNonce,
  };
}

export function requestIdentityBytes(record: RequestIdentityRecord): Uint8Array {
  const json = canonicalize(fromNative(record) as JsonValue);
  return new TextEncoder().encode(requestHashPrefix + json);
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  // crypto.subtle is [SecureContext]: a page served over http from a LAN address (a phone testing the dev server) has none
  const digest = (globalThis.crypto as Crypto | undefined)?.subtle === undefined ? sha256(bytes) : new Uint8Array(await crypto.subtle.digest("SHA-256", bytes as BufferSource));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** Browser-side identity for requests that carry no secrets. */
export async function computeRequestIdentity(record: RequestIdentityRecord): Promise<string> {
  return "rid:sha256:" + (await sha256Hex(requestIdentityBytes(record)));
}

/** Server-side identity with a scope-private 256-bit key (Node/SSR only; the key never reaches the browser). */
export async function computeRequestIdentityHmac(record: RequestIdentityRecord, scopeKey: Uint8Array): Promise<string> {
  if (scopeKey.byteLength < 32) {
    throw new RangeError("scope key must be at least 256 bits");
  }
  const key = await crypto.subtle.importKey("raw", scopeKey as BufferSource, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, requestIdentityBytes(record) as BufferSource);
  return "rid:hmac-sha256:" + Array.from(new Uint8Array(mac), (b) => b.toString(16).padStart(2, "0")).join("");
}

/** 128-bit random opaque nonce as base64url. */
export function createScopeNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let binary = "";
  for (const b of bytes) {
    binary += String.fromCharCode(b);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

const identityPattern = /^rid:(?:sha256|hmac-sha256):[a-f0-9]{64}$/;

export function isRequestIdentity(value: string): boolean {
  return identityPattern.test(value);
}
