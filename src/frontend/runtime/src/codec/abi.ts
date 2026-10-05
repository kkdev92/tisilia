import type { JsonValue } from "../json/ast.js";
import { resolveLimits, type Limits } from "../json/limits.js";
import { CodecError, indexPath, propertyPath } from "./errors.js";

/**
 * Codec ABI 0.3. Each capability is an independent method; a codec implements only the ones it
 * supports and the contract records which ones exist. Nothing here receives HttpContext, tokens or secrets.
 */
export interface CodecContext {
  /** RFC 6901 pointer of the value being processed (for diagnostics). */
  readonly path: string;
  readonly profileId: string;
  readonly limits: Limits;
  /** Non-secret declared context entries (culture, timezone …) bound to the operation's bindings. */
  readonly context: ReadonlyMap<string, string>;
  /** Cooperative cancellation / deadline check; throws CodecError("cancelled") when work must stop. */
  readonly checkpoint: () => void;
  child(segment: string | number): CodecContext;
}

export interface RequestCodec<T> {
  encodeRequest(value: T, context: CodecContext): JsonValue;
}

export interface ResponseCodec<T> {
  decodeResponse(wire: JsonValue, context: CodecContext): T;
}

export interface RequestKeyCodec<K> {
  encodeKey(value: K, context: CodecContext): string;
}

export interface ResponseKeyCodec<K> {
  decodeKey(value: string, context: CodecContext): K;
}

export interface RequestInputCodec<T> {
  /** Explorer-only: builds a domain value from editor text or an AST (never reuses the response decoder). */
  parseRequestInput(input: string | JsonValue, context: CodecContext): T;
}

export interface DomainValidator<T> {
  /** Validates and normalizes a public value; throws CodecError with the failing path. */
  validateDomain(value: unknown, context: CodecContext): T;
}

/** A registered codec: identity plus whichever capabilities exist. Generated registries expose these objects. */
export interface Codec<T = unknown, K = T> extends DomainValidator<T>, Partial<RequestCodec<T>>, Partial<ResponseCodec<T>>, Partial<RequestKeyCodec<K>>, Partial<ResponseKeyCodec<K>>, Partial<RequestInputCodec<T>> {
  readonly id: string;
  readonly typeId: string;
  /**
   * A JSON null on the wire is a value of this codec's domain, not the absence of one: lossless json-value (JsonElement, object and
   * JsonNode members without `?`) and token unions with a null branch. Structural codecs then pass the token to the codec instead of
   * mapping it to semantic null (the contract's rule: a wire admits null when it has a null branch or is lossless JSON).
   */
  readonly ownsNullToken?: boolean;
}

export interface CodecContextOptions {
  readonly profileId?: string;
  readonly limits?: Partial<Limits>;
  readonly context?: ReadonlyMap<string, string> | Record<string, string>;
  readonly signal?: AbortSignal;
  readonly deadline?: number;
  readonly now?: () => number;
}

class Context implements CodecContext {
  constructor(
    readonly path: string,
    readonly profileId: string,
    readonly limits: Limits,
    readonly context: ReadonlyMap<string, string>,
    readonly checkpoint: () => void,
  ) {}

  child(segment: string | number): CodecContext {
    return new Context(this.path + (typeof segment === "number" ? indexPath(segment) : propertyPath(segment)), this.profileId, this.limits, this.context, this.checkpoint);
  }
}

export function createCodecContext(options: CodecContextOptions = {}): CodecContext {
  const limits: Limits = resolveLimits(options.limits);
  const map = options.context instanceof Map ? options.context : new Map(Object.entries(options.context ?? {}));
  const signal = options.signal;
  const deadline = options.deadline;
  const checkpoint = (): void => {
    if (signal?.aborted) {
      throw new CodecError("cancelled", "", "operation was cancelled");
    }
    if (deadline !== undefined && (options.now ?? Date.now)() >= deadline) {
      throw new CodecError("cancelled", "", "deadline exceeded");
    }
  };
  return new Context("", options.profileId ?? "", limits, map, checkpoint);
}

/**
 * A context carrying additional non-secret entries (a binding's declared context, e.g. the converter instance's settings):
 * the entries are added to the operation-level context; path, profile, limits and checkpoint are shared.
 */
export function withContext(context: CodecContext, entries: Readonly<Record<string, string>>): CodecContext {
  const merged = new Map(context.context);
  for (const [name, value] of Object.entries(entries)) {
    merged.set(name, value);
  }
  return new Context(context.path, context.profileId, context.limits, merged, context.checkpoint);
}

export function requireRequest<T>(codec: Codec<T>): RequestCodec<T> {
  if (codec.encodeRequest === undefined) {
    throw new CodecError("unsupported", "", `codec '${codec.id}' has no request capability`, codec.id);
  }
  return codec as RequestCodec<T>;
}

export function requireResponse<T>(codec: Codec<T>): ResponseCodec<T> {
  if (codec.decodeResponse === undefined) {
    throw new CodecError("unsupported", "", `codec '${codec.id}' has no response capability`, codec.id);
  }
  return codec as ResponseCodec<T>;
}
