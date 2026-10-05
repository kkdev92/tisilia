import type { Codec, CodecContext } from "../codec/abi.js";
import { CodecError } from "../codec/errors.js";
import { formatDecimal, validateDecimal } from "../primitives/decimal.js";
import { formatFloat32, formatFloat64 } from "../primitives/float.js";
import { isSmallInteger, validateBigInteger, validateSmallInteger, type IntegerScalarName } from "../primitives/integers.js";
import { validateChar, validateGuid, validateString } from "../primitives/text.js";
import { formatDateOnly, formatDateTimeOffset, formatDateTimeUnspecified, formatDateTimeUtc, formatDuration, formatTimeOnly, validateDateOnly, validateDateTimeOffset, validateDateTimeTicks, validateDuration, validateTimeOnly } from "../primitives/datetime.js";
import type { ScalarName } from "../codec/scalars.js";

/**
 * HTTP string binders: independent of the JSON codecs. The canonical writer emits the invariant form
 * ASP.NET Core's TryParse-based binding accepts; the accepted alias set is recorded by the server-acceptance binding.
 */
export type ParameterLocation = "path" | "query" | "header";
export type NullPolicy = "reject" | "omit" | "literal";

export interface Binder<T> {
  readonly id: string;
  readonly location: ParameterLocation;
  readonly cardinality: "single" | "repeated";
  readonly nullPolicy: NullPolicy;
  readonly nullLiteral?: string;
  readonly emptyPolicy: "reject" | "allow";
  /** Canonical text of one value (before percent-encoding). */
  format(value: T, context: CodecContext): string;
}

export interface StandardBinderOptions {
  readonly id?: string;
  readonly cardinality?: "single" | "repeated";
  readonly nullPolicy?: NullPolicy;
  readonly nullLiteral?: string;
  readonly emptyPolicy?: "reject" | "allow";
}

/** Canonical invariant text of a builtin scalar for route/query/header binding. */
export function formatScalarForBinding(name: ScalarName, value: unknown, path: string): string {
  switch (name) {
    case "string":
      return validateString(value, path);
    case "char":
      return validateChar(value, path);
    case "boolean":
      if (typeof value !== "boolean") {
        throw new CodecError("type-mismatch", path, "boolean required");
      }
      return value ? "true" : "false";
    case "guid":
      return validateGuid(value, path);
    case "int8":
    case "uint8":
    case "int16":
    case "uint16":
    case "int32":
    case "uint32":
    case "int64":
    case "uint64": {
      const n = name as IntegerScalarName;
      return isSmallInteger(n) ? String(validateSmallInteger(n, value, path)) : validateBigInteger(n, value, path).toString();
    }
    case "decimal":
      return formatDecimal(validateDecimal(value, path));
    case "float32":
      if (typeof value !== "number") {
        throw new CodecError("type-mismatch", path, "number required");
      }
      return formatFloat32(value, path);
    case "float64":
      if (typeof value !== "number") {
        throw new CodecError("type-mismatch", path, "number required");
      }
      return formatFloat64(value, path);
    case "date-only":
      return formatDateOnly(validateDateOnly(value, path));
    case "time-only":
      return formatTimeOnly(validateTimeOnly(value, path));
    case "datetime-utc":
      return formatDateTimeUtc({ kind: "datetime-utc", ticks: validateDateTimeTicks(value, path, "utc") });
    case "datetime-unspecified":
      return formatDateTimeUnspecified({ kind: "datetime-unspecified", ticks: validateDateTimeTicks(value, path, "unspecified") });
    case "datetime-offset":
      return formatDateTimeOffset(validateDateTimeOffset(value, path));
    case "duration":
      return formatDuration(validateDuration(value, path));
    case "bytes":
    case "json-value":
    case "datetime-local-wire":
      throw new CodecError("unsupported", path, `${name} has no standard HTTP string binder`);
  }
}

/**
 * The binder of a parameter whose type a module codec carries (an additional codec such as Int128 or Version): the canonical text is
 * the request codec's wire — its string value or its number lexeme — which the server's TryParse-invariant binding reads.
 */
export function codecBinder<T = unknown>(codec: () => Codec<T>, location: ParameterLocation, options: StandardBinderOptions = {}): Binder<T> {
  const cardinality = options.cardinality ?? "single";
  const nullPolicy = options.nullPolicy ?? "reject";
  if (location === "path" && (cardinality === "repeated" || nullPolicy === "omit")) {
    throw new Error("path binders cannot be repeated or omit null");
  }
  if (nullPolicy === "literal" && options.nullLiteral === undefined) {
    throw new Error("literal null policy requires nullLiteral");
  }
  const binder: Binder<T> = {
    id: options.id ?? `codec.binder.${location}`,
    location,
    cardinality,
    nullPolicy,
    emptyPolicy: options.emptyPolicy ?? "reject",
    format: (value, ctx) => {
      const resolved = codec();
      if (resolved.encodeRequest === undefined) {
        throw new CodecError("unsupported", ctx.path, `codec '${resolved.id}' has no request capability to write a parameter`, resolved.id);
      }
      const wire = resolved.encodeRequest(value, ctx);
      if (wire.kind === "string") {
        return wire.value;
      }
      if (wire.kind === "number") {
        return wire.text;
      }
      throw new CodecError("unsupported", ctx.path, `codec '${resolved.id}' writes a JSON ${wire.kind}, which has no parameter text`, resolved.id);
    },
  };
  if (options.nullLiteral !== undefined) {
    (binder as { nullLiteral?: string }).nullLiteral = options.nullLiteral;
  }
  return binder;
}

export interface EnumBinderOptions extends StandardBinderOptions {
  /** The enum is a [Flags] enum: a combination of defined flags counts as defined. */
  readonly flags?: boolean;
  /** The server binds only defined values (MVC's enum model binder): any other value is refused before sending. */
  readonly definedOnly?: boolean;
}

/**
 * The binder of an enum parameter. ASP.NET Core binds enums with `Enum.TryParse` (case-sensitive C# member names,
 * integers, comma-separated flags) or MVC's enum model binder (the same texts, defined values only): the client writes a defined
 * member's C# name and the integer otherwise. `members` are the C# names and values of the enum.
 */
export function enumBinder<T = unknown>(codec: () => Codec<T>, members: readonly (readonly [string, bigint])[], location: ParameterLocation, options: EnumBinderOptions = {}): Binder<T> {
  const base = codecBinder<T>(codec, location, options);
  const byValue = new Map<bigint, string>();
  for (const [name, value] of members) {
    if (!byValue.has(value)) {
      byValue.set(value, name);
    }
  }
  const composedOfFlags = (value: bigint): boolean => {
    let rest = value;
    for (const [, flag] of [...members].sort((a, b) => (a[1] < b[1] ? 1 : a[1] > b[1] ? -1 : 0))) {
      if (flag !== 0n && (rest & flag) === flag) {
        rest &= ~flag;
      }
    }
    return rest === 0n;
  };
  return {
    ...base,
    format: (value, ctx) => {
      const validated = codec().validateDomain(value, ctx) as unknown;
      const n = typeof validated === "bigint" ? validated : BigInt(validated as number);
      const name = byValue.get(n);
      if (name !== undefined) {
        return name;
      }
      if (options.definedOnly === true && !(options.flags === true && n !== 0n && composedOfFlags(n))) {
        throw new CodecError("domain-rule", ctx.path, `the server binds only defined enum values; ${n} is not one`);
      }
      return n.toString();
    },
  };
}

export function standardBinder<T = unknown>(name: ScalarName, location: ParameterLocation, options: StandardBinderOptions = {}): Binder<T> {
  const cardinality = options.cardinality ?? "single";
  const nullPolicy = options.nullPolicy ?? "reject";
  if (location === "path" && (cardinality === "repeated" || nullPolicy === "omit")) {
    throw new Error("path binders cannot be repeated or omit null");
  }
  if (nullPolicy === "literal" && options.nullLiteral === undefined) {
    throw new Error("literal null policy requires nullLiteral");
  }
  const binder: Binder<T> = {
    id: options.id ?? `std.binder.${name}.${location}${cardinality === "repeated" ? ".repeated" : ""}${nullPolicy === "omit" ? ".omit-null" : ""}${options.emptyPolicy === "allow" ? ".allow-empty" : ""}`,
    location,
    cardinality,
    nullPolicy,
    emptyPolicy: options.emptyPolicy ?? "reject",
    format: (value, ctx) => formatScalarForBinding(name, value, ctx.path),
  };
  if (options.nullLiteral !== undefined) {
    (binder as { nullLiteral?: string }).nullLiteral = options.nullLiteral;
  }
  return binder;
}
