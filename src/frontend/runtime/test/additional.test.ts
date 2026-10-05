import { describe, expect, it } from "vitest";
import * as additional from "../../../backend/Tisilia.Generator/Additional/tisilia-additional.js";
import { createCodecContext, withContext } from "../src/codec/abi.js";
import { asCodecError } from "../src/codec/errors.js";
import { enumCodec } from "../src/codec/structural.js";
import { enumBinder } from "../src/http/binders.js";
import type { JsonValue } from "../src/json/ast.js";

/**
 * The additional codec module that Tisilia.Generator embeds and `tisilia codec install-additional` installs. The values
 * below come from the .NET 10 oracle (Half.ToString / Half.Parse over every binary16 value, IPAddress.ToString, System.Text.Json's
 * converters); the programs in tests/oracle/AdditionalTypes run the full sweeps, these fixtures pin the edges.
 */
const ctx = createCodecContext();
const numbers = (flags: string) => withContext(ctx, { numbers: flags });
const str = (value: string): JsonValue => ({ kind: "string", value });
const num = (text: string): JsonValue => ({ kind: "number", text });

function codeOf(run: () => unknown): string | undefined {
  try {
    run();
    return undefined;
  } catch (error) {
    return asCodecError(error)?.code ?? "not-a-codec-error";
  }
}

function halfFromBits(bits: number): number {
  const sign = bits >> 15 ? -1 : 1;
  const exp = (bits >> 10) & 0x1f;
  const frac = bits & 0x3ff;
  return exp === 0 ? sign * frac * 2 ** -24 : sign * (1024 + frac) * 2 ** (exp - 25);
}

describe("Int128 / UInt128 / BigInteger", () => {
  it("keeps canonical decimal strings in range", () => {
    expect(additional.int128DomainRule("170141183460469231731687303715884105727", ctx)).toBe("170141183460469231731687303715884105727");
    expect(additional.int128DomainRule("-170141183460469231731687303715884105728", ctx)).toBe("-170141183460469231731687303715884105728");
    expect(codeOf(() => additional.int128DomainRule("170141183460469231731687303715884105728", ctx))).toBe("range");
    expect(codeOf(() => additional.int128DomainRule("-0", ctx))).toBe("grammar");
    expect(codeOf(() => additional.int128DomainRule("01", ctx))).toBe("grammar");
    expect(codeOf(() => additional.int128DomainRule(1, ctx))).toBe("type-mismatch");
    expect(codeOf(() => additional.uint128DomainRule("-1", ctx))).toBe("range");
    expect(additional.uint128DomainRule("340282366920938463463374607431768211455", ctx)).toBe("340282366920938463463374607431768211455");
  });

  it("writes number tokens and reads what the position's number handling writes", () => {
    expect(additional.int128RequestEncode.encodeRequest("-5", numbers("rw"))).toEqual(num("-5"));
    expect(additional.int128ResponseDecode.decodeResponse(num("42"), ctx)).toBe("42");
    expect(additional.int128ResponseDecode.decodeResponse(str("42"), numbers("w"))).toBe("42");
    expect(codeOf(() => additional.int128ResponseDecode.decodeResponse(str("42"), ctx))).toBe("type-mismatch");
    expect(codeOf(() => additional.int128ResponseDecode.decodeResponse(num("42"), numbers("w")))).toBe("type-mismatch");
    expect(codeOf(() => additional.int128ResponseDecode.decodeResponse(num("1.0"), ctx))).toBe("grammar");
    // BigInteger never follows number handling (a Tisilia converter, not one of System.Text.Json's number converters)
    expect(codeOf(() => additional.bigIntegerResponseDecode.decodeResponse(str("1"), numbers("w")))).toBe("type-mismatch");
  });

  it("caps BigInteger at 4096 characters like BigIntegerJsonConverter", () => {
    expect(additional.bigIntegerDomainRule("1" + "0".repeat(4095), ctx)).toHaveLength(4096);
    expect(codeOf(() => additional.bigIntegerDomainRule("1" + "0".repeat(4096), ctx))).toBe("range");
    expect(additional.bigIntegerDomainRule("-" + "9".repeat(4095), ctx)).toHaveLength(4096);
  });

  it("describes the server's lexemes in its grammars", () => {
    expect(additional.int128Grammar.test("-0")).toBe(true); // Int128.TryParse reads it as 0
    expect(additional.int128StringGrammar.test(" +5 ")).toBe(true); // NumberStyles.Integer
    expect(additional.uint128StringGrammar.test("-1")).toBe(false);
    expect(additional.int128Grammar.id).toBe("tisilia-additional.grammar.int128");
  });

  it("round-trips dictionary keys", () => {
    expect(additional.int128Key.decodeKey(additional.int128Key.encodeKey("-12", ctx), ctx)).toBe("-12");
  });
});

describe("Half", () => {
  it("formats like Half.ToString (shortest round-trip, ties to even, scientific below 1E-4)", () => {
    const oracle: ReadonlyArray<readonly [number, string]> = [
      [0x0001, "6E-08"], [0x0002, "1E-07"], [0x0004, "2.4E-07"], [0x0123, "1.734E-05"], [0x0400, "6.104E-05"], [0x3c00, "1"], [0x7bff, "65500"],
      [0x8000, "-0"], [0x8001, "-6E-08"], [0xfbff, "-65500"], [0x2e66, "0.1"], [0x3555, "0.3333"], [0x5640, "100"],
      [0x2a00, "0.04688"], [0x3300, "0.2188"], [0x4060, "2.188"], // ties to the even upper digit
      [0x3100, "0.1562"], [0x2000, "0.007812"], // ties to the even lower digit
    ];
    for (const [bits, text] of oracle) {
      expect(additional.formatHalf(halfFromBits(bits)), `0x${bits.toString(16)}`).toBe(text);
    }
  });

  it("parses decimal lexemes exactly (ties to even, overflow from 65520)", () => {
    expect(additional.parseHalfLexeme("65519.99999")).toBe(65504);
    expect(additional.parseHalfLexeme("65520")).toBe(Infinity);
    expect(additional.parseHalfLexeme("-65520")).toBe(-Infinity);
    expect(additional.parseHalfLexeme("0.0000000298023223876953125")).toBe(0); // exactly half of the smallest subnormal: to even
    expect(additional.parseHalfLexeme("0.0000000298023223876953126")).toBe(2 ** -24);
    expect(additional.parseHalfLexeme("1.00048828125")).toBe(1); // midpoint above 1: to even
    expect(Object.is(additional.parseHalfLexeme("-0"), -0)).toBe(true);
    expect(additional.parseHalfLexeme("1e-30")).toBe(0);
  });

  it("keeps the domain to binary16 values and rounds only on request", () => {
    expect(additional.isHalf(0.1)).toBe(false);
    expect(additional.halfOf(0.1)).toBe(0.0999755859375);
    expect(codeOf(() => additional.halfDomainRule(0.1, ctx))).toBe("precision");
    expect(codeOf(() => additional.halfDomainRule(Number.NaN, ctx))).toBe("range");
    expect(Number.isNaN(additional.halfDomainRule(Number.NaN, numbers("n")))).toBe(true);
  });

  it("encodes numbers and the named literals of AllowNamedFloatingPointLiterals", () => {
    expect(additional.halfRequestEncode.encodeRequest(0.0999755859375, ctx)).toEqual(num("0.1"));
    expect(additional.halfRequestEncode.encodeRequest(-Infinity, numbers("n"))).toEqual(str("-Infinity"));
    expect(Object.is(additional.halfResponseDecode.decodeResponse(num("-0"), ctx), -0)).toBe(true);
    expect(additional.halfResponseDecode.decodeResponse(str("6E-08"), numbers("w"))).toBe(2 ** -24);
    expect(additional.halfResponseDecode.decodeResponse(str("Infinity"), numbers("n"))).toBe(Infinity);
    expect(codeOf(() => additional.halfResponseDecode.decodeResponse(num("70000"), ctx))).toBe("range");
    expect(codeOf(() => additional.halfResponseDecode.decodeResponse(str("NaN"), numbers("w")))).toBe("grammar");
  });
});

describe("Uri", () => {
  it("sends RFC 3986 references the server's Uri parser keeps", () => {
    for (const text of ["https://example.com/a/b?q=1#f", "relative/path?x=y", "", "/abs/%20", "../up", "urn:isbn:0451450523", "http://[::1]:8080/", "mailto:someone@example.com", "file:///C:/data/x.txt", "http://user@host:65535/p;a=b?q#f"]) {
      expect(additional.uriRequestEncode.encodeRequest(text, ctx), text).toEqual(str(text));
    }
    // refused by .NET 10 (probed): one-letter schemes, hosts outside DNS names, file paths with ':' after a bare drive, '/' in a mailto local part
    for (const text of ["C:/x", "http://a..b/", "http://exa~mple.com/", "http://exa%41mple.com/", "http://_a/", "file:///C:", "file://host:80/x", "mailto:a/b@c.com", "net.tcp://host/x", "http://a:65536/", "a b"]) {
      expect(codeOf(() => additional.uriRequestEncode.encodeRequest(text, ctx)), text).toBe("grammar");
    }
  });

  it("reads any string the server wrote (Uri.OriginalString)", () => {
    expect(additional.uriResponseDecode.decodeResponse(str("\\\\server\\share"), ctx)).toBe("\\\\server\\share");
    expect(additional.uriValidate.validateDomain("http://例え.jp/", ctx)).toBe("http://例え.jp/");
  });
});

describe("Version, IPAddress, Rune", () => {
  it("keeps canonical versions", () => {
    expect(additional.versionDomainRule("2147483647.2147483647.2147483647.2147483647", ctx)).toHaveLength(43);
    for (const text of ["1", "01.0", "1.02", "2147483648.0", "1.2.3.4.5", "1.2.", " 1.2"]) {
      expect(codeOf(() => additional.versionDomainRule(text, ctx)), text).toBeDefined();
    }
  });

  it("formats IPv6 like IPAddress.ToString", () => {
    expect(additional.ipv6Text([0, 0, 0, 0, 0, 0, 0, 1])).toBe("::1");
    expect(additional.ipv6Text([0x2001, 0xdb8, 0, 0, 0, 0, 0, 1])).toBe("2001:db8::1");
    expect(additional.ipv6Text([0, 0, 0, 0, 0, 0xffff, 0x0102, 0x0304])).toBe("::ffff:1.2.3.4");
    expect(additional.ipv6Text([0, 0, 0, 0, 0, 0, 0x0102, 0x0304])).toBe("::1.2.3.4");
    expect(additional.ipv6Text([0, 0, 0, 0, 0, 0, 1, 0])).toBe("::0.1.0.0");
    expect(additional.ipv6Text([1, 0, 0, 0, 2, 0, 0, 3])).toBe("1::2:0:0:3");
    expect(additional.ipv6Text([0xfe80, 0, 0, 0, 0, 0, 0, 1], 4294967295)).toBe("fe80::1%4294967295");
  });

  it("accepts only the canonical address text", () => {
    for (const text of ["192.168.0.1", "::", "::ffff:10.0.0.1", "fe80::1%3", "1:2:3:4:5:6:7:8"]) {
      expect(additional.ipAddressDomainRule(text, ctx)).toBe(text);
    }
    for (const text of ["01.2.3.4", "1.2.3", "::FFFF:1.2.3.4", "0:0:0:0:0:0:0:1", "::0:1", "1::2:0:0:0:3", "fe80::1%0", "fe80::1%01", "[::1]"]) {
      expect(codeOf(() => additional.ipAddressDomainRule(text, ctx)), text).toBe("grammar");
    }
  });

  it("holds exactly one Unicode scalar per Rune", () => {
    expect(additional.runeDomainRule("😀", ctx)).toBe("😀");
    expect(additional.runeRequestInput.parseRequestInput(" ", ctx)).toBe(" ");
    for (const text of ["", "ab", "\ud800", "\udc00", "e\u0301"]) {
      expect(codeOf(() => additional.runeDomainRule(text, ctx)), JSON.stringify(text)).toBe("grammar");
    }
  });
});

describe("module errors", () => {
  it("are classified like the runtime's CodecError", () => {
    try {
      additional.versionDomainRule("1", ctx.child("version"));
      expect.unreachable();
    } catch (error) {
      const codecError = asCodecError(error);
      expect(codecError?.code).toBe("grammar");
      expect(codecError?.path).toBe("/version");
    }
  });
});

describe("IPNetwork, Index, Range", () => {
  it("keeps the canonical CIDR text IPNetwork.ToString writes", () => {
    for (const text of ["10.0.0.0/8", "0.0.0.0/0", "::/0", "2001:db8::/32", "fe80::%2/64", "::ffff:10.0.0.0/104", "203.0.113.7/32"]) {
      expect(additional.ipNetworkDomainRule(text, ctx)).toBe(text);
    }
    // IPNetwork.TryParse would normalize these (host bits, prefix zeros, a scope it drops, casing): one network, one text
    for (const text of ["10.0.0.1/8", "10.0.0.0/08", "fe80::1%2/64", "::%2/0", "2001:DB8::/32", "10.0.0.0/33", "10.0.0.0", "::/129"]) {
      expect(codeOf(() => additional.ipNetworkDomainRule(text, ctx)), text).toBe("grammar");
    }
  });

  it("writes C# index and range syntax", () => {
    expect(additional.indexRequestEncode.encodeRequest("^3", ctx)).toEqual(str("^3"));
    expect(additional.rangeResponseDecode.decodeResponse(str("0..^0"), ctx)).toBe("0..^0");
    for (const text of ["-1", "^2147483648", "01", ""]) {
      expect(codeOf(() => additional.indexDomainRule(text, ctx)), text).toBe("grammar");
    }
    for (const text of ["..5", "3..", "1..2..3", "1...2"]) {
      expect(codeOf(() => additional.rangeDomainRule(text, ctx)), text).toBe("grammar");
    }
  });
});

describe("Complex and JsonValue", () => {
  it("writes {real, imaginary} with lexemes that read back to the same doubles", () => {
    expect(additional.complexRequestEncode.encodeRequest({ real: -0, imaginary: 1e21 }, ctx)).toEqual({ kind: "object", entries: [{ name: "real", value: num("-0") }, { name: "imaginary", value: num("1e+21") }] });
    const decoded = additional.complexResponseDecode.decodeResponse({ kind: "object", entries: [{ name: "imaginary", value: num("5E-324") }, { name: "real", value: num("-0") }] }, ctx);
    expect(Object.is(decoded.real, -0)).toBe(true);
    expect(decoded.imaginary).toBe(5e-324);
    expect(codeOf(() => additional.complexResponseDecode.decodeResponse({ kind: "object", entries: [{ name: "real", value: num("1") }] }, ctx))).toBe("missing-required");
    expect(codeOf(() => additional.complexResponseDecode.decodeResponse({ kind: "object", entries: [{ name: "real", value: num("1") }, { name: "imaginary", value: num("2") }, { name: "phase", value: num("0") }] }, ctx))).toBe("unexpected-property");
    expect(codeOf(() => additional.complexDomainRule({ real: Number.NaN, imaginary: 0 }, ctx))).toBe("range");
  });

  it("keeps one JSON scalar with its lexeme", () => {
    expect(additional.jsonScalarResponseDecode.decodeResponse(num("1.50"), ctx)).toEqual(num("1.50"));
    expect(additional.jsonScalarRequestEncode.encodeRequest({ kind: "boolean", value: false }, ctx)).toEqual({ kind: "boolean", value: false });
    for (const wire of [{ kind: "null" }, { kind: "object", entries: [] }, { kind: "array", items: [] }] as JsonValue[]) {
      expect(codeOf(() => additional.jsonScalarResponseDecode.decodeResponse(wire, ctx)), wire.kind).toBe("type-mismatch");
    }
    expect(additional.jsonScalarOracle(num("1.0") as never, num("1.00") as never)).toBe(false);
  });
});

describe("enumBinder", () => {
  const color = enumCodec({ id: "c", typeId: "c", underlying: "int32", flags: false, allowUndefinedInteger: true, stringForm: false, members: [{ name: "Red", value: 1n }, { name: "Green", value: 2n, serializedName: "green" }] });
  const perms = enumCodec({ id: "p", typeId: "p", underlying: "int32", flags: true, allowUndefinedInteger: true, stringForm: false, members: [{ name: "None", value: 0n }, { name: "Read", value: 1n }, { name: "Write", value: 2n }] });

  it("writes the C# member name (never a serialized name) and the integer otherwise", () => {
    const binder = enumBinder(() => color, [["Red", 1n], ["Green", 2n]], "query");
    expect(binder.format(2, ctx)).toBe("Green");
    expect(binder.format(7, ctx)).toBe("7"); // minimal APIs: Enum.TryParse reads any integer
  });

  it("refuses undefined values when the server binds defined values only (MVC)", () => {
    const binder = enumBinder(() => color, [["Red", 1n], ["Green", 2n]], "query", { definedOnly: true });
    expect(codeOf(() => binder.format(7, ctx))).toBe("domain-rule");
    const flags = enumBinder(() => perms, [["None", 0n], ["Read", 1n], ["Write", 2n]], "query", { definedOnly: true, flags: true });
    expect(flags.format(3, ctx)).toBe("3"); // a combination of defined flags is defined
    expect(flags.format(0, ctx)).toBe("None");
    expect(codeOf(() => flags.format(4, ctx))).toBe("domain-rule");
  });
});

describe("TimeZoneInfo and CultureInfo (environment-bound)", () => {
  const env = withContext(ctx, { zones: JSON.stringify(["Asia/Tokyo", "UTC"]), cultures: JSON.stringify(["", "ja-JP"]) });

  it("sends only the ids the exporting server recorded", () => {
    expect(additional.timeZoneIdRequestEncode.encodeRequest("Asia/Tokyo", env)).toEqual(str("Asia/Tokyo"));
    expect(additional.cultureNameRequestEncode.encodeRequest("", env)).toEqual(str("")); // the invariant culture
    expect(codeOf(() => additional.timeZoneIdRequestEncode.encodeRequest("asia/tokyo", env))).toBe("domain-rule");
    expect(codeOf(() => additional.cultureNameRequestEncode.encodeRequest("en-US", env))).toBe("domain-rule");
    expect(additional.timeZoneIds(env)).toEqual(["Asia/Tokyo", "UTC"]);
  });

  it("reads whatever id the server's value has", () => {
    expect(additional.timeZoneIdResponseDecode.decodeResponse(str("Custom/Zone"), env)).toBe("Custom/Zone");
    expect(additional.cultureNameValidate.validateDomain("xx", env)).toBe("xx");
  });
});
