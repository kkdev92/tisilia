import { CodecError } from "../codec/errors.js";
import type { ScalarName } from "../codec/scalars.js";
import { formatDecimal, parseDecimalLexeme } from "../primitives/decimal.js";
import { formatFloat32, formatFloat64, parseFloat32Lexeme, parseFloat64Lexeme } from "../primitives/float.js";
import { checkIntegerRange, isSmallInteger, type IntegerScalarName } from "../primitives/integers.js";
import { decodeBase64, encodeBase64, parseGuid, validateChar } from "../primitives/text.js";
import {
  formatDateOnly,
  formatDateTime,
  formatDateTimeLocalWire,
  formatDateTimeOffset,
  formatDateTimeUnspecified,
  formatDateTimeUtc,
  parseDateOnly,
  parseDateTime,
  parseDateTimeLocalWire,
  parseDateTimeOffset,
  parseDateTimeUnspecified,
  parseDateTimeUtc,
  parseTimeOnly,
  ticksPerDay,
  ticksPerHour,
  ticksPerMinute,
  ticksPerSecond,
  validateDateTimeRequest,
  type DateTime,
  type DateTimeLocalWire,
  type DateTimeOffset,
  type Duration,
  type TimeOnly,
} from "../primitives/datetime.js";
import { isXmlSpace } from "./dom.js";

/**
 * The text grammars of XML wires: the forms MVC's XmlSerializer formatters write for a CLR type (XmlConvert and XmlCustomFormatter,
 * dotnet/runtime v10.0.0). A client writes these forms, which the server reads; it reads what the server writes in them.
 */
export type XmlScalarGrammar =
  | "xml-string"
  | "xml-boolean"
  | "xml-integer"
  | "xml-decimal"
  | "xml-float"
  | "xml-datetime"
  | "xml-date"
  | "xml-time-only"
  | "xml-datetime-offset"
  | "xml-duration"
  | "xml-guid"
  | "xml-char"
  | "xml-base64"
  | "xml-hex";

export interface XmlLexical<T> {
  /** The value's text as the server reads it (request). */
  format(value: T, path: string): string;
  /** The value the server's text stands for (response). */
  parse(text: string, path: string): T;
}

/** The text without leading and trailing XML white space (the server's number and date readers allow it). */
export function trimXmlSpace(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && isXmlSpace(text.charCodeAt(start))) {
    start++;
  }
  while (end > start && isXmlSpace(text.charCodeAt(end - 1))) {
    end--;
  }
  return start === 0 && end === text.length ? text : text.slice(start, end);
}

/** A JSON number lexeme: the server writes decimal and binary floating-point values in this grammar (no `+`, no leading zeros). */
const jsonNumber = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/;

function integerLexical(name: IntegerScalarName): XmlLexical<number | bigint> {
  return {
    format: (value) => (typeof value === "bigint" ? value.toString(10) : String(value)),
    parse: (text, path) => {
      // XmlConvert.ToInt32 and the others: an optional sign and digits, with white space around
      const t = trimXmlSpace(text);
      if (!/^[+-]?[0-9]+$/.test(t)) {
        throw new CodecError("grammar", path, `${name} XML text must be an optional sign and digits`);
      }
      const value = checkIntegerRange(name, BigInt(t), path);
      return isSmallInteger(name) ? Number(value) : value;
    },
  };
}

function floatLexical(name: "float32" | "float64"): XmlLexical<number> {
  const single = name === "float32";
  return {
    // XmlConvert writes INF, -INF and NaN; it reads the shortest form the client writes (exponent with an explicit sign included)
    format: (value, path) => (Number.isNaN(value) ? "NaN" : value === Infinity ? "INF" : value === -Infinity ? "-INF" : single ? formatFloat32(value, path) : formatFloat64(value, path)),
    parse: (text, path) => {
      const t = trimXmlSpace(text);
      if (t === "NaN") {
        return Number.NaN;
      }
      if (t === "INF") {
        return Infinity;
      }
      if (t === "-INF") {
        return -Infinity;
      }
      if (!jsonNumber.test(t)) {
        throw new CodecError("grammar", path, `${name} XML text must be a number, INF, -INF or NaN`);
      }
      return single ? parseFloat32Lexeme(t, path) : parseFloat64Lexeme(t, path);
    },
  };
}

/** `HH:mm:ss.FFFFFFF`: XmlSerializer writes a TimeOnly with its fraction's trailing zeros left out (XmlCustomFormatter.FromTimeOnly). */
function formatXmlTimeOnly(t: TimeOnly): string {
  const pad2 = (n: bigint): string => n.toString(10).padStart(2, "0");
  const fraction = t.ticks % ticksPerSecond;
  const text = `${pad2(t.ticks / ticksPerHour)}:${pad2((t.ticks % ticksPerHour) / ticksPerMinute)}:${pad2((t.ticks % ticksPerMinute) / ticksPerSecond)}`;
  return fraction === 0n ? text : text + "." + fraction.toString(10).padStart(7, "0").replace(/0+$/, "");
}

/** XmlConvert writes a zero offset as `Z`. */
function formatXmlDateTimeOffset(d: DateTimeOffset): string {
  const text = formatDateTimeOffset(d);
  return d.offsetMinutes === 0 ? text.slice(0, -"+00:00".length) + "Z" : text;
}

const durationMax = 9223372036854775807n;
const durationMin = -9223372036854775808n;

/** xs:duration as XmlConvert writes a TimeSpan (XsdDuration): days, hours under 24, minutes, seconds; zero is `PT0S`. */
export function formatXmlDuration(d: Duration): string {
  const negative = d.ticks < 0n;
  const abs = negative ? -d.ticks : d.ticks;
  const days = abs / ticksPerDay;
  const hours = (abs / ticksPerHour) % 24n;
  const minutes = (abs / ticksPerMinute) % 60n;
  const seconds = (abs / ticksPerSecond) % 60n;
  const fraction = abs % ticksPerSecond;
  let text = (negative ? "-" : "") + "P";
  if (days !== 0n) {
    text += days.toString(10) + "D";
  }
  if (hours !== 0n || minutes !== 0n || seconds !== 0n || fraction !== 0n) {
    text += "T";
    if (hours !== 0n) {
      text += hours.toString(10) + "H";
    }
    if (minutes !== 0n) {
      text += minutes.toString(10) + "M";
    }
    if (seconds !== 0n || fraction !== 0n) {
      text += seconds.toString(10);
      if (fraction !== 0n) {
        text += "." + fraction.toString(10).padStart(7, "0").replace(/0+$/, "");
      }
      text += "S";
    }
  }
  return text.endsWith("P") ? text + "T0S" : text;
}

/**
 * xs:duration with days, hours, minutes and seconds (up to 7 fraction digits). The server writes only these; years and months, which
 * XmlConvert reads as 365 and 30 days, are refused.
 */
export function parseXmlDuration(text: string, path: string): Duration {
  const t = trimXmlSpace(text);
  const m = /^(-)?P(?:([0-9]+)D)?(?:T(?:([0-9]+)H)?(?:([0-9]+)M)?(?:([0-9]+)(?:\.([0-9]{1,7}))?S)?)?$/.exec(t);
  if (m === null || (m[2] === undefined && m[3] === undefined && m[4] === undefined && m[5] === undefined) || t.endsWith("T")) {
    throw new CodecError("grammar", path, "duration XML text must be an xs:duration of days, hours, minutes and seconds");
  }
  const part = (s: string | undefined): bigint => (s === undefined ? 0n : BigInt(s));
  let ticks = part(m[2]) * ticksPerDay + part(m[3]) * ticksPerHour + part(m[4]) * ticksPerMinute + part(m[5]) * ticksPerSecond + (m[6] === undefined ? 0n : BigInt(m[6].padEnd(7, "0")));
  if (m[1] === "-") {
    ticks = -ticks;
  }
  if (ticks > durationMax || ticks < durationMin) {
    throw new CodecError("range", path, "duration is outside the TimeSpan range");
  }
  return { kind: "duration", ticks };
}

function hexOf(bytes: Uint8Array): string {
  let out = "";
  for (const b of bytes) {
    out += b.toString(16).toUpperCase().padStart(2, "0");
  }
  return out;
}

function bytesOfHex(text: string, path: string): Uint8Array {
  const t = trimXmlSpace(text);
  if (!/^(?:[0-9A-Fa-f]{2})*$/.test(t)) {
    throw new CodecError("grammar", path, "hexBinary XML text must be pairs of hexadecimal digits");
  }
  const out = new Uint8Array(t.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = Number.parseInt(t.slice(i * 2, i * 2 + 2), 16);
  }
  return out;
}

function dateTimeLexical(scalar: ScalarName): XmlLexical<DateTime> {
  // XmlConvert's round-trip form (XmlCustomFormatter.FromDateTime): System.Text.Json writes a DateTime the same way
  switch (scalar) {
    case "datetime-utc":
      return { format: (v) => formatDateTimeUtc(v as never), parse: (t, p) => parseDateTimeUtc(trimXmlSpace(t), p) };
    case "datetime-unspecified":
      return { format: (v) => formatDateTimeUnspecified(v as never), parse: (t, p) => parseDateTimeUnspecified(trimXmlSpace(t), p) };
    case "datetime-local-wire":
      return { format: (v, p) => formatDateTimeLocalWire(validateDateTimeRequest(v, p) as DateTimeLocalWire), parse: (t, p) => parseDateTimeLocalWire(trimXmlSpace(t), p) };
    default:
      return { format: (v, p) => formatDateTime(validateDateTimeRequest(v, p)), parse: (t, p) => parseDateTime(trimXmlSpace(t), p) };
  }
}

/** The lexical form of a builtin scalar in an XML grammar; the value given to `format` is already a valid domain value. */
export function xmlLexical(grammar: XmlScalarGrammar, scalar: ScalarName): XmlLexical<unknown> {
  switch (grammar) {
    case "xml-string":
      return { format: (v) => v as string, parse: (t) => t };
    case "xml-boolean":
      return {
        format: (v) => (v === true ? "true" : "false"),
        parse: (text, path) => {
          // XmlConvert.ToBoolean: true, false, 1 or 0 with white space around
          switch (trimXmlSpace(text)) {
            case "true":
            case "1":
              return true;
            case "false":
            case "0":
              return false;
            default:
              throw new CodecError("grammar", path, "boolean XML text must be true, false, 1 or 0");
          }
        },
      };
    case "xml-integer":
      return integerLexical(scalar as IntegerScalarName) as XmlLexical<unknown>;
    case "xml-decimal":
      return {
        format: (v) => formatDecimal(v as never),
        parse: (text, path) => {
          const t = trimXmlSpace(text);
          if (!jsonNumber.test(t) || /[eE]/.test(t)) {
            throw new CodecError("grammar", path, "decimal XML text must be digits with an optional fraction");
          }
          return parseDecimalLexeme(t, path);
        },
      };
    case "xml-float":
      return floatLexical(scalar as "float32" | "float64") as XmlLexical<unknown>;
    case "xml-datetime":
      return dateTimeLexical(scalar) as XmlLexical<unknown>;
    case "xml-date":
      return { format: (v) => formatDateOnly(v as never), parse: (t, p) => parseDateOnly(trimXmlSpace(t), p) };
    case "xml-time-only":
      return { format: (v) => formatXmlTimeOnly(v as TimeOnly), parse: (t, p) => parseTimeOnly(trimXmlSpace(t), p) };
    case "xml-datetime-offset":
      return { format: (v) => formatXmlDateTimeOffset(v as DateTimeOffset), parse: (t, p) => parseDateTimeOffset(trimXmlSpace(t), p) };
    case "xml-duration":
      return { format: (v) => formatXmlDuration(v as Duration), parse: parseXmlDuration };
    case "xml-guid":
      return { format: (v) => v as string, parse: (t, p) => parseGuid(trimXmlSpace(t), p) };
    case "xml-char":
      return {
        // XmlCustomFormatter.FromChar: the UTF-16 code unit as a number
        format: (v) => String((v as string).charCodeAt(0)),
        parse: (text, path) => {
          const t = trimXmlSpace(text);
          if (!/^[0-9]+$/.test(t) || Number(t) > 0xffff) {
            throw new CodecError("grammar", path, "char XML text must be a UTF-16 code unit number");
          }
          return validateChar(String.fromCharCode(Number(t)), path);
        },
      };
    case "xml-base64":
      return { format: (v) => encodeBase64(v as Uint8Array), parse: (t, p) => decodeBase64(trimXmlSpace(t), p) };
    case "xml-hex":
      return { format: (v) => hexOf(v as Uint8Array), parse: bytesOfHex };
  }
}

/** One constant of an XML enum: its value and its XML name, in XmlSerializer's order. */
export interface XmlEnumName {
  readonly value: bigint;
  readonly name: string;
}

/**
 * An enum value as XmlSerializer writes it: the name of the first constant with the value; for a flags enum without one, the names of
 * the constants whose bits are all in the value, in order (XmlCustomFormatter.FromEnum), or the empty text for zero without a zero constant.
 */
export function formatXmlEnum(value: bigint, names: readonly XmlEnumName[], flags: boolean, path: string): string {
  const exact = names.find((n) => n.value === value);
  if (exact !== undefined) {
    return exact.name;
  }
  if (flags) {
    let rest = value;
    const parts: string[] = [];
    for (const n of names) {
      if (n.value === 0n) {
        continue;
      }
      if (rest === 0n) {
        break;
      }
      if ((n.value & value) === n.value) {
        parts.push(n.name);
        rest &= ~n.value;
      }
    }
    if (rest === 0n) {
      return parts.join(" ");
    }
  }
  throw new CodecError("domain-rule", path, "the server writes only defined enum values");
}

/** An enum value from its XML name; for a flags enum, names separated by spaces (none is zero). */
export function parseXmlEnum(text: string, names: readonly XmlEnumName[], flags: boolean, path: string): bigint {
  if (!flags) {
    const named = names.find((n) => n.name === text);
    if (named === undefined) {
      throw new CodecError("grammar", path, "unknown enum name");
    }
    return named.value;
  }
  let value = 0n;
  for (const part of text.split(" ")) {
    if (part.length === 0) {
      continue;
    }
    const named = names.find((n) => n.name === part);
    if (named === undefined) {
      throw new CodecError("grammar", path, "unknown enum name");
    }
    value |= named.value;
  }
  return value;
}
