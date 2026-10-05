import { CodecError } from "../codec/errors.js";
import { bitLength, pow10, splitDecimalLexeme } from "./bigmath.js";

/**
 * Correctly rounded decimal → binary conversion (round-half-to-even) using exact BigInt arithmetic
 * (float32 must not be derived through Number → Math.fround). Works for any lexeme length.
 */
interface BinaryFormat {
  readonly mantissaBits: number; // 24 for binary32, 53 for binary64
  readonly minExponent: number; // exponent of the smallest subnormal: -149 / -1074
  readonly maxExponent: number; // overflow when value >= 2^maxExponent: 128 / 1024
}

const binary32: BinaryFormat = { mantissaBits: 24, minExponent: -149, maxExponent: 128 };
const binary64: BinaryFormat = { mantissaBits: 53, minExponent: -1074, maxExponent: 1024 };

export interface FloatParseResult {
  readonly value: number;
  readonly overflow: boolean;
}

function roundToBinary(digits: bigint, exp10: number, negative: boolean, format: BinaryFormat): FloatParseResult {
  if (digits === 0n) {
    return { value: negative ? -0 : 0, overflow: false };
  }
  // value = N / D
  const N = exp10 >= 0 ? digits * pow10(exp10) : digits;
  const D = exp10 >= 0 ? 1n : pow10(-exp10);
  const mant = BigInt(format.mantissaBits);
  const two = 2n;
  // Initial exponent estimate so that q = floor(N / (D * 2^e)) has about mantissaBits bits.
  let e = bitLength(N) - bitLength(D) - format.mantissaBits;
  const compute = (exp: number): { q: bigint; rem2: bigint; den: bigint } => {
    // q = floor(N / (D*2^exp)); compare 2*rem with den for rounding
    if (exp >= 0) {
      const den = D << BigInt(exp);
      const q = N / den;
      return { q, rem2: (N - q * den) * two, den };
    }
    const num = N << BigInt(-exp);
    const q = num / D;
    return { q, rem2: (num - q * D) * two, den: D };
  };
  let r = compute(e);
  while (r.q >= 1n << mant) {
    e++;
    r = compute(e);
  }
  while (r.q < 1n << (mant - 1n) && e > format.minExponent) {
    e--;
    r = compute(e);
  }
  if (e < format.minExponent) {
    e = format.minExponent;
    r = compute(e);
  }
  let q = r.q;
  if (r.rem2 > r.den || (r.rem2 === r.den && (q & 1n) === 1n)) {
    q += 1n;
    if (q === 1n << mant) {
      q >>= 1n;
      e++;
    }
  }
  if (e + bitLength(q) > format.maxExponent) {
    return { value: negative ? -Infinity : Infinity, overflow: true };
  }
  // q < 2^53 is exact as a double; multiplying by a power of two is exact when representable.
  const magnitude = Number(q) * 2 ** e;
  return { value: negative ? -magnitude : magnitude, overflow: false };
}

function parseFloatLexemeCore(text: string, path: string, format: BinaryFormat, name: string): FloatParseResult {
  const parts = splitDecimalLexeme(text);
  if (parts === undefined) {
    throw new CodecError("grammar", path, `invalid ${name} lexeme`);
  }
  if (parts.digits < 0n) {
    // absurd exponent: either overflow or underflow to zero
    return parts.exp10 > 0 ? { value: parts.negative ? -Infinity : Infinity, overflow: true } : { value: parts.negative ? -0 : 0, overflow: false };
  }
  if (parts.digits === 0n) {
    return { value: parts.negative ? -0 : 0, overflow: false };
  }
  // Fast bounds: value < 10^(digitCount+exp10); if that is below 2^(minExponent-1) the result is zero.
  const digitCount = parts.digits.toString(10).length;
  const magnitudeEstimate = digitCount + parts.exp10; // value < 10^magnitudeEstimate
  if (magnitudeEstimate < -400) {
    return { value: parts.negative ? -0 : 0, overflow: false };
  }
  if (magnitudeEstimate > 400) {
    return { value: parts.negative ? -Infinity : Infinity, overflow: true };
  }
  return roundToBinary(parts.digits, parts.exp10, parts.negative, format);
}

/** Exact decimal → binary32 rounding. Throws range on overflow; subnormals and signed zero are preserved. */
export function parseFloat32Lexeme(text: string, path: string): number {
  const r = parseFloatLexemeCore(text, path, binary32, "float32");
  if (r.overflow) {
    throw new CodecError("range", path, "float32 value overflows binary32");
  }
  return r.value;
}

/** Exact decimal → binary64 rounding. Throws range on overflow. */
export function parseFloat64Lexeme(text: string, path: string): number {
  const r = parseFloatLexemeCore(text, path, binary64, "float64");
  if (r.overflow) {
    throw new CodecError("range", path, "float64 value overflows binary64");
  }
  return r.value;
}

export function isFloat32Value(value: number): boolean {
  return Number.isFinite(value) && Math.fround(value) === value;
}

/**
 * Shortest decimal lexeme that decodes (under exact rounding) to the same float32 value. `toPrecision` yields the
 * correctly rounded decimal expansion of the exact binary value, so increasing precision until round-trip is the
 * shortest representation (the same idea as Ryu, verified by exact re-parsing rather than by Math.fround).
 */
export function formatFloat32(value: number, path = ""): string {
  if (!Number.isFinite(value)) {
    throw new CodecError("range", path, "float32 canonical writer only writes finite values");
  }
  if (Math.fround(value) !== value) {
    throw new CodecError("type-mismatch", path, "value is not exactly representable as float32");
  }
  if (value === 0) {
    return Object.is(value, -0) ? "-0" : "0";
  }
  for (let precision = 1; precision <= 9; precision++) {
    const candidate = normalizeExponent(value.toPrecision(precision));
    // a short candidate near the top of the range may round past the largest finite value; that is not the answer
    const parsed = parseFloatLexemeCore(candidate, path, binary32, "float32");
    if (!parsed.overflow && Object.is(parsed.value, value)) {
      return candidate;
    }
  }
  throw new CodecError("range", path, "float32 could not be formatted");
}

/** Shortest round-trip binary64 lexeme (ECMAScript Number::toString), rejecting non-finite values. */
export function formatFloat64(value: number, path = ""): string {
  if (!Number.isFinite(value)) {
    throw new CodecError("range", path, "float64 canonical writer only writes finite values");
  }
  if (value === 0) {
    return Object.is(value, -0) ? "-0" : "0";
  }
  return String(value);
}

/**
 * `s` without its trailing zeros, by index. `/0+$/` is retried from every zero of a run that does not end the string; the
 * strings here are at most a few dozen characters, so it was never slow, but the loop does not depend on that.
 */
function withoutTrailingZeros(s: string): string {
  let end = s.length;
  while (end > 0 && s.charCodeAt(end - 1) === 0x30) {
    end--;
  }
  return s.slice(0, end);
}

/** Rewrites `1.50e+2`-style output of toPrecision into a minimal valid JSON lexeme (strip trailing zeros of the mantissa). */
function normalizeExponent(s: string): string {
  const e = s.indexOf("e");
  let mantissa = e >= 0 ? s.slice(0, e) : s;
  const exponent = e >= 0 ? s.slice(e) : "";
  if (mantissa.includes(".")) {
    mantissa = withoutTrailingZeros(mantissa).replace(/\.$/, "");
  }
  return mantissa + exponent;
}

/**
 * Formats a double the way System.Text.Json / .NET Core 3.0+ does ("R" shortest digits, scientific when the decimal
 * exponent is < -5 or ≥ 17 for double / ≥ 9 for float, `E+XX`/`E-XX` with at least two exponent digits).
 * Used only by the optional exact-wire (G3) equivalence; the canonical writer uses the ECMAScript form.
 */
export function formatDotnetShortest(value: number, single: boolean): string {
  if (!Number.isFinite(value)) {
    throw new RangeError("non-finite");
  }
  if (value === 0) {
    return Object.is(value, -0) ? "-0" : "0";
  }
  const shortest = single ? formatFloat32(value) : formatFloat64(value);
  const negative = shortest.startsWith("-");
  const body = negative ? shortest.slice(1) : shortest;
  // decompose into digits and decimal exponent (value = 0.d1d2... × 10^n style)
  const eIdx = body.search(/e/i);
  const mantissa = eIdx >= 0 ? body.slice(0, eIdx) : body;
  const expPart = eIdx >= 0 ? Number.parseInt(body.slice(eIdx + 1), 10) : 0;
  const dot = mantissa.indexOf(".");
  const intPart = dot >= 0 ? mantissa.slice(0, dot) : mantissa;
  const fracPart = dot >= 0 ? mantissa.slice(dot + 1) : "";
  let digits = (intPart + fracPart).replace(/^0+/, "");
  const leadingZerosRemoved = intPart.length + fracPart.length - digits.length;
  const pointPos = intPart.length - leadingZerosRemoved + expPart; // number of digits before the decimal point
  digits = withoutTrailingZeros(digits);
  const sciExp = pointPos - 1;
  const precision = single ? 9 : 17;
  let out: string;
  // .NET uses positional notation when -5 < exponent < precision (observed: 0.0001 positional, 1E-05 scientific, 1E+17 scientific)
  if (sciExp > -5 && sciExp < precision) {
    if (pointPos <= 0) {
      out = "0." + "0".repeat(-pointPos) + digits;
    } else if (pointPos >= digits.length) {
      out = digits + "0".repeat(pointPos - digits.length);
    } else {
      out = digits.slice(0, pointPos) + "." + digits.slice(pointPos);
    }
  } else {
    const absExp = Math.abs(sciExp);
    out = digits[0]! + (digits.length > 1 ? "." + digits.slice(1) : "") + "E" + (sciExp < 0 ? "-" : "+") + String(absExp).padStart(2, "0");
  }
  return negative ? "-" + out : out;
}
