import { describe, expect, it } from "vitest";
import * as portable from "../../../../tests/fixtures/modules/demo.portable/demo.portable.portable.js";
import { createCodecContext } from "../src/codec/abi.js";
import { scalarCodec, type ScalarName } from "../src/codec/scalars.js";
import type { JsonValue } from "../src/json/ast.js";
import { decimal } from "../src/primitives/decimal.js";
import { guid } from "../src/primitives/text.js";

/**
 * The self-contained portable module carries its own copy of the scalar primitives (PortableTsPrelude.cs, ported from
 * src/frontend/runtime/src/primitives). These tests pin both sides to the same canonical forms and the same rejections over
 * the frozen sample Invoice fixture (tests/fixtures/modules/demo.portable), which uses every scalar family.
 */
const ctx = createCodecContext();
const context = { path: "" };

const invoice: portable.Invoice = {
  id: guid("550e8400-e29b-41d4-a716-446655440000"),
  customer: { name: "Ada", email: null, since: { kind: "date-only", year: 2026, month: 9, day: 30 } },
  total: { amount: decimal(1, 125n, 1), currency: "JPY" },
  issued: { kind: "datetime-offset", ticks: 639263774451234567n, offsetMinutes: 540 },
  due: { kind: "date-only", year: 9999, month: 12, day: 31 },
  at: { kind: "time-only", ticks: 863999999999n },
  stamp: { kind: "datetime-utc", ticks: 0n },
  local: { kind: "datetime-unspecified", ticks: 3155378975999999999n },
  localWire: { kind: "datetime-local-wire", ticks: 639263774451234567n, offsetMinutes: 540 },
  period: { kind: "duration", ticks: -9223372036854775808n },
  ratio: 0.1,
  weight: Math.fround(3.4028235e38),
  payload: new Uint8Array([0, 1, 2, 255]),
  initial: "A",
  shape: { kind: "rect", width: -2147483648, height: 2147483647, tags: ["a", "b"] },
  payment: { method: "cash" },
  lines: [{ sku: "x", qty: 9223372036854775807n, price: decimal(-1, 79228162514264337593543950335n, 28) }],
};

function entry(wire: JsonValue, name: string): JsonValue {
  if (wire.kind !== "object") {
    throw new Error("object expected");
  }
  const found = wire.entries.find((e) => e.name === name);
  if (found === undefined) {
    throw new Error(`member ${name} missing`);
  }
  return found.value;
}

function withMember(wire: JsonValue, name: string, value: JsonValue): JsonValue {
  if (wire.kind !== "object") {
    throw new Error("object expected");
  }
  return { kind: "object", entries: wire.entries.map((e) => (e.name === name ? { name, value } : e)) };
}

const scalarMembers: readonly [member: keyof portable.Invoice & string, scalar: ScalarName][] = [
  ["id", "guid"],
  ["issued", "datetime-offset"],
  ["due", "date-only"],
  ["at", "time-only"],
  ["stamp", "datetime-utc"],
  ["local", "datetime-unspecified"],
  ["localWire", "datetime-local-wire"],
  ["period", "duration"],
  ["weight", "float32"],
  ["payload", "bytes"],
  ["initial", "char"],
];

describe("portable prelude ⇄ runtime primitives", () => {
  const wire = portable.invoiceRequestEncode.encodeRequest(invoice, context) as JsonValue;

  it("writes every scalar exactly as the runtime's canonical writers do", () => {
    for (const [member, scalar] of scalarMembers) {
      expect(entry(wire, member), member).toEqual(scalarCodec(scalar).encodeRequest!(invoice[member], ctx));
    }
    // float64 with the string representation carries the runtime's shortest round-trip lexeme
    const ratio = scalarCodec("float64").encodeRequest!(invoice.ratio, ctx);
    expect(entry(wire, "ratio")).toEqual({ kind: "string", value: ratio.kind === "number" ? ratio.text : "" });
    expect(entry(wire, "due")).toEqual({ kind: "string", value: "9999-12-31" });
    expect(entry(wire, "at")).toEqual({ kind: "string", value: "23:59:59.9999999" });
    expect(entry(wire, "stamp")).toEqual({ kind: "string", value: "0001-01-01T00:00:00Z" });
    expect(entry(wire, "period")).toEqual({ kind: "string", value: "-10675199.02:48:05.4775808" });
    expect(entry(wire, "payload")).toEqual({ kind: "string", value: "AAEC/w==" });
    expect(entry(entry(wire, "shape"), "kind")).toEqual({ kind: "string", value: "rect" });
  });

  it("round-trips the whole invoice through the module's decoder", () => {
    const decoded = portable.invoiceResponseDecode.decodeResponse(wire, context);
    expect(decoded).toEqual(invoice);
  });

  it("decodes every scalar wire to the same domain value as the runtime", () => {
    for (const [member, scalar] of scalarMembers) {
      const decoded = portable.invoiceResponseDecode.decodeResponse(wire, context) as unknown as Record<string, unknown>;
      expect(decoded[member], member).toEqual(scalarCodec(scalar).decodeResponse!(entry(wire, member), ctx));
    }
  });

  it("rejects the same invalid lexemes with the same codes as the runtime", () => {
    const cases: readonly [member: string, scalar: ScalarName, lexeme: string][] = [
      ["due", "date-only", "2024-02-30"],
      ["due", "date-only", "2024-2-1"],
      ["at", "time-only", "12:00"],
      ["at", "time-only", "25:00:00"],
      ["issued", "datetime-offset", "2026-09-30"],
      ["issued", "datetime-offset", "2026-09-30T15:04:05.12345678+09:00"],
      ["stamp", "datetime-utc", "2026-09-30T15:04:05+09:00"],
      ["local", "datetime-unspecified", "2026-09-30T15:04:05Z"],
      ["localWire", "datetime-local-wire", "2026-09-30T15:04:05Z"],
      ["localWire", "datetime-local-wire", "2026-09-30T15:04:05"],
      ["period", "duration", "00:60:00"],
      ["period", "duration", "1 day"],
      ["payload", "bytes", "abc"],
      ["payload", "bytes", "/wB="],
      ["initial", "char", "ab"],
      ["id", "guid", "550e8400e29b41d4a716446655440000"],
    ];
    for (const [member, scalar, lexeme] of cases) {
      const bad: JsonValue = { kind: "string", value: lexeme };
      let runtimeCode = "";
      try {
        scalarCodec(scalar).decodeResponse!(bad, ctx);
      } catch (e) {
        runtimeCode = (e as { code: string }).code;
      }
      let portableCode = "";
      try {
        portable.invoiceResponseDecode.decodeResponse(withMember(wire, member, bad), context);
      } catch (e) {
        portableCode = (e as { code: string }).code;
      }
      expect(runtimeCode, `${scalar} ${lexeme}`).not.toBe("");
      expect(portableCode, `${scalar} ${lexeme}`).toBe(runtimeCode);
    }
    // float32 beyond binary32 is a range error on both sides (the C# reader rejects it too, unlike Utf8JsonReader.GetSingle)
    expect(() => scalarCodec("float32").decodeResponse!({ kind: "number", text: "1e39" }, ctx)).toThrow(/overflow/);
    expect(() => portable.invoiceResponseDecode.decodeResponse(withMember(wire, "weight", { kind: "number", text: "1e39" }), context)).toThrow(/overflow/);
  });

  it("validates domain values with the runtime's rules", () => {
    expect(() => portable.invoiceValidate.validateDomain({ ...invoice, weight: 0.1 }, context)).toThrow(/float32/);
    expect(() => portable.invoiceValidate.validateDomain({ ...invoice, ratio: Number.POSITIVE_INFINITY }, context)).toThrow(/finite/);
    expect(() => portable.invoiceValidate.validateDomain({ ...invoice, at: { kind: "time-only", ticks: 864000000000n } }, context)).toThrow(/within one day/);
    expect(() => portable.invoiceValidate.validateDomain({ ...invoice, initial: "\ud800" }, context)).toThrow(/surrogate/);
    expect(() => portable.invoiceValidate.validateDomain({ ...invoice, shape: { kind: "triangle" } }, context)).toThrow(/discriminator/);
    expect(portable.invoiceValidate.validateDomain(invoice, context)).toEqual(invoice);
  });

  it("checks strings without String.prototype.isWellFormed (ES2024; the module runs in ES2022 browsers)", () => {
    const native = Object.getOwnPropertyDescriptor(String.prototype, "isWellFormed");
    Reflect.deleteProperty(String.prototype, "isWellFormed");
    try {
      expect((String.prototype as { isWellFormed?: unknown }).isWellFormed).toBeUndefined();
      const named = (name: string) => ({ ...invoice, customer: { ...invoice.customer, name } });
      for (const lone of ["\ud800", "a\udc00", "\ud800\ud800", "x\udbff"]) {
        expect(() => portable.invoiceValidate.validateDomain(named(lone), context), JSON.stringify(lone)).toThrow(/well-formed string/);
      }
      expect(portable.invoiceValidate.validateDomain(named("Ada 😀 東京"), context)).toEqual(named("Ada 😀 東京"));
    } finally {
      Object.defineProperty(String.prototype, "isWellFormed", native!);
    }
  });
});
