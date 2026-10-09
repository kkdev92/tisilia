import type { JsonValue } from "../json/ast.js";
import { resolveLimits, type Limits } from "../json/limits.js";
import { CodecError, indexPath, propertyPath } from "./errors.js";
import { ReferenceGraph } from "./graph.js";

/**
 * Codec ABI 0.1. Each capability is an independent method; a codec implements only the ones it
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
  /** Optional CLR key identity when wire spelling differs from equality (DateTime.Kind is ignored by .NET). */
  keyIdentity?(value: K, context: CodecContext): string;
  /** Refuses key combinations whose server-side identity cannot be established from the contract. */
  validateKeySet?(values: readonly K[], context: CodecContext): void;
}

export interface ResponseKeyCodec<K> {
  decodeKey(value: string, context: CodecContext): K;
  /**
   * Optional identity of a key the server wrote, when it differs from keyIdentity (the key as the server reads it): a DateTime key written
   * with the server's offset is equal to the server's other keys by the ticks written, but read back it is converted again.
   */
  responseKeyIdentity?(value: K, context: CodecContext): string;
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
    readonly graph: ReferenceGraph,
  ) {}

  child(segment: string | number): CodecContext {
    return new Context(this.path + (typeof segment === "number" ? indexPath(segment) : propertyPath(segment)), this.profileId, this.limits, this.context, this.checkpoint, this.graph);
  }
}

const foreignGraphs = new WeakMap<CodecContext, ReferenceGraph>();

/**
 * The reference graph a context and its children share (ReferenceHandler.Preserve values of one call). A context that does not come
 * from createCodecContext gets a graph of its own.
 */
export function graphOf(context: CodecContext): ReferenceGraph {
  if (context instanceof Context) {
    return context.graph;
  }
  let graph = foreignGraphs.get(context);
  if (graph === undefined) {
    graph = new ReferenceGraph();
    foreignGraphs.set(context, graph);
  }
  return graph;
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
  return new Context("", options.profileId ?? "", limits, map, checkpoint, new ReferenceGraph());
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
  return new Context(context.path, context.profileId, context.limits, merged, context.checkpoint, graphOf(context));
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
