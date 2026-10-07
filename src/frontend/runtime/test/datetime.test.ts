import { describe, expect, it } from "vitest";
import { scalarCodec, type ScalarName } from "../src/codec/scalars.js";
import { createCodecContext } from "../src/codec/abi.js";
import { formatDateTime, parseDateTime, maxDateTimeTicks, type DateTime } from "../src/primitives/datetime.js";
import { CodecError } from "../src/codec/errors.js";
import { mapCodec } from "../src/codec/structural.js";
import { TisiliaMap } from "../src/codec/map.js";

const context = createCodecContext({ profileId: "test" });
describe("mixed DateTime", () => {
  it.each([
    ["2026-09-30T06:04:05.1234567Z", "datetime-utc"],
    ["2026-09-30T06:04:05.1234567", "datetime-unspecified"],
    ["2026-09-30T15:04:05.1234567+09:00", "datetime-local-wire"],
    ["0001-01-01T00:00:00Z", "datetime-utc"],
    ["9999-12-31T23:59:59.9999999", "datetime-unspecified"],
  ])("keeps the Kind and all seven fractional digits: %s", (text, kind) => {
    const codec = scalarCodec("datetime" as ScalarName);
    const value = codec.decodeResponse!({ kind: "string", value: text }, context);
    expect(value).toMatchObject({ kind });
    expect(codec.encodeRequest!(value, context)).toEqual({ kind: "string", value: text });
    expect(codec.decodeKey!(text, context)).toEqual(value);
    expect(codec.encodeKey!(value, context)).toEqual(text);
    expect(codec.parseRequestInput!(text, context)).toEqual(value);
  });

  it("round trips a seeded corpus without using JS Date", () => {
    const codec = scalarCodec("datetime");
    let seed = 123456789n;
    for (let i = 0; i < 1500; i++) {
      seed = (seed * 6364136223846793005n + 1n) & ((1n << 64n) - 1n);
      const ticks = 864000000000n + seed % (maxDateTimeTicks - 1728000000000n);
      const value = i % 3 === 0 ? { kind: "datetime-utc" as const, ticks }
        : i % 3 === 1 ? { kind: "datetime-unspecified" as const, ticks }
        : { kind: "datetime-local-wire" as const, ticks, offsetMinutes: i % 1681 - 840 };
      expect(codec.decodeResponse!(codec.encodeRequest!(value, context), context)).toEqual(value);
      expect(parseDateTime(formatDateTime(value))).toEqual(value);
    }
  });

  it("rejects invalid dates, offsets, tags, numeric ticks and excess precision", () => {
    const codec = scalarCodec("datetime");
    for (const text of ["", "2026-02-30T00:00:00Z", "2026-01-01T24:00:00Z", "2026-01-01t12:00:00z", "2026-01-01T00:00:00+14:01", "2026-01-01T00:00:00.12345678Z"]) {
      expect(() => codec.decodeResponse!({ kind: "string", value: text }, context), text).toThrow(CodecError);
    }
    for (const value of [new Date(), "2026-01-01T00:00:00Z", {}, { kind: "datetime-utc", ticks: 1 }, { kind: "datetime-offset", ticks: 1n, offsetMinutes: 0 }, { kind: "datetime-utc", ticks: -1n }, { kind: "datetime-local-wire", ticks: 1n, offsetMinutes: 0.5 }]) {
      expect(() => codec.encodeRequest!(value, context)).toThrow(CodecError);
    }
  });

  it("distinguishes Z, numeric zero offset and no suffix", () => {
    expect(parseDateTime("2026-01-01T00:00:00Z").kind).toBe("datetime-utc");
    expect(parseDateTime("2026-01-01T00:00:00+00:00").kind).toBe("datetime-local-wire");
    expect(parseDateTime("2026-01-01T00:00:00").kind).toBe("datetime-unspecified");
  });

  it("reads local range extremes but rejects sending instants outside STJ's reader range", () => {
    const codec = scalarCodec("datetime");
    for (const text of ["0001-01-01T00:00:00+14:00", "9999-12-31T23:59:59.9999999-14:00"]) {
      const value = codec.decodeResponse!({ kind: "string", value: text }, context);
      expect(value).toMatchObject({ kind: "datetime-local-wire" });
      expect(() => codec.encodeRequest!(value, context)).toThrow(CodecError);
      expect(() => codec.encodeKey!(value, context)).toThrow(CodecError);
    }
  });

  it("uses CLR DateTime key equality and refuses zone-dependent multi-key requests", () => {
    const codec = mapCodec<DateTime, string>({ id: "dates.codec", typeId: "dates", key: scalarCodec("datetime"), value: scalarCodec("string"), valueNullable: false, comparer: "structural" });
    const utc = parseDateTime("2026-01-01T00:00:00Z"), unspecified = parseDateTime("2026-01-01T00:00:00"), local = parseDateTime("2026-01-01T00:00:00+09:00");
    expect(() => codec.encodeRequest!(TisiliaMap.of([[utc, "a"], [unspecified, "b"]]), context)).toThrow(/collide/);
    expect(() => codec.encodeRequest!(TisiliaMap.of([[local, "a"], [utc, "b"]]), context)).toThrow(/server time zone/);
    const result = codec.decodeResponse!({ kind: "object", entries: [{ name: "0001-01-01T00:00:00+14:00", value: { kind: "string", value: "min" } }] }, context);
    expect(result.get(parseDateTime("0001-01-01T00:00:00"))).toBe("min");
  });
});
