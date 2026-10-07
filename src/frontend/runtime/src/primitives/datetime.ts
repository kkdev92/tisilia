import { CodecError } from "../codec/errors.js";

/**
 * Date/time scalars. All values keep 100-nanosecond ticks as bigint; nothing is routed through JS Date.
 * Grammar follows the System.Text.Json ISO 8601-1:2019 extended profile as observed on .NET 10 (tests/oracle/DotnetStjOracle).
 */

export const ticksPerSecond = 10_000_000n;
export const ticksPerMinute = 600_000_000n;
export const ticksPerHour = 36_000_000_000n;
export const ticksPerDay = 864_000_000_000n;
/** DateTime.MaxValue.Ticks */
export const maxDateTimeTicks = 3155378975999999999n;

export interface DateOnly {
  readonly kind: "date-only";
  readonly year: number;
  readonly month: number;
  readonly day: number;
}

export interface TimeOnly {
  readonly kind: "time-only";
  /** 0 .. 863_999_999_999 */
  readonly ticks: bigint;
}

export interface DateTimeUtc {
  readonly kind: "datetime-utc";
  readonly ticks: bigint;
}

export interface DateTimeUnspecified {
  readonly kind: "datetime-unspecified";
  readonly ticks: bigint;
}

export interface DateTimeLocalWire {
  readonly kind: "datetime-local-wire";
  /** Local calendar ticks as written by the server. */
  readonly ticks: bigint;
  readonly offsetMinutes: number;
}

/** System.Text.Json DateTime, with its wire Kind preserved and 100 ns precision. */
export type DateTime = DateTimeUtc | DateTimeUnspecified | DateTimeLocalWire;

export interface DateTimeOffset {
  readonly kind: "datetime-offset";
  /** Calendar ticks in the offset's local time (DateTimeOffset.Ticks). */
  readonly ticks: bigint;
  /** Offset in minutes, -840 .. 840 (DateTimeOffset.Offset). */
  readonly offsetMinutes: number;
}

export interface Duration {
  readonly kind: "duration";
  /** Signed Int64 ticks (TimeSpan.Ticks). */
  readonly ticks: bigint;
}

// ---------------------------------------------------------------- calendar math (proleptic Gregorian)

export function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

const daysToMonth365 = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365];
const daysToMonth366 = [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366];

export function daysInMonth(year: number, month: number): number {
  const table = isLeapYear(year) ? daysToMonth366 : daysToMonth365;
  return table[month]! - table[month - 1]!;
}

export function isValidDate(year: number, month: number, day: number): boolean {
  return Number.isInteger(year) && year >= 1 && year <= 9999 && Number.isInteger(month) && month >= 1 && month <= 12 && Number.isInteger(day) && day >= 1 && day <= daysInMonth(year, month);
}

/** Days since 0001-01-01 (DateTime day number). */
export function daysFromCivil(year: number, month: number, day: number): number {
  const y = year - 1;
  const table = isLeapYear(year) ? daysToMonth366 : daysToMonth365;
  return y * 365 + Math.floor(y / 4) - Math.floor(y / 100) + Math.floor(y / 400) + table[month - 1]! + day - 1;
}

export function civilFromDays(days: number): DateOnly {
  // Algorithm mirrors System.DateTime.GetDate (400/100/4-year cycles).
  let n = days;
  const y400 = Math.floor(n / 146097);
  n -= y400 * 146097;
  let y100 = Math.floor(n / 36524);
  if (y100 === 4) {
    y100 = 3;
  }
  n -= y100 * 36524;
  const y4 = Math.floor(n / 1461);
  n -= y4 * 1461;
  let y1 = Math.floor(n / 365);
  if (y1 === 4) {
    y1 = 3;
  }
  n -= y1 * 365;
  const year = y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;
  const table = isLeapYear(year) ? daysToMonth366 : daysToMonth365;
  let month = 1;
  while (n >= table[month]!) {
    month++;
  }
  return { kind: "date-only", year, month, day: n - table[month - 1]! + 1 };
}

// ---------------------------------------------------------------- parsing pieces

function twoDigits(s: string, at: number): number {
  const a = s.charCodeAt(at) - 48;
  const b = s.charCodeAt(at + 1) - 48;
  if (a < 0 || a > 9 || b < 0 || b > 9) {
    return -1;
  }
  return a * 10 + b;
}

function fourDigits(s: string, at: number): number {
  const hi = twoDigits(s, at);
  const lo = twoDigits(s, at + 2);
  return hi < 0 || lo < 0 ? -1 : hi * 100 + lo;
}

interface ParsedDateTime {
  readonly year: number;
  readonly month: number;
  readonly day: number;
  readonly hour: number;
  readonly minute: number;
  readonly second: number;
  /** 0 .. 9_999_999 (7 digits, right-padded) */
  readonly fraction: number;
  /** "none" | "utc" | number of minutes */
  readonly offset: "none" | "utc" | number;
}

/**
 * Parses `YYYY-MM-DD[THH:mm[:ss[.f{1,7}]]][Z|±HH:mm]`. Only uppercase T/Z, colon offsets and at most seven fraction
 * digits are accepted (the canonical subset; 8–16 digit fractions are an explicit alias profile).
 */
function parseIsoDateTime(s: string, path: string, name: string): ParsedDateTime {
  const fail = (): never => {
    throw new CodecError("grammar", path, `invalid ${name} lexeme`);
  };
  if (s.length < 10) {
    fail();
  }
  const year = fourDigits(s, 0);
  if (year < 0 || s.charCodeAt(4) !== 0x2d) {
    fail();
  }
  const month = twoDigits(s, 5);
  if (month < 0 || s.charCodeAt(7) !== 0x2d) {
    fail();
  }
  const day = twoDigits(s, 8);
  if (day < 0 || !isValidDate(year, month, day)) {
    throw new CodecError("range", path, `${name} date does not exist`);
  }
  let hour = 0;
  let minute = 0;
  let second = 0;
  let fraction = 0;
  let offset: "none" | "utc" | number = "none";
  let p = 10;
  if (p < s.length) {
    if (s.charCodeAt(p) !== 0x54) {
      fail();
    }
    p++;
    hour = twoDigits(s, p);
    if (hour < 0 || hour > 23 || s.charCodeAt(p + 2) !== 0x3a) {
      fail();
    }
    minute = twoDigits(s, p + 3);
    if (minute < 0 || minute > 59) {
      fail();
    }
    p += 5;
    if (p < s.length && s.charCodeAt(p) === 0x3a) {
      second = twoDigits(s, p + 1);
      if (second < 0 || second > 59) {
        fail();
      }
      p += 3;
      if (p < s.length && s.charCodeAt(p) === 0x2e) {
        p++;
        const start = p;
        while (p < s.length && s.charCodeAt(p) >= 48 && s.charCodeAt(p) <= 57) {
          p++;
        }
        const digits = p - start;
        if (digits < 1 || digits > 7) {
          fail();
        }
        fraction = Number.parseInt(s.slice(start, p).padEnd(7, "0"), 10);
      }
    }
    if (p < s.length) {
      const c = s.charCodeAt(p);
      if (c === 0x5a) {
        offset = "utc";
        p++;
      } else if (c === 0x2b || c === 0x2d) {
        const sign = c === 0x2d ? -1 : 1;
        const oh = twoDigits(s, p + 1);
        if (oh < 0 || s.charCodeAt(p + 3) !== 0x3a) {
          fail();
        }
        const om = twoDigits(s, p + 4);
        if (om < 0 || om > 59) {
          fail();
        }
        p += 6;
        const minutes = sign * (oh * 60 + om);
        if (minutes < -840 || minutes > 840) {
          throw new CodecError("range", path, `${name} offset must be within ±14:00`);
        }
        offset = minutes;
      } else {
        fail();
      }
    }
  }
  if (p !== s.length) {
    fail();
  }
  return { year, month, day, hour, minute, second, fraction, offset };
}

function ticksOf(p: ParsedDateTime): bigint {
  return (
    BigInt(daysFromCivil(p.year, p.month, p.day)) * ticksPerDay +
    BigInt(p.hour) * ticksPerHour +
    BigInt(p.minute) * ticksPerMinute +
    BigInt(p.second) * ticksPerSecond +
    BigInt(p.fraction)
  );
}

function pad2(n: number): string {
  return n < 10 ? "0" + String(n) : String(n);
}

function formatFraction(ticks: bigint): string {
  const frac = Number(ticks % ticksPerSecond);
  if (frac === 0) {
    return "";
  }
  return "." + String(frac).padStart(7, "0").replace(/0+$/, "");
}

function formatCalendar(ticks: bigint): string {
  const days = Number(ticks / ticksPerDay);
  const { year, month, day } = civilFromDays(days);
  const rem = ticks % ticksPerDay;
  const hour = Number(rem / ticksPerHour);
  const minute = Number((rem % ticksPerHour) / ticksPerMinute);
  const second = Number((rem % ticksPerMinute) / ticksPerSecond);
  return `${String(year).padStart(4, "0")}-${pad2(month)}-${pad2(day)}T${pad2(hour)}:${pad2(minute)}:${pad2(second)}${formatFraction(ticks)}`;
}

function formatOffset(offsetMinutes: number): string {
  const sign = offsetMinutes < 0 ? "-" : "+";
  const abs = Math.abs(offsetMinutes);
  return `${sign}${pad2(Math.floor(abs / 60))}:${pad2(abs % 60)}`;
}

function checkTicks(ticks: bigint, path: string, name: string): bigint {
  if (ticks < 0n || ticks > maxDateTimeTicks) {
    throw new CodecError("range", path, `${name} is outside the DateTime range`);
  }
  return ticks;
}

// ---------------------------------------------------------------- DateOnly

export function parseDateOnly(s: string, path = ""): DateOnly {
  if (s.length !== 10) {
    throw new CodecError("grammar", path, "date-only must be YYYY-MM-DD");
  }
  const p = parseIsoDateTime(s, path, "date-only");
  return { kind: "date-only", year: p.year, month: p.month, day: p.day };
}

export function formatDateOnly(d: DateOnly): string {
  return `${String(d.year).padStart(4, "0")}-${pad2(d.month)}-${pad2(d.day)}`;
}

export function validateDateOnly(value: unknown, path: string): DateOnly {
  const d = value as DateOnly;
  if (typeof value !== "object" || value === null || !isValidDate(d.year, d.month, d.day)) {
    throw new CodecError("type-mismatch", path, "date-only requires a valid {year, month, day}");
  }
  return { kind: "date-only", year: d.year, month: d.month, day: d.day };
}

// ---------------------------------------------------------------- TimeOnly

/** Canonical `HH:mm:ss[.fffffff]`; TimeOnly writes seconds always and a 7-digit fraction when non-zero. */
export function parseTimeOnly(s: string, path = ""): TimeOnly {
  const m = /^(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(s);
  if (m === null) {
    throw new CodecError("grammar", path, "time-only must be HH:mm:ss[.fffffff]");
  }
  const h = Number(m[1]);
  const mi = Number(m[2]);
  const sec = Number(m[3]);
  if (h > 23 || mi > 59 || sec > 59) {
    throw new CodecError("range", path, "time-only components out of range");
  }
  const frac = m[4] === undefined ? 0n : BigInt(m[4].padEnd(7, "0"));
  return { kind: "time-only", ticks: BigInt(h) * ticksPerHour + BigInt(mi) * ticksPerMinute + BigInt(sec) * ticksPerSecond + frac };
}

export function formatTimeOnly(t: TimeOnly): string {
  const hour = Number(t.ticks / ticksPerHour);
  const minute = Number((t.ticks % ticksPerHour) / ticksPerMinute);
  const second = Number((t.ticks % ticksPerMinute) / ticksPerSecond);
  const frac = Number(t.ticks % ticksPerSecond);
  return `${pad2(hour)}:${pad2(minute)}:${pad2(second)}${frac === 0 ? "" : "." + String(frac).padStart(7, "0")}`;
}

export function validateTimeOnly(value: unknown, path: string): TimeOnly {
  const t = value as TimeOnly;
  if (typeof value !== "object" || value === null || typeof t.ticks !== "bigint" || t.ticks < 0n || t.ticks >= ticksPerDay) {
    throw new CodecError("type-mismatch", path, "time-only requires ticks within one day");
  }
  return { kind: "time-only", ticks: t.ticks };
}

// ---------------------------------------------------------------- DateTime (Utc / Unspecified / Local wire)

/** Reads the suffix without consulting the browser's time zone or coercing a zone-less value to UTC. */
export function parseDateTime(s: string, path = ""): DateTime {
  const p = parseIsoDateTime(s, path, "datetime");
  const ticks = checkTicks(ticksOf(p), path, "datetime");
  if (p.offset === "utc") return { kind: "datetime-utc", ticks };
  if (p.offset === "none") return { kind: "datetime-unspecified", ticks };
  return { kind: "datetime-local-wire", ticks, offsetMinutes: p.offset };
}

export function formatDateTime(value: DateTime): string {
  switch (value.kind) {
    case "datetime-utc": return formatDateTimeUtc(value);
    case "datetime-unspecified": return formatDateTimeUnspecified(value);
    case "datetime-local-wire": return formatDateTimeLocalWire(value);
  }
}

export function validateDateTime(value: unknown, path: string): DateTime {
  const kind = typeof value === "object" && value !== null ? (value as { kind?: unknown }).kind : undefined;
  switch (kind) {
    case "datetime-utc": return { kind, ticks: validateDateTimeTicks(value, path, "utc") };
    case "datetime-unspecified": return { kind, ticks: validateDateTimeTicks(value, path, "unspecified") };
    case "datetime-local-wire": return validateDateTimeLocalWire(value, path);
    default: throw new CodecError("type-mismatch", path, "datetime requires a UTC, unspecified or local wire tick record");
  }
}

/** STJ can write a local extreme that its DateTimeOffset-based reader cannot accept. */
export function validateDateTimeRequest(value: unknown, path: string): DateTime {
  const result = validateDateTime(value, path);
  if (result.kind === "datetime-local-wire") {
    const utc = result.ticks - BigInt(result.offsetMinutes) * ticksPerMinute;
    if (utc < 0n || utc > maxDateTimeTicks) throw new CodecError("range", path, "datetime request UTC instant is outside the DateTime range");
  }
  return result;
}

export function parseDateTimeUtc(s: string, path = ""): DateTimeUtc {
  const p = parseIsoDateTime(s, path, "datetime-utc");
  if (p.offset !== "utc") {
    throw new CodecError("grammar", path, "datetime-utc requires the Z suffix");
  }
  return { kind: "datetime-utc", ticks: checkTicks(ticksOf(p), path, "datetime-utc") };
}

export function formatDateTimeUtc(d: DateTimeUtc): string {
  return formatCalendar(d.ticks) + "Z";
}

export function parseDateTimeUnspecified(s: string, path = ""): DateTimeUnspecified {
  const p = parseIsoDateTime(s, path, "datetime-unspecified");
  if (p.offset !== "none") {
    throw new CodecError("grammar", path, "datetime-unspecified must not carry an offset");
  }
  return { kind: "datetime-unspecified", ticks: checkTicks(ticksOf(p), path, "datetime-unspecified") };
}

export function formatDateTimeUnspecified(d: DateTimeUnspecified): string {
  return formatCalendar(d.ticks);
}

export function parseDateTimeLocalWire(s: string, path = ""): DateTimeLocalWire {
  const p = parseIsoDateTime(s, path, "datetime-local-wire");
  if (typeof p.offset !== "number") {
    throw new CodecError("grammar", path, "datetime-local-wire requires a ±HH:mm offset");
  }
  return { kind: "datetime-local-wire", ticks: checkTicks(ticksOf(p), path, "datetime-local-wire"), offsetMinutes: p.offset };
}

export function formatDateTimeLocalWire(d: DateTimeLocalWire): string {
  return formatCalendar(d.ticks) + formatOffset(d.offsetMinutes);
}

// ---------------------------------------------------------------- DateTimeOffset

/** Requires an explicit offset; `Z` is accepted on read (STJ writes +00:00 but reads Z). */
export function parseDateTimeOffset(s: string, path = ""): DateTimeOffset {
  const p = parseIsoDateTime(s, path, "datetime-offset");
  if (p.offset === "none") {
    throw new CodecError("grammar", path, "datetime-offset requires an offset (the server's local offset is never assumed)");
  }
  const offsetMinutes = p.offset === "utc" ? 0 : p.offset;
  const ticks = checkTicks(ticksOf(p), path, "datetime-offset");
  const utcTicks = ticks - BigInt(offsetMinutes) * ticksPerMinute;
  if (utcTicks < 0n || utcTicks > maxDateTimeTicks) {
    throw new CodecError("range", path, "datetime-offset UTC instant is outside the DateTime range");
  }
  return { kind: "datetime-offset", ticks, offsetMinutes };
}

/** Canonical writer: local calendar ticks plus `±HH:mm`; zero offset is written `+00:00` exactly like System.Text.Json. */
export function formatDateTimeOffset(d: DateTimeOffset): string {
  return formatCalendar(d.ticks) + formatOffset(d.offsetMinutes);
}

// ---------------------------------------------------------------- JS Date interop (explicit, millisecond precision)

/** DateTime ticks of the Unix epoch, 1970-01-01T00:00:00Z. */
const unixEpochTicks = 621_355_968_000_000_000n;
const ticksPerMillisecond = 10_000n;

function instantToDate(utcTicks: bigint): Date {
  const since = utcTicks - unixEpochTicks;
  let ms = since / ticksPerMillisecond;
  if (since % ticksPerMillisecond < 0n) {
    ms -= 1n; // floor: ticks below a millisecond are dropped toward the past
  }
  return new Date(Number(ms));
}

function dateToUtcTicks(date: Date, path: string): bigint {
  const ms = date.getTime();
  if (Number.isNaN(ms)) {
    throw new CodecError("range", path, "the Date is invalid");
  }
  const ticks = BigInt(ms) * ticksPerMillisecond + unixEpochTicks;
  if (ticks < 0n || ticks > maxDateTimeTicks) {
    throw new CodecError("range", path, "the Date is outside the .NET DateTime range (years 1 to 9999)");
  }
  return ticks;
}

/** The instant of a DateTimeOffset as a JS Date. A Date holds milliseconds: ticks below a millisecond are dropped. */
export function dateTimeOffsetToDate(d: DateTimeOffset): Date {
  return instantToDate(d.ticks - BigInt(d.offsetMinutes) * ticksPerMinute);
}

/** A JS Date as a DateTimeOffset at the given offset in minutes (default 0, written `+00:00`); exact, a Date has whole milliseconds. */
export function dateTimeOffsetFromDate(date: Date, offsetMinutes = 0, path = ""): DateTimeOffset {
  if (!Number.isInteger(offsetMinutes) || offsetMinutes < -840 || offsetMinutes > 840) {
    throw new CodecError("range", path, "the offset must be whole minutes between -840 and 840");
  }
  const ticks = dateToUtcTicks(date, path) + BigInt(offsetMinutes) * ticksPerMinute;
  if (ticks < 0n || ticks > maxDateTimeTicks) {
    throw new CodecError("range", path, "the local time at that offset is outside the .NET DateTime range");
  }
  return { kind: "datetime-offset", ticks, offsetMinutes };
}

/** A UTC DateTime as a JS Date. A Date holds milliseconds: ticks below a millisecond are dropped. */
export function dateTimeUtcToDate(d: DateTimeUtc): Date {
  return instantToDate(d.ticks);
}

/** A JS Date as a UTC DateTime; exact, a Date has whole milliseconds. */
export function dateTimeUtcFromDate(date: Date, path = ""): DateTimeUtc {
  return { kind: "datetime-utc", ticks: dateToUtcTicks(date, path) };
}

export function validateDateTimeOffset(value: unknown, path: string): DateTimeOffset {
  const d = value as DateTimeOffset;
  if (typeof value !== "object" || value === null || typeof d.ticks !== "bigint" || !Number.isInteger(d.offsetMinutes) || d.offsetMinutes < -840 || d.offsetMinutes > 840) {
    throw new CodecError("type-mismatch", path, "datetime-offset requires {ticks: bigint, offsetMinutes}");
  }
  checkTicks(d.ticks, path, "datetime-offset");
  const utc = d.ticks - BigInt(d.offsetMinutes) * ticksPerMinute;
  if (utc < 0n || utc > maxDateTimeTicks) {
    throw new CodecError("range", path, "datetime-offset UTC instant is outside the DateTime range");
  }
  return { kind: "datetime-offset", ticks: d.ticks, offsetMinutes: d.offsetMinutes };
}

export function validateDateTimeTicks(value: unknown, path: string, kind: "utc" | "unspecified"): bigint {
  const d = value as { ticks?: unknown; kind?: unknown };
  if (typeof value !== "object" || value === null || typeof d.ticks !== "bigint" || d.kind !== "datetime-" + kind) {
    throw new CodecError("type-mismatch", path, `datetime-${kind} requires {kind: "datetime-${kind}", ticks: bigint}`);
  }
  return checkTicks(d.ticks, path, `datetime-${kind}`);
}

export function validateDateTimeLocalWire(value: unknown, path: string): DateTimeLocalWire {
  const d = value as DateTimeLocalWire;
  if (typeof value !== "object" || value === null || d.kind !== "datetime-local-wire" || typeof d.ticks !== "bigint" || !Number.isInteger(d.offsetMinutes) || d.offsetMinutes < -840 || d.offsetMinutes > 840) {
    throw new CodecError("type-mismatch", path, "datetime-local-wire requires {kind: \"datetime-local-wire\", ticks, offsetMinutes}");
  }
  return { kind: "datetime-local-wire", ticks: checkTicks(d.ticks, path, "datetime-local-wire"), offsetMinutes: d.offsetMinutes };
}

// ---------------------------------------------------------------- Duration (TimeSpan "c" format)

const durationMax = 9223372036854775807n;
const durationMin = -9223372036854775808n;

/**
 * Canonical `[-][d.]hh:mm:ss[.fffffff]` as written by System.Text.Json ('c' format: two-digit hh, fraction always
 * 7 digits when non-zero). Read aliases such as `1:2:3`, `5` or `02:03` exist server-side but are not canonical.
 */
export function parseDuration(s: string, path = ""): Duration {
  const m = /^(-)?(?:(\d{1,8})\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d{7}))?$/.exec(s);
  if (m === null) {
    throw new CodecError("grammar", path, "duration must be [-][d.]hh:mm:ss[.fffffff]");
  }
  const days = m[2] === undefined ? 0n : BigInt(m[2]);
  const hours = Number(m[3]);
  const minutes = Number(m[4]);
  const seconds = Number(m[5]);
  if (hours > 23 || minutes > 59 || seconds > 59) {
    throw new CodecError("range", path, "duration components out of range");
  }
  const frac = m[6] === undefined ? 0n : BigInt(m[6]);
  let ticks = days * ticksPerDay + BigInt(hours) * ticksPerHour + BigInt(minutes) * ticksPerMinute + BigInt(seconds) * ticksPerSecond + frac;
  if (m[1] === "-") {
    ticks = -ticks;
  }
  if (ticks > durationMax || ticks < durationMin) {
    throw new CodecError("range", path, "duration is outside the Int64 tick range");
  }
  return { kind: "duration", ticks };
}

export function formatDuration(d: Duration): string {
  const negative = d.ticks < 0n;
  const abs = negative ? -d.ticks : d.ticks;
  const days = abs / ticksPerDay;
  const rem = abs % ticksPerDay;
  const hours = Number(rem / ticksPerHour);
  const minutes = Number((rem % ticksPerHour) / ticksPerMinute);
  const seconds = Number((rem % ticksPerMinute) / ticksPerSecond);
  const frac = Number(rem % ticksPerSecond);
  return `${negative ? "-" : ""}${days === 0n ? "" : String(days) + "."}${pad2(hours)}:${pad2(minutes)}:${pad2(seconds)}${frac === 0 ? "" : "." + String(frac).padStart(7, "0")}`;
}

export function validateDuration(value: unknown, path: string): Duration {
  const d = value as Duration;
  if (typeof value !== "object" || value === null || typeof d.ticks !== "bigint" || d.ticks > durationMax || d.ticks < durationMin) {
    throw new CodecError("type-mismatch", path, "duration requires Int64 ticks");
  }
  return { kind: "duration", ticks: d.ticks };
}
