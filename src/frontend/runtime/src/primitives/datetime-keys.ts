import { CodecError } from "../codec/errors.js";
import { formatDateTimeUtc, maxDateTimeTicks, ticksPerHour, ticksPerMinute, type DateTime } from "./datetime.js";

/**
 * The server's time zone as a contract records it (TisiliaOptions.DateTimes.ServerTimeZone): the UTC offset .NET gives each instant
 * from `from` to `until`. System.Text.Json reads a DateTime written with an offset as DateTimeOffset.LocalDateTime, the UTC ticks plus that
 * offset, and DateTime dictionary keys are equal when their ticks are: keys a client writes differently can be one key on the server,
 * which keeps the last value.
 */
export interface ServerTimeZone {
  readonly zone: string;
  readonly from: bigint;
  readonly until: bigint;
  /** Ascending UTC ticks; the first is `from`. */
  readonly starts: readonly bigint[];
  /** The offset from `starts[i]` on, in ticks. */
  readonly offsets: readonly bigint[];
}

/** .NET time zone offsets are within ±14 hours (TimeZoneInfo and DateTimeOffset). */
const maxOffsetTicks = 14n * ticksPerHour;

/** Reads the binding context value `{"zone", "until", "offsets": [["ticks", minutes], …]}`; undefined when it is malformed. */
export function parseServerTimeZone(text: string): ServerTimeZone | undefined {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return undefined;
  }
  const doc = raw as { zone?: unknown; until?: unknown; offsets?: unknown };
  if (typeof raw !== "object" || raw === null || typeof doc.zone !== "string" || doc.zone.length === 0 || !isTicks(doc.until) || !Array.isArray(doc.offsets) || doc.offsets.length === 0) {
    return undefined;
  }
  const until = BigInt(doc.until);
  const starts: bigint[] = [];
  const offsets: bigint[] = [];
  for (const entry of doc.offsets as unknown[]) {
    if (!Array.isArray(entry) || entry.length !== 2 || !isTicks(entry[0]) || !Number.isInteger(entry[1]) || Math.abs(entry[1] as number) > 840) {
      return undefined;
    }
    const start = BigInt(entry[0]);
    if (start >= until || (starts.length > 0 && start <= starts[starts.length - 1]!)) {
      return undefined;
    }
    starts.push(start);
    offsets.push(BigInt(entry[1] as number) * ticksPerMinute);
  }
  return { zone: doc.zone, from: starts[0]!, until, starts, offsets };
}

function isTicks(value: unknown): value is string {
  return typeof value === "string" && /^(0|[1-9][0-9]{0,18})$/.test(value) && BigInt(value) <= maxDateTimeTicks;
}

function offsetAt(zone: ServerTimeZone, instant: bigint): bigint | undefined {
  if (instant < zone.from || instant >= zone.until) {
    return undefined;
  }
  let lo = 0;
  let hi = zone.starts.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (zone.starts[mid]! <= instant) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  return zone.offsets[lo]!;
}

function clampTicks(ticks: bigint): bigint {
  return ticks < 0n ? 0n : ticks > maxDateTimeTicks ? maxDateTimeTicks : ticks;
}

/**
 * The DateTime the server reads for a key: its ticks, or — for a key with an offset whose instant the contract's zone does not cover —
 * the range its ticks lie in. DateTime.ToLocalTime clamps to the DateTime range (System.Private.CoreLib v10.0.0).
 */
type ServerKey =
  | { readonly kind: "exact"; readonly ticks: bigint; readonly instant: bigint | undefined }
  | { readonly kind: "range"; readonly lo: bigint; readonly hi: bigint; readonly instant: bigint };

function serverKey(value: DateTime, zone: ServerTimeZone | undefined): ServerKey {
  if (value.kind !== "datetime-local-wire") {
    return { kind: "exact", ticks: value.ticks, instant: undefined };
  }
  const instant = value.ticks - BigInt(value.offsetMinutes) * ticksPerMinute;
  const offset = zone === undefined ? undefined : offsetAt(zone, instant);
  return offset === undefined
    ? { kind: "range", lo: clampTicks(instant - maxOffsetTicks), hi: clampTicks(instant + maxOffsetTicks), instant }
    : { kind: "exact", ticks: clampTicks(instant + offset), instant };
}

/** The identity of a request key on the server: its ticks there, or its instant when they are not known (no two such keys can collide). */
export function dateTimeKeyIdentity(value: DateTime, zone: ServerTimeZone | undefined): string {
  const key = serverKey(value, zone);
  return key.kind === "exact" ? key.ticks.toString() : "instant:" + key.instant.toString();
}

/**
 * Refuses request keys that are one key on the server, and keys that could be one when the contract cannot tell: a key with an offset
 * whose instant the declared zone does not cover (or without a declaration) may be read with any offset within ±14 hours.
 */
export function checkDateTimeKeys(values: readonly DateTime[], zone: ServerTimeZone | undefined, keyPath: (index: number) => string): void {
  if (values.length < 2 || !values.some((v) => v.kind === "datetime-local-wire")) {
    return;
  }
  const keys = values.map((v) => serverKey(v, zone));
  const instants = new Map<bigint, number>();
  const exact = new Map<bigint, number>();
  keys.forEach((key, i) => {
    if (key.instant !== undefined) {
      if (instants.has(key.instant)) {
        throw new CodecError("key-collision", keyPath(i), "two DateTime keys are the same instant written with different offsets, which the server reads as one key and keeps the last value of");
      }
      instants.set(key.instant, i);
    }
    if (key.kind === "exact") {
      if (exact.has(key.ticks)) {
        throw new CodecError("key-collision", keyPath(i), zone === undefined
          ? "two DateTime keys have the same ticks, which the server reads as one key (DateTime equality ignores Kind) and keeps the last value of"
          : `two DateTime keys are the same DateTime on a server in time zone '${zone.zone}', which converts a key with an offset to its own time and keeps the last value of keys that are equal`);
      }
      exact.set(key.ticks, i);
    }
  });
  const ranges = keys.map((key, i) => ({ key, i })).filter((r): r is { key: Extract<ServerKey, { kind: "range" }>; i: number } => r.key.kind === "range");
  if (ranges.length === 0) {
    return;
  }
  const uncertain = (i: number): CodecError => new CodecError("unsupported", keyPath(i), zone === undefined
    ? "two DateTime keys can be one key on the server, depending on its time zone: declare the zone (TisiliaOptions.DateTimes.ServerTimeZone), or keep keys with an offset more than 28 hours apart and more than 14 hours from the other keys"
    : `two DateTime keys can be one key on the server: a key with an offset is outside ${formatDateTimeUtc({ kind: "datetime-utc", ticks: zone.from })} to ${formatDateTimeUtc({ kind: "datetime-utc", ticks: zone.until })}, which the declared server time zone '${zone.zone}' covers; keep such keys more than 28 hours apart and more than 14 hours from the other keys`);
  ranges.sort((a, b) => (a.key.lo < b.key.lo ? -1 : a.key.lo > b.key.lo ? 1 : 0));
  let reach = ranges[0]!;
  for (const range of ranges.slice(1)) {
    if (range.key.lo <= reach.key.hi) {
      throw uncertain(Math.max(range.i, reach.i));
    }
    if (range.key.hi > reach.key.hi) {
      reach = range;
    }
  }
  const known = [...exact.entries()].sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
  for (const { key, i } of ranges) {
    // the first known key at or after the range's start
    let lo = 0;
    let hi = known.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (known[mid]![0] < key.lo) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    if (lo < known.length && known[lo]![0] <= key.hi) {
      throw uncertain(Math.max(i, known[lo]![1]));
    }
  }
}
