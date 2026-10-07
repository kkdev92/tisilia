/**
 * Failure classification. Every failure carries only safe metadata by default; raw bodies are retained
 * only when the caller opts in (the Explorer applies its own redaction before showing anything).
 */
export interface ResponseMetadata {
  readonly status: number;
  readonly mediaType: string | undefined;
  readonly bodyBytes: number;
  readonly headers: readonly (readonly [string, string])[];
}

export interface UnexpectedResponse {
  readonly kind: "unexpected-response";
  readonly operationId: string;
  readonly metadata: ResponseMetadata;
  readonly reason: "undeclared-status" | "undeclared-media" | "opaque";
  readonly rawBody?: Uint8Array;
}

export interface CodecFailure {
  readonly kind: "codec-failure";
  readonly operationId: string;
  readonly caseId: string;
  readonly metadata: ResponseMetadata;
  /** Fixed safe code (parse error code or CodecError code). */
  readonly code: string;
  /** RFC 6901 pointer into the body. */
  readonly path: string;
  readonly message: string;
  readonly rawBody?: Uint8Array;
}

export interface TransportFailure {
  readonly kind: "transport-failure";
  readonly operationId: string;
  readonly reason: "network" | "redirect" | "read" | "request-encoding" | "url" | "cors";
  readonly message: string;
  readonly metadata?: ResponseMetadata;
}

export interface Cancelled {
  readonly kind: "cancelled";
  readonly operationId: string;
}

export interface Timeout {
  readonly kind: "timeout";
  readonly operationId: string;
  readonly timeoutMs: number;
}

export interface LimitFailure {
  readonly kind: "limit-failure";
  readonly operationId: string;
  readonly limit: "maxBodyBytes" | "maxDepth" | "maxTokens" | "maxNumberCharacters";
  readonly metadata?: ResponseMetadata;
}

export interface ContractMismatch {
  readonly kind: "contract-mismatch";
  readonly operationId: string;
  readonly expectedSemanticHash: string;
  readonly actualSemanticHash: string;
  readonly metadata: ResponseMetadata;
}

export type RuntimeFailure = UnexpectedResponse | CodecFailure | TransportFailure | Cancelled | Timeout | LimitFailure | ContractMismatch;

/** A decoded response case. Bodyless cases carry no `data` property at all. */
export interface ResponseCaseResult<TCaseId extends string, TStatus extends number, TData> {
  readonly kind: "response";
  readonly caseId: TCaseId;
  readonly status: TStatus;
  readonly headers: readonly (readonly [string, string])[];
  readonly data: TData;
}

export interface BodylessCaseResult<TCaseId extends string, TStatus extends number> {
  readonly kind: "response";
  readonly caseId: TCaseId;
  readonly status: TStatus;
  readonly headers: readonly (readonly [string, string])[];
}

export function isFailure(value: { kind: string }): value is RuntimeFailure {
  return value.kind !== "response" && value.kind !== "download" && value.kind !== "subscription";
}
