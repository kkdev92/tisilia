import { CodecError } from "../codec/errors.js";
import { pow10, splitDecimalLexeme } from "./bigmath.js";

/**
 * System.Decimal public representation: sign, 96-bit coefficient and scale 0..28.
 * Equality by default is numeric (`1.0` equals `1.00`); scale and the sign of zero are reported as not preserved.
 */
export interface Decimal {
  readonly kind: "decimal";
  readonly sign: -1 | 1;
  readonly coefficient: bigint;
  readonly scale: number;
}

export const decimalMaxCoefficient = 79228162514264337593543950335n; // 2^96 - 1
export const decimalMaxScale = 28;

export function decimal(sign: -1 | 1, coefficient: bigint, scale: number): Decimal {
  if (coefficient < 0n || coefficient > decimalMaxCoefficient) {
    throw new RangeError("decimal coefficient out of range");
  }
  if (!Number.isInteger(scale) || scale < 0 || scale > decimalMaxScale) {
    throw new RangeError("decimal scale out of range");
  }
  return { kind: "decimal", sign, coefficient, scale };
}

export function isDecimal(value: unknown): value is Decimal {
  return (
    typeof value === "object" &&
    value !== null &&
    (value as Decimal).kind === "decimal" &&
    ((value as Decimal).sign === 1 || (value as Decimal).sign === -1) &&
    typeof (value as Decimal).coefficient === "bigint" &&
    typeof (value as Decimal).scale === "number"
  );
}

export function validateDecimal(value: unknown, path: string): Decimal {
  if (!isDecimal(value)) {
    throw new CodecError("type-mismatch", path, "Decimal requires {sign, coefficient, scale}");
  }
  if (value.coefficient < 0n || value.coefficient > decimalMaxCoefficient) {
    throw new CodecError("range", path, "decimal coefficient exceeds 96 bits");
  }
  if (!Number.isInteger(value.scale) || value.scale < 0 || value.scale > decimalMaxScale) {
    throw new CodecError("range", path, "decimal scale must be 0..28");
  }
  return value;
}

export interface DecimalParseOptions {
  /**
   * Accept lexemes with more than 28 fractional digits by rounding half away from zero the way the .NET 10 reader
   * does (observed: 1.000…00025 → …03). Off by default: Tisilia never calls a rounded alias lossless.
   */
  readonly roundOverPrecision?: boolean;
}

/**
 * Parses an RFC 8259 number lexeme (exponent allowed) exactly like System.Text.Json's decimal reader:
 * scale = fraction digits − exponent, clamped at 0 (observed: "1.0e2"→100, "100e-2"→1.00, "0.10e1"→1.0).
 */
export function parseDecimalLexeme(text: string, path: string, options: DecimalParseOptions = {}): Decimal {
  const parts = splitDecimalLexeme(text);
  if (parts === undefined || parts.digits < 0n) {
    throw new CodecError(parts === undefined ? "grammar" : "range", path, "invalid decimal lexeme");
  }
  let coefficient = parts.digits;
  let scale = -parts.exp10;
  if (scale < 0) {
    if (-scale > 60) {
      throw new CodecError("range", path, "decimal value exceeds the representable range");
    }
    coefficient *= pow10(-scale);
    scale = 0;
  }
  if (scale > decimalMaxScale) {
    // drop trailing zeros first: they carry no value beyond the representable scale
    while (scale > decimalMaxScale && coefficient % 10n === 0n) {
      coefficient /= 10n;
      scale--;
    }
    if (scale > decimalMaxScale) {
      if (!options.roundOverPrecision) {
        throw new CodecError("precision", path, "decimal lexeme has more than 28 fractional digits");
      }
      const drop = scale - decimalMaxScale;
      const divisor = pow10(drop);
      const quotient = coefficient / divisor;
      const remainder = coefficient % divisor;
      coefficient = remainder * 2n >= divisor ? quotient + 1n : quotient;
      scale = decimalMaxScale;
    }
  }
  if (coefficient > decimalMaxCoefficient) {
    throw new CodecError("range", path, "decimal value exceeds 96 bits");
  }
  return { kind: "decimal", sign: parts.negative && coefficient !== 0n ? -1 : 1, coefficient, scale };
}

/** Canonical writer: fixed-point decimal, scale preserved, no exponent, no sign on zero. */
export function formatDecimal(value: Decimal): string {
  const digits = value.coefficient.toString(10);
  let body: string;
  if (value.scale === 0) {
    body = digits;
  } else {
    const padded = digits.padStart(value.scale + 1, "0");
    body = padded.slice(0, padded.length - value.scale) + "." + padded.slice(padded.length - value.scale);
  }
  return value.sign < 0 && value.coefficient !== 0n ? "-" + body : body;
}

/** Numeric comparison ignoring scale and the sign of zero. */
export function compareDecimal(a: Decimal, b: Decimal): number {
  const aZero = a.coefficient === 0n;
  const bZero = b.coefficient === 0n;
  const as = aZero ? 0 : a.sign;
  const bs = bZero ? 0 : b.sign;
  if (as !== bs) {
    return as < bs ? -1 : 1;
  }
  if (as === 0) {
    return 0;
  }
  const scale = Math.max(a.scale, b.scale);
  const av = a.coefficient * pow10(scale - a.scale);
  const bv = b.coefficient * pow10(scale - b.scale);
  const cmp = av < bv ? -1 : av > bv ? 1 : 0;
  return as < 0 ? -cmp : cmp;
}

export function decimalEquals(a: Decimal, b: Decimal): boolean {
  return compareDecimal(a, b) === 0;
}

/**
 * Convenience constructor from fixed-point text ("12.50", "-0.5", "+3", "007.10"): the scale is the number of fraction digits, a
 * sign and leading zeros of the integer part are accepted (form input), exponents and grouping are not.
 */
export function decimalFromString(text: string): Decimal {
  const match = /^([+-]?)(\d+)(\.\d+)?$/.exec(text.trim());
  if (match === null) {
    throw new CodecError("grammar", "", "decimal input must be fixed-point digits");
  }
  const integer = match[2]!.replace(/^0+(?=\d)/, "");
  return parseDecimalLexeme((match[1] === "-" ? "-" : "") + integer + (match[3] ?? ""), "");
}
