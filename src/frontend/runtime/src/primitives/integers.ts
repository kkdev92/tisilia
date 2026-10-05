import { CodecError } from "../codec/errors.js";

/** Integer scalars. 64-bit values are branded bigints; smaller ones are validated numbers. */
export type IntegerScalarName = "int8" | "uint8" | "int16" | "uint16" | "int32" | "uint32" | "int64" | "uint64";

declare const brand: unique symbol;

/** Branded bigint for System.Int64: never passes through Number. */
export type Int64 = bigint & { readonly [brand]: "Int64" };
/** Branded bigint for System.UInt64. */
export type UInt64 = bigint & { readonly [brand]: "UInt64" };

export const integerRanges: Readonly<Record<IntegerScalarName, readonly [bigint, bigint]>> = Object.freeze({
  int8: [-128n, 127n],
  uint8: [0n, 255n],
  int16: [-32768n, 32767n],
  uint16: [0n, 65535n],
  int32: [-2147483648n, 2147483647n],
  uint32: [0n, 4294967295n],
  int64: [-9223372036854775808n, 9223372036854775807n],
  uint64: [0n, 18446744073709551615n],
});

/** Canonical integer grammar `-?(0|[1-9][0-9]*)` (unsigned scalars additionally forbid the sign). */
const canonicalInteger = /^-?(?:0|[1-9][0-9]*)$/;

export function isCanonicalIntegerLexeme(text: string): boolean {
  return canonicalInteger.test(text);
}

/** Parses a JSON number lexeme as an integer scalar. Fractions and exponents are rejected like System.Text.Json does for integer types. */
export function parseIntegerLexeme(name: IntegerScalarName, text: string, path: string): bigint {
  if (!canonicalInteger.test(text)) {
    throw new CodecError("grammar", path, `${name} requires an integer lexeme without fraction or exponent`);
  }
  const value = BigInt(text);
  return checkIntegerRange(name, value, path);
}

/**
 * Parses the string form accepted by JsonNumberHandling.AllowReadingFromString / dictionary keys: an optional sign
 * and digits, leading zeros allowed (observed on .NET 10: "012" and "+12" accepted, " 1" and "1e1" rejected).
 */
export function parseIntegerString(name: IntegerScalarName, text: string, path: string): bigint {
  if (!/^[+-]?[0-9]+$/.test(text)) {
    throw new CodecError("grammar", path, `${name} string form must be [+-]?digits`);
  }
  const value = BigInt(text);
  return checkIntegerRange(name, value, path);
}

export function checkIntegerRange(name: IntegerScalarName, value: bigint, path: string): bigint {
  const [min, max] = integerRanges[name];
  if (value < min || value > max) {
    throw new CodecError("range", path, `${name} value is outside [${min}, ${max}]`);
  }
  return value === 0n ? 0n : value; // -0 normalizes to 0
}

export function formatInteger(value: bigint): string {
  return value.toString(10);
}

export function int64(value: bigint | number | string): Int64 {
  const v = typeof value === "bigint" ? value : BigInt(value);
  return checkIntegerRange("int64", v, "") as Int64;
}

export function uint64(value: bigint | number | string): UInt64 {
  const v = typeof value === "bigint" ? value : BigInt(value);
  return checkIntegerRange("uint64", v, "") as UInt64;
}

export function isSmallInteger(name: IntegerScalarName): boolean {
  return name !== "int64" && name !== "uint64";
}

/** Validates a public-domain value for a small integer scalar (JS number). */
export function validateSmallInteger(name: IntegerScalarName, value: unknown, path: string): number {
  if (typeof value !== "number" || !Number.isInteger(value)) {
    throw new CodecError("type-mismatch", path, `${name} requires an integer number`);
  }
  const [min, max] = integerRanges[name];
  if (BigInt(value) < min || BigInt(value) > max) {
    throw new CodecError("range", path, `${name} value is outside [${min}, ${max}]`);
  }
  return Object.is(value, -0) ? 0 : value;
}

/** Validates a public-domain value for int64/uint64 (bigint only; numbers are refused to avoid silent precision loss). */
export function validateBigInteger(name: IntegerScalarName, value: unknown, path: string): bigint {
  if (typeof value !== "bigint") {
    throw new CodecError("type-mismatch", path, `${name} requires a bigint`);
  }
  return checkIntegerRange(name, value, path);
}
