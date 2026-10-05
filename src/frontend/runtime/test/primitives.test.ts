import { describe, expect, it } from "vitest";
import { compareDecimal, decimalFromString, formatDecimal, parseDecimalLexeme } from "../src/primitives/decimal.js";
import { formatDotnetShortest, formatFloat32, formatFloat64, parseFloat32Lexeme, parseFloat64Lexeme } from "../src/primitives/float.js";
import { parseIntegerLexeme, parseIntegerString } from "../src/primitives/integers.js";
import { decodeBase64, encodeBase64, parseGuid } from "../src/primitives/text.js";
import {
  dateTimeOffsetFromDate,
  dateTimeOffsetToDate,
  dateTimeUtcFromDate,
  dateTimeUtcToDate,
  formatDateOnly,
  formatDateTimeOffset,
  formatDateTimeUtc,
  formatDuration,
  formatTimeOnly,
  parseDateOnly,
  parseDateTimeOffset,
  parseDateTimeUnspecified,
  parseDateTimeUtc,
  parseDuration,
  parseTimeOnly,
} from "../src/primitives/datetime.js";
import { CodecError } from "../src/codec/errors.js";

// Expected values are what .NET 10's System.Text.Json produces (tests/oracle/DotnetStjOracle prints them).

describe("decimal", () => {
  it("keeps scale like System.Text.Json (1.00 stays 1.00, 1.2300 stays 1.2300)", () => {
    expect(formatDecimal(parseDecimalLexeme("1.00", ""))).toBe("1.00");
    expect(formatDecimal(parseDecimalLexeme("1.2300", ""))).toBe("1.2300");
    expect(formatDecimal(parseDecimalLexeme("0.0000000000000000000000000001", ""))).toBe("0.0000000000000000000000000001");
  });

  it("applies the .NET scale rule for exponents: 1.0e2→100, 100e-2→1.00, 0.10e1→1.0, 1e-28 scale 28", () => {
    expect(formatDecimal(parseDecimalLexeme("1.0e2", ""))).toBe("100");
    expect(formatDecimal(parseDecimalLexeme("1.50e2", ""))).toBe("150");
    expect(formatDecimal(parseDecimalLexeme("100e-2", ""))).toBe("1.00");
    expect(formatDecimal(parseDecimalLexeme("0.10e1", ""))).toBe("1.0");
    expect(parseDecimalLexeme("1e-28", "").scale).toBe(28);
    expect(formatDecimal(parseDecimalLexeme("1e28", ""))).toBe("10000000000000000000000000000");
  });

  it("accepts the maximum coefficient and rejects overflow", () => {
    expect(formatDecimal(parseDecimalLexeme("79228162514264337593543950335", ""))).toBe("79228162514264337593543950335");
    expect(() => parseDecimalLexeme("79228162514264337593543950336", "")).toThrow(CodecError);
    expect(() => parseDecimalLexeme("1e29", "")).toThrow(CodecError);
    expect(() => parseDecimalLexeme("79228162514264337593543950335.5", "")).toThrow(CodecError);
  });

  it("drops the sign of zero on write like .NET and compares numerically", () => {
    expect(formatDecimal(parseDecimalLexeme("-0.00", ""))).toBe("0.00");
    expect(compareDecimal(decimalFromString("1.0"), decimalFromString("1.00"))).toBe(0);
    expect(compareDecimal(decimalFromString("-1.5"), decimalFromString("1.5"))).toBe(-1);
    expect(compareDecimal(decimalFromString("2"), decimalFromString("1.999"))).toBe(1);
  });

  it("builds decimals from form text: sign and leading zeros accepted, the scale is the fraction digits", () => {
    expect(formatDecimal(decimalFromString("007.10"))).toBe("7.10");
    expect(formatDecimal(decimalFromString(" +3 "))).toBe("3");
    expect(formatDecimal(decimalFromString("-00.50"))).toBe("-0.50");
    expect(formatDecimal(decimalFromString("0000"))).toBe("0");
    for (const bad of ["1e2", "1.", ".5", "1,000", "", "--1", "0x10"]) {
      expect(() => decimalFromString(bad), bad).toThrow(CodecError);
    }
  });

  it("rejects more than 28 fractional digits unless rounding is opted in (then half away from zero like .NET)", () => {
    expect(() => parseDecimalLexeme("1.00000000000000000000000000025", "")).toThrow(/28 fractional/);
    expect(formatDecimal(parseDecimalLexeme("1.00000000000000000000000000025", "", { roundOverPrecision: true }))).toBe("1.0000000000000000000000000003");
    expect(formatDecimal(parseDecimalLexeme("1.000000000000000000000000000449", "", { roundOverPrecision: true }))).toBe("1.0000000000000000000000000004");
    expect(formatDecimal(parseDecimalLexeme("1.000000000000000000000000000450", "", { roundOverPrecision: true }))).toBe("1.0000000000000000000000000005");
    // trailing zeros beyond scale 28 carry no value and are accepted
    expect(formatDecimal(parseDecimalLexeme("0.00000000000000000000000000000", ""))).toBe("0.0000000000000000000000000000");
  });
});

describe("float32", () => {
  it("rounds decimal lexemes directly to binary32 with ties-to-even (no double rounding)", () => {
    const bits = (v: number): string => {
      const buf = new DataView(new ArrayBuffer(4));
      buf.setFloat32(0, v);
      return buf.getUint32(0).toString(16).padStart(8, "0");
    };
    expect(bits(parseFloat32Lexeme("1.00000005960464477539062500", ""))).toBe("3f800000"); // exact halfway → even
    expect(bits(parseFloat32Lexeme("1.0000000596046448", ""))).toBe("3f800001"); // just above halfway
    expect(parseFloat32Lexeme("16777217", "")).toBe(16777216);
    expect(parseFloat32Lexeme("0.1", "")).toBe(Math.fround(0.1));
    expect(parseFloat32Lexeme("3.4028235e38", "")).toBe(3.4028234663852886e38);
    expect(parseFloat32Lexeme("1e-45", "")).toBe(1.401298464324817e-45);
    expect(Object.is(parseFloat32Lexeme("-0.0", ""), -0)).toBe(true);
    expect(() => parseFloat32Lexeme("3.4028236e38", "")).toThrow(CodecError);
    expect(() => parseFloat32Lexeme("1e39", "")).toThrow(CodecError);
  });

  it("formats the shortest lexeme that decodes back to the same float32", () => {
    expect(formatFloat32(Math.fround(0.1))).toBe("0.1");
    expect(formatFloat32(16777216)).toBe("16777216");
    expect(formatFloat32(Math.fround(3.4028235e38))).toBe("3.4028235e+38");
    expect(formatFloat32(Math.fround(1e-45))).toBe("1e-45");
    expect(formatFloat32(Math.fround(100.5))).toBe("100.5");
    expect(formatFloat32(Math.fround(1.00000006))).toBe("1.0000001");
    expect(() => formatFloat32(0.1)).toThrow(CodecError); // not a float32 value
  });

  it("matches the .NET shortest 'R' formatting rules for the exact-wire equivalence", () => {
    expect(formatDotnetShortest(Math.fround(3.4028235e38), true)).toBe("3.4028235E+38");
    expect(formatDotnetShortest(Math.fround(1e-45), true)).toBe("1E-45");
    expect(formatDotnetShortest(Math.fround(1.1754944e-38), true)).toBe("1.1754944E-38");
    expect(formatDotnetShortest(Math.fround(1e14), true)).toBe("1E+14");
    expect(formatDotnetShortest(Math.fround(1e8), true)).toBe("100000000");
    expect(formatDotnetShortest(Math.fround(123456.7), true)).toBe("123456.7");
  });
});

describe("float64", () => {
  it("is correctly rounded for halfway and long inputs", () => {
    const bits = (v: number): string => {
      const buf = new DataView(new ArrayBuffer(8));
      buf.setFloat64(0, v);
      return buf.getBigUint64(0).toString(16).padStart(16, "0");
    };
    expect(bits(parseFloat64Lexeme("1.00000000000000011102230246251565404236316680908203125", ""))).toBe("3ff0000000000000");
    expect(bits(parseFloat64Lexeme("1.000000000000000111022302462515654042363166809082031251", ""))).toBe("3ff0000000000001");
    expect(bits(parseFloat64Lexeme("0.1", ""))).toBe("3fb999999999999a");
    expect(bits(parseFloat64Lexeme("2.2250738585072011e-308", ""))).toBe("000fffffffffffff");
    expect(parseFloat64Lexeme("9007199254740993", "")).toBe(9007199254740992);
    expect(parseFloat64Lexeme("1" + "0".repeat(400) + "e-400", "")).toBe(1);
    expect(parseFloat64Lexeme("1e-400", "")).toBe(0);
    expect(parseFloat64Lexeme("5e-324", "")).toBe(5e-324);
    expect(parseFloat64Lexeme("1.7976931348623157e308", "")).toBe(Number.MAX_VALUE);
    expect(() => parseFloat64Lexeme("1e400", "")).toThrow(CodecError);
    expect(() => parseFloat64Lexeme("1.7976931348623159e308", "")).toThrow(CodecError);
  });

  it("agrees with Number() on a corpus of random lexemes", () => {
    let seed = 12345;
    const rnd = (): number => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      return seed / 0x7fffffff;
    };
    for (let i = 0; i < 2000; i++) {
      const digits = Math.floor(rnd() * 25) + 1;
      let s = "";
      for (let d = 0; d < digits; d++) {
        s += String(Math.floor(rnd() * 10));
      }
      s = s.replace(/^0+(?=\d)/, "");
      const frac = Math.floor(rnd() * 20);
      const text = (rnd() < 0.5 ? "-" : "") + s + (frac > 0 ? "." + String(Math.floor(rnd() * 10 ** Math.min(frac, 15))).padStart(Math.min(frac, 15), "0") : "") + (rnd() < 0.3 ? "e" + String(Math.floor(rnd() * 600) - 300) : "");
      const expected = Number(text);
      if (!Number.isFinite(expected)) {
        expect(() => parseFloat64Lexeme(text, "")).toThrow(CodecError);
      } else {
        expect(Object.is(parseFloat64Lexeme(text, ""), expected), text).toBe(true);
      }
    }
  });

  it("formats -0 and finite doubles, rejecting NaN/Infinity", () => {
    expect(formatFloat64(-0)).toBe("-0");
    expect(formatFloat64(1e21)).toBe("1e+21");
    expect(formatFloat64(0.30000000000000004)).toBe("0.30000000000000004");
    expect(() => formatFloat64(Number.NaN)).toThrow(CodecError);
    expect(formatDotnetShortest(1e16, false)).toBe("10000000000000000");
    expect(formatDotnetShortest(1e17, false)).toBe("1E+17");
    expect(formatDotnetShortest(1e-5, false)).toBe("1E-05");
    expect(formatDotnetShortest(0.0001, false)).toBe("0.0001");
    expect(formatDotnetShortest(1.2345678901234568e20, false)).toBe("1.2345678901234568E+20");
  });
});

describe("integers", () => {
  it("rejects fractions, exponents and overflow like System.Text.Json", () => {
    expect(parseIntegerLexeme("int64", "9007199254740993", "")).toBe(9007199254740993n);
    expect(parseIntegerLexeme("int64", "-0", "")).toBe(0n);
    expect(() => parseIntegerLexeme("int64", "1e2", "")).toThrow(CodecError);
    expect(() => parseIntegerLexeme("int64", "1.0", "")).toThrow(CodecError);
    expect(() => parseIntegerLexeme("int64", "9223372036854775808", "")).toThrow(CodecError);
    expect(() => parseIntegerLexeme("uint64", "-1", "")).toThrow(CodecError);
    expect(parseIntegerLexeme("uint64", "18446744073709551615", "")).toBe(18446744073709551615n);
    expect(() => parseIntegerLexeme("uint8", "256", "")).toThrow(CodecError);
    expect(() => parseIntegerLexeme("int32", "01", "")).toThrow(CodecError);
  });

  it("accepts the string alias grammar observed for AllowReadingFromString (leading zeros, plus sign)", () => {
    expect(parseIntegerString("int32", "012", "")).toBe(12n);
    expect(parseIntegerString("int32", "+12", "")).toBe(12n);
    expect(() => parseIntegerString("int32", " 1", "")).toThrow(CodecError);
    expect(() => parseIntegerString("int32", "1e1", "")).toThrow(CodecError);
    expect(() => parseIntegerString("int32", "", "")).toThrow(CodecError);
  });
});

describe("guid / bytes", () => {
  it("accepts only the D format and normalizes case", () => {
    expect(parseGuid("550E8400-E29B-41D4-A716-446655440000", "")).toBe("550e8400-e29b-41d4-a716-446655440000");
    expect(() => parseGuid("{550e8400-e29b-41d4-a716-446655440000}", "")).toThrow(CodecError);
    expect(() => parseGuid("550e8400e29b41d4a716446655440000", "")).toThrow(CodecError);
  });

  it("base64 requires padding and canonical trailing bits", () => {
    expect(encodeBase64(new Uint8Array([255, 0]))).toBe("/wA=");
    expect(encodeBase64(new Uint8Array([]))).toBe("");
    expect(Array.from(decodeBase64("/wA=", ""))).toEqual([255, 0]);
    expect(Array.from(decodeBase64("", ""))).toEqual([]);
    expect(() => decodeBase64("/wA", "")).toThrow(CodecError);
    expect(() => decodeBase64("_wA=", "")).toThrow(CodecError);
    expect(() => decodeBase64("/wB=", "")).toThrow(CodecError);
    expect(() => decodeBase64("/w A=", "")).toThrow(CodecError);
    const all = new Uint8Array(256).map((_, i) => i);
    expect(Array.from(decodeBase64(encodeBase64(all), ""))).toEqual(Array.from(all));
  });
});

describe("date/time", () => {
  it("writes DateTime like System.Text.Json (fraction trimmed, Z for utc, +00:00 for offset zero)", () => {
    const utc = parseDateTimeUtc("2026-09-30T15:04:05.1010000Z", "");
    expect(formatDateTimeUtc(utc)).toBe("2026-09-30T15:04:05.101Z");
    expect(formatDateTimeUtc(parseDateTimeUtc("2026-09-30T15:04:05Z", ""))).toBe("2026-09-30T15:04:05Z");
    expect(formatDateTimeOffset(parseDateTimeOffset("2026-09-30T15:04:05Z", ""))).toBe("2026-09-30T15:04:05+00:00");
    expect(formatDateTimeOffset(parseDateTimeOffset("2026-09-30T15:04:05.1234567+09:00", ""))).toBe("2026-09-30T15:04:05.1234567+09:00");
    expect(formatDateTimeOffset(parseDateTimeOffset("2026-09-30T15:04:05-00:30", ""))).toBe("2026-09-30T15:04:05-00:30");
    expect(formatDateTimeOffset(parseDateTimeOffset("9999-12-31T23:59:59.9999999+00:00", ""))).toBe("9999-12-31T23:59:59.9999999+00:00");
  });

  it("rejects the aliases the canonical subset excludes and the values .NET rejects", () => {
    for (const bad of ["2026-09-30t15:04:05", "2026-09-30T15:04:05z", "2026-09-30 15:04:05", "2026-02-30", "2026-06-30T23:59:60Z", "2026-09-30T24:00:00Z", "2026-09-30T15:04:05.12345678Z", "2026-09-30T15:04:05.Z", "2026-09-30T15Z", "2100-02-29T00:00:00Z", "0000-01-01T00:00:00Z"]) {
      expect(() => parseDateTimeUtc(bad, ""), bad).toThrow(CodecError);
    }
    expect(() => parseDateTimeOffset("2026-09-30T15:04:05", "")).toThrow(/offset/);
    expect(() => parseDateTimeOffset("2026-09-30T15:04:05+14:01", "")).toThrow(CodecError);
    expect(() => parseDateTimeOffset("2026-09-30T15:04:05+15:00", "")).toThrow(CodecError);
    expect(() => parseDateTimeOffset("0001-01-01T00:00:00+01:00", "")).toThrow(CodecError);
    expect(() => parseDateTimeOffset("9999-12-31T23:59:59-01:00", "")).toThrow(CodecError);
    expect(formatDateTimeOffset(parseDateTimeOffset("2026-09-30T15:04:05+14:00", ""))).toBe("2026-09-30T15:04:05+14:00");
    expect(parseDateTimeUnspecified("2024-02-29", "").ticks).toBe(parseDateTimeUnspecified("2024-02-29T00:00:00", "").ticks);
    expect(() => parseDateTimeUnspecified("2026-09-30T15:04:05Z", "")).toThrow(CodecError);
  });

  it("round-trips the DateTime range through ticks", () => {
    expect(parseDateTimeUtc("0001-01-01T00:00:00Z", "").ticks).toBe(0n);
    expect(parseDateTimeUtc("9999-12-31T23:59:59.9999999Z", "").ticks).toBe(3155378975999999999n);
    expect(formatDateTimeUtc({ kind: "datetime-utc", ticks: 3155378975999999999n })).toBe("9999-12-31T23:59:59.9999999Z");
    expect(formatDateTimeUtc({ kind: "datetime-utc", ticks: 0n })).toBe("0001-01-01T00:00:00Z");
    // new DateTime(2000, 1, 1).Ticks == 630822816000000000 (day 730119); 2000-02-29 is 59 days later
    expect(parseDateTimeUtc("2000-02-29T12:00:00Z", "").ticks).toBe(730178n * 864_000_000_000n + 432_000_000_000n);
    expect(parseDateTimeUtc("2000-01-01T00:00:00Z", "").ticks).toBe(630822816000000000n);
  });

  it("handles DateOnly, TimeOnly and TimeSpan canonical forms", () => {
    expect(formatDateOnly(parseDateOnly("2026-09-30", ""))).toBe("2026-09-30");
    expect(() => parseDateOnly("2026-9-30", "")).toThrow(CodecError);
    expect(formatTimeOnly(parseTimeOnly("05:15:00", ""))).toBe("05:15:00");
    expect(formatTimeOnly(parseTimeOnly("05:15:00.0000001", ""))).toBe("05:15:00.0000001");
    expect(formatTimeOnly(parseTimeOnly("23:59:59.9999999", ""))).toBe("23:59:59.9999999");
    expect(() => parseTimeOnly("24:00:00", "")).toThrow(CodecError);
    expect(() => parseTimeOnly("05:15", "")).toThrow(CodecError); // read alias, not canonical
    expect(formatDuration(parseDuration("00:00:00.0000001", ""))).toBe("00:00:00.0000001");
    expect(formatDuration(parseDuration("-1.12:00:00", ""))).toBe("-1.12:00:00");
    expect(formatDuration(parseDuration("10675199.02:48:05.4775807", ""))).toBe("10675199.02:48:05.4775807");
    expect(formatDuration(parseDuration("-10675199.02:48:05.4775808", ""))).toBe("-10675199.02:48:05.4775808");
    expect(formatDuration(parseDuration("02:03:04.5000000", ""))).toBe("02:03:04.5000000");
    expect(formatDuration({ kind: "duration", ticks: 0n })).toBe("00:00:00");
    expect(() => parseDuration("10675200.00:00:00", "")).toThrow(CodecError);
    expect(() => parseDuration("25:00:00", "")).toThrow(CodecError);
    expect(() => parseDuration("02:03:04.5", "")).toThrow(CodecError); // canonical fraction is exactly 7 digits
  });
});

describe("ordinal casing (.NET StringComparer.OrdinalIgnoreCase)", () => {
  it("compares with .NET's simple uppercase, never JavaScript's full case mapping", async () => {
    const { ordinalEqualsIgnoreCase, ordinalUpper } = await import("../src/primitives/ordinalCasing.js");
    // full mappings that .NET does not apply, and the dotless i / long s .NET leaves alone
    expect(ordinalEqualsIgnoreCase("ß", "SS")).toBe(false);
    expect(ordinalEqualsIgnoreCase("ﬃ", "FFI")).toBe(false);
    expect(ordinalEqualsIgnoreCase("ı", "I")).toBe(false);
    expect(ordinalEqualsIgnoreCase("ſ", "S")).toBe(false);
    // simple mappings JavaScript's toUpperCase does not produce, title case and supplementary planes
    expect(ordinalEqualsIgnoreCase("ᾳ", "ᾼ")).toBe(true);
    expect(ordinalEqualsIgnoreCase("ǆ", "ǅ")).toBe(true);
    expect(ordinalEqualsIgnoreCase("𐐨", "𐐀")).toBe(true);
    expect(ordinalEqualsIgnoreCase("firstName", "FIRSTNAME")).toBe(true);
    expect(ordinalUpper("straße ǆ")).toBe("STRAßE Ǆ");
  });

  it("keeps keys .NET distinguishes apart in an ignore-case map", async () => {
    const { TisiliaMap } = await import("../src/codec/map.js");
    const map = TisiliaMap.from<string, number>("ordinal-ignore-case", (k) => k, [["ß", 1], ["SS", 2], ["Key", 3]]);
    expect(map.size).toBe(3);
    expect(map.get("ss")).toBe(2);
    expect(map.get("KEY")).toBe(3);
  });
});

describe("JS Date interop", () => {
  it("converts instants to and from Date at millisecond precision", () => {
    // ticks below a millisecond are dropped toward the past, also before 1970
    expect(dateTimeOffsetToDate(parseDateTimeOffset("2026-10-02T12:34:56.7891234+09:00")).toISOString()).toBe("2026-10-02T03:34:56.789Z");
    expect(dateTimeOffsetToDate(parseDateTimeOffset("1969-12-31T23:59:59.9999999+00:00")).toISOString()).toBe("1969-12-31T23:59:59.999Z");
    expect(dateTimeUtcToDate(parseDateTimeUtc("0001-01-01T00:00:00Z")).toISOString()).toBe("0001-01-01T00:00:00.000Z");
    const date = new Date("2026-10-02T03:34:56.789Z");
    expect(formatDateTimeOffset(dateTimeOffsetFromDate(date))).toBe("2026-10-02T03:34:56.789+00:00");
    expect(formatDateTimeOffset(dateTimeOffsetFromDate(date, 540))).toBe("2026-10-02T12:34:56.789+09:00");
    expect(formatDateTimeOffset(dateTimeOffsetFromDate(date, -330))).toBe("2026-10-01T22:04:56.789-05:30");
    expect(formatDateTimeUtc(dateTimeUtcFromDate(date))).toBe("2026-10-02T03:34:56.789Z");
    expect(dateTimeOffsetToDate(dateTimeOffsetFromDate(date, 540)).getTime()).toBe(date.getTime());
    expect(dateTimeUtcToDate(dateTimeUtcFromDate(date)).getTime()).toBe(date.getTime());
  });

  it("refuses dates .NET cannot hold and offsets it does not have", () => {
    expect(() => dateTimeUtcFromDate(new Date(Number.NaN))).toThrow(CodecError);
    expect(() => dateTimeUtcFromDate(new Date("+010000-01-01T00:00:00Z"))).toThrow(CodecError);
    expect(() => dateTimeUtcFromDate(new Date("0000-12-31T23:59:59.999Z"))).toThrow(CodecError); // one millisecond before 0001-01-01
    expect(() => dateTimeOffsetFromDate(new Date(0), 841)).toThrow(CodecError);
    expect(() => dateTimeOffsetFromDate(new Date(0), 1.5)).toThrow(CodecError);
    // the local time must stay in range too: 9999-12-31T23:00Z at +14:00 is year 10000 locally
    expect(() => dateTimeOffsetFromDate(new Date("9999-12-31T23:00:00Z"), 840)).toThrow(CodecError);
  });

  it("parses without a path, like int64() and guid()", () => {
    expect(formatDateOnly(parseDateOnly("2026-10-02"))).toBe("2026-10-02");
    expect(formatTimeOnly(parseTimeOnly("12:34:56"))).toBe("12:34:56");
    expect(formatDuration(parseDuration("1.02:03:04"))).toBe("1.02:03:04");
  });
});
