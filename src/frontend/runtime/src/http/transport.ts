import { resolveLimits, type Limits } from "../json/limits.js";
import type { ResponseMetadata } from "./result.js";
import { BudgetEnded, discard, ExecutionBudget } from "./budget.js";

/**
 * Fetch wrapper enforcing the transport rules: redirect:"error", no retry, explicit credentials mode, timeout via
 * AbortSignal, and streaming body reads bounded by maxBodyBytes (decompressed bytes as delivered to the application).
 */
export interface TransportRequest {
  readonly url: URL;
  readonly method: string;
  readonly headers: readonly (readonly [string, string])[];
  readonly body?: Uint8Array;
  readonly signal?: AbortSignal;
  readonly credentials?: RequestCredentials;
}

export interface TransportResponse {
  readonly status: number;
  readonly headers: readonly (readonly [string, string])[];
  readonly mediaType: string | undefined;
  readonly body: Uint8Array;
  readonly metadata: ResponseMetadata;
}

export type TransportOutcome =
  | { readonly kind: "ok"; readonly response: TransportResponse }
  | { readonly kind: "transport-failure"; readonly reason: "network" | "redirect" | "read" | "cors"; readonly message: string; readonly metadata?: ResponseMetadata }
  | { readonly kind: "timeout" }
  | { readonly kind: "cancelled" }
  | { readonly kind: "limit-failure"; readonly limit: "maxBodyBytes"; readonly metadata: ResponseMetadata };

export interface TransportOptions {
  readonly limits?: Partial<Limits>;
  /** Injected fetch for tests and Node adapters; defaults to globalThis.fetch. */
  readonly fetch?: typeof fetch;
  /** Epoch-millisecond clock; injectable for deterministic deadline tests. */
  readonly now?: () => number;
}

function headerList(headers: Headers): (readonly [string, string])[] {
  const list: (readonly [string, string])[] = [];
  headers.forEach((value, name) => {
    list.push([name.toLowerCase(), value]);
  });
  return list;
}

export async function send(request: TransportRequest, options: TransportOptions = {}, sharedBudget?: ExecutionBudget): Promise<TransportOutcome> {
  const limits = resolveLimits(options.limits);
  const fetchImpl = options.fetch ?? globalThis.fetch;
  // the runtime targets the current browsers and Node 24 (no polyfills for older environments); both have these
  const budget = sharedBudget ?? new ExecutionBudget(limits.timeoutMs, request.signal, options.now);
  const signal = budget.signal;
  const init: RequestInit = {
    method: request.method,
    headers: request.headers.map(([n, v]) => [n, v] as [string, string]),
    redirect: "error",
    credentials: request.credentials ?? "same-origin",
    signal,
    cache: "no-store",
  };
  if (request.body !== undefined) {
    init.body = request.body as BodyInit;
  }
  let response: Response | undefined;
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  let metadata: ResponseMetadata | undefined;
  let stage: "network" | "read" = "network";
  const chunks: Uint8Array[] = [];
  let complete = false;
  try {
    response = await budget.wait(() => {
      const pending = fetchImpl(request.url, init);
      void pending.then(r => { if (signal.aborted) { discard(() => r.body?.cancel()); } }, () => {});
      return pending;
    });
    if (response.type === "opaqueredirect") {
      return { kind: "transport-failure", reason: "redirect", message: "redirect responses are not followed (redirect:error)" };
    }
    if (response.type === "opaque" || response.type === "error" || response.status === 0) {
      return { kind: "transport-failure", reason: "cors", message: "an opaque/error response cannot be read as a file" };
    }
    stage = "read";
    const headers = headerList(response.headers);
    const mediaType = response.headers.get("content-type") ?? undefined;
    let total = 0;
    const updateMetadata = (): ResponseMetadata => ({ status: response!.status, mediaType, bodyBytes: total, headers });
    metadata = updateMetadata();
    const stream: unknown = response.body;
    if (stream !== null) {
      if (typeof (stream as ReadableStream<Uint8Array> | undefined)?.getReader !== "function") {
        return { kind: "transport-failure", reason: "read", message: "fetch adapter must provide a bounded Web Stream or null body; arrayBuffer fallback is not supported", metadata };
      }
      reader = (stream as ReadableStream<Uint8Array>).getReader();
      for (;;) {
        const chunk = await budget.wait(() => reader!.read());
        if (chunk.done) { break; }
        if (!(chunk.value instanceof Uint8Array)) { throw new TypeError("response stream yielded a non-byte chunk"); }
        total += chunk.value.byteLength;
        metadata = updateMetadata();
        if (total > limits.maxBodyBytes) {
          // Fix the primary outcome before attempting cleanup. Cleanup never blocks or replaces it.
          return { kind: "limit-failure", limit: "maxBodyBytes", metadata };
        }
        chunks.push(chunk.value);
      }
    }
    budget.check();
    const body = chunks.length === 1 ? chunks[0]! : new Uint8Array(total);
    if (chunks.length > 1) {
      let offset = 0;
      for (const chunk of chunks) { body.set(chunk, offset); offset += chunk.byteLength; }
    }
    complete = true;
    return { kind: "ok", response: { status: response.status, headers, mediaType, body, metadata: updateMetadata() } };
  } catch (error) {
    if (error instanceof BudgetEnded) { return { kind: error.kind }; }
    // Node's fetch (undici) names the reason in `cause` ("unexpected redirect", "connect ECONNREFUSED …"). Browsers never say why:
    // a refused redirect is a plain network error (Fetch, redirect mode "error") and CORS details only reach the console — Chrome
    // "Failed to fetch", Firefox "NetworkError when attempting to fetch resource.", Safari "Load failed". The reason stays
    // "network" there, and the message says what it may have been.
    // An adapter or stream error can contain request headers, bodies or arbitrary thrown values. Inspect it only for
    // classification; public diagnostics use fixed text and never stringify it or copy its message/cause/stack.
    let detail = "";
    try {
      if (error instanceof Error) {
        if (typeof error.message === "string") { detail = error.message; }
        if (error.cause instanceof Error && typeof error.cause.message === "string") { detail += ": " + error.cause.message; }
      }
    } catch { /* Error-like values may have throwing accessors; retain the generic classification. */ }
    const reason = stage === "read" ? "read" : /redirect/i.test(detail) ? "redirect" : /cors/i.test(detail) ? "cors" : "network";
    const messages = {
      read: "the response body could not be read",
      redirect: "redirect responses are not followed (redirect:error)",
      cors: "the request was refused by CORS",
      network: "the request failed: an unreachable server, a CORS refusal and a refused redirect can fail alike",
    };
    return { kind: "transport-failure", reason, message: messages[reason], ...(metadata === undefined ? {} : { metadata }) };
  } finally {
    if (reader !== undefined) {
      if (!complete) { discard(() => reader!.cancel()); }
      discard(() => reader!.releaseLock());
    } else if (!complete && response !== undefined) { discard(() => response!.body?.cancel()); }
    chunks.length = 0;
    if (sharedBudget === undefined) { budget.dispose(); }
  }
}
