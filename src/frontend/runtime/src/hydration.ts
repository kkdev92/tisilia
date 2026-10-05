import { decodeUtf8Strict, JsonParseError } from "./json/parser.js";
import type { OperationDescriptor, RawOutcome, RawResponse } from "./http/client.js";
import type { RuntimeFailure } from "./http/result.js";
import type { BodylessEnvelope, EnvelopeFailureCode, FailureEnvelope, HydrationEnvelope, JsonEnvelope, TextEnvelope } from "./envelope.js";

/**
 * Turning a selected response case into a hydration envelope and back. Only cases declared
 * `browser-safe` carry a body; everything else becomes a `failure` envelope with a fixed code and a safe message id
 * (never a raw body, exception text or secret). Bodies stay as `bodyText`, so no number precision is lost in the
 * payload and the browser decodes with the same case codec as the server.
 */
export interface EnvelopeContext {
  readonly semanticHash: string;
  readonly operationId: string;
  readonly requestIdentity: string;
  readonly scopeNonce: string;
}

function failure(ctx: EnvelopeContext, code: EnvelopeFailureCode, safeMessageId: string): FailureEnvelope {
  return { format: "tisilia.hydration-envelope", version: "0.1", ...ctx, kind: "failure", code, safeMessageId };
}

/** Fixed code + safe id for a runtime failure (failure kinds → envelope failure codes). */
export function failureEnvelopeOf(ctx: EnvelopeContext, result: RuntimeFailure): FailureEnvelope {
  switch (result.kind) {
    case "transport-failure":
      return failure(ctx, "transport", "transport." + result.reason);
    case "codec-failure":
      return failure(ctx, "codec", "codec." + result.code);
    case "cancelled":
      return failure(ctx, "cancelled", "cancelled");
    case "timeout":
      return failure(ctx, "timeout", "timeout");
    case "limit-failure":
      return failure(ctx, "limit", "limit." + result.limit);
    case "contract-mismatch":
      return failure(ctx, "contract-mismatch", "contract-mismatch");
    case "unexpected-response":
      return failure(ctx, "unexpected-response", "unexpected-response." + result.reason);
  }
}

/**
 * Envelope for a raw outcome. A case whose hydration is `server-only` yields `failure/server-only` without a body
 * (the server may still consume the decoded value itself).
 */
export function createHydrationEnvelope(operation: OperationDescriptor, raw: RawOutcome, ctx: EnvelopeContext): HydrationEnvelope {
  if (raw.kind !== "raw") {
    return failureEnvelopeOf(ctx, raw);
  }
  const declared = operation.responses.find((c) => c.caseId === raw.caseId);
  if (declared === undefined) {
    return failure(ctx, "unexpected-response", "unexpected-response.undeclared-case");
  }
  if (declared.hydration !== "browser-safe" || declared.body.kind === "binary" || raw.bodyKind === "binary") {
    return failure(ctx, "server-only", "hydration.server-only");
  }
  const base = { format: "tisilia.hydration-envelope" as const, version: "0.1" as const, ...ctx, responseCaseId: raw.caseId, status: raw.status, headers: raw.headers.map(([name, value]) => ({ name, value })) };
  if (raw.bodyKind === "none") {
    return { ...base, kind: "bodyless" };
  }
  let bodyText: string;
  try {
    bodyText = decodeUtf8Strict(raw.body);
  } catch (error) {
    // a body that is not UTF-8 (or starts with a BOM) is the codec failure the browser would report for it, not an exception of the
    // SSR render (decodeResponse classifies the same bytes as codec-failure)
    if (error instanceof JsonParseError) {
      return failure(ctx, "codec", "codec." + error.code);
    }
    throw error;
  }
  return raw.bodyKind === "text"
    ? { ...base, kind: "text", mediaType: raw.mediaType ?? "text/plain", bodyText }
    : { ...base, kind: "json", mediaType: raw.mediaType ?? "application/json", bodyText };
}

export interface EnvelopeCheck {
  readonly semanticHash: string;
  readonly operationId: string;
  readonly scopeNonce: string;
}

export type EnvelopeMismatch = "semantic-hash" | "operation" | "scope" | "undeclared-case" | "server-only-case" | "status";

/**
 * Hydration-time checks: the envelope must belong to this contract, operation and scope, and name a
 * declared browser-safe case with its status. Returns the mismatch instead of throwing so that callers can refetch.
 */
export function checkHydrationEnvelope(operation: OperationDescriptor, envelope: HydrationEnvelope, expected: EnvelopeCheck): EnvelopeMismatch | undefined {
  if (envelope.semanticHash !== expected.semanticHash) {
    return "semantic-hash";
  }
  if (envelope.operationId !== expected.operationId || envelope.operationId !== operation.id) {
    return "operation";
  }
  if (envelope.scopeNonce !== expected.scopeNonce) {
    return "scope";
  }
  if (envelope.kind === "failure") {
    return undefined;
  }
  const declared = operation.responses.find((c) => c.caseId === envelope.responseCaseId);
  if (declared === undefined) {
    return "undeclared-case";
  }
  if (declared.hydration !== "browser-safe" || declared.body.kind === "binary") {
    return "server-only-case";
  }
  if (declared.status !== envelope.status) {
    return "status";
  }
  return undefined;
}

/** The raw case a body envelope transports; `decodeResponse` then applies the case codec exactly as on the server. */
export function rawFromEnvelope(envelope: JsonEnvelope | BodylessEnvelope | TextEnvelope): RawResponse {
  const headers = envelope.headers.map((h) => [h.name.toLowerCase(), h.value] as const);
  const body = envelope.kind === "bodyless" ? new Uint8Array() : new TextEncoder().encode(envelope.bodyText);
  const mediaType = envelope.kind === "bodyless" ? undefined : envelope.mediaType;
  return {
    kind: "raw",
    caseId: envelope.responseCaseId,
    status: envelope.status,
    mediaType,
    headers,
    bodyKind: envelope.kind === "bodyless" ? "none" : envelope.kind,
    body,
    metadata: { status: envelope.status, mediaType, bodyBytes: body.byteLength, headers },
  };
}
