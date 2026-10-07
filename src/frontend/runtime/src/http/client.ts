import { createCodecContext, type Codec, type CodecContext } from "../codec/abi.js";
import { asCodecError, CodecError } from "../codec/errors.js";
import type { CodecRef } from "../codec/structural.js";
import { decodeUtf8Strict, JsonParseError, parseJsonBytes } from "../json/parser.js";
import type { JsonValue } from "../json/ast.js";
import { writeJsonBytes } from "../json/writer.js";
import { resolveLimits, type Limits } from "../json/limits.js";
import type { Binder } from "./binders.js";
import { isUtf8OrUnspecified, parseMediaType } from "./mediaType.js";
import type { BodylessCaseResult, ResponseCaseResult, ResponseMetadata, RuntimeFailure } from "./result.js";
import { send, type TransportOptions } from "./transport.js";
import { buildUrl, encodePathSegment, encodeQueryComponent, isCredentialHeader, validateHeaderName, validateHeaderToken, validateHeaderValue, type QueryEntry } from "./url.js";
import { buildPlannedUrl, type RoutePlan } from "./routes.js";
import { suggestedFileName } from "./filename.js";
import { BudgetEnded, ExecutionBudget } from "./budget.js";
import { SseParser, type ServerSentEvent } from "./sse.js";
import { encodeForm, FormLimitError, type FormFieldDescriptor } from "./forms.js";

export type HttpMethod = "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE" | "OPTIONS";

export interface ParameterDescriptor {
  readonly id?: string;
  readonly name: string;
  readonly location: "path" | "query" | "header";
  readonly binder: Binder<unknown>;
  readonly presence: "required" | "optional";
  readonly nullable: boolean;
  /** Reads the argument from the typed args object. */
  readonly get: (args: unknown) => unknown;
}

export type RequestBodyDescriptor = JsonRequestBodyDescriptor | BinaryRequestBodyDescriptor | FormRequestBodyDescriptor;

export interface FormRequestBodyDescriptor {
  readonly kind: "form";
  readonly mediaType: string;
  readonly presence: "required" | "optional";
  readonly fields: readonly FormFieldDescriptor[];
  readonly get: (args: unknown) => unknown;
}

export interface BinaryRequestBodyDescriptor {
  readonly kind: "binary";
  readonly mediaType: string;
  readonly presence: "required" | "optional";
  readonly get: (args: unknown) => unknown;
}

export interface JsonRequestBodyDescriptor {
  readonly kind?: "json";
  readonly mediaType: string;
  readonly codec: CodecRef<unknown>;
  readonly presence: "required" | "optional";
  readonly nullable: boolean;
  readonly get: (args: unknown) => unknown;
  /** The server profile's effective JSON depth (System.Text.Json MaxDepth): a deeper body is a limit failure before it is sent. */
  readonly maxDepth?: number;
}

export type ResponseBodyDescriptor =
  | { readonly kind: "sse"; readonly mediaType: string; readonly dataFormat: "text" | "json"; readonly codec: CodecRef<unknown>; readonly nullable: boolean; readonly profileId?: string }
  | { readonly kind: "none" }
  | { readonly kind: "json"; readonly mediaType: string; readonly codec: CodecRef<unknown>; readonly nullable: boolean; readonly profileId?: string }
  | { readonly kind: "text"; readonly mediaType: string }
  | { readonly kind: "binary"; readonly mediaType: string };

/** Complete content-decoded bytes. Uint8Array elements remain mutable. */
export interface BufferedFile {
  readonly bytes: Uint8Array;
  readonly contentType: string;
  readonly suggestedFileName?: string;
}

/** A completed finite download. The caller owns the sink and commits or discards it after the outcome. */
export interface StreamedFile {
  readonly bytesWritten: number;
  readonly contentType: string;
  readonly suggestedFileName?: string;
}

/** Writes are awaited in order. Observe signal to stop an in-flight write on cancellation or timeout. */
export type DownloadSink = (chunk: Uint8Array, signal: AbortSignal) => void | Promise<void>;
export type DownloadResult = { readonly kind: "download"; readonly caseId: string; readonly status: number; readonly headers: readonly (readonly [string, string])[]; readonly file: StreamedFile } | OperationResult;
export type EventSink<T = unknown> = (event: ServerSentEvent<T>, signal: AbortSignal) => void | Promise<void>;
export type SubscriptionResult = { readonly kind: "subscription"; readonly caseId: string; readonly status: number; readonly headers: readonly (readonly [string, string])[]; readonly eventsReceived: number } | OperationResult;

export interface ResponseCaseDescriptor {
  readonly caseId: string;
  readonly status: number;
  readonly body: ResponseBodyDescriptor;
  readonly hydration: "server-only" | "browser-safe";
  readonly exposedHeaders: readonly string[];
}

export interface OperationDescriptor {
  readonly id: string;
  readonly method: HttpMethod;
  readonly route: string;
  /** Required on descriptors generated from contract format 0.1. Absence retains legacy required-segment descriptors only. */
  readonly routePlan?: RoutePlan;
  readonly profileId?: string;
  readonly parameters: readonly ParameterDescriptor[];
  readonly requestBody?: RequestBodyDescriptor;
  readonly responses: readonly ResponseCaseDescriptor[];
  readonly requestExecution: "browser-allowed" | "server-only";
  readonly requestHeaderAllowlist: readonly string[];
}

export interface CredentialProvider {
  /** Returns credential headers (e.g. Authorization). Values are never logged or persisted by the runtime. */
  (operation: OperationDescriptor): Promise<readonly (readonly [string, string])[]> | readonly (readonly [string, string])[];
}

export interface ClientOptions {
  readonly baseUrl: string;
  readonly limits?: Partial<Limits>;
  readonly credentials?: RequestCredentials;
  readonly credentialProvider?: CredentialProvider;
  /** Additional non-credential headers (must be allowlisted by the operation, e.g. accept-language). */
  readonly headers?: readonly (readonly [string, string])[];
  /** Opt-in contract guard: compare this hash with the response header named by `semanticHashHeader`. */
  readonly expectedSemanticHash?: string;
  readonly semanticHashHeader?: string;
  readonly signal?: AbortSignal;
  /** Keep raw response bytes on failures (Explorer only; redaction is the caller's responsibility). */
  readonly retainRawBody?: boolean;
  readonly context?: Record<string, string>;
  readonly transport?: TransportOptions;
}

export interface PreparedRequest {
  readonly url: URL;
  readonly encodedPath: string;
  readonly queryEntries: readonly QueryEntry[];
  readonly method: HttpMethod;
  /** Headers excluding credentials (safe for previews and request identity). */
  readonly headers: readonly (readonly [string, string])[];
  readonly bodyText: string | undefined;
  readonly bodyBytes: Uint8Array | undefined;
}

export type OperationResult = ResponseCaseResult<string, number, unknown> | BodylessCaseResult<string, number> | RuntimeFailure;

function resolveCodec(ref: CodecRef<unknown>): Codec<unknown> {
  return typeof ref === "function" ? ref() : ref;
}

/**
 * Encodes arguments into a concrete request without sending it (Explorer preview uses the same writer).
 * Throws CodecError for domain / binder / URL violations.
 */
export function prepareRequest(operation: OperationDescriptor, args: unknown, options: ClientOptions, ctx?: CodecContext): PreparedRequest {
  const context =
    ctx ??
    createCodecContext({
      ...(operation.profileId === undefined ? {} : { profileId: operation.profileId }),
      ...(options.limits !== undefined ? { limits: options.limits } : {}),
      ...(options.signal !== undefined ? { signal: options.signal } : {}),
      ...(options.context !== undefined ? { context: options.context } : {}),
    });
  const pathValues = new Map<string, string>();
  const queryEntries: QueryEntry[] = [];
  const headers: (readonly [string, string])[] = [];
  const allow = new Set(operation.requestHeaderAllowlist.map((h) => h.toLowerCase()));
  for (const p of operation.parameters) {
    const pctx = context.child(p.name);
    const raw = p.get(args);
    if (raw === undefined) {
      if (p.presence === "required") {
        throw new CodecError("missing-required", pctx.path, `parameter '${p.name}' is required`);
      }
      continue;
    }
    const values: string[] = [];
    if (raw === null) {
      if (!p.nullable || p.binder.nullPolicy === "reject") {
        throw new CodecError("null-not-allowed", pctx.path, `parameter '${p.name}' does not allow null`);
      }
      if (p.binder.nullPolicy === "omit") {
        continue;
      }
      values.push(p.binder.nullLiteral ?? "");
    } else if (p.binder.cardinality === "repeated") {
      if (!Array.isArray(raw)) {
        throw new CodecError("type-mismatch", pctx.path, `parameter '${p.name}' must be an array`);
      }
      raw.forEach((item, i) => {
        values.push(p.binder.format(item, pctx.child(i)));
      });
    } else {
      values.push(p.binder.format(raw, pctx));
    }
    for (const v of values) {
      if (v.length === 0 && p.binder.emptyPolicy === "reject") {
        throw new CodecError("grammar", pctx.path, `parameter '${p.name}' cannot be empty`);
      }
      switch (p.location) {
        case "path":
          if (operation.routePlan !== undefined && p.id === undefined) { throw new CodecError("unsupported", pctx.path, "resolved routes require a stable parameter id"); }
          pathValues.set(operation.routePlan === undefined ? p.name : p.id!, operation.routePlan === undefined ? encodePathSegment(v, pctx.path) : v);
          break;
        case "query":
          queryEntries.push({ name: encodeQueryComponent(p.name), value: encodeQueryComponent(v) });
          break;
        case "header":
          validateHeaderName(p.name, pctx.path);
          headers.push([p.name.toLowerCase(), validateHeaderValue(v, pctx.path)]);
          break;
      }
    }
  }
  for (const [name, value] of options.headers ?? []) {
    validateHeaderName(name);
    if (isCredentialHeader(name)) {
      throw new CodecError("unsupported", "", "credential headers are supplied by the credential provider only");
    }
    if (!allow.has(name.toLowerCase())) {
      throw new CodecError("unsupported", "", `header '${name}' is not allowlisted for operation '${operation.id}'`);
    }
    headers.push([name.toLowerCase(), validateHeaderValue(value)]);
  }
  let bodyText: string | undefined;
  let bodyBytes: Uint8Array | undefined;
  if (operation.requestBody !== undefined) {
    const raw = operation.requestBody.get(args);
    const bctx = context.child("body");
    if (raw === undefined) {
      if (operation.requestBody.presence === "required") {
        throw new CodecError("missing-required", bctx.path, "request body is required");
      }
    } else if (operation.requestBody.kind === "form") {
      const form = encodeForm(operation.requestBody.fields, operation.requestBody.mediaType, raw, bctx);
      bodyBytes = form.bytes;
      bodyText = form.text;
      headers.push(["content-type", form.contentType]);
    } else if (operation.requestBody.kind === "binary") {
      if (!(raw instanceof Uint8Array)) {
        throw new CodecError("type-mismatch", bctx.path, "raw request body requires a Uint8Array");
      }
      if (raw.byteLength > context.limits.maxBodyBytes) {
        throw new RequestLimitError("maxBodyBytes", bctx.path, "raw request body exceeds the byte limit");
      }
      // Snapshot the view before awaiting credentials: later mutation must not change the prepared request.
      bodyBytes = new Uint8Array(raw);
      headers.push(["content-type", operation.requestBody.mediaType]);
    } else {
      let wire;
      if (raw === null) {
        if (!operation.requestBody.nullable) {
          throw new CodecError("null-not-allowed", bctx.path, "request body does not allow null");
        }
        wire = { kind: "null" } as const;
      } else {
        const codec = resolveCodec(operation.requestBody.codec);
        if (codec.encodeRequest === undefined) {
          throw new CodecError("unsupported", bctx.path, `codec '${codec.id}' has no request capability`, codec.id);
        }
        wire = codec.encodeRequest(raw, bctx);
      }
      // The depth and byte limits bound the request as well as the response; the server profile's MaxDepth is part of the
      // contract, so a body the server would refuse is reported here instead of being sent
      const maxDepth = Math.min(context.limits.maxDepth, operation.requestBody.maxDepth ?? Number.POSITIVE_INFINITY);
      const depth = jsonDepth(wire);
      if (depth > maxDepth) {
        throw new RequestLimitError("maxDepth", bctx.path, `request body nests ${depth} containers; the limit is ${maxDepth}`);
      }
      bodyBytes = writeJsonBytes(wire);
      if (bodyBytes.byteLength > context.limits.maxBodyBytes) {
        throw new RequestLimitError("maxBodyBytes", bctx.path, `request body has ${bodyBytes.byteLength} bytes; the limit is ${context.limits.maxBodyBytes}`);
      }
      bodyText = new TextDecoder().decode(bodyBytes);
      headers.push(["content-type", operation.requestBody.mediaType]);
    }
  }
  if (operation.method === "GET" || operation.method === "HEAD") {
    if (bodyBytes !== undefined) {
      throw new CodecError("unsupported", "", "GET/HEAD requests cannot carry a body");
    }
  }
  const built = operation.routePlan === undefined ? buildUrl(options.baseUrl, operation.route, pathValues, queryEntries)
    : buildPlannedUrl(options.baseUrl, operation.routePlan, pathValues, queryEntries);
  return { url: built.url, encodedPath: built.encodedPath, queryEntries: built.queryEntries, method: operation.method, headers, bodyText, bodyBytes };
}

/** A selected response case before decoding: what a hydration envelope carries. */
export interface RawResponse {
  readonly kind: "raw";
  readonly caseId: string;
  readonly status: number;
  readonly mediaType: string | undefined;
  /** Exposed headers of the case only (lowercase names). */
  readonly headers: readonly (readonly [string, string])[];
  readonly bodyKind: "none" | "json" | "text" | "binary" | "sse";
  readonly body: Uint8Array;
  readonly metadata: ResponseMetadata;
}

export type RawOutcome = RawResponse | RuntimeFailure;

function codecContextFor(operation: OperationDescriptor, options: ClientOptions, limits: Limits, budget?: ExecutionBudget): CodecContext {
  return createCodecContext({
    ...(operation.profileId === undefined ? {} : { profileId: operation.profileId }),
    limits,
    ...(budget !== undefined ? { deadline: budget.deadline, now: budget.now } : {}),
    ...(options.signal !== undefined ? { signal: options.signal } : {}),
    ...(options.context !== undefined ? { context: options.context } : {}),
  });
}

/**
 * Encodes, sends and selects the response case without decoding the body.
 * The raw case is what SSR hydration envelopes transport; `decodeResponse` turns it into the typed result with the
 * same decoder in every environment. Never throws for classified outcomes.
 */
export async function fetchResponse(operation: OperationDescriptor, args: unknown, options: ClientOptions, prepared?: PreparedRequest): Promise<RawOutcome> {
  const budget = new ExecutionBudget(resolveLimits(options.limits).timeoutMs, options.signal, options.transport?.now);
  try { return await fetchWithinBudget(operation, args, options, budget, prepared); }
  catch (error) { if (error instanceof BudgetEnded) { return budgetFailure(operation, budget, error); } throw error; }
  finally { budget.dispose(); }
}

function budgetFailure(operation: OperationDescriptor, budget: ExecutionBudget, error: BudgetEnded): RuntimeFailure {
  return error.kind === "cancelled" ? { kind: "cancelled", operationId: operation.id } : { kind: "timeout", operationId: operation.id, timeoutMs: budget.timeoutMs };
}

async function fetchWithinBudget(operation: OperationDescriptor, args: unknown, options: ClientOptions, budget: ExecutionBudget, prepared?: PreparedRequest, consumer?: (selected: ResponseCaseDescriptor, metadata: ResponseMetadata) => DownloadSink | undefined): Promise<RawOutcome & { readonly streamed?: true }> {
  const limits: Limits = resolveLimits(options.limits);
  budget.check();
  const context = codecContextFor(operation, options, limits, budget);
  let request: PreparedRequest;
  try {
    request = prepared ?? prepareRequest(operation, args, options, context);
  } catch (error) {
    if (error instanceof RequestLimitError || error instanceof FormLimitError) {
      return { kind: "limit-failure", operationId: operation.id, limit: error instanceof FormLimitError ? "maxBodyBytes" : error.limit };
    }
    const codecError = asCodecError(error);
    if (codecError !== undefined) {
      if (codecError.code === "cancelled") {
        budget.check();
        return { kind: "cancelled", operationId: operation.id };
      }
      return { kind: "transport-failure", operationId: operation.id, reason: "request-encoding", message: `${codecError.code} at ${codecError.path}: ${codecError.message}` };
    }
    throw error;
  }
  const headers = [...request.headers];
  if (options.credentialProvider !== undefined) {
    // names are checked as tokens only: the Nuxt module forwards an incoming cookie during SSR, which Node's fetch sends; a header that
    // cannot be sent is a request-encoding failure like any other argument, never an exception (values never reach the message)
    try {
      for (const [name, value] of await budget.wait(() => options.credentialProvider!(operation))) {
        headers.push([validateHeaderToken(name, "/credentials").toLowerCase(), validateHeaderValue(value, "/credentials/" + name.toLowerCase())]);
      }
    } catch (error) {
      if (error instanceof BudgetEnded) { throw error; }
      // Providers are application code and may put credentials in an exception, including a CodecError.
      return { kind: "transport-failure", operationId: operation.id, reason: "request-encoding", message: "credential provider failed or supplied invalid credential headers" };
    }
  }
  const acceptable = operation.responses
    .map((r) => (r.body.kind === "none" ? undefined : r.body.mediaType))
    .filter((m): m is string => m !== undefined);
  if (acceptable.length > 0 && !headers.some(([n]) => n === "accept")) {
    headers.push(["accept", [...new Set(acceptable)].join(", ")]);
  }
  const outcome = await send(
    {
      url: request.url,
      method: request.method,
      headers,
      ...(request.bodyBytes !== undefined ? { body: request.bodyBytes } : {}),
      ...(options.signal !== undefined ? { signal: options.signal } : {}),
      ...(options.credentials !== undefined ? { credentials: options.credentials } : {}),
    },
    { ...options.transport, limits },
    budget,
    consumer === undefined ? undefined : metadata => {
      // Select using headers before any byte reaches application code. JSON errors still use the normal codec path.
      if (operation.method === "HEAD" || metadata.status === 204 || metadata.status === 205 || metadata.status === 304) { return undefined; }
      if (options.expectedSemanticHash !== undefined && options.semanticHashHeader !== undefined) {
        const actual = metadata.headers.find(([name]) => name === options.semanticHashHeader!.toLowerCase())?.[1];
        if (actual !== undefined && actual !== options.expectedSemanticHash) { return undefined; }
      }
      const media = metadata.mediaType === undefined ? undefined : parseMediaType(metadata.mediaType);
      const cases = operation.responses.filter(c => c.status === metadata.status);
      if (cases.some(c => c.body.kind === "none")) { return undefined; }
      const selected = media === undefined ? undefined : cases.find(c => c.body.kind !== "none" && parseMediaType(c.body.mediaType)?.essence === media.essence);
      if (selected === undefined || (selected.body.kind !== "binary" && !isUtf8OrUnspecified(media!))) { return undefined; }
      return consumer(selected, metadata);
    },
  );
  switch (outcome.kind) {
    case "cancelled":
      return { kind: "cancelled", operationId: operation.id };
    case "timeout":
      return { kind: "timeout", operationId: operation.id, timeoutMs: limits.timeoutMs };
    case "limit-failure":
      return { kind: "limit-failure", operationId: operation.id, limit: outcome.limit, metadata: outcome.metadata };
    case "transport-failure":
      return { kind: "transport-failure", operationId: operation.id, reason: outcome.reason, message: outcome.message, ...(outcome.metadata !== undefined ? { metadata: outcome.metadata } : {}) };
    case "ok":
      break;
  }
  const response = outcome.response;
  const raw = options.retainRawBody === true ? { rawBody: response.body } : {};

  if (options.expectedSemanticHash !== undefined && options.semanticHashHeader !== undefined) {
    const actual = response.headers.find(([n]) => n === options.semanticHashHeader!.toLowerCase())?.[1];
    if (actual !== undefined && actual !== options.expectedSemanticHash) {
      return { kind: "contract-mismatch", operationId: operation.id, expectedSemanticHash: options.expectedSemanticHash, actualSemanticHash: actual, metadata: response.metadata };
    }
  }

  const statusCases = operation.responses.filter((r) => r.status === response.status);
  if (statusCases.length === 0) {
    return { kind: "unexpected-response", operationId: operation.id, metadata: response.metadata, reason: "undeclared-status", ...raw };
  }
  const forcedBodyless = operation.method === "HEAD" || response.status === 204 || response.status === 205 || response.status === 304;
  const media = response.mediaType === undefined ? undefined : parseMediaType(response.mediaType);
  const exposed = (c: ResponseCaseDescriptor): (readonly [string, string])[] =>
    response.headers.filter(([n]) => c.exposedHeaders.some((e) => e.toLowerCase() === n));

  // bodyless case selection: only when the declared case is bodyless
  const bodyless = statusCases.find((c) => c.body.kind === "none");
  if (bodyless !== undefined && (forcedBodyless || response.body.byteLength === 0 || statusCases.length === 1)) {
    if (response.body.byteLength !== 0 && !forcedBodyless) {
      return { kind: "codec-failure", operationId: operation.id, caseId: bodyless.caseId, metadata: response.metadata, code: "unexpected-body", path: "", message: "declared bodyless case received a body", ...raw };
    }
    return { kind: "raw", caseId: bodyless.caseId, status: response.status, mediaType: response.mediaType, headers: exposed(bodyless), bodyKind: "none", body: new Uint8Array(), metadata: response.metadata };
  }
  if (forcedBodyless || media === undefined) {
    return { kind: "unexpected-response", operationId: operation.id, metadata: response.metadata, reason: "undeclared-media", ...raw };
  }
  const selected = statusCases.find((c) => c.body.kind !== "none" && parseMediaType(c.body.mediaType)?.essence === media.essence);
  if (selected === undefined || selected.body.kind === "none") {
    return { kind: "unexpected-response", operationId: operation.id, metadata: response.metadata, reason: "undeclared-media", ...raw };
  }
  if (selected.body.kind !== "binary" && !isUtf8OrUnspecified(media)) {
    return { kind: "codec-failure", operationId: operation.id, caseId: selected.caseId, metadata: response.metadata, code: "charset", path: "", message: "only UTF-8 bodies are supported", ...raw };
  }
  return { kind: "raw", caseId: selected.caseId, status: response.status, mediaType: response.mediaType, headers: exposed(selected), bodyKind: selected.body.kind, body: response.body, metadata: response.metadata, ...(response.streamed === true ? { streamed: true as const } : {}) };
}

/** Decodes a selected case with the case codec. Failures pass through unchanged. */
export function decodeResponse(operation: OperationDescriptor, raw: RawOutcome, options: ClientOptions, budget?: ExecutionBudget): OperationResult {
  if (raw.kind !== "raw") {
    return raw;
  }
  const limits: Limits = resolveLimits(options.limits);
  const selected = operation.responses.find((c) => c.caseId === raw.caseId);
  if (selected === undefined) {
    return { kind: "unexpected-response", operationId: operation.id, metadata: raw.metadata, reason: "undeclared-status" };
  }
  const context = codecContextFor((selected.body.kind === "json" || selected.body.kind === "sse") && selected.body.profileId !== undefined ? { ...operation, profileId: selected.body.profileId } : operation, options, limits, budget);
  const retained = options.retainRawBody === true ? { rawBody: raw.body } : {};
  if (raw.bodyKind === "none" || selected.body.kind === "none") {
    return { kind: "response", caseId: selected.caseId, status: raw.status, headers: raw.headers };
  }
  try {
    budget?.check();
    if (selected.body.kind === "sse") {
      const parser = new SseParser();
      const events: ServerSentEvent<unknown>[] = [];
      for (const event of parser.push(raw.body)) {
        context.checkpoint();
        events.push(decodeSseEvent(operation, selected, event, raw.metadata, options, budget));
      }
      parser.finish();
      return { kind: "response", caseId: selected.caseId, status: raw.status, headers: raw.headers, data: events };
    }
    if (selected.body.kind === "binary") {
      const headers = raw.headers.filter(([name]) => selected.exposedHeaders.some(h => h.toLowerCase() === name.toLowerCase()));
      const filename = suggestedFileName(headers.find(([name]) => name.toLowerCase() === "content-disposition")?.[1]);
      const data: BufferedFile = { bytes: raw.body, contentType: raw.mediaType!, ...(filename === undefined ? {} : { suggestedFileName: filename }) };
      return { kind: "response", caseId: selected.caseId, status: raw.status, headers, data };
    }
    if (selected.body.kind === "text") {
      const text = decodeUtf8Strict(raw.body);
      return { kind: "response", caseId: selected.caseId, status: raw.status, headers: raw.headers, data: text };
    }
    if (raw.body.byteLength === 0) {
      return { kind: "codec-failure", operationId: operation.id, caseId: selected.caseId, metadata: raw.metadata, code: "empty-body", path: "", message: "declared JSON case received an empty body (JSON null is a body)", ...retained };
    }
    const wire = parseJsonBytes(raw.body, { limits, checkpoint: context.checkpoint });
    let data: unknown;
    if (wire.kind === "null") {
      if (!selected.body.nullable) {
        return { kind: "codec-failure", operationId: operation.id, caseId: selected.caseId, metadata: raw.metadata, code: "null-not-allowed", path: "", message: "declared case does not allow a null body", ...retained };
      }
      data = null;
    } else {
      const codec = resolveCodec(selected.body.codec);
      if (codec.decodeResponse === undefined) {
        throw new CodecError("unsupported", "", `codec '${codec.id}' has no response capability`, codec.id);
      }
      data = codec.decodeResponse(wire, context);
    }
    return { kind: "response", caseId: selected.caseId, status: raw.status, headers: raw.headers, data };
  } catch (error) {
    if (error instanceof SseDecodeFailure) { return error.failure; }
    if (error instanceof JsonParseError) {
      if (error.code === "depth-limit" || error.code === "token-limit" || error.code === "number-length-limit") {
        return { kind: "limit-failure", operationId: operation.id, limit: error.code === "depth-limit" ? "maxDepth" : error.code === "token-limit" ? "maxTokens" : "maxNumberCharacters", metadata: raw.metadata };
      }
      return { kind: "codec-failure", operationId: operation.id, caseId: selected.caseId, metadata: raw.metadata, code: error.code, path: "", message: error.message, ...retained };

    }
    const codecError = asCodecError(error);
    if (codecError !== undefined) {
      if (codecError.code === "cancelled") {
        return options.signal?.aborted === true ? { kind: "cancelled", operationId: operation.id } : { kind: "timeout", operationId: operation.id, timeoutMs: limits.timeoutMs };
      }
      return { kind: "codec-failure", operationId: operation.id, caseId: selected.caseId, metadata: raw.metadata, code: codecError.code, path: codecError.path, message: codecError.message, ...retained };
    }
    throw error;
  }
}

/** A request body beyond a limit: reported as a limit failure, never sent. */
export class RequestLimitError extends CodecError {
  constructor(
    readonly limit: "maxDepth" | "maxBodyBytes",
    path: string,
    message: string,
  ) {
    super("limit", path, message);
  }
}

/** Container nesting of a JSON value, counted like System.Text.Json's MaxDepth (a scalar is 0, `{}` and `[]` are 1). */
export function jsonDepth(value: JsonValue): number {
  let max = 0;
  const stack: [JsonValue, number][] = [[value, 0]];
  while (stack.length > 0) {
    const [v, d] = stack.pop()!;
    if (v.kind === "array") {
      max = Math.max(max, d + 1);
      for (const item of v.items) {
        stack.push([item, d + 1]);
      }
    } else if (v.kind === "object") {
      max = Math.max(max, d + 1);
      for (const entry of v.entries) {
        stack.push([entry.value, d + 1]);
      }
    }
  }
  return max;
}

/** Executes one operation end to end: encode → send → case dispatch → decode. Never throws for classified outcomes. */
export async function execute(operation: OperationDescriptor, args: unknown, options: ClientOptions): Promise<OperationResult> {
  return (await executeWithRaw(operation, args, options)).result;
}

/**
 * Streams a declared binary response into a caller-owned sink without retaining its chunks.
 * Returns kind:"download" only after EOF and all writes succeed. Declared JSON/text errors are decoded normally.
 * Limits include decompressed bytes and sink time. A failure can leave bytes in the sink; commit only on success.
 */
export async function download(operation: OperationDescriptor, args: unknown, options: ClientOptions, sink: DownloadSink): Promise<DownloadResult> {
  const budget = new ExecutionBudget(resolveLimits(options.limits).timeoutMs, options.signal, options.transport?.now);
  try {
    const raw = await fetchWithinBudget(operation, args, options, budget, undefined, selected => selected.body.kind === "binary" ? sink : undefined);
    if (raw.kind === "raw" && raw.bodyKind === "binary" && raw.streamed === true) {
      budget.check();
      const filename = suggestedFileName(raw.headers.find(([name]) => name.toLowerCase() === "content-disposition")?.[1]);
      return { kind: "download", caseId: raw.caseId, status: raw.status, headers: raw.headers, file: { bytesWritten: raw.metadata.bodyBytes, contentType: raw.mediaType!, ...(filename === undefined ? {} : { suggestedFileName: filename }) } };
    }
    const result = decodeResponse(operation, raw, options, budget);
    if (result.kind === "response") { budget.check(); }
    return result;
  } catch (error) { if (error instanceof BudgetEnded) { return budgetFailure(operation, budget, error); } throw error; }
  finally { budget.dispose(); }
}

class SseDecodeFailure extends Error {
  constructor(readonly failure: RuntimeFailure) { super("event data did not match the contract"); }
}

function decodeSseEvent(operation: OperationDescriptor, selected: ResponseCaseDescriptor, event: ServerSentEvent<string>, metadata: ResponseMetadata, options: ClientOptions, budget?: ExecutionBudget): ServerSentEvent<unknown> {
  if (selected.body.kind !== "sse" || selected.body.dataFormat === "text") { return event; }
  const body = selected.body;
  const jsonCase: ResponseCaseDescriptor = { ...selected, body: { ...body, kind: "json", mediaType: "application/json" } };
  // ASP.NET's ServerSentEventsResult writes null data as an empty data field, not the four JSON bytes "null".
  const bytes = new TextEncoder().encode(event.data === "" ? "null" : event.data);
  const decoded = decodeResponse({ ...operation, responses: [jsonCase] }, { kind: "raw", caseId: selected.caseId, status: metadata.status, mediaType: "application/json", headers: [], bodyKind: "json", body: bytes, metadata }, { ...options, retainRawBody: false }, budget);
  if (decoded.kind !== "response") { throw new SseDecodeFailure(decoded); }
  return { ...event, data: "data" in decoded ? decoded.data : undefined };
}

/**
 * Receives SSE events as they arrive, awaiting each handler before reading more input. Does not reconnect or retain events.
 * timeoutMs and maxBodyBytes bound the entire connection; each JSON event also uses the codec/parser limits.
 * Events already delivered remain delivered on failure. EOF alone returns kind:"subscription".
 */
export async function subscribe<T = unknown>(operation: OperationDescriptor, args: unknown, options: ClientOptions, onEvent: EventSink<T>): Promise<SubscriptionResult> {
  const budget = new ExecutionBudget(resolveLimits(options.limits).timeoutMs, options.signal, options.transport?.now);
  const parser = new SseParser();
  let eventsReceived = 0;
  let failure: RuntimeFailure | undefined;
  let selectedCase: ResponseCaseDescriptor | undefined;
  let streamMetadata: ResponseMetadata | undefined;
  const parseFailure = (error: unknown): void => {
    if (error instanceof SseDecodeFailure) { failure = error.failure; }
    else if (error instanceof JsonParseError && selectedCase !== undefined && streamMetadata !== undefined) {
      failure = { kind: "codec-failure", operationId: operation.id, caseId: selectedCase.caseId, metadata: streamMetadata, code: error.code, path: "", message: error.message };
    }
  };
  try {
    const raw = await fetchWithinBudget(operation, args, options, budget, undefined, (selected, metadata) => {
      if (selected.body.kind !== "sse") { return undefined; }
      selectedCase = selected;
      streamMetadata = metadata;
      return async (chunk, signal) => {
        streamMetadata = { ...metadata, bodyBytes: streamMetadata!.bodyBytes + chunk.byteLength };
        const iterator = parser.push(chunk);
        for (;;) {
          let event: ServerSentEvent<unknown>;
          try {
            budget.check();
            const next = iterator.next();
            if (next.done) { break; }
            event = decodeSseEvent(operation, selected, next.value, streamMetadata, options, budget);
          } catch (error) { parseFailure(error); throw error; }
          // Handler errors are deliberately outside the codec catch; transport supplies a fixed, sanitized diagnostic.
          await onEvent(event as ServerSentEvent<T>, signal);
          eventsReceived++;
        }
      };
    });
    if (failure !== undefined) { return failure; }
    if (raw.kind === "raw" && raw.bodyKind === "sse" && raw.streamed === true) {
      try { parser.finish(); } catch (error) { parseFailure(error); if (failure !== undefined) { return failure; } throw error; }
      budget.check();
      return { kind: "subscription", caseId: raw.caseId, status: raw.status, headers: raw.headers, eventsReceived };
    }
    return decodeResponse(operation, raw, options, budget);
  } catch (error) { if (error instanceof BudgetEnded) { return budgetFailure(operation, budget, error); } throw error; }
  finally { budget.dispose(); }
}

/** The Explorer and SSR adapter share the same bounded execution, retaining raw bytes only for their own redaction/envelope handling. */
export async function executeWithRaw(operation: OperationDescriptor, args: unknown, options: ClientOptions, prepared?: PreparedRequest): Promise<{ readonly raw: RawOutcome; readonly result: OperationResult }> {
  const budget = new ExecutionBudget(resolveLimits(options.limits).timeoutMs, options.signal, options.transport?.now);
  try {
    const raw = await fetchWithinBudget(operation, args, options, budget, prepared);
    const result = decodeResponse(operation, raw, options, budget);
    if (result.kind === "response") { budget.check(); }
    // Never give the envelope adapter a raw success if decoding failed or exceeded the shared deadline.
    return { raw: result.kind === "response" ? raw : result, result };
  } catch (error) { if (error instanceof BudgetEnded) { const result = budgetFailure(operation, budget, error); return { raw: result, result }; } throw error; }
  finally { budget.dispose(); }
}
