import type { JsonValue } from "../json/ast.js";
import { parseJson } from "../json/parser.js";
import type { Codec, CodecContext } from "./abi.js";
import { CodecError } from "./errors.js";
import { formatDecimal, parseDecimalLexeme, validateDecimal, type Decimal } from "../primitives/decimal.js";
import { formatFloat32, formatFloat64, parseFloat32Lexeme, parseFloat64Lexeme } from "../primitives/float.js";
import { formatInteger, integerRanges, isSmallInteger, parseIntegerLexeme, parseIntegerString, validateBigInteger, validateSmallInteger, type IntegerScalarName } from "../primitives/integers.js";
import { decodeBase64, encodeBase64, parseGuid, validateBytes, validateChar, validateGuid, validateString, checkUtf16Length, type Guid } from "../primitives/text.js";
import {
  formatDateOnly,
  formatDateTimeLocalWire,
  formatDateTimeOffset,
  formatDateTimeUnspecified,
  formatDateTimeUtc,
  formatDuration,
  formatTimeOnly,
  parseDateOnly,
  parseDateTimeLocalWire,
  parseDateTimeOffset,
  parseDateTimeUnspecified,
  parseDateTimeUtc,
  parseDuration,
  parseTimeOnly,
  validateDateOnly,
  validateDateTimeLocalWire,
  validateDateTimeOffset,
  validateDateTimeTicks,
  validateDuration,
  validateTimeOnly,
  type DateOnly,
  type DateTimeLocalWire,
  type DateTimeOffset,
  type DateTimeUnspecified,
  type DateTimeUtc,
  type Duration,
  type TimeOnly,
} from "../primitives/datetime.js";

/** Effective number handling of a usage position (mirrors the generator's NumberProfile). */
export interface NumberProfile {
  readonly readFromString: boolean;
  readonly writeAsString: boolean;
  readonly namedLiterals: boolean;
}

export const strictNumbers: NumberProfile = Object.freeze({ readFromString: false, writeAsString: false, namedLiterals: false });
export const webNumbers: NumberProfile = Object.freeze({ readFromString: true, writeAsString: false, namedLiterals: false });

export type ScalarName =
  | "string"
  | "boolean"
  | "char"
  | "guid"
  | "bytes"
  | "json-value"
  | IntegerScalarName
  | "decimal"
  | "float32"
  | "float64"
  | "date-only"
  | "time-only"
  | "datetime-utc"
  | "datetime-unspecified"
  | "datetime-local-wire"
  | "datetime-offset"
  | "duration";

export interface ScalarOptions {
  readonly id?: string;
  readonly typeId?: string;
  readonly numbers?: NumberProfile;
  readonly minUtf16Length?: number;
  readonly maxUtf16Length?: number;
}

function expectString(wire: JsonValue, ctx: CodecContext, what: string): string {
  if (wire.kind !== "string") {
    throw new CodecError("type-mismatch", ctx.path, `${what} requires a JSON string but found ${wire.kind}`);
  }
  return wire.value;
}

function typeIdOf(name: ScalarName): string {
  return "std." + name;
}

/**
 * Scalar codec for a builtin scalar. Value representation:
 * string/boolean/char/guid → string|boolean; small integers → number; int64/uint64 → bigint; decimal → Decimal;
 * floats → number; bytes → Uint8Array; dates → tick records; json-value → the AST itself.
 */
export function scalarCodec<T = unknown>(name: ScalarName, options: ScalarOptions = {}): Codec<T, T> {
  const typeId = options.typeId ?? typeIdOf(name);
  const numbers = options.numbers ?? strictNumbers;
  const id = options.id ?? typeId + ".codec" + numberSuffix(name, numbers);
  const base = { id, typeId };
  switch (name) {
    case "string":
      return {
        ...base,
        validateDomain: (v, ctx) => {
          const s = validateString(v, ctx.path);
          checkUtf16Length(s, options.minUtf16Length, options.maxUtf16Length, ctx.path);
          return s;
        },
        encodeRequest: (v, ctx) => {
          const s = validateString(v, ctx.path);
          checkUtf16Length(s, options.minUtf16Length, options.maxUtf16Length, ctx.path);
          return { kind: "string", value: s };
        },
        decodeResponse: (w, ctx) => {
          const s = validateString(expectString(w, ctx, "string"), ctx.path);
          checkUtf16Length(s, options.minUtf16Length, options.maxUtf16Length, ctx.path);
          return s;
        },
        encodeKey: (v, ctx) => validateString(v, ctx.path),
        decodeKey: (v, ctx) => validateString(v, ctx.path),
        parseRequestInput: (input, ctx) => (typeof input === "string" ? input : validateString(expectString(input, ctx, "string"), ctx.path)),
      } as Codec<unknown> as Codec<T, T>;
    case "boolean":
      return {
        ...base,
        validateDomain: (v, ctx) => {
          if (typeof v !== "boolean") {
            throw new CodecError("type-mismatch", ctx.path, "boolean required");
          }
          return v;
        },
        encodeRequest: (v, ctx) => {
          if (typeof v !== "boolean") {
            throw new CodecError("type-mismatch", ctx.path, "boolean required");
          }
          return { kind: "boolean", value: v };
        },
        decodeResponse: (w, ctx) => {
          if (w.kind !== "boolean") {
            throw new CodecError("type-mismatch", ctx.path, `boolean requires a JSON boolean but found ${w.kind}`);
          }
          return w.value;
        },
        // Dictionary<bool,…> keys are written "True"/"False" by System.Text.Json (observed on .NET 10)
        encodeKey: (v) => (v ? "True" : "False"),
        decodeKey: (v, ctx) => {
          if (v === "True" || v === "true") {
            return true;
          }
          if (v === "False" || v === "false") {
            return false;
          }
          throw new CodecError("grammar", ctx.path, "boolean key must be True or False");
        },
        parseRequestInput: (input, ctx) => {
          if (typeof input === "string") {
            const t = input.trim();
            if (t === "true") {
              return true;
            }
            if (t === "false") {
              return false;
            }
            throw new CodecError("grammar", ctx.path, "boolean input must be true or false");
          }
          if (input.kind !== "boolean") {
            throw new CodecError("type-mismatch", ctx.path, "boolean required");
          }
          return input.value;
        },
      } as Codec<unknown> as Codec<T, T>;
    case "char":
      return {
        ...base,
        validateDomain: (v, ctx) => validateChar(v, ctx.path),
        encodeRequest: (v, ctx) => ({ kind: "string", value: validateChar(v, ctx.path) }),
        decodeResponse: (w, ctx) => validateChar(expectString(w, ctx, "char"), ctx.path),
        encodeKey: (v, ctx) => validateChar(v, ctx.path),
        decodeKey: (v, ctx) => validateChar(v, ctx.path),
        parseRequestInput: (input, ctx) => validateChar(typeof input === "string" ? input : expectString(input, ctx, "char"), ctx.path),
      } as Codec<unknown> as Codec<T, T>;
    case "guid":
      return {
        ...base,
        validateDomain: (v, ctx) => validateGuid(v, ctx.path),
        encodeRequest: (v, ctx) => ({ kind: "string", value: validateGuid(v, ctx.path) }),
        decodeResponse: (w, ctx) => parseGuid(expectString(w, ctx, "guid"), ctx.path),
        encodeKey: (v, ctx) => validateGuid(v, ctx.path) as string,
        decodeKey: (v, ctx) => parseGuid(v, ctx.path),
        parseRequestInput: (input, ctx) => parseGuid((typeof input === "string" ? input : expectString(input, ctx, "guid")).trim(), ctx.path),
      } as Codec<Guid> as Codec<T, T>;
    case "bytes":
      return {
        ...base,
        validateDomain: (v, ctx) => validateBytes(v, ctx.path),
        encodeRequest: (v, ctx) => ({ kind: "string", value: encodeBase64(validateBytes(v, ctx.path)) }),
        decodeResponse: (w, ctx) => decodeBase64(expectString(w, ctx, "bytes"), ctx.path),
        parseRequestInput: (input, ctx) => decodeBase64((typeof input === "string" ? input : expectString(input, ctx, "bytes")).trim(), ctx.path),
      } as Codec<Uint8Array> as Codec<T, T>;
    case "json-value":
      return {
        ...base,
        ownsNullToken: true,
        validateDomain: (v, ctx) => {
          if (!isJsonValue(v)) {
            throw new CodecError("type-mismatch", ctx.path, "lossless JsonValue required");
          }
          return v;
        },
        encodeRequest: (v, ctx) => {
          if (!isJsonValue(v)) {
            throw new CodecError("type-mismatch", ctx.path, "lossless JsonValue required");
          }
          return v;
        },
        decodeResponse: (w) => w,
        parseRequestInput: (input, ctx) => (typeof input === "string" ? parseJson(input, { limits: ctx.limits, checkpoint: ctx.checkpoint }) : input),
      } as Codec<JsonValue> as Codec<T, T>;
    case "int8":
    case "uint8":
    case "int16":
    case "uint16":
    case "int32":
    case "uint32":
    case "int64":
    case "uint64":
      return integerCodec(name, base, numbers) as Codec<unknown> as Codec<T, T>;
    case "decimal":
      return decimalCodec(base, numbers) as Codec<Decimal> as Codec<T, T>;
    case "float32":
    case "float64":
      return floatCodec(name, base, numbers) as Codec<number> as Codec<T, T>;
    case "date-only":
      return stringScalar<DateOnly>(base, validateDateOnly, parseDateOnly, formatDateOnly) as Codec<DateOnly> as Codec<T, T>;
    case "time-only":
      return stringScalar<TimeOnly>(base, validateTimeOnly, parseTimeOnly, formatTimeOnly) as Codec<TimeOnly> as Codec<T, T>;
    case "datetime-utc":
      return stringScalar<DateTimeUtc>(base, (v, p) => ({ kind: "datetime-utc", ticks: validateDateTimeTicks(v, p, "utc") }), parseDateTimeUtc, formatDateTimeUtc) as Codec<DateTimeUtc> as Codec<T, T>;
    case "datetime-unspecified":
      return stringScalar<DateTimeUnspecified>(base, (v, p) => ({ kind: "datetime-unspecified", ticks: validateDateTimeTicks(v, p, "unspecified") }), parseDateTimeUnspecified, formatDateTimeUnspecified) as Codec<DateTimeUnspecified> as Codec<T, T>;
    case "datetime-local-wire":
      return stringScalar<DateTimeLocalWire>(base, validateDateTimeLocalWire, parseDateTimeLocalWire, formatDateTimeLocalWire) as Codec<DateTimeLocalWire> as Codec<T, T>;
    case "datetime-offset":
      return stringScalar<DateTimeOffset>(base, validateDateTimeOffset, parseDateTimeOffset, formatDateTimeOffset) as Codec<DateTimeOffset> as Codec<T, T>;
    case "duration":
      return stringScalar<Duration>(base, validateDuration, parseDuration, formatDuration) as Codec<Duration> as Codec<T, T>;
  }
}

function numberSuffix(name: ScalarName, numbers: NumberProfile): string {
  const isNumber = name === "decimal" || name === "float32" || name === "float64" || name in integerRanges;
  if (!isNumber) {
    return "";
  }
  const isFloat = name === "float32" || name === "float64";
  const r = numbers.readFromString;
  const w = numbers.writeAsString;
  const n = isFloat && numbers.namedLiterals;
  if (!r && !w && !n) {
    return "";
  }
  return "." + (r ? "r" : "") + (w ? "w" : "") + (n ? "n" : "");
}

function isJsonValue(v: unknown): v is JsonValue {
  if (typeof v !== "object" || v === null) {
    return false;
  }
  const kind = (v as { kind?: unknown }).kind;
  return kind === "null" || kind === "boolean" || kind === "string" || kind === "number" || kind === "array" || kind === "object";
}

function stringScalar<T>(base: { id: string; typeId: string }, validate: (v: unknown, path: string) => T, parse: (s: string, path: string) => T, format: (v: T) => string): Codec<T, T> {
  return {
    ...base,
    validateDomain: (v, ctx) => validate(v, ctx.path),
    encodeRequest: (v, ctx) => ({ kind: "string", value: format(validate(v, ctx.path)) }),
    decodeResponse: (w, ctx) => parse(expectString(w, ctx, base.typeId), ctx.path),
    encodeKey: (v, ctx) => format(validate(v, ctx.path)),
    decodeKey: (s, ctx) => parse(s, ctx.path),
    parseRequestInput: (input, ctx) => parse((typeof input === "string" ? input : expectString(input, ctx, base.typeId)).trim(), ctx.path),
  };
}

function integerCodec(name: IntegerScalarName, base: { id: string; typeId: string }, numbers: NumberProfile): Codec<number | bigint> {
  const small = isSmallInteger(name);
  const validate = (v: unknown, path: string): number | bigint => (small ? validateSmallInteger(name, v, path) : validateBigInteger(name, v, path));
  const toDomain = (b: bigint): number | bigint => (small ? Number(b) : b);
  return {
    ...base,
    validateDomain: (v, ctx) => validate(v, ctx.path),
    encodeRequest: (v, ctx) => {
      const value = validate(v, ctx.path);
      // the canonical writer always emits a JSON number, which the server reads under every number handling; the string forms
      // (AllowReadingFromString) are read aliases, and WriteAsString only changes what the server writes
      return { kind: "number", text: typeof value === "bigint" ? formatInteger(value) : String(value) };
    },
    decodeResponse: (w, ctx) => {
      // a profile that writes numbers as strings never writes a bare number: that token is a server-write negative
      if (w.kind === "number" && !numbers.writeAsString) {
        return toDomain(parseIntegerLexeme(name, w.text, ctx.path));
      }
      if (w.kind === "string" && numbers.writeAsString) {
        return toDomain(parseIntegerLexeme(name, w.value, ctx.path));
      }
      throw new CodecError("type-mismatch", ctx.path, `${name} requires a JSON ${numbers.writeAsString ? "string" : "number"} but found ${w.kind}`);
    },
    // Dictionary keys use the parser of the CLR type: leading zeros and '+' are accepted on read, canonical digits are written
    encodeKey: (v, ctx) => {
      const value = validate(v, ctx.path);
      return typeof value === "bigint" ? formatInteger(value) : String(value);
    },
    decodeKey: (s, ctx) => toDomain(parseIntegerString(name, s, ctx.path)),
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        return toDomain(parseIntegerString(name, input.trim(), ctx.path));
      }
      if (input.kind === "number") {
        return toDomain(parseIntegerLexeme(name, input.text, ctx.path));
      }
      if (input.kind === "string") {
        return toDomain(parseIntegerString(name, input.value, ctx.path));
      }
      throw new CodecError("type-mismatch", ctx.path, `${name} input must be a number`);
    },
  };
}

function decimalCodec(base: { id: string; typeId: string }, numbers: NumberProfile): Codec<Decimal> {
  return {
    ...base,
    validateDomain: (v, ctx) => validateDecimal(v, ctx.path),
    encodeRequest: (v, ctx) => ({ kind: "number", text: formatDecimal(validateDecimal(v, ctx.path)) }),
    decodeResponse: (w, ctx) => {
      if (w.kind === "number" && !numbers.writeAsString) {
        return parseDecimalLexeme(w.text, ctx.path);
      }
      if (w.kind === "string" && numbers.writeAsString) {
        return parseDecimalLexeme(w.value, ctx.path);
      }
      throw new CodecError("type-mismatch", ctx.path, `decimal requires a JSON ${numbers.writeAsString ? "string" : "number"} but found ${w.kind}`);
    },
    encodeKey: (v, ctx) => formatDecimal(validateDecimal(v, ctx.path)),
    decodeKey: (s, ctx) => parseDecimalLexeme(s.startsWith("+") ? s.slice(1) : s, ctx.path),
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        const t = input.trim();
        return parseDecimalLexeme(t.startsWith("+") ? t.slice(1) : t, ctx.path);
      }
      if (input.kind === "number") {
        return parseDecimalLexeme(input.text, ctx.path);
      }
      if (input.kind === "string") {
        return parseDecimalLexeme(input.value, ctx.path);
      }
      throw new CodecError("type-mismatch", ctx.path, "decimal input must be a number");
    },
  };
}

function floatCodec(name: "float32" | "float64", base: { id: string; typeId: string }, numbers: NumberProfile): Codec<number> {
  const single = name === "float32";
  const parse = single ? parseFloat32Lexeme : parseFloat64Lexeme;
  const format = single ? formatFloat32 : formatFloat64;
  const validate = (v: unknown, path: string): number => {
    if (typeof v !== "number") {
      throw new CodecError("type-mismatch", path, `${name} requires a number`);
    }
    if (single && Number.isFinite(v) && Math.fround(v) !== v) {
      throw new CodecError("range", path, "value is not representable as float32");
    }
    if (!Number.isFinite(v) && !numbers.namedLiterals) {
      throw new CodecError("range", path, `${name} must be finite unless the profile allows named floating-point literals`);
    }
    return v;
  };
  const named = (s: string, path: string): number => {
    switch (s) {
      case "NaN":
        return Number.NaN;
      case "Infinity":
        return Number.POSITIVE_INFINITY;
      case "-Infinity":
        return Number.NEGATIVE_INFINITY;
      default:
        throw new CodecError("grammar", path, `${name} named literal must be NaN, Infinity or -Infinity`);
    }
  };
  return {
    ...base,
    validateDomain: (v, ctx) => validate(v, ctx.path),
    encodeRequest: (v, ctx) => {
      const value = validate(v, ctx.path);
      if (!Number.isFinite(value)) {
        return { kind: "string", value: Number.isNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity" };
      }
      return { kind: "number", text: format(value, ctx.path) };
    },
    decodeResponse: (w, ctx) => {
      if (w.kind === "number" && !numbers.writeAsString) {
        return parse(w.text, ctx.path);
      }
      if (w.kind === "string") {
        if (numbers.namedLiterals && (w.value === "NaN" || w.value === "Infinity" || w.value === "-Infinity")) {
          return named(w.value, ctx.path);
        }
        if (numbers.writeAsString) {
          return parse(w.value, ctx.path);
        }
      }
      throw new CodecError("type-mismatch", ctx.path, `${name} requires a JSON number but found ${w.kind}`);
    },
    encodeKey: (v, ctx) => {
      const value = validate(v, ctx.path);
      if (!Number.isFinite(value)) {
        throw new CodecError("range", ctx.path, "non-finite keys are not supported");
      }
      return format(value, ctx.path);
    },
    decodeKey: (s, ctx) => parse(s.startsWith("+") ? s.slice(1) : s, ctx.path),
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        const t = input.trim();
        if (numbers.namedLiterals && (t === "NaN" || t === "Infinity" || t === "-Infinity")) {
          return named(t, ctx.path);
        }
        return parse(t.startsWith("+") ? t.slice(1) : t, ctx.path);
      }
      if (input.kind === "number") {
        return parse(input.text, ctx.path);
      }
      if (input.kind === "string") {
        return parse(input.value, ctx.path);
      }
      throw new CodecError("type-mismatch", ctx.path, `${name} input must be a number`);
    },
  };
}
