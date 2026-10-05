import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { createContractRegistry, type ContractDocument } from "../src/contract/interpreter.js";
import { createCodecContext } from "../src/codec/abi.js";
import { parseJson } from "../src/json/parser.js";
import { writeJson } from "../src/json/writer.js";
import { formatDecimal, type Decimal } from "../src/primitives/decimal.js";
import { prepareRequest } from "../src/http/client.js";
import { guid } from "../src/primitives/text.js";
import { int64 } from "../src/primitives/integers.js";
import * as money from "../../../../tests/fixtures/modules/demo.money/money.js";

const contractPath = fileURLToPath(new URL("../../../../tests/fixtures/minimal-api.contract.json", import.meta.url));
const document = JSON.parse(readFileSync(contractPath, "utf8")) as ContractDocument;
const modules = new Map<string, Readonly<Record<string, unknown>>>([["demo.money", money as unknown as Readonly<Record<string, unknown>>]]);

describe("contract interpreter (the same registry/codecs as the generated client)", () => {
  it("decodes the example contract's response wire with precise int64, decimal scale and 100 ns ticks", () => {
    const { registry, operations } = createContractRegistry(document, { modules });
    const codec = registry.get<{ id: string; revision: bigint; nickname: string | null; balance: { amount: Decimal; currency: string }; createdAt: { ticks: bigint; offsetMinutes: number } }>("sample-api.Tisilia.Samples.MinimalApi.UserResponse.response.codec");
    const wire = parseJson('{"id":"550e8400-e29b-41d4-a716-446655440000","revision":9007199254740993,"nickname":null,"balance":{"amount":"123.4500","currency":"JPY"},"bonus":525,"createdAt":"2026-09-30T15:04:05.1234567+09:00"}');
    const value = codec.decodeResponse!(wire, createCodecContext({ profileId: "sample-api.profile.minimal" }));
    expect(value.revision).toBe(9007199254740993n);
    // the same CLR type under a member-level converter: its own codec (demo.MoneyCents) decodes hundredths
    expect(formatDecimal((value as unknown as { bonus: { amount: Decimal; currency: string } }).bonus.amount)).toBe("5.25");
    expect(value.nickname).toBeNull();
    expect(formatDecimal(value.balance.amount)).toBe("123.4500");
    expect(value.balance.currency).toBe("JPY");
    expect(value.createdAt.ticks).toBe(639263774451234567n); // day 739888 × 864e9 + 15:04:05.1234567
    expect(value.createdAt.offsetMinutes).toBe(540);
    expect([...operations.keys()]).toEqual(["users.get", "users.put", "portable.money", "portable.shape", "portable.invoice", "portable.tree", "users.labels", "users.tags", "mvc.money"]);
    expect(operations.get("users.get")?.responses.map((r) => r.caseId + ":" + r.hydration)).toEqual(["users.get.ok:browser-safe", "users.get.not-found:server-only"]);
  });

  it("encodes a request through the paired module and the same binders as the generated client", () => {
    const { registry, operations } = createContractRegistry(document, { modules });
    const put = operations.get("users.put")!;
    const args = { id: guid("550e8400-e29b-41d4-a716-446655440000"), body: { revision: int64(9007199254740993n), nickname: "neo", balance: { amount: money.moneyRequestInput.parseRequestInput("42.1", createCodecContext()).amount, currency: "JPY" } } };
    const prepared = prepareRequest(put, args, { baseUrl: "http://api.test" });
    expect(prepared.encodedPath).toBe("/users/550e8400-e29b-41d4-a716-446655440000");
    expect(prepared.bodyText).toBe('{"revision":9007199254740993,"nickname":"neo","balance":"42.1000"}');
    const request = registry.get("sample-api.Tisilia.Samples.MinimalApi.UserPutRequest.request.codec");
    expect(writeJson(request.encodeRequest!({ revision: int64(1n) }, createCodecContext()))).toBe('{"revision":1}');
  });

  it("rejects wires the generated codecs reject and resolves nullable, enum and map codecs", () => {
    const { registry } = createContractRegistry(document, { modules });
    const ctx = createCodecContext();
    expect(() => registry.get("std.int64.codec.r").decodeResponse!(parseJson("9223372036854775808"), ctx)).toThrow();
    expect(registry.get("std.string.codec.nullable").decodeResponse!(parseJson("null"), ctx)).toBeNull();
    expect(registry.get("sample-api.Tisilia.Samples.MinimalApi.Visibility.number.codec").decodeResponse!(parseJson("2"), ctx)).toBe(2);
    const map = registry.get<Map<string, bigint>>("sample-api.System.Collections.Generic.Dictionary_System.String_System.Int64_.response.codec").decodeResponse!(parseJson('{"seen":9007199254740993}'), ctx);
    expect(map.get("seen")).toBe(9007199254740993n);
    expect(registry.ids().length).toBe(document.codecs.length);
  });

  it("refuses module codecs whose module is not installed instead of guessing", () => {
    const { registry } = createContractRegistry(document);
    expect(() => registry.get("demo.Money.codec")).toThrow(/not installed/);
  });
});
