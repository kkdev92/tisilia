// Credentials for Explorer calls: Bearer tokens, Basic, API keys and other credential headers entered for this page
// only. They live in component memory — never in history, URLs, storage or the contract — reach requests through the runtime's
// credential provider (never through previews or request identity), and are gone on reload or "sign out". Cookies stay the
// browser's: no field sets a Cookie header, and cookie-authenticated APIs simply work for a page served by the application.
import { encodeBase64, isForbiddenRequestHeader } from "@kkdev92/tisilia-runtime";
import { t } from "./i18n.js";

export type Credential =
  | { readonly kind: "bearer"; readonly token: string }
  | { readonly kind: "basic"; readonly username: string; readonly password: string }
  | { readonly kind: "api-key"; readonly header: string; readonly value: string }
  | { readonly kind: "header"; readonly name: string; readonly value: string };

/** Which credential headers a call went with, without their values: what the code shown for that call names as placeholders. */
export type CredentialShape =
  | { readonly kind: "bearer" }
  | { readonly kind: "basic" }
  | { readonly kind: "api-key"; readonly header: string }
  | { readonly kind: "header"; readonly name: string };

/** What the host says the application authenticates with (from its registered authentication handlers). */
export interface AuthHint {
  readonly scheme: string;
  readonly kind: "bearer" | "cookie" | "basic" | "api-key" | "negotiate" | "certificate" | "other";
  readonly handler?: string;
}

const headerToken = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

/** Why a credential cannot be sent from a browser, or undefined when it can. */
export function credentialProblem(credential: Credential): string | undefined {
  const name = credentialHeaderName(credential);
  if (!headerToken.test(name)) {
    return t().problemInvalidName(name);
  }
  const lower = name.toLowerCase();
  if (lower === "cookie") {
    return t().problemCookie;
  }
  if (lower !== "authorization" && isForbiddenRequestHeader(lower)) {
    return t().problemForbidden(name);
  }
  const values = credential.kind === "basic" ? [credential.username, credential.password] : [credential.kind === "bearer" ? credential.token : credential.value];
  if (values.every((v) => v.length === 0)) {
    return t().problemEmpty;
  }
  if (credential.kind === "basic") {
    return credential.username.includes(":") ? t().problemBasicColon : controlCharacter(credential.username + credential.password) ? t().problemControl : undefined;
  }
  const value = credential.kind === "bearer" ? credential.token : credential.value;
  // header values are visible ASCII and spaces (RFC 9110 field-value; the runtime refuses the rest)
  return /^[\x20-\x7e\t]*$/.test(value) ? undefined : t().problemAscii;
}

function controlCharacter(s: string): boolean {
  return [...s].some((c) => c.charCodeAt(0) < 0x20 || c.charCodeAt(0) === 0x7f);
}

export function credentialHeaderName(credential: Credential): string {
  switch (credential.kind) {
    case "bearer":
    case "basic":
      return "Authorization";
    case "api-key":
      return credential.header.trim();
    case "header":
      return credential.name.trim();
  }
}

/** The shapes of the credentials a call sends (the ones `credentialHeaders` sends), values left out. */
export function credentialShapes(credentials: readonly Credential[]): CredentialShape[] {
  return credentials
    .filter((c) => credentialProblem(c) === undefined)
    .map((c): CredentialShape => (c.kind === "api-key" ? { kind: c.kind, header: c.header } : c.kind === "header" ? { kind: c.kind, name: c.name } : { kind: c.kind }));
}

/** The headers a set of credentials adds to each call; the later of two credentials for the same header wins. */
export function credentialHeaders(credentials: readonly Credential[]): (readonly [string, string])[] {
  const byName = new Map<string, readonly [string, string]>();
  for (const c of credentials) {
    if (credentialProblem(c) !== undefined) {
      continue;
    }
    const name = credentialHeaderName(c).toLowerCase();
    switch (c.kind) {
      case "bearer":
        byName.set(name, [name, "Bearer " + c.token.trim().replace(/^bearer\s+/i, "")]);
        break;
      case "basic":
        // RFC 7617: user-id ":" password, UTF-8, base64
        byName.set(name, [name, "Basic " + encodeBase64(new TextEncoder().encode(c.username + ":" + c.password))]);
        break;
      default:
        byName.set(name, [name, c.value.trim()]);
    }
  }
  return [...byName.values()];
}

/** A short description that shows no secret (`Bearer token`, `API key · X-API-Key`). */
export function describeCredential(credential: Credential): string {
  switch (credential.kind) {
    case "bearer":
      return t().bearerToken;
    case "basic":
      return `Basic · ${credential.username}`;
    case "api-key":
      return t().apiKeyNamed(credential.header);
    case "header":
      return t().headerNamed(credential.name);
  }
}

/** Host hints from the page (`<meta name="tisilia-explorer-auth">`, JSON); anything unreadable is ignored. */
export function parseAuthHints(content: string | undefined): AuthHint[] {
  if (content === undefined || content.length === 0) {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(content);
    if (!Array.isArray(parsed)) {
      return [];
    }
    const kinds = new Set(["bearer", "cookie", "basic", "api-key", "negotiate", "certificate", "other"]);
    return parsed
      .filter((h): h is { scheme: string; kind: string; handler?: string } => typeof h === "object" && h !== null && typeof (h as { scheme?: unknown }).scheme === "string" && kinds.has(String((h as { kind?: unknown }).kind)))
      .map((h) => ({ scheme: h.scheme, kind: h.kind as AuthHint["kind"], ...(typeof h.handler === "string" ? { handler: h.handler } : {}) }));
  } catch {
    return [];
  }
}

/**
 * A token in a response the user may want to authorize with (an Identity `/login` answer, an OAuth token response): the first string
 * among the usual names at the top level. The value is never shown; the button only names the field.
 */
export function tokenField(body: unknown): { readonly field: string; readonly token: string } | undefined {
  if (typeof body !== "object" || body === null || Array.isArray(body)) {
    return undefined;
  }
  const record = body as Record<string, unknown>;
  for (const field of ["accessToken", "access_token", "token", "jwt", "idToken", "id_token"]) {
    const value = record[field];
    if (typeof value === "string" && value.length >= 8 && /^[\x21-\x7e]+$/.test(value)) {
      return { field, token: value };
    }
  }
  return undefined;
}
