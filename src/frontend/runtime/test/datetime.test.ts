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
    expect(() => codec.encodeRequest!(TisiliaMap.of([[local, "a"], [utc, "b"]]), context)).toThrow(/ServerTimeZone/);
    const result = codec.decodeResponse!({ kind: "object", entries: [{ name: "0001-01-01T00:00:00+14:00", value: { kind: "string", value: "min" } }] }, context);
    expect(result.get(parseDateTime("0001-01-01T00:00:00"))).toBe("min");
  });
});

// a zone with the 2026 offsets of Europe/Berlin: +01:00, daylight time +02:00 from 2026-03-29T01:00Z to 2026-10-25T01:00Z
const ticksOf = (text: string): bigint => parseDateTime(text).ticks;
const berlin2026 = JSON.stringify({
  zone: "Europe/Berlin",
  until: ticksOf("2027-01-01T00:00:00Z").toString(),
  offsets: [[ticksOf("2026-01-01T00:00:00Z").toString(), 60], [ticksOf("2026-03-29T01:00:00Z").toString(), 120], [ticksOf("2026-10-25T01:00:00Z").toString(), 60]],
});

describe("DateTime dictionary keys as the server reads them", () => {
  const keys = (name: "datetime" | "datetime-local-wire", zone?: string) => mapCodec<DateTime, string>({
    id: "dates.codec", typeId: "dates", valueNullable: false, comparer: "structural",
    key: scalarCodec(name, zone === undefined ? {} : { context: { serverTimeZone: zone } }), value: scalarCodec("string"),
  });
  const send = (codec: ReturnType<typeof keys>, ...texts: string[]) => codec.encodeRequest!(TisiliaMap.of(texts.map((t, i) => [parseDateTime(t), String(i)] as const)), context);
  const code = (action: () => unknown): string | undefined => {
    try {
      action();
      return undefined;
    } catch (e) {
      return e instanceof CodecError ? e.code : "not-a-codec-error";
    }
  };

  it.each(["datetime", "datetime-local-wire"] as const)("refuses one instant written with two offsets, in every zone (%s)", (name) => {
    for (const zone of [undefined, berlin2026]) {
      expect(code(() => send(keys(name, zone), "2026-06-01T09:00:00+09:00", "2026-06-01T02:00:00+02:00"))).toBe("key-collision");
    }
  });

  it.each(["datetime", "datetime-local-wire"] as const)("without the server's zone sends keys no zone can make one, and refuses the others (%s)", (name) => {
    const codec = keys(name);
    // offsets are within ±14 hours: instants more than 28 hours apart stay apart
    expect(code(() => send(codec, "2026-06-01T00:00:00+00:00", "2026-06-02T04:00:00.0000001+00:00"))).toBeUndefined();
    expect(code(() => send(codec, "2026-06-01T00:00:00+00:00", "2026-06-02T04:00:00+00:00"))).toBe("unsupported");
    expect(() => send(codec, "2026-06-01T00:00:00+00:00", "2026-06-01T01:00:00+00:00")).toThrow(/TisiliaOptions\.DateTimes\.ServerTimeZone/);
    // a single key is always sent
    expect(code(() => send(codec, "2026-10-25T02:30:00+02:00"))).toBeUndefined();
  });

  it("without the server's zone keeps a key with an offset more than 14 hours from the keys without one", () => {
    const codec = keys("datetime");
    expect(code(() => send(codec, "2026-06-01T00:00:00+00:00", "2026-06-01T14:00:00.0000001Z", "2026-05-31T09:59:59.9999999"))).toBeUndefined();
    expect(code(() => send(codec, "2026-06-01T00:00:00+00:00", "2026-06-01T14:00:00Z"))).toBe("unsupported");
  });

  it.each(["datetime", "datetime-local-wire"] as const)("with the server's zone refuses exactly the keys it reads as one (%s)", (name) => {
    const codec = keys(name, berlin2026);
    // the hour that repeats when daylight time ends: 00:30Z and 01:30Z are both 02:30 in Berlin
    expect(() => send(codec, "2026-10-25T02:30:00+02:00", "2026-10-25T02:30:00+01:00")).toThrow(/'Europe\/Berlin'/);
    expect(code(() => send(codec, "2026-10-25T02:30:00+02:00", "2026-10-25T02:30:00+01:00"))).toBe("key-collision");
    // 23:30Z and 00:30Z are 01:30 and 02:30 in Berlin; keys an hour apart elsewhere in the year stay apart too
    expect(code(() => send(codec, "2026-10-25T01:30:00+02:00", "2026-10-25T01:30:00+01:00", "2026-06-01T10:00:00+02:00", "2026-06-01T11:00:00+02:00"))).toBeUndefined();
    // a key read in Berlin's time is equal to a key without an offset that has the same ticks
    if (name === "datetime") {
      expect(code(() => send(codec, "2026-07-01T09:00:00+00:00", "2026-07-01T11:00:00"))).toBe("key-collision");
      expect(code(() => send(codec, "2026-07-01T09:00:00+00:00", "2026-07-01T10:00:00", "2026-07-01T12:00:00Z"))).toBeUndefined();
    }
  });

  it("with the server's zone falls back to the ±14 hour rule outside the instants the zone covers", () => {
    const codec = keys("datetime-local-wire", berlin2026);
    expect(code(() => send(codec, "2025-06-01T00:00:00+00:00", "2025-06-03T00:00:00+00:00", "2026-06-01T00:00:00+00:00"))).toBeUndefined();
    expect(() => send(codec, "2025-06-01T00:00:00+00:00", "2025-06-01T12:00:00+00:00")).toThrow(/outside 2026-01-01T00:00:00Z to 2027-01-01T00:00:00Z/);
  });

  it("decodes keys the server wrote by the ticks written, also a local time that does not exist there", () => {
    // a Local DateTime of 02:00 on the day daylight time starts does not exist in Berlin; the server writes it with +01:00, which reads
    // back as 03:00, but in the server's dictionary it is not the key 03:00
    for (const name of ["datetime", "datetime-local-wire"] as const) {
      const map = keys(name, berlin2026).decodeResponse!({ kind: "object", entries: [
        { name: "2026-03-29T02:00:00+01:00", value: { kind: "string", value: "a" } },
        { name: "2026-03-29T03:00:00+02:00", value: { kind: "string", value: "b" } },
      ] }, context);
      expect(map.size).toBe(2);
      // sent back, the two are one key on the server
      expect(code(() => keys(name, berlin2026).encodeRequest!(map, context))).toBe("key-collision");
    }
  });

  it("refuses keys from editor input with the key codec's reason", () => {
    const codec = keys("datetime", berlin2026);
    const input = { kind: "object" as const, entries: ["2026-10-25T02:30:00+02:00", "2026-10-25T02:30:00+01:00"].map((name) => ({ name, value: { kind: "string" as const, value: name } })) };
    expect(code(() => codec.parseRequestInput!(input, context))).toBe("key-collision");
  });

  it("refuses keys when the contract's zone is malformed", () => {
    for (const zone of ["{", JSON.stringify({ zone: "x", until: "1", offsets: [["2", 0]] }), JSON.stringify({ zone: "x", until: "9", offsets: [["1", 841]] }), JSON.stringify({ zone: "x", until: "9", offsets: [["2", 0], ["2", 60]] })]) {
      expect(() => send(keys("datetime-local-wire", zone), "2026-06-01T00:00:00+00:00", "2026-06-05T00:00:00+00:00")).toThrow(/malformed/);
    }
  });
});
