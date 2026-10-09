import type { JsonEntry, JsonObject, JsonValue } from "../json/ast.js";
import { CodecError } from "./errors.js";

/**
 * The values of one call that ReferenceHandler.Preserve can share. System.Text.Json numbers the values it writes from "1" in each
 * serialization (one response, one server-sent event) and writes `{"$ref": id}` for a value it wrote before; it reads a request the
 * same way. A codec context and its children share one graph, which is cleared when the outermost structural codec call returns.
 */
export class ReferenceGraph {
  /** Structural codec calls in progress on this graph. */
  depth = 0;
  /** Decoding: each id read and the value made for it, with the codec that made it. */
  readonly decoded = new Map<string, { readonly value: unknown; readonly codecId: string }>();
  /** Validation: each value given and its validated copy, per codec (one value can be validated by several codecs). */
  readonly validated = new Map<object, Map<string, unknown>>();
  /** Encoding: each validated value written with an id, per codec. */
  readonly written = new Map<object, Map<string, string>>();
  /** Encoding: the values whose members are being written; reaching one of them again is a value that contains itself. */
  readonly active = new Set<object>();
  /** Encoding: the objects written with `$id` first (a collection as `{"$id", "$values"}`). */
  readonly carriers = new Map<JsonObject, { readonly id: string; readonly collection: boolean }>();
  /** Encoding: the ids a `$ref` names; the other `$id`s are removed before the request is returned. */
  readonly referenced = new Set<string>();
  nextId = 1;

  clear(): void {
    this.decoded.clear();
    this.validated.clear();
    this.written.clear();
    this.active.clear();
    this.carriers.clear();
    this.referenced.clear();
    this.nextId = 1;
  }
}

/** Runs one structural codec call on the context's graph; the outermost call sees `outermost` and leaves the graph cleared. */
export function inGraph<T>(graph: ReferenceGraph, run: (outermost: boolean) => T): T {
  const outermost = graph.depth === 0;
  graph.depth++;
  try {
    return run(outermost);
  } finally {
    graph.depth--;
    if (graph.depth === 0) {
      graph.clear();
    }
  }
}

/** The validated copy of a value for one codec, made once per call: shared values stay shared and a cycle stays a cycle. */
export function validatedCopy<T extends object>(graph: ReferenceGraph, value: object, codecId: string, create: () => T, fill: (copy: T) => void): T {
  const byCodec = graph.validated.get(value);
  const known = byCodec?.get(codecId);
  if (known !== undefined) {
    return known as T;
  }
  const copy = create();
  remember(graph, value, codecId, copy);
  // the copy validates to itself: an encoder validates the values it was given again
  remember(graph, copy, codecId, copy);
  fill(copy);
  return copy;
}

function remember(graph: ReferenceGraph, value: object, codecId: string, copy: object): void {
  let byCodec = graph.validated.get(value);
  if (byCodec === undefined) {
    byCodec = new Map();
    graph.validated.set(value, byCodec);
  }
  byCodec.set(codecId, copy);
}

/** Registers the value a decoder made for `$id` before it reads the value's members, so that they can refer to it. */
export function registerDecoded(graph: ReferenceGraph, id: string, value: unknown, codecId: string, path: string): void {
  if (graph.decoded.has(id)) {
    throw new CodecError("type-mismatch", path, "the server wrote one \"$id\" twice (ReferenceHandler.Preserve)", codecId);
  }
  graph.decoded.set(id, { value, codecId });
}

/**
 * The value a `{"$ref": id}` object names: one the server wrote before in this response, made by one of `codecIds` (a union accepts the
 * codecs of its variants). A value first written at a position of another type has another shape here, so it is refused.
 */
export function resolveDecoded(graph: ReferenceGraph, wire: JsonObject, codecIds: readonly string[], ownerId: string, path: string): unknown {
  const ref = wire.entries[0]!;
  if (wire.entries.length !== 1 || ref.value.kind !== "string") {
    throw new CodecError("type-mismatch", path, "an object with \"$ref\" has no other property and names an id as a string (ReferenceHandler.Preserve)", ownerId);
  }
  const known = graph.decoded.get(ref.value.value);
  if (known === undefined) {
    throw new CodecError("type-mismatch", path, "the server referred to an id it had not written before (ReferenceHandler.Preserve)", ownerId);
  }
  if (!codecIds.includes(known.codecId)) {
    throw new CodecError("unsupported", path, "the server referred to a value it wrote first at a position of another type, which has another shape here; Tisilia reads a reference only where the value has the type of its first position", ownerId);
  }
  return known.value;
}

/**
 * The id a request writes for a value it reaches at a position whose server read accepts reference metadata: an existing id (the
 * caller writes `{"$ref": id}`) when the value was written before with the same codec, else a new id (the caller writes `$id` first).
 * A value of another codec is written again; a value that contains itself is refused where no reference can stand for it.
 */
export function writeReference(graph: ReferenceGraph, value: object, codecId: string, referable: boolean, path: string): { readonly ref: string } | { readonly id: string | undefined } {
  const written = graph.written.get(value)?.get(codecId);
  if (referable && written !== undefined) {
    graph.referenced.add(written);
    return { ref: written };
  }
  if (graph.active.has(value)) {
    throw new CodecError("unsupported", path, "the value contains itself here, and the server reads no reference at this position (an array, an immutable collection, a struct or a type built through its constructor); only a reference can stand for a value inside itself", codecId);
  }
  if (!referable) {
    return { id: undefined };
  }
  const id = String(graph.nextId++);
  let byCodec = graph.written.get(value);
  if (byCodec === undefined) {
    byCodec = new Map();
    graph.written.set(value, byCodec);
  }
  byCodec.set(codecId, id);
  return { id };
}

/** Writes `$id` first: `{"$id": id, …entries}`, or `{"$id": id, "$values": [...]}` for a collection. */
export function carrier(graph: ReferenceGraph, id: string, entries: JsonEntry[], collection: boolean): JsonObject {
  const node: JsonObject = { kind: "object", entries: [{ name: "$id", value: { kind: "string", value: id } }, ...entries] };
  graph.carriers.set(node, { id, collection });
  return node;
}

/** The request without the `$id`s no `$ref` names: an object loses its first entry and a collection becomes its plain array. */
export function withoutUnusedIds(graph: ReferenceGraph, value: JsonValue): JsonValue {
  if (graph.carriers.size === 0 || [...graph.carriers.values()].every((c) => graph.referenced.has(c.id))) {
    return value;
  }
  const strip = (v: JsonValue): JsonValue => {
    if (v.kind === "array") {
      let changed = false;
      const items = v.items.map((item) => {
        const stripped = strip(item);
        changed ||= stripped !== item;
        return stripped;
      });
      return changed ? { kind: "array", items } : v;
    }
    if (v.kind !== "object") {
      return v;
    }
    const carried = graph.carriers.get(v);
    if (carried !== undefined && !graph.referenced.has(carried.id)) {
      if (carried.collection) {
        return strip(v.entries[1]!.value);
      }
      return { kind: "object", entries: v.entries.slice(1).map((e) => ({ name: e.name, value: strip(e.value) })) };
    }
    let changed = false;
    const entries = v.entries.map((e) => {
      const stripped = strip(e.value);
      changed ||= stripped !== e.value;
      return stripped === e.value ? e : { name: e.name, value: stripped };
    });
    return changed ? { kind: "object", entries } : v;
  };
  return strip(value);
}
