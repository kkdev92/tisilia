import { type JsonEntry, type JsonValue, jsonNull } from "../json/ast.js";
import type { Codec, CodecContext } from "./abi.js";
import { CodecError } from "./errors.js";
import { TisiliaMap, type KeyComparer } from "./map.js";
import { ordinalEqualsIgnoreCase, ordinalUpper } from "../primitives/ordinalCasing.js";

/** Lazy codec reference so that recursive types do not recurse at module initialization. */
export type CodecRef<T> = Codec<T> | (() => Codec<T>);

function resolve<T>(ref: CodecRef<T>): Codec<T> {
  return typeof ref === "function" ? ref() : ref;
}

export type Presence = "required" | "optional";
export type NameMatching = "ordinal" | "ordinal-ignore-case";
export type DuplicatePolicy = "reject" | "last-wins";
export type AdditionalPolicy = "reject" | "ignore" | "capture";

export interface PropertyDescriptor {
  /** Effective JSON name (already resolved from CLR name / naming policy by the exporter). */
  readonly name: string;
  readonly codec: CodecRef<unknown>;
  /** Presence in the public domain object. */
  readonly presence: Presence;
  /** Presence on the server-read wire (request). */
  readonly readPresence?: Presence;
  /** Presence on the server-write wire (response). */
  readonly writePresence?: Presence;
  readonly nullable: boolean;
}

export interface ObjectCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly properties: readonly PropertyDescriptor[];
  readonly nameMatching: NameMatching;
  readonly duplicates: DuplicatePolicy;
  readonly readAdditional: AdditionalPolicy;
  readonly writeAdditional: AdditionalPolicy;
  /** Codec for captured extension values (JsonExtensionData); required when either additional policy is "capture". */
  readonly extension?: CodecRef<unknown>;
  /** Property name that receives captured extension entries on the public object. */
  readonly extensionProperty?: string;
  readonly request: boolean;
  readonly response: boolean;
}

export type Extension = ReadonlyMap<string, unknown>;

/** Null-prototype record so that JSON names can never reach Object.prototype. */
function createRecord(): Record<string, unknown> {
  return Object.create(null) as Record<string, unknown>;
}

function matchesName(matching: NameMatching, a: string, b: string): boolean {
  if (a === b) {
    return true;
  }
  if (matching === "ordinal-ignore-case") {
    // System.Text.Json matches names with StringComparer.OrdinalIgnoreCase (simple case mapping of .NET's tables)
    return ordinalEqualsIgnoreCase(a, b);
  }
  return false;
}

/**
 * Builtin object codec: domain properties map to same-named wire properties in both directions.
 * Missing optional properties are absent (not undefined); own-property `undefined` is diagnosed.
 */
export function objectCodec<T extends object>(d: ObjectCodecDescriptor): Codec<T> {
  const props = d.properties;
  const validate = (value: unknown, ctx: CodecContext): T => {
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      throw new CodecError("type-mismatch", ctx.path, "object required", d.id);
    }
    const out = createRecord();
    const source = value as Record<string, unknown>;
    for (const p of props) {
      const has = Object.prototype.hasOwnProperty.call(source, p.name);
      if (!has) {
        if (p.presence === "required") {
          throw new CodecError("missing-required", ctx.child(p.name).path, `required property '${p.name}' is missing`, d.id);
        }
        continue;
      }
      const v = source[p.name];
      if (v === undefined) {
        throw new CodecError("undefined-not-allowed", ctx.child(p.name).path, `property '${p.name}' is undefined; omit it or set null`, d.id);
      }
      if (v === null) {
        if (!p.nullable) {
          throw new CodecError("null-not-allowed", ctx.child(p.name).path, `property '${p.name}' does not allow null`, d.id);
        }
        out[p.name] = null;
        continue;
      }
      out[p.name] = resolve(p.codec).validateDomain(v, ctx.child(p.name));
    }
    if (d.extensionProperty !== undefined && d.extension !== undefined && Object.prototype.hasOwnProperty.call(source, d.extensionProperty)) {
      const ext = source[d.extensionProperty];
      if (!(ext instanceof Map)) {
        throw new CodecError("type-mismatch", ctx.child(d.extensionProperty).path, "extension data must be a Map", d.id);
      }
      const extCodec = resolve(d.extension);
      const copy = new Map<string, unknown>();
      for (const [k, v] of ext) {
        if (props.some((p) => matchesName(d.nameMatching, p.name, k))) {
          throw new CodecError("unexpected-property", ctx.child(k).path, `extension entry '${k}' collides with a known property`, d.id);
        }
        copy.set(k, extCodec.validateDomain(v, ctx.child(k)));
      }
      out[d.extensionProperty] = copy;
    }
    return out as T;
  };

  const encodeRequest = d.request
    ? (value: T, ctx: CodecContext): JsonValue => {
        ctx.checkpoint();
        const validated = validate(value, ctx) as Record<string, unknown>;
        const entries: JsonEntry[] = [];
        for (const p of props) {
          const has = Object.prototype.hasOwnProperty.call(validated, p.name);
          if (!has) {
            if ((p.readPresence ?? p.presence) === "required") {
              throw new CodecError("missing-required", ctx.child(p.name).path, `the server requires '${p.name}'`, d.id);
            }
            continue;
          }
          const v = validated[p.name];
          if (v === null) {
            entries.push({ name: p.name, value: jsonNull });
            continue;
          }
          const codec = resolve(p.codec);
          if (codec.encodeRequest === undefined) {
            throw new CodecError("unsupported", ctx.child(p.name).path, `codec '${codec.id}' has no request capability`, codec.id);
          }
          entries.push({ name: p.name, value: codec.encodeRequest(v, ctx.child(p.name)) });
        }
        if (d.extensionProperty !== undefined && d.extension !== undefined && d.readAdditional === "capture") {
          const ext = validated[d.extensionProperty] as Map<string, unknown> | undefined;
          if (ext !== undefined) {
            const extCodec = resolve(d.extension);
            if (extCodec.encodeRequest === undefined) {
              throw new CodecError("unsupported", ctx.path, `extension codec '${extCodec.id}' has no request capability`, extCodec.id);
            }
            for (const [k, v] of ext) {
              entries.push({ name: k, value: extCodec.encodeRequest(v, ctx.child(k)) });
            }
          }
        }
        return { kind: "object", entries };
      }
    : undefined;

  const decodeResponse = d.response
    ? (wire: JsonValue, ctx: CodecContext): T => {
        ctx.checkpoint();
        if (wire.kind !== "object") {
          throw new CodecError("type-mismatch", ctx.path, `object required but found ${wire.kind}`, d.id);
        }
        const out = createRecord();
        const seen = new Set<string>();
        let extension: Map<string, unknown> | undefined;
        for (const entry of wire.entries) {
          const p = props.find((x) => matchesName(d.nameMatching, x.name, entry.name));
          if (p === undefined) {
            switch (d.writeAdditional) {
              case "reject":
                throw new CodecError("unexpected-property", ctx.child(entry.name).path, `unexpected property '${entry.name}'`, d.id);
              case "ignore":
                continue;
              case "capture": {
                if (d.extension === undefined) {
                  throw new CodecError("unsupported", ctx.path, "capture policy without extension codec", d.id);
                }
                extension ??= new Map();
                if (extension.has(entry.name)) {
                  if (d.duplicates === "reject") {
                    throw new CodecError("duplicate-property", ctx.child(entry.name).path, `duplicate property '${entry.name}'`, d.id);
                  }
                }
                const extCodec = resolve(d.extension);
                if (extCodec.decodeResponse === undefined) {
                  throw new CodecError("unsupported", ctx.path, `extension codec '${extCodec.id}' has no response capability`, extCodec.id);
                }
                extension.set(entry.name, extCodec.decodeResponse(entry.value, ctx.child(entry.name)));
                continue;
              }
            }
          }
          if (seen.has(p.name)) {
            if (d.duplicates === "reject") {
              throw new CodecError("duplicate-property", ctx.child(entry.name).path, `duplicate property '${entry.name}'`, d.id);
            }
            // last-wins: fall through and overwrite
          }
          seen.add(p.name);
          if (entry.value.kind === "null" && !(resolve(p.codec).ownsNullToken === true && !p.nullable)) {
            if (!p.nullable) {
              throw new CodecError("null-not-allowed", ctx.child(p.name).path, `property '${p.name}' is null but the public type is not nullable`, d.id);
            }
            out[p.name] = null;
            continue;
          }
          const codec = resolve(p.codec);
          if (codec.decodeResponse === undefined) {
            throw new CodecError("unsupported", ctx.child(p.name).path, `codec '${codec.id}' has no response capability`, codec.id);
          }
          out[p.name] = codec.decodeResponse(entry.value, ctx.child(p.name));
        }
        for (const p of props) {
          if (!seen.has(p.name) && (p.writePresence ?? p.presence) === "required") {
            throw new CodecError("missing-required", ctx.child(p.name).path, `required property '${p.name}' is missing`, d.id);
          }
        }
        if (extension !== undefined && d.extensionProperty !== undefined) {
          out[d.extensionProperty] = extension;
        }
        return out as T;
      }
    : undefined;

  const codec: Codec<T> = {
    id: d.id,
    typeId: d.typeId,
    validateDomain: validate,
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        throw new CodecError("unsupported", ctx.path, "object input requires a JSON value editor", d.id);
      }
      // Explorer typed input: decode through the *request* wire shape using the same codecs the encoder uses.
      return validate(inputToDomain(input, ctx), ctx);
    },
  };
  if (encodeRequest !== undefined) {
    (codec as { encodeRequest?: typeof encodeRequest }).encodeRequest = encodeRequest;
  }
  if (decodeResponse !== undefined) {
    (codec as { decodeResponse?: typeof decodeResponse }).decodeResponse = decodeResponse;
  }
  return codec;

  function inputToDomain(input: JsonValue, ctx: CodecContext): unknown {
    if (input.kind !== "object") {
      throw new CodecError("type-mismatch", ctx.path, "object required", d.id);
    }
    const out = createRecord();
    for (const entry of input.entries) {
      const p = props.find((x) => matchesName(d.nameMatching, x.name, entry.name));
      if (p === undefined) {
        throw new CodecError("unexpected-property", ctx.child(entry.name).path, `unknown property '${entry.name}'`, d.id);
      }
      if (entry.value.kind === "null") {
        out[p.name] = null;
        continue;
      }
      const codec = resolve(p.codec);
      if (codec.parseRequestInput === undefined) {
        throw new CodecError("unsupported", ctx.child(p.name).path, `codec '${codec.id}' has no request input capability`, codec.id);
      }
      out[p.name] = codec.parseRequestInput(entry.value, ctx.child(p.name));
    }
    return out;
  }
}

export interface ArrayCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly element: CodecRef<unknown>;
  readonly elementNullable: boolean;
}

export function arrayCodec<E>(d: ArrayCodecDescriptor): Codec<readonly (E | null)[]> {
  const validate = (value: unknown, ctx: CodecContext): readonly (E | null)[] => {
    if (!Array.isArray(value)) {
      throw new CodecError("type-mismatch", ctx.path, "array required", d.id);
    }
    const element = resolve(d.element);
    const out: (E | null)[] = [];
    // an index loop (not Array#map) so that holes are seen instead of skipped
    for (let i = 0; i < value.length; i++) {
      const item: unknown = value[i];
      if (!(i in value) || item === undefined) {
        throw new CodecError("undefined-not-allowed", ctx.child(i).path, "array holes and undefined are not JSON", d.id);
      }
      if (item === null) {
        if (!d.elementNullable) {
          throw new CodecError("null-not-allowed", ctx.child(i).path, "array element does not allow null", d.id);
        }
        out.push(null);
        continue;
      }
      out.push(element.validateDomain(item, ctx.child(i)) as E);
    }
    return out;
  };
  return {
    id: d.id,
    typeId: d.typeId,
    validateDomain: validate,
    encodeRequest: (value, ctx) => {
      ctx.checkpoint();
      const items = validate(value, ctx);
      const element = resolve(d.element);
      if (element.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${element.id}' has no request capability`, element.id);
      }
      return { kind: "array", items: items.map((item, i) => (item === null ? jsonNull : element.encodeRequest!(item, ctx.child(i)))) };
    },
    decodeResponse: (wire, ctx) => {
      ctx.checkpoint();
      if (wire.kind !== "array") {
        throw new CodecError("type-mismatch", ctx.path, `array required but found ${wire.kind}`, d.id);
      }
      const element = resolve(d.element);
      if (element.decodeResponse === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${element.id}' has no response capability`, element.id);
      }
      return wire.items.map((item, i) => {
        if (item.kind === "null" && !(element.ownsNullToken === true && !d.elementNullable)) {
          if (!d.elementNullable) {
            throw new CodecError("null-not-allowed", ctx.child(i).path, "array element is null but the public type is not nullable", d.id);
          }
          return null;
        }
        return element.decodeResponse!(item, ctx.child(i)) as E;
      });
    },
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string" || input.kind !== "array") {
        throw new CodecError("type-mismatch", ctx.path, "array input required", d.id);
      }
      const element = resolve(d.element);
      if (element.parseRequestInput === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${element.id}' has no request input capability`, element.id);
      }
      return input.items.map((item, i) => (item.kind === "null" && d.elementNullable ? null : (element.parseRequestInput!(item, ctx.child(i)) as E)));
    },
  };
}

export interface MapCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly key: CodecRef<unknown>;
  readonly value: CodecRef<unknown>;
  readonly valueNullable: boolean;
  readonly comparer: KeyComparer;
}

/** Dictionary codec: keys go through the key capabilities, collisions after encoding are rejected. */
export function mapCodec<K, V>(d: MapCodecDescriptor): Codec<TisiliaMap<K, V | null>> {
  const keyCodec = (): Codec<unknown> => resolve(d.key);
  const validate = (value: unknown, ctx: CodecContext): TisiliaMap<K, V | null> => {
    // a plain Map is accepted too: identity is decided below, by the key codec and the contract's comparer, never by the input map
    if (!(value instanceof TisiliaMap) && !(value instanceof Map)) {
      throw new CodecError("type-mismatch", ctx.path, "TisiliaMap or Map required", d.id);
    }
    const kc = keyCodec();
    if (kc.encodeKey === undefined) {
      throw new CodecError("unsupported", ctx.path, `key codec '${kc.id}' has no key capability`, kc.id);
    }
    const out = new TisiliaMap<K, V | null>(d.comparer, (k) => kc.encodeKey!(k, ctx));
    const vc = resolve(d.value);
    for (const [k, v] of value.entries()) {
      const key = kc.validateDomain(k, ctx) as K;
      if (v === undefined) {
        throw new CodecError("undefined-not-allowed", ctx.path, "map values cannot be undefined", d.id);
      }
      if (v === null && !d.valueNullable) {
        throw new CodecError("null-not-allowed", ctx.path, "map value does not allow null", d.id);
      }
      const encodedKey = kc.encodeKey(key, ctx);
      if (out.hasEncoded(encodedKey)) {
        throw new CodecError("key-collision", ctx.child(encodedKey).path, "two keys collide after encoding under the map comparer", d.id);
      }
      out.set(key, v === null ? null : (vc.validateDomain(v, ctx.child(encodedKey)) as V));
    }
    return out;
  };
  return {
    id: d.id,
    typeId: d.typeId,
    validateDomain: validate,
    encodeRequest: (value, ctx) => {
      ctx.checkpoint();
      const map = validate(value, ctx);
      const kc = keyCodec();
      const vc = resolve(d.value);
      if (vc.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${vc.id}' has no request capability`, vc.id);
      }
      const entries: JsonEntry[] = [];
      for (const [k, v] of map.entries()) {
        const name = kc.encodeKey!(k, ctx);
        entries.push({ name, value: v === null ? jsonNull : vc.encodeRequest(v, ctx.child(name)) });
      }
      return { kind: "object", entries };
    },
    decodeResponse: (wire, ctx) => {
      ctx.checkpoint();
      if (wire.kind !== "object") {
        throw new CodecError("type-mismatch", ctx.path, `object required but found ${wire.kind}`, d.id);
      }
      const kc = keyCodec();
      if (kc.decodeKey === undefined || kc.encodeKey === undefined) {
        throw new CodecError("unsupported", ctx.path, `key codec '${kc.id}' has no key capabilities`, kc.id);
      }
      const vc = resolve(d.value);
      if (vc.decodeResponse === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${vc.id}' has no response capability`, vc.id);
      }
      const out = new TisiliaMap<K, V | null>(d.comparer, (k) => kc.encodeKey!(k, ctx));
      for (const entry of wire.entries) {
        const key = kc.decodeKey(entry.name, ctx.child(entry.name)) as K;
        if (out.has(key)) {
          throw new CodecError("duplicate-property", ctx.child(entry.name).path, "duplicate map key", d.id);
        }
        if (entry.value.kind === "null" && !(vc.ownsNullToken === true && !d.valueNullable)) {
          if (!d.valueNullable) {
            throw new CodecError("null-not-allowed", ctx.child(entry.name).path, "map value is null but the public type is not nullable", d.id);
          }
          out.set(key, null);
          continue;
        }
        out.set(key, vc.decodeResponse(entry.value, ctx.child(entry.name)) as V);
      }
      return out;
    },
    // Explorer typed input: a JSON object whose names go through the key grammar and whose values through the value
    // codec's input, as a response is read; the contract declares this capability for every map codec
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        throw new CodecError("unsupported", ctx.path, "map input requires a JSON value editor", d.id);
      }
      if (input.kind !== "object") {
        throw new CodecError("type-mismatch", ctx.path, `object required but found ${input.kind}`, d.id);
      }
      const kc = keyCodec();
      if (kc.decodeKey === undefined || kc.encodeKey === undefined) {
        throw new CodecError("unsupported", ctx.path, `key codec '${kc.id}' has no key capabilities`, kc.id);
      }
      const vc = resolve(d.value);
      const out = new TisiliaMap<K, V | null>(d.comparer, (k) => kc.encodeKey!(k, ctx));
      for (const entry of input.entries) {
        const key = kc.decodeKey(entry.name, ctx.child(entry.name)) as K;
        if (out.has(key)) {
          throw new CodecError("duplicate-property", ctx.child(entry.name).path, "duplicate map key", d.id);
        }
        if (entry.value.kind === "null" && !(vc.ownsNullToken === true && !d.valueNullable)) {
          if (!d.valueNullable) {
            throw new CodecError("null-not-allowed", ctx.child(entry.name).path, "map value does not allow null", d.id);
          }
          out.set(key, null);
          continue;
        }
        if (vc.parseRequestInput === undefined) {
          throw new CodecError("unsupported", ctx.child(entry.name).path, `codec '${vc.id}' has no request input capability`, vc.id);
        }
        out.set(key, vc.parseRequestInput(entry.value, ctx.child(entry.name)) as V);
      }
      return validate(out, ctx);
    },
  };
}

/** Nullable wrapper: null passes through (bypass) when the wire has a null branch; the inner codec never sees null. */
export function nullableCodec<T>(inner: CodecRef<T>, id?: string): Codec<T | null> {
  const get = (): Codec<T> => resolve(inner);
  return {
    get id() {
      return id ?? get().id + ".nullable";
    },
    get typeId() {
      return get().typeId;
    },
    validateDomain: (v, ctx) => (v === null ? null : get().validateDomain(v, ctx)),
    encodeRequest: (v, ctx) => {
      if (v === null) {
        return jsonNull;
      }
      const c = get();
      if (c.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${c.id}' has no request capability`, c.id);
      }
      return c.encodeRequest(v, ctx);
    },
    decodeResponse: (w, ctx) => {
      if (w.kind === "null") {
        return null;
      }
      const c = get();
      if (c.decodeResponse === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${c.id}' has no response capability`, c.id);
      }
      return c.decodeResponse(w, ctx);
    },
    parseRequestInput: (input, ctx) => {
      if (typeof input !== "string" && input.kind === "null") {
        return null;
      }
      if (typeof input === "string" && input.trim() === "null") {
        return null;
      }
      const c = get();
      if (c.parseRequestInput === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${c.id}' has no request input capability`, c.id);
      }
      return c.parseRequestInput(input, ctx);
    },
  };
}

export interface EnumMember {
  readonly name: string;
  readonly value: bigint;
  readonly serializedName?: string;
}

export interface EnumCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly underlying: "int8" | "uint8" | "int16" | "uint16" | "int32" | "uint32" | "int64" | "uint64";
  readonly flags: boolean;
  readonly allowUndefinedInteger: boolean;
  readonly members: readonly EnumMember[];
  /** true for JsonStringEnumConverter profiles: names on the wire, integers as fallback. */
  readonly stringForm: boolean;
}

const enumRanges: Record<EnumCodecDescriptor["underlying"], readonly [bigint, bigint]> = {
  int8: [-128n, 127n],
  uint8: [0n, 255n],
  int16: [-32768n, 32767n],
  uint16: [0n, 65535n],
  int32: [-2147483648n, 2147483647n],
  uint32: [0n, 4294967295n],
  int64: [-9223372036854775808n, 9223372036854775807n],
  uint64: [0n, 18446744073709551615n],
};

/**
 * Enum codec. Public value is the underlying integer (number, or bigint for 64-bit bases);
 * undefined integers are preserved when allowed. String form follows JsonStringEnumConverter as observed on .NET 10:
 * names (case-insensitive on read), integer strings, integers; Flags written as "A, B"; undefined values as numbers.
 */
export function enumCodec(d: EnumCodecDescriptor): Codec<number | bigint> {
  const [min, max] = enumRanges[d.underlying];
  const big = d.underlying === "int64" || d.underlying === "uint64";
  const toDomain = (v: bigint): number | bigint => (big ? v : Number(v));
  const byValue = new Map<bigint, EnumMember>();
  const byName = new Map<string, EnumMember>();
  for (const m of d.members) {
    byValue.set(m.value, byValue.get(m.value) ?? m);
    // System.Text.Json's enum converter looks names up with StringComparer.OrdinalIgnoreCase
    byName.set(ordinalUpper(m.serializedName ?? m.name), m);
  }
  const validate = (v: unknown, ctx: CodecContext): bigint => {
    let value: bigint;
    if (typeof v === "bigint") {
      value = v;
    } else if (typeof v === "number" && Number.isInteger(v)) {
      value = BigInt(v);
    } else {
      throw new CodecError("type-mismatch", ctx.path, "enum requires an integer value", d.id);
    }
    if (value < min || value > max) {
      throw new CodecError("range", ctx.path, `enum value outside ${d.underlying}`, d.id);
    }
    if (!d.allowUndefinedInteger && !isDefined(value)) {
      throw new CodecError("domain-rule", ctx.path, "undefined enum value", d.id);
    }
    return value;
  };
  const isDefined = (value: bigint): boolean => {
    if (byValue.has(value)) {
      return true;
    }
    if (!d.flags) {
      return false;
    }
    let rest = value;
    for (const m of d.members) {
      if (m.value !== 0n && (rest & m.value) === m.value) {
        rest &= ~m.value;
      }
    }
    return rest === 0n;
  };
  const formatName = (value: bigint): string | undefined => {
    const exact = byValue.get(value);
    if (exact !== undefined) {
      return exact.serializedName ?? exact.name;
    }
    if (!d.flags || value === 0n) {
      return undefined;
    }
    const names: string[] = [];
    let rest = value;
    for (const m of [...d.members].sort((a, b) => (a.value < b.value ? -1 : a.value > b.value ? 1 : 0))) {
      if (m.value !== 0n && (rest & m.value) === m.value) {
        names.push(m.serializedName ?? m.name);
        rest &= ~m.value;
      }
    }
    return rest === 0n ? names.join(", ") : undefined;
  };
  const parseName = (text: string, ctx: CodecContext): bigint => {
    if (/^[+-]?[0-9]+$/.test(text)) {
      return validate(BigInt(text), ctx);
    }
    let total = 0n;
    for (const part of text.split(",")) {
      const m = byName.get(ordinalUpper(part.trim()));
      if (m === undefined) {
        throw new CodecError("grammar", ctx.path, "unknown enum member name", d.id);
      }
      total |= m.value;
    }
    return validate(total, ctx);
  };
  return {
    id: d.id,
    typeId: d.typeId,
    validateDomain: (v, ctx) => toDomain(validate(v, ctx)),
    encodeRequest: (v, ctx) => {
      const value = validate(v, ctx);
      if (d.stringForm) {
        const name = formatName(value);
        if (name !== undefined) {
          return { kind: "string", value: name };
        }
      }
      return { kind: "number", text: value.toString() };
    },
    decodeResponse: (w, ctx) => {
      if (w.kind === "number") {
        if (!/^-?(?:0|[1-9][0-9]*)$/.test(w.text)) {
          throw new CodecError("grammar", ctx.path, "enum requires an integer lexeme", d.id);
        }
        return toDomain(validate(BigInt(w.text), ctx));
      }
      if (w.kind === "string" && d.stringForm) {
        return toDomain(parseName(w.value, ctx));
      }
      throw new CodecError("type-mismatch", ctx.path, `enum requires a JSON ${d.stringForm ? "string or number" : "number"} but found ${w.kind}`, d.id);
    },
    // Dictionary<TEnum,…> keys are always written as names by System.Text.Json (observed on .NET 10)
    encodeKey: (v, ctx) => {
      const value = validate(v, ctx);
      return formatName(value) ?? value.toString();
    },
    decodeKey: (s, ctx) => toDomain(parseName(s, ctx)),
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        return toDomain(parseName(input.trim(), ctx));
      }
      if (input.kind === "number") {
        return toDomain(validate(BigInt(input.text), ctx));
      }
      if (input.kind === "string") {
        return toDomain(parseName(input.value, ctx));
      }
      throw new CodecError("type-mismatch", ctx.path, "enum input must be a name or integer", d.id);
    },
  };
}

export interface TaggedUnionVariant {
  readonly tag: string | bigint;
  readonly codec: CodecRef<unknown>;
}

export interface TaggedUnionDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly discriminator: string;
  readonly variants: readonly TaggedUnionVariant[];
  /** Domain property that carries the variant tag (generated as a literal type). */
  readonly tagProperty: string;
}

/** Polymorphic object dispatch on a discriminator literal: no body sniffing, unknown tags fail. */
export function taggedUnionCodec<T extends object>(d: TaggedUnionDescriptor): Codec<T> {
  const tagKey = (t: string | bigint): string => (typeof t === "bigint" ? "n:" + t.toString() : "s:" + t);
  const table = new Map(d.variants.map((v) => [tagKey(v.tag), v] as const));
  const tagOf = (value: unknown, ctx: CodecContext): TaggedUnionVariant => {
    if (typeof value !== "object" || value === null) {
      throw new CodecError("type-mismatch", ctx.path, "object required", d.id);
    }
    const raw = (value as Record<string, unknown>)[d.tagProperty];
    const key = typeof raw === "string" ? "s:" + raw : typeof raw === "bigint" ? "n:" + raw.toString() : typeof raw === "number" ? "n:" + String(raw) : undefined;
    const variant = key === undefined ? undefined : table.get(key);
    if (variant === undefined) {
      throw new CodecError("unknown-discriminator", ctx.child(d.tagProperty).path, "unknown or missing discriminator", d.id);
    }
    return variant;
  };
  return {
    id: d.id,
    typeId: d.typeId,
    validateDomain: (v, ctx) => resolve(tagOf(v, ctx).codec).validateDomain(v, ctx) as T,
    encodeRequest: (v, ctx) => {
      const variant = tagOf(v, ctx);
      const codec = resolve(variant.codec);
      if (codec.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${codec.id}' has no request capability`, codec.id);
      }
      return codec.encodeRequest(v, ctx);
    },
    decodeResponse: (w, ctx) => {
      if (w.kind !== "object") {
        throw new CodecError("type-mismatch", ctx.path, `object required but found ${w.kind}`, d.id);
      }
      const entry = w.entries.find((e) => e.name === d.discriminator);
      if (entry === undefined) {
        throw new CodecError("unknown-discriminator", ctx.child(d.discriminator).path, "discriminator is missing", d.id);
      }
      const key = entry.value.kind === "string" ? "s:" + entry.value.value : entry.value.kind === "number" ? "n:" + entry.value.text : undefined;
      const variant = key === undefined ? undefined : table.get(key);
      if (variant === undefined) {
        throw new CodecError("unknown-discriminator", ctx.child(d.discriminator).path, "unrecognized discriminator value", d.id);
      }
      const codec = resolve(variant.codec);
      if (codec.decodeResponse === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${codec.id}' has no response capability`, codec.id);
      }
      return codec.decodeResponse(w, ctx) as T;
    },
    // Explorer typed input: the discriminator picks the variant as on the wire, and the variant reads the whole object
    // (the tag is one of its properties); the contract declares this capability for every union codec
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        throw new CodecError("unsupported", ctx.path, "union input requires a JSON value editor", d.id);
      }
      if (input.kind !== "object") {
        throw new CodecError("type-mismatch", ctx.path, `object required but found ${input.kind}`, d.id);
      }
      const entry = input.entries.find((e) => e.name === d.discriminator);
      if (entry === undefined) {
        throw new CodecError("unknown-discriminator", ctx.child(d.discriminator).path, "discriminator is missing", d.id);
      }
      const key = entry.value.kind === "string" ? "s:" + entry.value.value : entry.value.kind === "number" ? "n:" + entry.value.text : undefined;
      const variant = key === undefined ? undefined : table.get(key);
      if (variant === undefined) {
        throw new CodecError("unknown-discriminator", ctx.child(d.discriminator).path, "unrecognized discriminator value", d.id);
      }
      const codec = resolve(variant.codec);
      if (codec.parseRequestInput === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${codec.id}' has no request input capability`, codec.id);
      }
      return codec.parseRequestInput(input, ctx) as T;
    },
  };
}

export interface TokenUnionBranch {
  readonly token: "null" | "boolean" | "number" | "string" | "array" | "object";
  readonly codec: CodecRef<unknown>;
}

/** Selects a branch by JSON token kind only: never tries branches in order. */
export function tokenUnionCodec<T>(id: string, typeId: string, branches: readonly TokenUnionBranch[], encodeWith: CodecRef<unknown>): Codec<T> {
  const byToken = new Map(branches.map((b) => [b.token, b] as const));
  return {
    id,
    typeId,
    ownsNullToken: byToken.has("null"),
    validateDomain: (v, ctx) => resolve(encodeWith).validateDomain(v, ctx) as T,
    encodeRequest: (v, ctx) => {
      const c = resolve(encodeWith);
      if (c.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${c.id}' has no request capability`, c.id);
      }
      return c.encodeRequest(v, ctx);
    },
    decodeResponse: (w, ctx) => {
      const b = byToken.get(w.kind);
      if (b === undefined) {
        throw new CodecError("type-mismatch", ctx.path, `no branch accepts a JSON ${w.kind}`, id);
      }
      const c = resolve(b.codec);
      if (c.decodeResponse === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${c.id}' has no response capability`, c.id);
      }
      return c.decodeResponse(w, ctx) as T;
    },
  };
}

/**
 * Brand: same wire and value as the base codec; the TypeScript type is distinguished with a unique symbol. The base is resolved
 * when a capability is first read, never while the brand is built: in generated code a brand may precede its base codec, whose
 * `const` is not initialized yet at that point (reading it would throw a ReferenceError).
 */
export function brandCodec<T>(id: string, typeId: string, base: CodecRef<unknown>): Codec<T> {
  const get = (): Codec<unknown> => resolve(base);
  const codec = {
    id,
    typeId,
    validateDomain: (v: unknown, ctx: CodecContext) => get().validateDomain(v, ctx) as T,
    get ownsNullToken() {
      return get().ownsNullToken === true;
    },
    get encodeRequest() {
      const b = get();
      return b.encodeRequest === undefined ? undefined : (v: T, ctx: CodecContext): JsonValue => b.encodeRequest!(v, ctx);
    },
    get decodeResponse() {
      const b = get();
      return b.decodeResponse === undefined ? undefined : (w: JsonValue, ctx: CodecContext): T => b.decodeResponse!(w, ctx) as T;
    },
    get encodeKey() {
      const b = get();
      return b.encodeKey === undefined ? undefined : (v: T, ctx: CodecContext): string => b.encodeKey!(v, ctx);
    },
    get decodeKey() {
      const b = get();
      return b.decodeKey === undefined ? undefined : (s: string, ctx: CodecContext): T => b.decodeKey!(s, ctx) as T;
    },
    get parseRequestInput() {
      const b = get();
      return b.parseRequestInput === undefined ? undefined : (i: string | JsonValue, ctx: CodecContext): T => b.parseRequestInput!(i, ctx) as T;
    },
  };
  // an absent capability reads as undefined, exactly like a codec that does not declare the method
  return codec as unknown as Codec<T>;
}
