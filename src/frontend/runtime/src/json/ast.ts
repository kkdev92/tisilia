/**
 * Lossless JSON AST. Numbers keep their RFC 8259 lexeme, objects keep their entries in order and
 * may contain duplicate names (policies are applied by codecs, never by the parser). Nothing here is a prototype-bearing
 * object keyed by JSON names, so `__proto__` and friends cannot pollute anything.
 */

export type JsonToken = "null" | "boolean" | "string" | "number" | "array" | "object";

export interface JsonNull {
  readonly kind: "null";
}

export interface JsonBoolean {
  readonly kind: "boolean";
  readonly value: boolean;
}

export interface JsonString {
  readonly kind: "string";
  /** Unescaped value; may contain lone surrogates when the input escaped them (codecs decide). */
  readonly value: string;
}

export interface JsonNumber {
  readonly kind: "number";
  /** Exact lexeme matching `-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?`. */
  readonly text: string;
}

export interface JsonArray {
  readonly kind: "array";
  readonly items: readonly JsonValue[];
}

export interface JsonEntry {
  readonly name: string;
  readonly value: JsonValue;
}

export interface JsonObject {
  readonly kind: "object";
  readonly entries: readonly JsonEntry[];
}

export type JsonValue = JsonNull | JsonBoolean | JsonString | JsonNumber | JsonArray | JsonObject;

export const jsonNull: JsonNull = Object.freeze({ kind: "null" });

export function jsonBoolean(value: boolean): JsonBoolean {
  return { kind: "boolean", value };
}

export function jsonString(value: string): JsonString {
  return { kind: "string", value };
}

const numberLexeme = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/;

/** Creates a number node after checking the RFC 8259 grammar; throws on an invalid lexeme. */
export function jsonNumber(text: string): JsonNumber {
  if (!numberLexeme.test(text)) {
    throw new TypeError("invalid JSON number lexeme");
  }
  return { kind: "number", text };
}

export function isNumberLexeme(text: string): boolean {
  return numberLexeme.test(text);
}

export function jsonArray(items: readonly JsonValue[]): JsonArray {
  return { kind: "array", items };
}

export function jsonObject(entries: readonly JsonEntry[]): JsonObject {
  return { kind: "object", entries };
}

export function jsonEntry(name: string, value: JsonValue): JsonEntry {
  return { name, value };
}

/** First entry with the given name under ordinal comparison, or undefined. */
export function findEntry(object: JsonObject, name: string): JsonEntry | undefined {
  for (const entry of object.entries) {
    if (entry.name === name) {
      return entry;
    }
  }
  return undefined;
}

/** Structural equality of two AST values (lexeme-exact for numbers, entry-order-sensitive for objects). */
export function jsonEquals(a: JsonValue, b: JsonValue): boolean {
  if (a.kind !== b.kind) {
    return false;
  }
  switch (a.kind) {
    case "null":
      return true;
    case "boolean":
      return a.value === (b as JsonBoolean).value;
    case "string":
      return a.value === (b as JsonString).value;
    case "number":
      return a.text === (b as JsonNumber).text;
    case "array": {
      const other = b as JsonArray;
      return a.items.length === other.items.length && a.items.every((item, i) => jsonEquals(item, other.items[i]!));
    }
    case "object": {
      const other = b as JsonObject;
      return (
        a.entries.length === other.entries.length &&
        a.entries.every((entry, i) => entry.name === other.entries[i]!.name && jsonEquals(entry.value, other.entries[i]!.value))
      );
    }
  }
}

/**
 * Converts a plain JavaScript value into an AST. Numbers become shortest round-trip lexemes, bigints exact integers;
 * NaN/Infinity/undefined/functions/symbols are rejected. Own enumerable string keys are used in insertion order.
 */
export function fromNative(value: unknown): JsonValue {
  if (value === null) {
    return jsonNull;
  }
  switch (typeof value) {
    case "boolean":
      return jsonBoolean(value);
    case "string":
      return jsonString(value);
    case "number":
      if (!Number.isFinite(value)) {
        throw new TypeError("non-finite numbers are not JSON");
      }
      return { kind: "number", text: formatDoubleLexeme(value) };
    case "bigint":
      return { kind: "number", text: value.toString() };
    case "object":
      if (Array.isArray(value)) {
        return jsonArray(value.map(fromNative));
      }
      if (value instanceof Map) {
        const entries: JsonEntry[] = [];
        for (const [k, v] of value) {
          if (typeof k !== "string") {
            throw new TypeError("map keys must be strings");
          }
          entries.push(jsonEntry(k, fromNative(v)));
        }
        return jsonObject(entries);
      }
      return jsonObject(
        Object.keys(value as Record<string, unknown>).map((k) => jsonEntry(k, fromNative((value as Record<string, unknown>)[k]))),
      );
    default:
      throw new TypeError(`cannot convert ${typeof value} to JSON`);
  }
}

/** ECMAScript Number::toString adjusted to RFC 8259 grammar (JS already emits `1e+21` / `1e-7`, both valid JSON). */
export function formatDoubleLexeme(value: number): string {
  if (Object.is(value, -0)) {
    return "-0";
  }
  return String(value);
}
