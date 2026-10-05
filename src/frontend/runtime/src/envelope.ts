import { isRequestIdentity } from "./identity.js";

/**
 * Hydration envelopes. JSON bodies stay as `bodyText` so that no number precision is lost in the Nuxt
 * payload; the envelope itself is plain JSON-safe control data. Server-only cases never produce a body envelope.
 */
interface EnvelopeBase {
  readonly format: "tisilia.hydration-envelope";
  readonly version: "0.3";
  readonly semanticHash: string;
  readonly operationId: string;
  readonly requestIdentity: string;
  readonly scopeNonce: string;
}

export interface JsonEnvelope extends EnvelopeBase {
  readonly kind: "json";
  readonly responseCaseId: string;
  readonly status: number;
  readonly mediaType: string;
  readonly bodyText: string;
  readonly headers: readonly { readonly name: string; readonly value: string }[];
}

export interface BodylessEnvelope extends EnvelopeBase {
  readonly kind: "bodyless";
  readonly responseCaseId: string;
  readonly status: number;
  readonly headers: readonly { readonly name: string; readonly value: string }[];
}

export interface TextEnvelope extends EnvelopeBase {
  readonly kind: "text";
  readonly responseCaseId: string;
  readonly status: number;
  readonly mediaType: string;
  readonly bodyText: string;
  readonly headers: readonly { readonly name: string; readonly value: string }[];
}

export type EnvelopeFailureCode = "transport" | "codec" | "cancelled" | "timeout" | "contract-mismatch" | "limit" | "unexpected-response" | "server-only";

export interface FailureEnvelope extends EnvelopeBase {
  readonly kind: "failure";
  readonly code: EnvelopeFailureCode;
  readonly safeMessageId: string;
}

export type HydrationEnvelope = JsonEnvelope | BodylessEnvelope | TextEnvelope | FailureEnvelope;

const digestPattern = /^sha256:[a-f0-9]{64}$/;
const idPattern = /^[A-Za-z][A-Za-z0-9_.:@/\-]{0,159}$/;

export class EnvelopeError extends Error {
  constructor(
    readonly path: string,
    message: string,
  ) {
    super(message);
    this.name = "EnvelopeError";
  }
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function requireString(o: Record<string, unknown>, key: string, pattern?: RegExp): string {
  const v = o[key];
  if (typeof v !== "string" || (pattern !== undefined && !pattern.test(v))) {
    throw new EnvelopeError("/" + key, `envelope.${key} is missing or invalid`);
  }
  return v;
}

function requireHeaders(o: Record<string, unknown>): { name: string; value: string }[] {
  const v = o["headers"];
  if (!Array.isArray(v)) {
    throw new EnvelopeError("/headers", "envelope.headers must be an array");
  }
  return v.map((h, i) => {
    if (!isRecord(h) || typeof h["name"] !== "string" || h["name"].length === 0 || typeof h["value"] !== "string" || Object.keys(h).length !== 2) {
      throw new EnvelopeError(`/headers/${i}`, "invalid header entry");
    }
    return { name: h["name"], value: h["value"] };
  });
}

function requireStatus(o: Record<string, unknown>): number {
  const v = o["status"];
  if (typeof v !== "number" || !Number.isInteger(v) || v < 200 || v > 599) {
    throw new EnvelopeError("/status", "envelope.status must be an integer 200..599");
  }
  return v;
}

function checkKeys(o: Record<string, unknown>, allowed: readonly string[]): void {
  for (const k of Object.keys(o)) {
    if (!allowed.includes(k)) {
      throw new EnvelopeError("/" + k, `unknown envelope field '${k}'`);
    }
  }
}

const baseKeys = ["format", "version", "semanticHash", "operationId", "requestIdentity", "scopeNonce", "kind"];

/** Structural validation mirroring hydration-envelope.schema.json (fail closed on unknown fields or variants). */
export function parseHydrationEnvelope(value: unknown): HydrationEnvelope {
  if (!isRecord(value)) {
    throw new EnvelopeError("", "envelope must be an object");
  }
  if (value["format"] !== "tisilia.hydration-envelope" || value["version"] !== "0.3") {
    throw new EnvelopeError("/format", "not a tisilia.hydration-envelope 0.3");
  }
  const base = {
    format: "tisilia.hydration-envelope" as const,
    version: "0.3" as const,
    semanticHash: requireString(value, "semanticHash", digestPattern),
    operationId: requireString(value, "operationId", idPattern),
    requestIdentity: requireString(value, "requestIdentity"),
    scopeNonce: requireString(value, "scopeNonce"),
  };
  if (!isRequestIdentity(base.requestIdentity)) {
    throw new EnvelopeError("/requestIdentity", "invalid request identity");
  }
  if (base.scopeNonce.length < 16) {
    throw new EnvelopeError("/scopeNonce", "scopeNonce too short");
  }
  switch (value["kind"]) {
    case "json":
      checkKeys(value, [...baseKeys, "responseCaseId", "status", "mediaType", "bodyText", "headers"]);
      return { ...base, kind: "json", responseCaseId: requireString(value, "responseCaseId", idPattern), status: requireStatus(value), mediaType: requireString(value, "mediaType"), bodyText: requireString(value, "bodyText"), headers: requireHeaders(value) };
    case "bodyless":
      checkKeys(value, [...baseKeys, "responseCaseId", "status", "headers"]);
      return { ...base, kind: "bodyless", responseCaseId: requireString(value, "responseCaseId", idPattern), status: requireStatus(value), headers: requireHeaders(value) };
    case "text":
      checkKeys(value, [...baseKeys, "responseCaseId", "status", "mediaType", "bodyText", "headers"]);
      return { ...base, kind: "text", responseCaseId: requireString(value, "responseCaseId", idPattern), status: requireStatus(value), mediaType: requireString(value, "mediaType"), bodyText: requireString(value, "bodyText"), headers: requireHeaders(value) };
    case "failure": {
      checkKeys(value, [...baseKeys, "code", "safeMessageId"]);
      const code = requireString(value, "code");
      const codes: EnvelopeFailureCode[] = ["transport", "codec", "cancelled", "timeout", "contract-mismatch", "limit", "unexpected-response", "server-only"];
      if (!codes.includes(code as EnvelopeFailureCode)) {
        throw new EnvelopeError("/code", "unknown failure code");
      }
      return { ...base, kind: "failure", code: code as EnvelopeFailureCode, safeMessageId: requireString(value, "safeMessageId", idPattern) };
    }
    default:
      throw new EnvelopeError("/kind", "unknown envelope kind");
  }
}
