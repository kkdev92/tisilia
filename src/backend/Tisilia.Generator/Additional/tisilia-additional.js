// tisilia-additional 0.3.0 — the TypeScript side of Tisilia's additional codecs (types outside the builtin scalar
// set are usable through certified additional codecs). The .NET side is registered by
// Tisilia.AspNetCore.Codecs.AdditionalCodecs; `tisilia codec install-additional` installs this file next to the application.
// Self-contained: it imports nothing and reports failures as Errors named "CodecError" with a code and a path.
//
//   .NET type        wire (System.Text.Json 10)                                      TypeScript domain
//   Int128, UInt128  number token (a string under WriteAsString)                     canonical decimal string in range
//   BigInteger       number token (Tisilia BigIntegerJsonConverter)                  canonical decimal string, ≤ 4096 chars
//   Half             number token; strings for WriteAsString and named literals      number exactly representable in binary16
//   Uri              string: Uri.OriginalString                                      string (requests: an RFC 3986 URI reference)
//   Version          string: Version.ToString()                                      canonical "major.minor[.build[.revision]]"
//   IPAddress        string: IPAddress.ToString() (Tisilia IPAddressJsonConverter)   canonical IPv4 / IPv6 text
//   Rune             string of one Unicode scalar (Tisilia RuneJsonConverter)        string holding one code point

function fail(code, path, message) {
  const e = new Error(message);
  e.name = "CodecError";
  e.code = code;
  e.path = path;
  return e;
}

/** A non-secret binding context entry, e.g. the number handling "numbers" of the position. */
function contextEntry(context, name) {
  const entries = context.context;
  return entries !== undefined && typeof entries.get === "function" ? entries.get(name) : undefined;
}

/** The position's JsonNumberHandling: r = AllowReadingFromString, w = WriteAsString, n = AllowNamedFloatingPointLiterals. */
function numbersOf(context) {
  const flags = contextEntry(context, "numbers") ?? "";
  return { readFromString: flags.includes("r"), writeAsString: flags.includes("w"), namedLiterals: flags.includes("n") };
}

function inputText(input, context, name) {
  if (typeof input === "string") return input.trim();
  if (input !== null && typeof input === "object" && input.kind === "string") return input.value;
  if (input !== null && typeof input === "object" && input.kind === "number") return input.text;
  throw fail("type-mismatch", context.path, `enter a ${name}`);
}

/** A wire grammar export (role grammar): the lexemes or strings of one wire, described by a test. */
function grammar(id, test) {
  return Object.freeze({ id: "tisilia-additional.grammar." + id, test });
}

// ---------------------------------------------------------------- integers: Int128, UInt128, BigInteger

const integerPattern = /^-?(?:0|[1-9][0-9]*)$/;
const int128Min = -(2n ** 127n);
const int128Max = 2n ** 127n - 1n;
const uint128Max = 2n ** 128n - 1n;
/** BigInteger lexemes are capped like the runtime's maxNumberCharacters default; BigIntegerJsonConverter enforces the same cap. */
export const bigIntegerMaxLength = 4096;

function integerDomain(name, min, max, maxLength) {
  return (value, context) => {
    if (typeof value !== "string") throw fail("type-mismatch", context.path, `${name} is a canonical decimal integer string`);
    if (!integerPattern.test(value) || value === "-0") throw fail("grammar", context.path, `${name} requires a canonical decimal integer`);
    if (maxLength !== undefined && value.length > maxLength) throw fail("range", context.path, `${name} has more than ${maxLength} characters`);
    const n = BigInt(value);
    if ((min !== undefined && n < min) || (max !== undefined && n > max)) throw fail("range", context.path, `${name} is out of range`);
    return value;
  };
}

/** Number-handling-aware integer codec exports (Int128/UInt128 follow JsonNumberHandling; BigInteger is always a number token). */
function integerExports(name, rule, numberHandling) {
  const validate = { validateDomain: rule };
  const requestEncode = {
    // the canonical writer: a JSON number, which System.Text.Json reads under every number handling
    encodeRequest(value, context) {
      return { kind: "number", text: rule(value, context) };
    },
  };
  const responseDecode = {
    decodeResponse(wire, context) {
      const writeAsString = numberHandling && numbersOf(context).writeAsString;
      if (wire.kind === "number" && !writeAsString) return rule(wire.text, context);
      if (wire.kind === "string" && writeAsString) return rule(wire.value, context);
      throw fail("type-mismatch", context.path, `${name} requires a JSON ${writeAsString ? "string" : "number"} but found ${wire.kind}`);
    },
  };
  const key = {
    encodeKey(value, context) {
      return rule(value, context);
    },
    decodeKey(text, context) {
      return rule(text, context);
    },
  };
  const requestInput = {
    parseRequestInput(input, context) {
      return rule(inputText(input, context, name), context);
    },
  };
  return { validate, requestEncode, responseDecode, key, requestInput };
}

/** Integer lexemes of a JSON number token in [min, max] ("-0" is 0, as Int128.TryParse reads it). */
function integerLexemeIn(min, max, maxLength) {
  return (text) => /^-?(?:0|[1-9][0-9]*)$/.test(text) && (maxLength === undefined || text.length <= maxLength) && (min === undefined || BigInt(text) >= min) && (max === undefined || BigInt(text) <= max);
}

/** Strings Int128.TryParse/UInt128.TryParse read with NumberStyles.Integer: white space around, one sign, digits (leading zeros allowed). */
function integerStringIn(min, max) {
  return (text) => {
    const m = /^[\t-\r ]*([+-]?)([0-9]+)[\t-\r ]*$/.exec(text);
    if (m === null) return false;
    const n = BigInt(m[1] === "-" ? "-" + m[2] : m[2]);
    return n >= min && n <= max;
  };
}

export const int128DomainRule = integerDomain("Int128", int128Min, int128Max);
const int128 = integerExports("Int128", int128DomainRule, true);
export const int128Validate = int128.validate;
export const int128RequestEncode = int128.requestEncode;
export const int128ResponseDecode = int128.responseDecode;
export const int128Key = int128.key;
export const int128RequestInput = int128.requestInput;
export function int128Oracle(a, b) {
  return a === b;
}
export const int128Grammar = grammar("int128", integerLexemeIn(int128Min, int128Max));
export const int128StringGrammar = grammar("int128-string", integerStringIn(int128Min, int128Max));

export const uint128DomainRule = integerDomain("UInt128", 0n, uint128Max);
const uint128 = integerExports("UInt128", uint128DomainRule, true);
export const uint128Validate = uint128.validate;
export const uint128RequestEncode = uint128.requestEncode;
export const uint128ResponseDecode = uint128.responseDecode;
export const uint128Key = uint128.key;
export const uint128RequestInput = uint128.requestInput;
export function uint128Oracle(a, b) {
  return a === b;
}
export const uint128Grammar = grammar("uint128", integerLexemeIn(0n, uint128Max));
export const uint128StringGrammar = grammar("uint128-string", integerStringIn(0n, uint128Max));

export const bigIntegerDomainRule = integerDomain("BigInteger", undefined, undefined, bigIntegerMaxLength);
const bigInteger = integerExports("BigInteger", bigIntegerDomainRule, false);
export const bigIntegerValidate = bigInteger.validate;
export const bigIntegerRequestEncode = bigInteger.requestEncode;
export const bigIntegerResponseDecode = bigInteger.responseDecode;
export const bigIntegerKey = bigInteger.key;
export const bigIntegerRequestInput = bigInteger.requestInput;
export function bigIntegerOracle(a, b) {
  return a === b;
}
export const bigIntegerGrammar = grammar("big-integer", integerLexemeIn(undefined, undefined, bigIntegerMaxLength));
/** Property names: the same canonical integer text (BigIntegerJsonConverter reads and writes them like values). */
export const bigIntegerStringGrammar = grammar("big-integer-string", (text) => integerPattern.test(text) && text !== "-0" && text.length <= bigIntegerMaxLength);

// ---------------------------------------------------------------- Half (IEEE 754 binary16)
// Values are numbers; a value must be exactly representable in binary16 (System.Text.Json reads and writes Half values, never
// arbitrary doubles). Lexemes are converted exactly (decimal → binary16, ties to even), never through a double first.

const halfMax = 65504;
const halfOverflow = 65520; // the midpoint between 65504 and 65536: this and everything above round to infinity

function bitLength(n) {
  return n === 0n ? 0 : n.toString(2).length;
}

/** Nearest binary16 value of the exact rational num/den (> 0), ties to even; Infinity when it overflows. */
function roundRationalToHalf(num, den) {
  // 2^e <= num/den < 2^(e+1)
  let e = bitLength(num) - bitLength(den);
  if (e >= 0 ? num < den << BigInt(e) : num << BigInt(-e) < den) e -= 1;
  const qe = e < -14 ? -24 : e - 10; // quantum exponent: subnormals share 2^-24, normals keep 11 significant bits
  // n = (num/den) / 2^qe as an exact rational
  const scaledNum = qe < 0 ? num << BigInt(-qe) : num;
  const scaledDen = qe < 0 ? den : den << BigInt(qe);
  let r = scaledNum / scaledDen;
  const twiceRemainder = (scaledNum - r * scaledDen) * 2n;
  if (twiceRemainder > scaledDen || (twiceRemainder === scaledDen && r % 2n === 1n)) r += 1n;
  const value = Number(r) * 2 ** qe;
  return value >= halfOverflow ? Infinity : value;
}

const jsonNumberPattern = /^(-?)(0|[1-9][0-9]*)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$/;

/** The binary16 value of a JSON number lexeme (exact), or Infinity/-Infinity when it overflows. */
export function parseHalfLexeme(text) {
  const m = jsonNumberPattern.exec(text);
  if (m === null) return undefined;
  const negative = m[1] === "-";
  const digits = (m[2] + (m[3] ?? "")).replace(/^0+(?=[0-9])/, "");
  const exponent = Number(m[4] ?? "0") - (m[3] ?? "").length;
  let value;
  const coefficient = BigInt(digits);
  if (coefficient === 0n) value = 0;
  else if (exponent > 10) value = Infinity; // ≥ 10^11 overflows binary16 whatever the digits
  else if (exponent >= 0) value = roundRationalToHalf(coefficient * 10n ** BigInt(exponent), 1n);
  else if (-exponent - digits.length > 30) value = 0; // below 10^-30: far under half of the smallest subnormal (2^-25)
  else value = roundRationalToHalf(coefficient, 10n ** BigInt(-exponent));
  return negative ? -value : value;
}

/** Whether x is a binary16 value (NaN and ±Infinity included). */
export function isHalf(x) {
  if (typeof x !== "number") return false;
  if (!Number.isFinite(x)) return true;
  if (x === 0) return true;
  const magnitude = Math.abs(x);
  if (magnitude > halfMax) return false;
  // exact: x is a double, so x / quantum is computed exactly when the quantum is a power of two
  let e = Math.floor(Math.log2(magnitude));
  if (2 ** e > magnitude) e -= 1;
  if (2 ** (e + 1) <= magnitude) e += 1;
  const quantum = 2 ** (e < -14 ? -24 : e - 10);
  return Number.isInteger(magnitude / quantum);
}

/** The nearest binary16 value of a number (ties to even), for preparing values: halfOf(0.1) === 0.0999755859375. */
export function halfOf(x) {
  if (typeof x !== "number" || Number.isNaN(x)) return NaN;
  if (!Number.isFinite(x) || x === 0) return x;
  const magnitude = Math.abs(x);
  // exact rational of a double: mantissa / 2^k
  let k = 0;
  let mantissa = magnitude;
  while (!Number.isInteger(mantissa)) {
    mantissa *= 2;
    k += 1;
  }
  const value = roundRationalToHalf(BigInt(mantissa), 1n << BigInt(k));
  return x < 0 ? -value : value;
}

/** Exact rational of a binary16 value: num / 2^24. */
function halfNumerator(x) {
  return BigInt(Math.round(Math.abs(x) * 2 ** 24));
}

/**
 * The text .NET writes for a Half (Half.ToString / Utf8JsonWriter): the shortest decimal that reads back to the same binary16 value
 * (the nearest one when several have that length, the even one on a tie), in scientific form ("6E-08") below 10^-4 and fixed form
 * otherwise.
 */
export function formatHalf(x) {
  if (Number.isNaN(x)) return "NaN";
  if (x === Infinity) return "Infinity";
  if (x === -Infinity) return "-Infinity";
  if (x === 0) return Object.is(x, -0) ? "-0" : "0";
  const sign = x < 0 ? "-" : "";
  const num = halfNumerator(x); // |x| = num / 2^24
  const den = 1n << 24n;
  // decimal exponent of the first significant digit: 10^E <= |x| < 10^(E+1)
  let E = num.toString().length - 8; // a first guess, then adjusted exactly
  const below = (p) => (p >= 0 ? num < den * 10n ** BigInt(p) : num * 10n ** BigInt(-p) < den);
  while (below(E)) E -= 1;
  while (!below(E + 1)) E += 1;
  for (let k = 1; k <= 5; k++) {
    const p = E - k + 1; // candidates are m × 10^p with k-digit m
    // floor and ceiling of |x| / 10^p
    const [n, d] = p >= 0 ? [num, den * 10n ** BigInt(p)] : [num * 10n ** BigInt(-p), den];
    const floor = n / d;
    const candidates = n % d === 0n ? [floor] : [floor, floor + 1n];
    let best;
    for (const m of candidates) {
      if (m === 0n) continue;
      const back = p >= 0 ? roundRationalToHalf(m * 10n ** BigInt(p), 1n) : roundRationalToHalf(m, 10n ** BigInt(-p));
      if (back !== Math.abs(x)) continue;
      // distance |m × 10^p - |x|| compared exactly: |m × d - n|; a tie goes to the even digit (Dragon4's IEEE rounding rule)
      const distance = m * d > n ? m * d - n : n - m * d;
      if (best === undefined || distance < best.distance || (distance === best.distance && m % 2n === 0n)) best = { m, distance };
    }
    if (best !== undefined) return sign + layout(best.m.toString(), p);
  }
  throw new Error("unreachable: five significant digits always identify a binary16 value");
}

/** Decimal digits × 10^p laid out like .NET: scientific "d.dddE±XX" when the leading exponent is below -4, fixed otherwise. */
function layout(digits, p) {
  const trimmed = digits.replace(/0+$/, "");
  const zeros = digits.length - trimmed.length;
  const mantissa = trimmed.length === 0 ? "0" : trimmed;
  const scale = p + zeros; // value = mantissa × 10^scale
  const leading = scale + mantissa.length - 1;
  if (leading < -4 || leading >= 15) {
    const tail = mantissa.slice(1);
    const exp = Math.abs(leading).toString().padStart(2, "0");
    return mantissa[0] + (tail.length > 0 ? "." + tail : "") + "E" + (leading < 0 ? "-" : "+") + exp;
  }
  if (scale >= 0) return mantissa + "0".repeat(scale);
  const point = mantissa.length + scale;
  return point > 0 ? mantissa.slice(0, point) + "." + mantissa.slice(point) : "0." + "0".repeat(-point) + mantissa;
}

const namedHalf = { NaN: NaN, Infinity: Infinity, "-Infinity": -Infinity };

export function halfDomainRule(value, context) {
  if (typeof value !== "number") throw fail("type-mismatch", context.path, "Half is a number");
  if (!Number.isFinite(value) && !numbersOf(context).namedLiterals) throw fail("range", context.path, "NaN and ±Infinity are Half values only with AllowNamedFloatingPointLiterals");
  if (!isHalf(value)) throw fail("precision", context.path, "the number is not exactly representable as a Half; round it with halfOf()");
  return value;
}

export const halfValidate = { validateDomain: halfDomainRule };

export const halfRequestEncode = {
  encodeRequest(value, context) {
    const v = halfDomainRule(value, context);
    return Number.isFinite(v) ? { kind: "number", text: formatHalf(v) } : { kind: "string", value: formatHalf(v) };
  },
};

function halfOfText(text, context) {
  const v = parseHalfLexeme(text);
  if (v === undefined) throw fail("grammar", context.path, "Half requires a JSON number lexeme");
  if (!Number.isFinite(v)) throw fail("range", context.path, "the number is outside the Half range");
  return v;
}

export const halfResponseDecode = {
  decodeResponse(wire, context) {
    const numbers = numbersOf(context);
    if (wire.kind === "number" && !numbers.writeAsString) return halfOfText(wire.text, context);
    if (wire.kind === "string") {
      if (numbers.namedLiterals && Object.hasOwn(namedHalf, wire.value)) return namedHalf[wire.value];
      if (numbers.writeAsString) return halfOfText(wire.value, context);
    }
    throw fail("type-mismatch", context.path, `Half requires a JSON ${numbers.writeAsString ? "string" : "number"} but found ${wire.kind}`);
  },
};

export const halfKey = {
  encodeKey(value, context) {
    const v = halfDomainRule(value, context);
    if (!Number.isFinite(v)) throw fail("range", context.path, "a Half dictionary key must be finite");
    return formatHalf(v);
  },
  decodeKey(text, context) {
    return halfOfText(text, context);
  },
};

export const halfRequestInput = {
  parseRequestInput(input, context) {
    const text = inputText(input, context, "Half");
    if (Object.hasOwn(namedHalf, text)) return halfDomainRule(namedHalf[text], context);
    return halfDomainRule(halfOfText(text, context), context);
  },
};

export function halfOracle(a, b) {
  return Object.is(a, b);
}
const finiteHalfLexeme = (text) => {
  const v = parseHalfLexeme(text);
  return v !== undefined && Number.isFinite(v);
};
/** Strings Half.TryParse reads (NumberStyles.Float | AllowThousands, invariant), to a finite value. */
const finiteHalfString = (text) => {
  const m = /^[\t-\r ]*([+-]?)([0-9][0-9,]*)?(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?[\t-\r ]*$/.exec(text);
  if (m === null || (m[2] === undefined && (m[3] === undefined || m[3] === ""))) return false;
  const whole = (m[2] ?? "0").replace(/,/g, "").replace(/^0+(?=[0-9])/, "");
  const lexeme = (m[1] === "-" ? "-" : "") + whole + (m[3] ? "." + m[3] : "") + (m[4] !== undefined ? "e" + m[4] : "");
  return finiteHalfLexeme(lexeme);
};
const namedHalfLiteral = (text) => text === "NaN" || text === "Infinity" || text === "-Infinity";
export const halfGrammar = grammar("half", finiteHalfLexeme);
export const halfNamedGrammar = grammar("half-named", namedHalfLiteral);
export const halfStringOrNamedGrammar = grammar("half-string-named", (text) => namedHalfLiteral(text) || finiteHalfString(text));

// ---------------------------------------------------------------- Uri
// The server writes Uri.OriginalString, which can be any string Uri.TryCreate(…, UriKind.RelativeOrAbsolute) accepted, so responses
// take any string. Requests are limited to RFC 3986 URI references (ASCII; IPv6 literals and percent-encoding included) that .NET's
// parser keeps — the subset System.Text.Json was checked to accept and write back unchanged.

export const uriMaxLength = 65519;
const hex = "[0-9A-Fa-f]";
const unreserved = "A-Za-z0-9\\-._~";
const subDelims = "!$&'()*+,;=";
const pct = `%${hex}${hex}`;
const pchar = `(?:[${unreserved}${subDelims}:@]|${pct})`;
const segment = `${pchar}*`;
const segmentNz = `${pchar}+`;
const segmentNzNc = `(?:[${unreserved}${subDelims}@]|${pct})+`;
const query = `(?:${pchar}|[/?])*`;
const decOctet = "(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9][0-9]|[0-9])";
const ipv4 = `${decOctet}\\.${decOctet}\\.${decOctet}\\.${decOctet}`;
const h16 = `${hex}{1,4}`;
const ls32 = `(?:${h16}:${h16}|${ipv4})`;
const ipv6 = [
  `(?:${h16}:){6}${ls32}`,
  `::(?:${h16}:){5}${ls32}`,
  `(?:${h16})?::(?:${h16}:){4}${ls32}`,
  `(?:(?:${h16}:){0,1}${h16})?::(?:${h16}:){3}${ls32}`,
  `(?:(?:${h16}:){0,2}${h16})?::(?:${h16}:){2}${ls32}`,
  `(?:(?:${h16}:){0,3}${h16})?::${h16}:${ls32}`,
  `(?:(?:${h16}:){0,4}${h16})?::${ls32}`,
  `(?:(?:${h16}:){0,5}${h16})?::${h16}`,
  `(?:(?:${h16}:){0,6}${h16})?::`,
].join("|");
const regName = `(?:[${unreserved}${subDelims}]|${pct})*`;
const port = "(?:[0-9]{1,4}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5])";
const userinfo = `(?:[${unreserved}${subDelims}:]|${pct})*`;
const authority = `(?:${userinfo}@)?(?:\\[(?:${ipv6})\\]|${ipv4}|${regName})(?::${port})?`;
const pathAbempty = `(?:/${segment})*`;
const pathAbsolute = `/(?:${segmentNz}(?:/${segment})*)?`;
const pathRootless = `${segmentNz}(?:/${segment})*`;
const pathNoscheme = `${segmentNzNc}(?:/${segment})*`;
const scheme = "[A-Za-z][A-Za-z0-9+\\-.]*";
const absoluteUri = `${scheme}:(?://${authority}${pathAbempty}|${pathAbsolute}|${pathRootless})(?:\\?${query})?(?:#${query})?`;
const relativeRef = `(?://${authority}${pathAbempty}|${pathAbsolute}|${pathNoscheme}|)(?:\\?${query})?(?:#${query})?`;
const uriReference = new RegExp(`^(?:${absoluteUri}|${relativeRef})$`);

// .NET's Uri parser refuses parts of RFC 3986 (probed against .NET 10 one component at a time): a host is a DNS name, an IPv4 or a
// bracketed IPv6 address — no '~', sub-delims or percent-encoding; a one-letter scheme is a drive letter; file URIs have no userinfo
// or port and no ':' in the path (a leading drive excepted); mailto checks the domain after '@'; net.tcp and net.pipe have their own
// parsers. Relative references are always kept.
// DNS names as DomainNameHelper.IsValid (dotnet/runtime v10.0.0) checks them: labels of 1..63 characters [A-Za-z0-9_-] starting with
// a letter or digit, separated by single dots, an optional final dot.
const dnsLabel = "[A-Za-z0-9][A-Za-z0-9_-]{0,62}";
const dnsHost = new RegExp(`^${dnsLabel}(?:\\.${dnsLabel})*\\.?$`);
const ipv4Host = new RegExp(`^${ipv4}$`);
const ipv6Host = new RegExp(`^\\[(?:${ipv6})\\]$`);
const schemePrefix = /^([A-Za-z][A-Za-z0-9+\-.]*):/;
// a '/' before the '@' ends .NET's userinfo scan and makes the text before it the host, so local parts have none
const mailtoAddress = new RegExp(`^[A-Za-z0-9\\-._~!$&'()*+,;=:]+@${dnsLabel}(?:\\.${dnsLabel})*$`);
const hostOk = (host) => ipv6Host.test(host) || ipv4Host.test(host) || dnsHost.test(host);
// file paths: no empty first segment (a UNC look-alike) and no ':' except a leading drive followed by a slash ("/C:/…")
const filePathOk = (path) => !path.startsWith("//") && !path.replace(/^\/[A-Za-z]:(?=\/)/, "").includes(":");

/** Whether .NET keeps an RFC 3986 URI reference (Uri.TryCreate(…, UriKind.RelativeOrAbsolute)); the caller checked the RFC grammar. */
function dotnetUriAccepts(text) {
  const m = schemePrefix.exec(text);
  if (m === null) return true;
  const scheme = m[1].toLowerCase();
  if (scheme.length < 2 || scheme === "net.tcp" || scheme === "net.pipe") return false;
  const rest = text.slice(m[0].length);
  const end = rest.search(/[?#]/);
  const hier = end < 0 ? rest : rest.slice(0, end);
  if (scheme === "mailto") return mailtoAddress.test(hier);
  if (!hier.startsWith("//")) return scheme !== "file" || filePathOk(hier);
  const slash = hier.indexOf("/", 2);
  const authority = slash < 0 ? hier.slice(2) : hier.slice(2, slash);
  const at = authority.indexOf("@");
  const hostPort = at < 0 ? authority : authority.slice(at + 1);
  const afterHost = hostPort.startsWith("[") ? hostPort.slice(hostPort.indexOf("]") + 1) : hostPort;
  const port = /:[0-9]*$/.exec(afterHost);
  const host = port === null ? hostPort : hostPort.slice(0, hostPort.length - port[0].length);
  if (scheme === "file") return at < 0 && port === null && (host === "" || hostOk(host)) && filePathOk(slash < 0 ? "" : hier.slice(slash));
  return host !== "" && hostOk(host);
}

/** The request rule: an RFC 3986 URI reference that Uri.TryCreate(…, RelativeOrAbsolute) accepts and keeps as its OriginalString. */
export function uriRequestRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "Uri is a string");
  if (value.length > uriMaxLength) throw fail("range", context.path, `Uri has more than ${uriMaxLength} characters`);
  if (!uriReference.test(value)) throw fail("grammar", context.path, "a request Uri must be an RFC 3986 URI reference (ASCII, percent-encoded)");
  if (!dotnetUriAccepts(value)) throw fail("grammar", context.path, "the server's Uri parser refuses this URI (hosts are DNS names or IP addresses; no one-letter scheme; file URIs without userinfo, port or ':' in the path; mailto needs user@domain)");
  return value;
}

/** The domain: Uri.OriginalString, any string the server's Uri holds (responses carry exactly that). */
export function uriDomainRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "Uri is a string");
  return value;
}

export const uriValidate = { validateDomain: uriDomainRule };
export const uriRequestEncode = {
  encodeRequest(value, context) {
    return { kind: "string", value: uriRequestRule(value, context) };
  },
};
export const uriResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, `Uri requires a JSON string but found ${wire.kind}`);
    return wire.value;
  },
};
export const uriRequestInput = {
  parseRequestInput(input, context) {
    return uriRequestRule(inputText(input, context, "URI"), context);
  },
};
export function uriOracle(a, b) {
  return a === b;
}
export const uriReferenceGrammar = grammar("uri-reference", (text) => text.length <= uriMaxLength && uriReference.test(text) && dotnetUriAccepts(text));

// ---------------------------------------------------------------- Version

const versionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?)?$/;

export function versionDomainRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "Version is a string");
  const m = versionPattern.exec(value);
  if (m === null) throw fail("grammar", context.path, "Version requires major.minor[.build[.revision]] without leading zeros");
  for (const part of m.slice(1)) {
    if (part !== undefined && (part.length > 10 || Number(part) > 2147483647)) throw fail("range", context.path, "a Version component is above Int32.MaxValue");
  }
  return value;
}

export const versionValidate = { validateDomain: versionDomainRule };
export const versionRequestEncode = {
  encodeRequest(value, context) {
    return { kind: "string", value: versionDomainRule(value, context) };
  },
};
export const versionResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, `Version requires a JSON string but found ${wire.kind}`);
    return versionDomainRule(wire.value, context);
  },
};
export const versionKey = {
  encodeKey(value, context) {
    return versionDomainRule(value, context);
  },
  decodeKey(text, context) {
    return versionDomainRule(text, context);
  },
};
export const versionRequestInput = {
  parseRequestInput(input, context) {
    return versionDomainRule(inputText(input, context, "version"), context);
  },
};
export function versionOracle(a, b) {
  return a === b;
}
export const versionGrammar = grammar("version", (text) => {
  const m = versionPattern.exec(text);
  return m !== null && m.slice(1).every((part) => part === undefined || (part.length <= 10 && Number(part) <= 2147483647));
});

// ---------------------------------------------------------------- IPAddress (canonical text, as IPAddress.ToString writes it)

function formatIPv4(bytes) {
  return bytes.join(".");
}

/** .NET's IPv6 text: longest run (≥ 2) of zero words compressed (first on ties), lowercase hex, embedded IPv4 per IPv6AddressHelper. */
function formatIPv6(words, scope) {
  const embedded =
    (words[0] === 0 && words[1] === 0 && words[2] === 0 && words[3] === 0 && words[6] !== 0 &&
      ((words[4] === 0 && (words[5] === 0 || words[5] === 0xffff)) || (words[4] === 0xffff && words[5] === 0))) ||
    (words[4] === 0 && words[5] === 0x5efe);
  const sections = (list) => {
    let bestStart = -1;
    let bestLength = 0;
    let runStart = -1;
    let runLength = 0;
    for (let i = 0; i < list.length; i++) {
      if (list[i] === 0) {
        if (runLength === 0) runStart = i;
        runLength++;
        if (runLength > bestLength) {
          bestLength = runLength;
          bestStart = runStart;
        }
      } else {
        runLength = 0;
      }
    }
    const hexOf = (w) => w.toString(16);
    if (bestLength < 2) return list.map(hexOf).join(":");
    return list.slice(0, bestStart).map(hexOf).join(":") + "::" + list.slice(bestStart + bestLength).map(hexOf).join(":");
  };
  let text;
  if (embedded) {
    const head = sections(words.slice(0, 6));
    text = head + (head.endsWith(":") ? "" : ":") + formatIPv4([words[6] >> 8, words[6] & 0xff, words[7] >> 8, words[7] & 0xff]);
  } else {
    text = sections(words);
  }
  return scope === 0 ? text : text + "%" + scope.toString();
}

const ipv4Strict = /^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$/;

/** Parses IPv6 text (with "::" and an optional embedded IPv4 and %scope) into words; undefined when malformed. */
function parseIPv6(text) {
  const percent = text.indexOf("%");
  let scope = 0;
  let body = text;
  if (percent >= 0) {
    const scopeText = text.slice(percent + 1);
    if (!/^[0-9]{1,10}$/.test(scopeText) || Number(scopeText) > 4294967295) return undefined;
    scope = Number(scopeText);
    body = text.slice(0, percent);
  }
  let tail = [];
  const lastColon = body.lastIndexOf(":");
  if (lastColon >= 0 && body.slice(lastColon + 1).includes(".")) {
    const v4 = ipv4Strict.exec(body.slice(lastColon + 1));
    if (v4 === null) return undefined;
    const b = v4.slice(1).map(Number);
    tail = [(b[0] << 8) | b[1], (b[2] << 8) | b[3]];
    body = body.slice(0, lastColon + 1) + (body[lastColon - 1] === ":" ? "" : "0"); // keep the colon structure, placeholder word
    if (!body.endsWith("::")) body = body.slice(0, -1).replace(/:$/, "");
  }
  const parts = body.split("::");
  if (parts.length > 2) return undefined;
  const group = (s) => (s === "" ? [] : s.split(":"));
  const head = group(parts[0]);
  const rest = parts.length === 2 ? group(parts[1]) : [];
  if ([...head, ...rest].some((g) => !/^[0-9a-fA-F]{1,4}$/.test(g))) return undefined;
  const words = [...head, ...rest].map((g) => parseInt(g, 16));
  const total = words.length + tail.length;
  if (parts.length === 2) {
    if (total > 7) return undefined;
    return { words: [...head.map((g) => parseInt(g, 16)), ...new Array(8 - total).fill(0), ...rest.map((g) => parseInt(g, 16)), ...tail], scope };
  }
  if (total !== 8) return undefined;
  return { words: [...words, ...tail], scope };
}

export function ipAddressDomainRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "IPAddress is a string");
  const v4 = ipv4Strict.exec(value);
  if (v4 !== null) return value; // the pattern admits only the canonical dotted-decimal form
  if (value.includes(":")) {
    const parsed = parseIPv6(value);
    if (parsed !== undefined && formatIPv6(parsed.words, parsed.scope) === value) return value;
  }
  throw fail("grammar", context.path, "IPAddress requires the canonical text IPAddress.ToString() writes");
}

/** The canonical text of an IPv6 address given as eight 16-bit words (and a scope id), as .NET writes it. */
export function ipv6Text(words, scope = 0) {
  return formatIPv6(words, scope);
}

export const ipAddressValidate = { validateDomain: ipAddressDomainRule };
export const ipAddressRequestEncode = {
  encodeRequest(value, context) {
    return { kind: "string", value: ipAddressDomainRule(value, context) };
  },
};
export const ipAddressResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, `IPAddress requires a JSON string but found ${wire.kind}`);
    return ipAddressDomainRule(wire.value, context);
  },
};
export const ipAddressKey = {
  encodeKey(value, context) {
    return ipAddressDomainRule(value, context);
  },
  decodeKey(text, context) {
    return ipAddressDomainRule(text, context);
  },
};
export const ipAddressRequestInput = {
  parseRequestInput(input, context) {
    return ipAddressDomainRule(inputText(input, context, "IP address"), context);
  },
};
export function ipAddressOracle(a, b) {
  return a === b;
}
export const ipAddressGrammar = grammar("ip-address", (text) => {
  try {
    ipAddressDomainRule(text, { path: "" });
    return true;
  } catch {
    return false;
  }
});

// ---------------------------------------------------------------- Rune (one Unicode scalar value)

export function runeDomainRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "Rune is a string");
  const cp = value.codePointAt(0);
  if (cp === undefined || String.fromCodePoint(cp) !== value || (cp >= 0xd800 && cp <= 0xdfff)) throw fail("grammar", context.path, "Rune requires exactly one Unicode scalar value");
  return value;
}

export const runeValidate = { validateDomain: runeDomainRule };
export const runeRequestEncode = {
  encodeRequest(value, context) {
    return { kind: "string", value: runeDomainRule(value, context) };
  },
};
export const runeResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, `Rune requires a JSON string but found ${wire.kind}`);
    return runeDomainRule(wire.value, context);
  },
};
export const runeKey = {
  encodeKey(value, context) {
    return runeDomainRule(value, context);
  },
  decodeKey(text, context) {
    return runeDomainRule(text, context);
  },
};
export const runeRequestInput = {
  parseRequestInput(input, context) {
    return runeDomainRule(typeof input === "string" ? input : inputText(input, context, "character"), context);
  },
};
export function runeOracle(a, b) {
  return a === b;
}
export const runeGrammar = grammar("rune", (text) => {
  const cp = text.codePointAt(0);
  return cp !== undefined && String.fromCodePoint(cp) === text && (cp < 0xd800 || cp > 0xdfff);
});

// ---------------------------------------------------------------- IPNetwork (CIDR, as IPNetwork.ToString writes it)
// IPNetwork.TryParse reads any IPAddress text and a digits-only prefix, then clears the bits after the prefix (and with them an IPv6
// scope, unless the address already was the network's first address); ToString writes the base address's canonical text, "/" and
// the prefix. The domain is that canonical text: a canonical address with zero host bits and a prefix of 0..32 (IPv4) or 0..128.

function ipNetworkParts(text) {
  const slash = text.lastIndexOf("/");
  if (slash < 0) return undefined;
  const address = text.slice(0, slash);
  const prefixText = text.slice(slash + 1);
  if (!/^(0|[1-9][0-9]{0,2})$/.test(prefixText)) return undefined;
  const prefix = Number(prefixText);
  if (ipv4Strict.test(address)) {
    if (prefix > 32) return undefined;
    const bytes = address.split(".").map(Number);
    for (let bit = prefix; bit < 32; bit++) if ((bytes[bit >> 3] >> (7 - (bit & 7))) & 1) return undefined;
    return { address, prefix, family: 4 };
  }
  if (!address.includes(":")) return undefined;
  const parsed = parseIPv6(address);
  if (parsed === undefined || formatIPv6(parsed.words, parsed.scope) !== address || prefix > 128) return undefined;
  if (prefix === 0 && parsed.scope !== 0) return undefined; // a zero prefix is IPAddress.IPv6Any, which has no scope
  for (let bit = prefix; bit < 128; bit++) if ((parsed.words[bit >> 4] >> (15 - (bit & 15))) & 1) return undefined;
  return { address, prefix, family: 6 };
}

export function ipNetworkDomainRule(value, context) {
  if (typeof value !== "string") throw fail("type-mismatch", context.path, "IPNetwork is a string");
  if (ipNetworkParts(value) === undefined) throw fail("grammar", context.path, "IPNetwork requires the canonical CIDR text IPNetwork.ToString() writes (\"10.0.0.0/8\", \"2001:db8::/32\": no host bits)");
  return value;
}

export const ipNetworkValidate = { validateDomain: ipNetworkDomainRule };
export const ipNetworkRequestEncode = {
  encodeRequest(value, context) {
    return { kind: "string", value: ipNetworkDomainRule(value, context) };
  },
};
export const ipNetworkResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, `IPNetwork requires a JSON string but found ${wire.kind}`);
    return ipNetworkDomainRule(wire.value, context);
  },
};
export const ipNetworkKey = {
  encodeKey(value, context) {
    return ipNetworkDomainRule(value, context);
  },
  decodeKey(text, context) {
    return ipNetworkDomainRule(text, context);
  },
};
export const ipNetworkRequestInput = {
  parseRequestInput(input, context) {
    return ipNetworkDomainRule(inputText(input, context, "network"), context);
  },
};
export function ipNetworkOracle(a, b) {
  return a === b;
}
export const ipNetworkGrammar = grammar("ip-network", (text) => ipNetworkParts(text) !== undefined);

// ---------------------------------------------------------------- Index and Range (C# index syntax, as Index/Range.ToString write it)
// Index: "3" counts from the start, "^3" from the end (0..Int32.MaxValue). Range: "<start>..<end>", both ends always written
// ("..5" is written "0..5", Range.All "0..^0").

const indexPattern = /^(\^?)(0|[1-9][0-9]{0,9})$/;
const indexOk = (text) => {
  const m = indexPattern.exec(text);
  return m !== null && Number(m[2]) <= 2147483647;
};
const rangeOk = (text) => {
  const parts = text.split("..");
  return parts.length === 2 && indexOk(parts[0]) && indexOk(parts[1]);
};

/** The exports of a codec whose domain is one canonical text (a JSON string on the wire and as a dictionary key). */
function textCodec(name, ok, message) {
  const rule = (value, context) => {
    if (typeof value !== "string") throw fail("type-mismatch", context.path, `${name} is a string`);
    if (!ok(value)) throw fail("grammar", context.path, message);
    return value;
  };
  return {
    rule,
    validate: { validateDomain: rule },
    requestEncode: {
      encodeRequest(value, context) {
        return { kind: "string", value: rule(value, context) };
      },
    },
    responseDecode: {
      decodeResponse(wire, context) {
        if (wire.kind !== "string") throw fail("type-mismatch", context.path, `${name} requires a JSON string but found ${wire.kind}`);
        return rule(wire.value, context);
      },
    },
    key: {
      encodeKey(value, context) {
        return rule(value, context);
      },
      decodeKey(text, context) {
        return rule(text, context);
      },
    },
    requestInput: {
      parseRequestInput(input, context) {
        return rule(inputText(input, context, name), context);
      },
    },
  };
}

const indexCodec = textCodec("Index", indexOk, "Index requires \"<n>\" or \"^<n>\" with 0 <= n <= 2147483647, as Index.ToString() writes it");
export const indexDomainRule = indexCodec.rule;
export const indexValidate = indexCodec.validate;
export const indexRequestEncode = indexCodec.requestEncode;
export const indexResponseDecode = indexCodec.responseDecode;
export const indexKey = indexCodec.key;
export const indexRequestInput = indexCodec.requestInput;
export function indexOracle(a, b) {
  return a === b;
}
export const indexGrammar = grammar("index", indexOk);

const rangeCodec = textCodec("Range", rangeOk, "Range requires \"<index>..<index>\" with both ends written, as Range.ToString() writes it");
export const rangeDomainRule = rangeCodec.rule;
export const rangeValidate = rangeCodec.validate;
export const rangeRequestEncode = rangeCodec.requestEncode;
export const rangeResponseDecode = rangeCodec.responseDecode;
export const rangeKey = rangeCodec.key;
export const rangeRequestInput = rangeCodec.requestInput;
export function rangeOracle(a, b) {
  return a === b;
}
export const rangeGrammar = grammar("range", rangeOk);

// ---------------------------------------------------------------- Complex ({ real, imaginary }: two finite doubles)
// ComplexJsonConverter writes {"real":…,"imaginary":…} with System.Text.Json's double formatting and reads exactly those two members.

function finiteDouble(value, path, name) {
  if (typeof value !== "number") throw fail("type-mismatch", path, `${name} is a number`);
  if (!Number.isFinite(value)) throw fail("range", path, `${name} must be finite`);
  return value;
}

/** A JSON number lexeme of a double that reads back to the same double ("-0" keeps its sign). */
function doubleLexeme(x) {
  return Object.is(x, -0) ? "-0" : String(x);
}

export function complexDomainRule(value, context) {
  if (value === null || typeof value !== "object") throw fail("type-mismatch", context.path, "Complex is { real, imaginary }");
  return { real: finiteDouble(value.real, context.path + "/real", "real"), imaginary: finiteDouble(value.imaginary, context.path + "/imaginary", "imaginary") };
}

export const complexValidate = { validateDomain: complexDomainRule };
export const complexRequestEncode = {
  encodeRequest(value, context) {
    const v = complexDomainRule(value, context);
    return { kind: "object", entries: [{ name: "real", value: { kind: "number", text: doubleLexeme(v.real) } }, { name: "imaginary", value: { kind: "number", text: doubleLexeme(v.imaginary) } }] };
  },
};

function complexFromObject(wire, context) {
  if (wire.kind !== "object") throw fail("type-mismatch", context.path, `Complex requires a JSON object but found ${wire.kind}`);
  const found = {};
  for (const entry of wire.entries) {
    if (entry.name !== "real" && entry.name !== "imaginary") throw fail("unexpected-property", context.path + "/" + entry.name, "Complex has only real and imaginary");
    if (Object.hasOwn(found, entry.name)) throw fail("duplicate-property", context.path + "/" + entry.name, `duplicate ${entry.name}`);
    if (entry.value.kind !== "number") throw fail("type-mismatch", context.path + "/" + entry.name, `${entry.name} requires a JSON number`);
    found[entry.name] = finiteDouble(Number(entry.value.text), context.path + "/" + entry.name, entry.name);
  }
  if (!Object.hasOwn(found, "real") || !Object.hasOwn(found, "imaginary")) throw fail("missing-required", context.path, "Complex requires real and imaginary");
  return { real: found.real, imaginary: found.imaginary };
}

export const complexResponseDecode = {
  decodeResponse(wire, context) {
    return complexFromObject(wire, context);
  },
};
export const complexRequestInput = {
  parseRequestInput(input, context) {
    if (typeof input === "string") {
      // "real,imaginary"
      const m = /^\s*([^,\s]+)\s*,\s*([^,\s]+)\s*$/.exec(input);
      if (m === null || !jsonNumberPattern.test(m[1]) || !jsonNumberPattern.test(m[2])) throw fail("grammar", context.path, "enter real,imaginary");
      return complexDomainRule({ real: Number(m[1]), imaginary: Number(m[2]) }, context);
    }
    return complexFromObject(input, context);
  },
};
export function complexOracle(a, b) {
  return Object.is(a.real, b.real) && Object.is(a.imaginary, b.imaginary);
}

// ---------------------------------------------------------------- JsonValue (System.Text.Json.Nodes): one JSON string, number or boolean
// A JsonValue node read from JSON keeps the token it was read from (a number keeps its lexeme) and writes it back unchanged; JSON
// objects and arrays are not JsonValues, and JSON null is a null reference (a nullable position), never a JsonValue.

function jsonScalar(value, path) {
  if (value !== null && typeof value === "object") {
    if (value.kind === "string" && typeof value.value === "string") return { kind: "string", value: value.value };
    if (value.kind === "number" && typeof value.text === "string" && jsonNumberPattern.test(value.text)) return { kind: "number", text: value.text };
    if (value.kind === "boolean" && typeof value.value === "boolean") return { kind: "boolean", value: value.value };
  }
  throw fail("type-mismatch", path, "JsonValue is a JSON string, number or boolean value");
}

export function jsonScalarDomainRule(value, context) {
  return jsonScalar(value, context.path);
}

export const jsonScalarValidate = { validateDomain: jsonScalarDomainRule };
export const jsonScalarRequestEncode = {
  encodeRequest(value, context) {
    return jsonScalar(value, context.path);
  },
};
export const jsonScalarResponseDecode = {
  decodeResponse(wire, context) {
    return jsonScalar(wire, context.path);
  },
};
export const jsonScalarRequestInput = {
  parseRequestInput(input, context) {
    return typeof input === "string" ? { kind: "string", value: input } : jsonScalar(input, context.path);
  },
};
export function jsonScalarOracle(a, b) {
  return a.kind === b.kind && (a.kind === "number" ? a.text === b.text : a.value === b.value);
}

/** JSON number lexemes (the number branch of a JsonValue). */
export const jsonNumberGrammar = grammar("json-number", (text) => jsonNumberPattern.test(text));

// ---------------------------------------------------------------- TimeZoneInfo / CultureInfo (ids the exporting server accepts)
// Which zone ids and culture names a server accepts depends on its environment (time zone database, ICU data). The registration
// records the ids it verified on the exporting server — FindSystemTimeZoneById(id).Id == id, GetCultureInfo(name, true).Name == name —
// as the binding context entries "zones" and "cultures" (JSON arrays); requests use those, responses carry whatever Id/Name the
// server's value has (a custom zone, an alias it resolved).

const contextLists = new Map();
function contextList(context, name) {
  const text = contextEntry(context, name) ?? "[]";
  let set = contextLists.get(text);
  if (set === undefined) {
    let list;
    try {
      list = JSON.parse(text);
    } catch {
      list = [];
    }
    set = new Set(Array.isArray(list) ? list.filter((v) => typeof v === "string") : []);
    contextLists.set(text, set);
  }
  return set;
}

function environmentIdCodec(name, entry, what) {
  const any = (value, context) => {
    if (typeof value !== "string") throw fail("type-mismatch", context.path, `${name} is a string`);
    return value;
  };
  const known = (value, context) => {
    any(value, context);
    if (!contextList(context, entry).has(value)) throw fail("domain-rule", context.path, `the server does not know the ${what} "${value}" (the ids it accepts are recorded at export)`);
    return value;
  };
  return {
    domainRule: any,
    requestRule: known,
    validate: { validateDomain: any },
    requestEncode: {
      encodeRequest(value, context) {
        return { kind: "string", value: known(value, context) };
      },
    },
    responseDecode: {
      decodeResponse(wire, context) {
        if (wire.kind !== "string") throw fail("type-mismatch", context.path, `${name} requires a JSON string but found ${wire.kind}`);
        return wire.value;
      },
    },
    requestInput: {
      parseRequestInput(input, context) {
        return known(typeof input === "string" ? input.trim() : inputText(input, context, what), context);
      },
    },
  };
}

const timeZone = environmentIdCodec("TimeZoneInfo", "zones", "time zone id");
/** Any zone id (what the server writes); requests use timeZoneIdRequestRule. */
export const timeZoneIdDomainRule = timeZone.domainRule;
/** A zone id the exporting server accepts (the binding context entry "zones"). */
export const timeZoneIdRequestRule = timeZone.requestRule;
export const timeZoneIdValidate = timeZone.validate;
export const timeZoneIdRequestEncode = timeZone.requestEncode;
export const timeZoneIdResponseDecode = timeZone.responseDecode;
export const timeZoneIdRequestInput = timeZone.requestInput;
export function timeZoneIdOracle(a, b) {
  return a === b;
}
/** The zone ids of the binding context (for pickers): the ids the exporting server accepts. */
export function timeZoneIds(context) {
  return [...contextList(context, "zones")];
}

const culture = environmentIdCodec("CultureInfo", "cultures", "culture name");
/** Any culture name (what the server writes); requests use cultureNameRequestRule. */
export const cultureNameDomainRule = culture.domainRule;
/** A culture name the exporting server accepts (the binding context entry "cultures"; "" is the invariant culture). */
export const cultureNameRequestRule = culture.requestRule;
export const cultureNameValidate = culture.validate;
export const cultureNameRequestEncode = culture.requestEncode;
export const cultureNameResponseDecode = culture.responseDecode;
export const cultureNameRequestInput = culture.requestInput;
export function cultureNameOracle(a, b) {
  return a === b;
}
/** The culture names of the binding context (for pickers): the names the exporting server accepts. */
export function cultureNames(context) {
  return [...contextList(context, "cultures")];
}
