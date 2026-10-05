/** Exact decimal lexeme decomposition and BigInt helpers shared by the numeric codecs. */

const pow10Cache: bigint[] = [1n];

export function pow10(n: number): bigint {
  if (n < 0 || !Number.isSafeInteger(n)) {
    throw new RangeError("pow10 exponent must be a non-negative integer");
  }
  while (pow10Cache.length <= n) {
    pow10Cache.push(pow10Cache[pow10Cache.length - 1]! * 10n);
  }
  return pow10Cache[n]!;
}

export interface DecimalLexeme {
  readonly negative: boolean;
  /** Significand digits as a non-negative integer (leading zeros removed; 0n for zero). */
  readonly digits: bigint;
  /** value = digits × 10^exp10 */
  readonly exp10: number;
  /** Number of fractional digits written in the lexeme (before applying the exponent). */
  readonly fractionDigits: number;
  readonly hasFraction: boolean;
  readonly hasExponent: boolean;
  readonly integerPartHasLeadingZero: boolean;
}

const lexemePattern = /^([+-]?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/;

/**
 * Splits `[-]digits[.digits][e[+-]digits]` into an exact (digits, exp10) pair. Accepts a leading '+' and leading
 * zeros so that callers can apply their own grammar policy; RFC 8259 callers pre-validate the lexeme.
 */
export function splitDecimalLexeme(text: string): DecimalLexeme | undefined {
  const m = lexemePattern.exec(text);
  if (m === null) {
    return undefined;
  }
  const negative = m[1] === "-";
  const intPart = m[2]!;
  const fracPart = m[3] ?? "";
  const expPart = m[4];
  let exp10 = fracPart.length === 0 ? 0 : -fracPart.length;
  if (expPart !== undefined) {
    if (expPart.replace(/^[+-]/, "").length > 9) {
      // an exponent this large cannot describe a representable float or decimal; treat as out of range
      return {
        negative,
        digits: -1n,
        exp10: expPart.startsWith("-") ? -1_000_000_000 : 1_000_000_000,
        fractionDigits: fracPart.length,
        hasFraction: fracPart.length > 0,
        hasExponent: true,
        integerPartHasLeadingZero: intPart.length > 1 && intPart.startsWith("0"),
      };
    }
    exp10 += Number.parseInt(expPart, 10);
  }
  const all = (intPart + fracPart).replace(/^0+/, "");
  const digits = all.length === 0 ? 0n : BigInt(all);
  return {
    negative,
    digits,
    exp10,
    fractionDigits: fracPart.length,
    hasFraction: fracPart.length > 0,
    hasExponent: expPart !== undefined,
    integerPartHasLeadingZero: intPart.length > 1 && intPart.startsWith("0"),
  };
}

export function bitLength(n: bigint): number {
  if (n < 0n) {
    n = -n;
  }
  if (n === 0n) {
    return 0;
  }
  return n.toString(2).length;
}

export function bigAbs(n: bigint): bigint {
  return n < 0n ? -n : n;
}
