// Framework-free Explorer core: the contract is loaded from the authorized route, modules are the
// locally served browser artifacts named by the contract, and every request/response goes through the same runtime
// pipeline as generated clients (prepareRequest → fetchResponse → decodeResponse). No UI-only JSON.stringify path.
import {
  createContractRegistry,
  executeWithRaw,
  formatDateOnly,
  formatDateTimeLocalWire,
  formatDateTimeOffset,
  formatDateTimeUnspecified,
  formatDateTimeUtc,
  formatDecimal,
  formatDuration,
  formatTimeOnly,
  isDecimal,
  parseJson,
  prepareRequest,
  TisiliaMap,
  writeJson,
  encodeBase64,
  createCodecContext,
  CodecError,
  JsonParseError,
  type ClientOptions,
  type ContractDocument,
  type ContractOperation,
  type ContractFormField,
  type CodecContext,
  type ContractRegistry,
  type JsonValue,
  type OperationDescriptor,
  type OperationResult,
  type PreparedRequest,
  type RawOutcome,
} from "@kkdev92/tisilia-runtime";
import { credentialHeaders, type Credential } from "./auth.js";
import { t, type FieldErrorInfo } from "./i18n.js";

export { shortError } from "./i18n.js";

export interface ExplorerLoadOptions {
  /** Base URL of the Explorer route (e.g. `/__tisilia/`); contract and modules are siblings of it. */
  readonly baseHref: string;
  readonly fetchImpl?: typeof fetch;
  readonly importImpl?: (url: string) => Promise<Record<string, unknown>>;
}

export interface ExplorerModel {
  readonly document: ContractDocument;
  readonly registry: ContractRegistry;
  readonly operations: readonly ContractOperation[];
  readonly moduleErrors: readonly string[];
}

/** Summary and description the application gave an operation or a type (`TisiliaOptions.Documentation`). */
export interface Documentation {
  readonly summary: string;
  readonly description: string;
}

export function documentationOf(document: ContractDocument, targetId: string): Documentation | undefined {
  const entries = (document as ContractDocument & { readonly documentation?: readonly { readonly targetId: string; readonly summary: string; readonly description: string }[] }).documentation ?? [];
  const entry = entries.find((d) => d.targetId === targetId);
  return entry === undefined || (entry.summary.length === 0 && entry.description.length === 0) ? undefined : { summary: entry.summary, description: entry.description };
}

/** Loads the contract and the browser artifacts of its modules from the authorized Explorer routes. */
export async function loadExplorer(options: ExplorerLoadOptions): Promise<ExplorerModel> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const importImpl = options.importImpl ?? ((url: string) => import(/* @vite-ignore */ url) as Promise<Record<string, unknown>>);
  const contractUrl = new URL("contract", options.baseHref).href;
  const response = await fetchImpl(contractUrl, { credentials: "same-origin", cache: "no-store" });
  if (!response.ok) {
    throw new Error(`contract route ${contractUrl} answered ${response.status}`);
  }
  // control documents hold only safe integers (SV01); JSON literals inside are ASTs, so the native parser is exact here
  const document = JSON.parse(await response.text()) as ContractDocument;
  if (document.format !== "tisilia.contract" || document.version !== "0.1") {
    throw new Error("expected tisilia.contract 0.1: re-export with Tisilia 0.1.0-alpha; other contract versions are not supported");
  }
  const modules = new Map<string, Readonly<Record<string, unknown>>>();
  const moduleErrors: string[] = [];
  for (const module of document.modules) {
    const artifact = module.artifacts.find((a) => a.target === "browser");
    if (artifact === undefined) {
      // a module whose exports all run in .NET (a behavior, a resolver) has nothing for the browser to load
      if (module.exports.some((e) => e.targets.includes("browser"))) {
        moduleErrors.push(`module '${module.id}' has no browser artifact; its codecs run only in the server/Node client`);
      }
      continue;
    }
    // served by the host from the module's declared path after a digest check; never from the contract itself
    const url = new URL("modules/" + encodeURIComponent(module.id) + "/" + artifact.path.split("/").map(encodeURIComponent).join("/"), options.baseHref).href;
    try {
      modules.set(module.id, await importImpl(url));
    } catch (error) {
      moduleErrors.push(`module '${module.id}' failed to load from ${url}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  const registry = createContractRegistry(document, { modules });
  return { document, registry, operations: document.operations, moduleErrors };
}

// ---------------------------------------------------------------- catalog

export interface OperationSummary {
  readonly id: string;
  readonly method: string;
  readonly route: string;
  readonly tags: readonly string[];
  readonly requestExecution: "browser-allowed" | "server-only";
  readonly hasRequestInput: boolean;
  readonly missingCapabilities: readonly string[];
  /** The application's one-line summary, when it gave one. */
  readonly summary: string;
  /** `[Authorize]` / `RequireAuthorization()` without `[AllowAnonymous]` (the contract's auth policy). */
  readonly authRequired: boolean;
  /** Antiforgery validation is required (a CSRF token must accompany the call). */
  readonly csrfRequired: boolean;
}

export function summarize(model: ExplorerModel): OperationSummary[] {
  return model.operations.map((op) => {
    const missing: string[] = [];
    let hasRequestInput = true;
    if (op.requestBody.kind === "json" || op.requestBody.kind === "xml") {
      const codec = model.document.codecs.find((c) => c.id === op.requestBody.kind && false) ?? model.document.codecs.find((c) => c.id === (op.requestBody as { use: { codecId: string } }).use.codecId);
      if (codec?.capabilities.request === undefined) {
        missing.push("request");
      }
      if (codec?.capabilities.requestInput === undefined) {
        hasRequestInput = false;
      }
    }
    for (const r of op.responses) {
      if (r.body.kind === "json" || r.body.kind === "xml") {
        const codec = model.document.codecs.find((c) => c.id === r.body.kind && false) ?? model.document.codecs.find((c) => c.id === (r.body as { use: { codecId: string } }).use.codecId);
        if (codec?.capabilities.response === undefined) {
          missing.push("response:" + r.id);
        }
      }
    }
    return {
      id: op.id,
      method: op.method,
      route: op.route,
      tags: op.tags,
      requestExecution: op.security.requestExecution,
      hasRequestInput,
      missingCapabilities: missing,
      summary: documentationOf(model.document, op.id)?.summary ?? "",
      authRequired: op.security.authPolicyId !== "tisilia.auth.anonymous@0.1",
      csrfRequired: op.security.csrfPolicyId !== "tisilia.csrf.none@0.1",
    };
  });
}

/**
 * Operations matching a search: every word must appear in the id, route, method, summary or a tag (`get users`, `post todo`,
 * `/users`); an exact method name alone selects that method.
 */
export function filterOperations(summaries: readonly OperationSummary[], query: string, tag: string | undefined): OperationSummary[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter((w) => w.length > 0);
  return summaries.filter((s) => {
    if (tag !== undefined && !s.tags.includes(tag)) {
      return false;
    }
    const haystack = [s.id, s.route, s.summary, ...s.tags].join(" ").toLowerCase();
    return words.every((w) => s.method.toLowerCase() === w || haystack.includes(w));
  });
}

// ---------------------------------------------------------------- typed input

export interface ParameterInput {
  readonly name: string;
  readonly location: "path" | "query" | "header";
  readonly typeId: string;
  readonly codecId: string;
  readonly presence: "required" | "optional";
  readonly nullable: boolean;
  readonly repeated: boolean;
}

export function parameterInputs(model: ExplorerModel, op: ContractOperation): ParameterInput[] {
  return op.parameters.map((p) => {
    const binder = model.document.binders.find((b) => b.id === p.binderId);
    return { name: p.name, location: p.location, typeId: p.use.typeId, codecId: p.use.codecId, presence: p.presence, nullable: p.use.semanticNullable, repeated: binder?.cardinality === "repeated" };
  });
}

export interface TypedInputs {
  readonly formValues?: Readonly<Record<string, string>>;
  readonly formFiles?: Readonly<Record<string, readonly import("@kkdev92/tisilia-runtime").UploadFile[]>>;
  readonly bodyBytes?: Uint8Array;
  /** Raw editor text per parameter; empty string means "not provided"; "null" literal means null for nullable parameters. */
  readonly parameters: Readonly<Record<string, string>>;
  /** JSON text of the body editor (typed mode) or raw body text (raw mode). */
  readonly body: string;
  /** The body as the form editor holds it (a JSON AST); used instead of `body` when present, so errors keep their field paths. */
  readonly bodyJson?: JsonValue;
}

/** An input error: where (a JSON pointer), the message, and its kind as a code (see FieldErrorInfo), which the page words. */
export interface InputError extends FieldErrorInfo {
  readonly path: string;
}

export interface BuiltArgs {
  readonly args: Record<string, unknown>;
  readonly errors: readonly InputError[];
}

/** The kind of an error a codec or the JSON parser threw: the codec's error code, or "invalid-json". */
function errorCode(error: unknown): string | undefined {
  return codecErrorOf(error)?.code ?? (error instanceof JsonParseError ? "invalid-json" : undefined);
}

/** An input error with the kind of what was thrown, when there is one. */
function thrown(path: string, error: unknown): InputError {
  const code = errorCode(error);
  return code === undefined ? { path, message: describeError(error) } : { path, message: describeError(error), code };
}

/** Builds public domain arguments from editor text through the codecs' request-input factories (never a UI-only parser). */
export function buildArgs(model: ExplorerModel, op: ContractOperation, inputs: TypedInputs): BuiltArgs {
  const args: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
  const errors: InputError[] = [];
  const context = createCodecContext();
  for (const p of parameterInputs(model, op)) {
    const text = inputs.parameters[p.name] ?? "";
    if (text.length === 0) {
      if (p.presence === "required") {
        errors.push({ path: "/" + p.name, message: "required", code: "required" });
      }
      continue;
    }
    if (text === "null" && p.nullable) {
      args[p.name] = null;
      continue;
    }
    const codec = model.registry.registry.get(p.codecId);
    if (codec.parseRequestInput === undefined) {
      errors.push({ path: "/" + p.name, message: `codec '${p.codecId}' has no request-input capability`, code: "no-request-input" });
      continue;
    }
    try {
      if (p.repeated) {
        args[p.name] = text
          .split("\n")
          .filter((line) => line.length > 0)
          .map((line, i) => codec.parseRequestInput!(line, context.child(p.name).child(i)));
      } else {
        args[p.name] = codec.parseRequestInput(text, context.child(p.name));
      }
    } catch (error) {
      errors.push(thrown("/" + p.name, error));
    }
  }
  if (op.requestBody.kind === "json") {
    const bodyText = inputs.body;
    if (inputs.bodyJson === undefined && bodyText.trim().length === 0) {
      if (op.requestBody.presence === "required") {
        errors.push({ path: "/body", message: "request body is required", code: "body-required" });
      }
    } else {
      const codec = model.registry.registry.get(op.requestBody.use.codecId);
      try {
        const ast = inputs.bodyJson ?? parseJson(bodyText);
        if (ast.kind === "null" && op.requestBody.use.semanticNullable) {
          args["body"] = null;
        } else if (codec.parseRequestInput === undefined) {
          errors.push({ path: "/body", message: `codec '${codec.id}' has no request-input capability; use raw mode`, code: "no-request-input" });
        } else {
          args["body"] = codec.parseRequestInput(ast, context.child("body"));
        }
      } catch (error) {
        // the codec names the field (`/body/items/0/when`): the form shows the message there
        const info = codecErrorOf(error);
        errors.push(thrown(info !== undefined && info.path.startsWith("/body") ? info.path : "/body", error));
      }
    }
  }
  if (op.requestBody.kind === "xml") {
    // the value the XML codec writes, edited as JSON like any body value; an XML body is never null (an optional one is left out)
    const bodyText = inputs.body;
    if (inputs.bodyJson === undefined && bodyText.trim().length === 0) {
      if (op.requestBody.presence === "required") {
        errors.push({ path: "/body", message: "request body is required", code: "body-required" });
      }
    } else {
      const codec = model.registry.registry.get(op.requestBody.use.codecId);
      try {
        const ast = inputs.bodyJson ?? parseJson(bodyText);
        if (codec.parseRequestInput === undefined) {
          errors.push({ path: "/body", message: `codec '${codec.id}' has no request-input capability`, code: "no-request-input" });
        } else {
          args["body"] = codec.parseRequestInput(ast, context.child("body"));
        }
      } catch (error) {
        const info = codecErrorOf(error);
        errors.push(thrown(info !== undefined && info.path.startsWith("/body") ? info.path : "/body", error));
      }
    }
  }
  if (op.requestBody.kind === "binary") {
    if (inputs.bodyBytes !== undefined) { args["body"] = inputs.bodyBytes; }
    else if (op.requestBody.presence === "required") { errors.push({ path: "/body", message: "request body is required", code: "body-required" }); }
  }
  if (op.requestBody.kind === "form") {
    const readFields = (fields: readonly ContractFormField[], prefix: string, context: CodecContext): Record<string, unknown> => {
      const body: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
      for (const field of fields) {
        const ctx = context.child(field.name);
        const path = ctx.path;
        const key = prefix + field.name;
        if (field.kind === "object") {
          const marker = inputs.formValues?.[key];
          if (marker === undefined && field.presence === "optional") { continue; }
          const count = field.repeated ? Number(marker ?? "0") : 1;
          if (!Number.isSafeInteger(count) || count < 1 || count > 1024) { errors.push({ path, message: "form collection requires 1–1024 items", code: "form-items" }); continue; }
          body[field.name] = field.repeated
            ? Array.from({ length: count }, (_, i) => readFields(field.fields ?? [], key + `[${i}].`, ctx.child(i)))
            : readFields(field.fields ?? [], key + ".", ctx);
        } else if (field.kind === "map") {
          // rows of a dictionary: the count at the field's key, each row's key and value text under a name no form field can have
          const marker = inputs.formValues?.[key];
          if (marker === undefined && field.presence === "optional") { continue; }
          const count = Number(marker ?? "0");
          if (!Number.isSafeInteger(count) || count < 1 || count > 1024) { errors.push({ path, message: "form dictionary requires 1–1024 entries", code: "form-items" }); continue; }
          const entries = new Map<unknown, unknown>();
          for (let i = 0; i < count; i++) {
            const keyText = inputs.formValues?.[mapEntryKey(key, i, "key")] ?? "";
            try {
              const entryKey = model.registry.registry.get(field.keyUse!.codecId).parseRequestInput!(keyText, ctx.child(keyText));
              if (entries.has(entryKey)) { errors.push({ path: ctx.child(keyText).path, message: "duplicate key", code: "duplicate-key" }); continue; }
              const codec = model.registry.registry.get(field.use!.codecId);
              if (codec.parseRequestInput === undefined) { throw new Error("form field codec has no request input"); }
              entries.set(entryKey, codec.parseRequestInput(inputs.formValues?.[mapEntryKey(key, i, "value")] ?? "", ctx.child(keyText)));
            } catch (error) { errors.push(thrown(ctx.child(keyText).path, error)); }
          }
          body[field.name] = entries;
        } else if (field.kind === "file") {
          const files = inputs.formFiles?.[key];
          if (files !== undefined && files.length > 0) { body[field.name] = field.repeated ? files : files[0]; }
          else if (field.presence === "required") { errors.push({ path, message: "required", code: "required" }); }
        } else {
          const text = inputs.formValues?.[key];
          if (text === undefined) {
            if (field.presence === "required") { errors.push({ path, message: "required", code: "required" }); }
            continue;
          }
          try {
            const codec = model.registry.registry.get(field.use!.codecId);
            if (codec.parseRequestInput === undefined) { throw new Error("form field codec has no request input"); }
            body[field.name] = field.repeated ? (text === "" ? [] : text.split("\n").map((line, i) => codec.parseRequestInput!(line, ctx.child(i)))) : codec.parseRequestInput(text, ctx);
          } catch (error) { errors.push(thrown(path, error)); }
        }
      }
      return body;
    };
    args["body"] = readFields(op.requestBody.fields, "", context.child("body"));
  }
  return { args, errors };
}

/** The draft key of a dictionary row's key or value: U+0001 cannot occur in a form field name, so it never meets one. */
export function mapEntryKey(fieldKey: string, row: number, part: "key" | "value"): string {
  return `${fieldKey}\u0001${row}\u0001${part}`;
}

/**
 * A codec error from the runtime or from a portable module: modules are self-contained and throw plain `Error`s named `CodecError`
 * with the same `code` and `path`, so they are recognised by shape rather than by class.
 */
export function codecErrorOf(error: unknown): { readonly code: string; readonly path: string; readonly message: string } | undefined {
  if (error instanceof CodecError) {
    return { code: error.code, path: error.path, message: error.message };
  }
  if (error instanceof Error && error.name === "CodecError") {
    const e = error as Error & { code?: unknown; path?: unknown };
    if (typeof e.code === "string" && typeof e.path === "string") {
      return { code: e.code, path: e.path, message: error.message };
    }
  }
  return undefined;
}

export function describeError(error: unknown): string {
  const codec = codecErrorOf(error);
  if (codec !== undefined) {
    return `${codec.code}${codec.path.length > 0 ? " at " + codec.path : ""}: ${codec.message}`;
  }
  if (error instanceof JsonParseError) {
    return `invalid JSON (${error.code}): ${error.message}`;
  }
  return error instanceof Error ? error.message : String(error);
}

// ---------------------------------------------------------------- execution

export interface ExecutionOptions {
  readonly baseUrl: string;
  readonly bearerToken?: string;
  /** Credentials from the Authorize dialog (memory only); sent through the runtime's credential provider, never previewed. */
  readonly credentials?: readonly Credential[];
  readonly expectedSemanticHash?: string;
  readonly semanticHashHeader?: string;
  readonly signal?: AbortSignal;
  /** Raw mode: send this body text instead of the generated request encoder's output. */
  readonly rawBody?: string;
  /** Runtime limits for this execution (body bytes, depth, tokens, number characters, timeout); a response beyond them is a limit failure, never a partial decode. */
  readonly limits?: Partial<{ readonly maxBodyBytes: number; readonly maxDepth: number; readonly maxTokens: number; readonly maxNumberCharacters: number; readonly timeoutMs: number; readonly maxDiagnosticBytes: number }>;
}

export interface Execution {
  readonly mode: "typed" | "raw";
  readonly prepared: PreparedRequest | undefined;
  readonly raw: RawOutcome | undefined;
  readonly result: OperationResult | undefined;
  readonly elapsedMs: number;
  readonly error: string | undefined;
}

export function clientOptions(options: ExecutionOptions): ClientOptions {
  const credentials: Credential[] = [...(options.bearerToken !== undefined && options.bearerToken.length > 0 ? [{ kind: "bearer", token: options.bearerToken } as const] : []), ...(options.credentials ?? [])];
  const headers = credentialHeaders(credentials);
  return {
    baseUrl: options.baseUrl,
    credentials: "same-origin",
    retainRawBody: true,
    ...(headers.length > 0 ? { credentialProvider: () => headers } : {}),
    ...(options.expectedSemanticHash !== undefined && options.semanticHashHeader !== undefined ? { expectedSemanticHash: options.expectedSemanticHash, semanticHashHeader: options.semanticHashHeader } : {}),
    ...(options.signal !== undefined ? { signal: options.signal } : {}),
    ...(options.limits !== undefined ? { limits: options.limits } : {}),
  };
}

/** Preview = the exact request the runtime writes; credentials are never part of the prepared request. */
export function preview(operation: OperationDescriptor, args: unknown, options: ExecutionOptions): PreparedRequest {
  return prepareRequest(operation, args, clientOptions(options));
}

export async function execute(operation: OperationDescriptor, args: unknown, options: ExecutionOptions): Promise<Execution> {
  const started = performance.now();
  const client = clientOptions(options);
  let prepared: PreparedRequest | undefined;
  try {
    prepared = prepareRequest(operation, args, client);
  } catch (error) {
    if (options.rawBody === undefined) {
      return { mode: "typed", prepared: undefined, raw: undefined, result: undefined, elapsedMs: performance.now() - started, error: describeError(error) };
    }
  }
  const mode: "typed" | "raw" = options.rawBody === undefined ? "typed" : "raw";
  if (mode === "raw") {
    const bytes = new TextEncoder().encode(options.rawBody!);
    const base = prepared ?? prepareRequest(operation, args, client);
    const headers = base.headers.filter(([n]) => n !== "content-type");
    if (operation.requestBody !== undefined) {
      headers.push(["content-type", operation.requestBody.mediaType]);
    }
    prepared = { ...base, headers, bodyText: options.rawBody!, bodyBytes: bytes };
  }
  const { raw, result } = await executeWithRaw(operation, args, client, prepared);
  return { mode, prepared, raw, result, elapsedMs: performance.now() - started, error: undefined };
}

/** The HTTP status that came back and the declared case it decoded as (either may be missing: no response, a failure). */
export function responseOf(execution: Execution): { readonly status: number | undefined; readonly caseId: string | undefined } {
  const result = execution.result;
  const raw = execution.raw;
  const metadata = raw === undefined ? undefined : raw.kind === "raw" ? raw.metadata : "metadata" in raw ? raw.metadata : undefined;
  return {
    status: result !== undefined && "status" in result ? (result as { status: number }).status : metadata?.status,
    caseId: result !== undefined && result.kind === "response" ? result.caseId : undefined,
  };
}

// ---------------------------------------------------------------- status

export interface ExecutionStatus {
  readonly level: "ok" | "failure" | "limit" | "cancelled" | "timeout" | "error";
  readonly text: string;
}

/** One line for the UI: what happened, with limit hits, cancellation and timeouts named explicitly. */
export function executionStatus(execution: Execution): ExecutionStatus {
  if (execution.error !== undefined) {
    return { level: "error", text: "not sent: " + execution.error };
  }
  const result = execution.result;
  if (result === undefined) {
    return { level: "error", text: "no result" };
  }
  switch (result.kind) {
    case "response":
      return { level: "ok", text: `response ${result.caseId} (${result.status}) in ${Math.round(execution.elapsedMs)} ms` };
    case "limit-failure":
      return { level: "limit", text: `limit reached: ${result.limit} — the response was not decoded` };
    case "cancelled":
      return { level: "cancelled", text: "cancelled before completion" };
    case "timeout":
      return { level: "timeout", text: `timed out after ${result.timeoutMs} ms` };
    case "transport-failure":
      return { level: "failure", text: `transport failure (${result.reason}): ${result.message}` };
    case "codec-failure":
      return { level: "failure", text: `codec failure ${result.code} at '${result.path}' (case ${result.caseId})` };
    case "contract-mismatch":
      return { level: "failure", text: "contract mismatch: the server reports another semantic hash" };
    default:
      return { level: "failure", text: result.kind };
  }
}

// ---------------------------------------------------------------- display (no rounding to number)

export interface DisplayNode {
  readonly label: string;
  readonly kind: string;
  readonly text?: string;
  readonly children?: readonly DisplayNode[];
}

const maxDepth = 24;
const maxChildren = 500;

/** Up to `maxChildren` children; past that, one marker saying how many were left out (the body's JSON or XML view shows every item). */
function childrenOf(entries: Iterable<readonly [string, unknown]>, total: number, depth: number, seen: Map<object, string>, path: string, wire: string): DisplayNode[] {
  const children: DisplayNode[] = [];
  for (const [key, value] of entries) {
    if (children.length >= maxChildren) {
      children.push({ label: "…", kind: "truncated", text: t().truncated(total - maxChildren, wire) });
      break;
    }
    children.push(describeValue(value, key, depth + 1, seen, path + "/" + key.replaceAll("~", "~0").replaceAll("/", "~1"), wire));
  }
  return children;
}

/**
 * A display tree of a decoded public value; BigInt/Decimal/dates keep their exact text. A value reached again — a shared or cyclic value
 * of a ReferenceHandler.Preserve response — is shown once and then as a reference to where it was (`#/children/0`). `wire` names the
 * view of the body as written (JSON, or XML for an XML case), which a list too long to show here points to.
 */
export function describeValue(value: unknown, label = "", depth = 0, seen = new Map<object, string>(), path = "#", wire = "JSON"): DisplayNode {
  if (depth > maxDepth) {
    return { label, kind: "truncated", text: "…" };
  }
  if (value === null) {
    return { label, kind: "null", text: "null" };
  }
  if (value === undefined) {
    return { label, kind: "absent", text: "(absent)" };
  }
  switch (typeof value) {
    case "string":
      return { label, kind: "string", text: value };
    case "number":
      return { label, kind: "number", text: Object.is(value, -0) ? "-0" : String(value) };
    case "bigint":
      return { label, kind: "int64", text: value.toString() };
    case "boolean":
      return { label, kind: "boolean", text: value ? "true" : "false" };
    case "object":
      break;
    default:
      return { label, kind: typeof value, text: String(value) };
  }
  if (value instanceof Uint8Array) {
    return { label, kind: "bytes", text: encodeBase64(value) };
  }
  if (isDecimal(value)) {
    return { label, kind: "decimal", text: formatDecimal(value) };
  }
  const first = seen.get(value);
  if (first !== undefined) {
    return { label, kind: "reference", text: t().sameValue(first) };
  }
  if (value instanceof TisiliaMap) {
    const map = value;
    seen.set(value, path);
    return { label, kind: "map", children: childrenOf((function* () { for (const [k, v] of map.entries()) yield [describeValue(k).text ?? "?", v] as const; })(), map.size, depth, seen, path, wire) };
  }
  if (value instanceof Map) {
    const map = value;
    seen.set(value, path);
    return { label, kind: "map", children: childrenOf((function* () { for (const [k, v] of map) yield [String(k), v] as const; })(), map.size, depth, seen, path, wire) };
  }
  if (Array.isArray(value)) {
    const items = value;
    seen.set(value, path);
    return { label, kind: "array", children: childrenOf((function* () { for (let i = 0; i < items.length; i++) yield [String(i), items[i]] as const; })(), items.length, depth, seen, path, wire) };
  }
  const record = value as Record<string, unknown>;
  const kind = record["kind"];
  if (typeof kind === "string") {
    switch (kind) {
      case "date-only":
        return { label, kind, text: formatDateOnly(value as never) };
      case "time-only":
        return { label, kind, text: formatTimeOnly(value as never) };
      case "datetime-utc":
        return { label, kind, text: formatDateTimeUtc(value as never) };
      case "datetime-unspecified":
        return { label, kind, text: formatDateTimeUnspecified(value as never) };
      case "datetime-local-wire":
        return { label, kind, text: formatDateTimeLocalWire(value as never) };
      case "datetime-offset":
        return { label, kind, text: formatDateTimeOffset(value as never) };
      case "duration":
        return { label, kind, text: formatDuration(value as never) };
      case "null":
      case "boolean":
      case "string":
      case "number":
      case "array":
      case "object":
        if ("entries" in record || "items" in record || "value" in record || "text" in record || kind === "null") {
          return { label, kind: "json-value", text: writeJson(value as JsonValue) };
        }
        break;
      default:
        break;
    }
  }
  const members = Object.entries(record);
  seen.set(value, path);
  return { label, kind: "object", children: childrenOf(members, members.length, depth, seen, path, wire) };
}

// ---------------------------------------------------------------- redaction

const builtinSecretHeaders = new Set(["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-csrf-token"]);
const knownSafeHeaders = new Set(["content-type", "content-length", "date", "etag", "cache-control", "vary", "x-tisilia-contract", "location", "allow", "accept", "accept-language", "user-agent"]);

export type RedactionMode = "mask" | "reveal";

/** Header display: builtin secrets are never revealed; other headers follow the default (mask) unless revealed for this session. */
export function redactHeaders(headers: readonly (readonly [string, string])[], mode: RedactionMode): { name: string; value: string; masked: boolean }[] {
  return headers.map(([name, value]) => {
    const lower = name.toLowerCase();
    if (builtinSecretHeaders.has(lower)) {
      return { name, value: "•••", masked: true };
    }
    if (mode === "reveal" || knownSafeHeaders.has(lower)) {
      return { name, value, masked: false };
    }
    return { name, value: "•••", masked: true };
  });
}

/** Value display: mask leaves (default) unless revealed; structure and types stay visible. */
export function redactTree(node: DisplayNode, mode: RedactionMode): DisplayNode {
  if (mode === "reveal") {
    return node;
  }
  if (node.children !== undefined) {
    return { ...node, children: node.children.map((c) => redactTree(c, mode)) };
  }
  // a reference names a place in the same tree, not a value
  if (node.kind === "null" || node.kind === "absent" || node.kind === "boolean" || node.kind === "truncated" || node.kind === "reference") {
    return node;
  }
  return { ...node, text: "•••" };
}

/**
 * JSON laid out with two-space indentation from the lossless AST — number lexemes as the server wrote them, never through a
 * JavaScript number. Masked, strings and numbers read ••• (as in the JSON tree); structure, booleans and null stay.
 */
export function prettyWire(value: JsonValue, mode: RedactionMode): string {
  const masked = mode !== "reveal";
  const walk = (v: JsonValue, indent: string): string => {
    switch (v.kind) {
      case "object": {
        if (v.entries.length === 0) {
          return "{}";
        }
        const inner = indent + "  ";
        return "{\n" + v.entries.map((e) => inner + writeJson({ kind: "string", value: e.name }) + ": " + walk(e.value, inner)).join(",\n") + "\n" + indent + "}";
      }
      case "array": {
        if (v.items.length === 0) {
          return "[]";
        }
        const inner = indent + "  ";
        return "[\n" + v.items.map((item) => inner + walk(item, inner)).join(",\n") + "\n" + indent + "]";
      }
      case "string":
        return masked ? '"•••"' : writeJson(v);
      case "number":
        return masked ? "•••" : v.text;
      case "boolean":
        return v.value ? "true" : "false";
      case "null":
        return "null";
    }
  };
  return walk(value, "");
}

/**
 * Where the API is: the page's URL up to the Explorer's route (`/__tisilia` unless configured), which the host names in a meta
 * element. It keeps what precedes the route — a PathBase (IIS virtual application, X-Forwarded-Prefix) or a proxy's prefix the
 * application never sees — where the origin alone would call `/items/1` instead of `/app/items/1`. Without the element (or when
 * the page is not below the route) the origin.
 */
export function apiBaseOf(pageUrl: string, explorerRoute: string | undefined): string {
  const page = new URL(pageUrl);
  if (explorerRoute === undefined) {
    return page.origin;
  }
  const route = explorerRoute.replace(/\/+$/, "");
  const directory = page.pathname.slice(0, page.pathname.lastIndexOf("/"));
  if (route === "" || route === "/") {
    return page.origin + directory;
  }
  return directory.endsWith(route) ? page.origin + directory.slice(0, directory.length - route.length) : page.origin;
}

export type CopyOutcome = { readonly copied: true } | { readonly copied: false; readonly reason: string };

/**
 * Writes text to the clipboard, or says why it could not: `navigator.clipboard` is `[SecureContext]` (W3C Clipboard API), so an
 * Explorer opened over plain HTTP from anything but localhost has none, and `writeText` rejects without permission or focus.
 */
export async function copyToClipboard(text: string, clipboard: Pick<Clipboard, "writeText"> | undefined): Promise<CopyOutcome> {
  if (clipboard === undefined) {
    return { copied: false, reason: t().clipboardInsecure };
  }
  try {
    await clipboard.writeText(text);
    return { copied: true };
  } catch (error) {
    return { copied: false, reason: t().clipboardRefused(error instanceof Error ? error.message : String(error)) };
  }
}
