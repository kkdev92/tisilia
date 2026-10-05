import { describe, expect, it } from "vitest";
import { createCodecContext } from "../src/codec/abi.js";
import { CodecError } from "../src/codec/errors.js";
import { TisiliaMap } from "../src/codec/map.js";
import { scalarCodec, webNumbers } from "../src/codec/scalars.js";
import { arrayCodec, brandCodec, enumCodec, mapCodec, nullableCodec, objectCodec, taggedUnionCodec } from "../src/codec/structural.js";
import { parseJson } from "../src/json/parser.js";
import { writeJson } from "../src/json/writer.js";
import { decimalFromString } from "../src/primitives/decimal.js";

const ctx = createCodecContext({ profileId: "test" });

describe("scalar codecs", () => {
  it("int64 never passes through Number", () => {
    const c = scalarCodec<bigint>("int64", { numbers: webNumbers });
    expect(c.decodeResponse!(parseJson("9007199254740993"), ctx)).toBe(9007199254740993n);
    expect(writeJson(c.encodeRequest!(9007199254740993n, ctx))).toBe("9007199254740993");
    expect(() => c.encodeRequest!(1 as unknown as bigint, ctx)).toThrow(CodecError);
    expect(() => c.decodeResponse!(parseJson('"1"'), ctx)).toThrow(CodecError); // WriteAsString is not part of the web profile
    expect(c.decodeKey!("012", ctx)).toBe(12n);
  });

  it("decimal keeps scale and emits fixed-point", () => {
    const c = scalarCodec("decimal");
    const v = c.decodeResponse!(parseJson("123.4500"), ctx);
    expect(v).toEqual({ kind: "decimal", sign: 1, coefficient: 1234500n, scale: 4 });
    expect(writeJson(c.encodeRequest!(v, ctx))).toBe("123.4500");
    expect(writeJson(c.encodeRequest!(decimalFromString("-0.5"), ctx))).toBe("-0.5");
    expect(c.decodeKey!("1.50", ctx)).toEqual({ kind: "decimal", sign: 1, coefficient: 150n, scale: 2 });
  });

  it("float profiles: named literals only when enabled", () => {
    const strict = scalarCodec<number>("float64");
    const named = scalarCodec<number>("float64", { numbers: { readFromString: false, writeAsString: false, namedLiterals: true } });
    expect(() => strict.decodeResponse!(parseJson('"NaN"'), ctx)).toThrow(CodecError);
    expect(Number.isNaN(named.decodeResponse!(parseJson('"NaN"'), ctx))).toBe(true);
    expect(writeJson(named.encodeRequest!(Number.POSITIVE_INFINITY, ctx))).toBe('"Infinity"');
    expect(() => strict.encodeRequest!(Number.POSITIVE_INFINITY, ctx)).toThrow(CodecError);
    expect(writeJson(strict.encodeRequest!(-0, ctx))).toBe("-0");
    expect(Object.is(strict.decodeResponse!(parseJson("-0.0"), ctx), -0)).toBe(true);
  });

  it("string rejects lone surrogates and enforces UTF-16 length", () => {
    const c = scalarCodec<string>("string", { maxUtf16Length: 3 });
    expect(c.decodeResponse!(parseJson('"😀"'), ctx)).toBe("😀");
    expect(() => c.decodeResponse!(parseJson('"\\ud83d"'), ctx)).toThrow(/surrogate/);
    expect(() => c.decodeResponse!(parseJson('"abcd"'), ctx)).toThrow(/longer/);
  });

  it("boolean keys use True/False like System.Text.Json", () => {
    const c = scalarCodec<boolean>("boolean");
    expect(c.encodeKey!(true, ctx)).toBe("True");
    expect(c.decodeKey!("False", ctx)).toBe(false);
  });
});

describe("object codec", () => {
  const int64 = scalarCodec<bigint>("int64", { numbers: webNumbers });
  const str = scalarCodec<string>("string");
  const user = objectCodec<{ revision: bigint; nickname?: string | null }>({
    id: "demo.User.codec",
    typeId: "demo.User",
    properties: [
      { name: "revision", codec: int64, presence: "required", nullable: false },
      { name: "nickname", codec: str, presence: "optional", nullable: true },
    ],
    nameMatching: "ordinal-ignore-case",
    duplicates: "last-wins",
    readAdditional: "ignore",
    writeAdditional: "ignore",
    request: true,
    response: true,
  });

  it("distinguishes missing, null and undefined", () => {
    expect(user.decodeResponse!(parseJson('{"revision":1}'), ctx)).toEqual({ revision: 1n });
    expect(Object.prototype.hasOwnProperty.call(user.decodeResponse!(parseJson('{"revision":1}'), ctx), "nickname")).toBe(false);
    expect(user.decodeResponse!(parseJson('{"revision":1,"nickname":null}'), ctx)).toEqual({ revision: 1n, nickname: null });
    expect(() => user.encodeRequest!({ revision: 1n, nickname: undefined } as never, ctx)).toThrow(/undefined/);
    expect(() => user.decodeResponse!(parseJson('{"nickname":"x"}'), ctx)).toThrow(/required/);
    expect(writeJson(user.encodeRequest!({ revision: 9007199254740993n }, ctx))).toBe('{"revision":9007199254740993}');
    expect(writeJson(user.encodeRequest!({ revision: 1n, nickname: null }, ctx))).toBe('{"revision":1,"nickname":null}');
  });

  it("applies name matching, duplicate and unknown-member policies", () => {
    expect(user.decodeResponse!(parseJson('{"Revision":1,"REVISION":2}'), ctx)).toEqual({ revision: 2n });
    expect(user.decodeResponse!(parseJson('{"revision":1,"zzz":true}'), ctx)).toEqual({ revision: 1n });
    const strict = objectCodec<{ revision: bigint }>({
      id: "demo.Strict.codec",
      typeId: "demo.Strict",
      properties: [{ name: "revision", codec: int64, presence: "required", nullable: false }],
      nameMatching: "ordinal",
      duplicates: "reject",
      readAdditional: "reject",
      writeAdditional: "reject",
      request: true,
      response: true,
    });
    expect(() => strict.decodeResponse!(parseJson('{"revision":1,"revision":2}'), ctx)).toThrow(/duplicate/);
    expect(() => strict.decodeResponse!(parseJson('{"Revision":1}'), ctx)).toThrow(/unexpected/);
    expect(() => strict.decodeResponse!(parseJson('{"revision":1,"zzz":true}'), ctx)).toThrow(/unexpected/);
  });

  it("never creates prototype-bearing keys", () => {
    const out = user.decodeResponse!(parseJson('{"revision":1,"__proto__":{"polluted":true},"constructor":1}'), ctx) as Record<string, unknown>;
    expect(Object.getPrototypeOf(out)).toBeNull();
    expect((({}) as Record<string, unknown>)["polluted"]).toBeUndefined();
  });

  it("captures extension data with a value codec and reports the failing path", () => {
    const jsonValue = scalarCodec("json-value");
    const problem = objectCodec<{ title?: string; extensions?: Map<string, unknown> }>({
      id: "demo.Problem.codec",
      typeId: "demo.Problem",
      properties: [{ name: "title", codec: str, presence: "optional", nullable: false }],
      nameMatching: "ordinal",
      duplicates: "reject",
      readAdditional: "capture",
      writeAdditional: "capture",
      extension: jsonValue,
      extensionProperty: "extensions",
      request: false,
      response: true,
    });
    const decoded = problem.decodeResponse!(parseJson('{"title":"x","traceId":"abc","n":9007199254740993.0000}'), ctx);
    expect(decoded.extensions?.get("n")).toEqual({ kind: "number", text: "9007199254740993.0000" });
    expect(problem.encodeRequest).toBeUndefined();
    try {
      user.decodeResponse!(parseJson('{"revision":"x"}'), ctx);
      throw new Error("unreachable");
    } catch (e) {
      expect(e).toBeInstanceOf(CodecError);
      expect((e as CodecError).path).toBe("/revision");
    }
  });
});

describe("collections", () => {
  it("arrays keep order, reject holes and honour element nullability", () => {
    const arr = arrayCodec<bigint>({ id: "a", typeId: "a", element: scalarCodec("int64"), elementNullable: false });
    expect(arr.decodeResponse!(parseJson("[1,2,3]"), ctx)).toEqual([1n, 2n, 3n]);
    expect(() => arr.decodeResponse!(parseJson("[1,null]"), ctx)).toThrow(/null/);
    expect(() => arr.encodeRequest!([1n, , 3n] as never, ctx)).toThrow(/holes/);
    const nullable = arrayCodec<bigint>({ id: "b", typeId: "b", element: scalarCodec("int64"), elementNullable: true });
    expect(writeJson(nullable.encodeRequest!([1n, null], ctx))).toBe("[1,null]");
  });

  it("maps use the key codec and comparer, rejecting collisions after encoding", () => {
    const map = mapCodec<string, bigint>({ id: "m", typeId: "m", key: scalarCodec("string"), value: scalarCodec("int64"), valueNullable: false, comparer: "ordinal-ignore-case" });
    const decoded = map.decodeResponse!(parseJson('{"a":1,"B":2}'), ctx);
    expect(decoded.get("A")).toBe(1n);
    expect(decoded.size).toBe(2);
    expect(() => map.decodeResponse!(parseJson('{"a":1,"A":2}'), ctx)).toThrow(/duplicate/);
    const value = TisiliaMap.from<string, bigint>("ordinal-ignore-case", (k) => k, [["x", 1n]]);
    expect(writeJson(map.encodeRequest!(value, ctx))).toBe('{"x":1}');
    const intKeys = mapCodec<number, string>({ id: "m2", typeId: "m2", key: scalarCodec("int32"), value: scalarCodec("string"), valueNullable: false, comparer: "structural" });
    expect([...intKeys.decodeResponse!(parseJson('{"01":"a"}'), ctx).entries()]).toEqual([[1, "a"]]);
  });

  it("maps take Explorer input like a response: keys through the key grammar, values through the value codec's input", () => {
    const map = mapCodec<string, bigint>({ id: "m", typeId: "m", key: scalarCodec("string"), value: scalarCodec("int64"), valueNullable: false, comparer: "ordinal-ignore-case" });
    const input = map.parseRequestInput!(parseJson('{"a":"9007199254740993","B":2}'), ctx);
    expect(input.get("A")).toBe(9007199254740993n);
    expect(writeJson(map.encodeRequest!(input, ctx))).toBe('{"a":9007199254740993,"B":2}');
    expect(() => map.parseRequestInput!(parseJson('{"a":1,"A":2}'), ctx)).toThrow(/duplicate/);
    expect(() => map.parseRequestInput!(parseJson('{"a":null}'), ctx)).toThrow(/null/);
    expect(() => map.parseRequestInput!(parseJson("[1]"), ctx)).toThrow(/object required/);
    expect(() => map.parseRequestInput!("{}", ctx)).toThrow(/JSON value editor/);
    const nullableValues = mapCodec<number, string>({ id: "m3", typeId: "m3", key: scalarCodec("int32"), value: scalarCodec("string"), valueNullable: true, comparer: "structural" });
    expect([...nullableValues.parseRequestInput!(parseJson('{"01":null,"2":"b"}'), ctx).entries()]).toEqual([[1, null], [2, "b"]]);
    expect(() => nullableValues.parseRequestInput!(parseJson('{"x":"a"}'), ctx)).toThrow(CodecError);
  });

  it("nullable wrapper passes null through without calling the inner codec", () => {
    const c = nullableCodec(scalarCodec<bigint>("int64"));
    expect(c.decodeResponse!(parseJson("null"), ctx)).toBeNull();
    expect(writeJson(c.encodeRequest!(null, ctx))).toBe("null");
    expect(c.decodeResponse!(parseJson("5"), ctx)).toBe(5n);
  });
});

describe("enum codec", () => {
  const color = enumCodec({
    id: "e",
    typeId: "e",
    underlying: "int32",
    flags: false,
    allowUndefinedInteger: true,
    stringForm: true,
    members: [
      { name: "Red", value: 1n },
      { name: "Green", value: 2n },
    ],
  });
  it("follows JsonStringEnumConverter behaviour observed on .NET 10", () => {
    expect(color.decodeResponse!(parseJson('"Red"'), ctx)).toBe(1);
    expect(color.decodeResponse!(parseJson('"red"'), ctx)).toBe(1);
    expect(color.decodeResponse!(parseJson('"999"'), ctx)).toBe(999);
    expect(color.decodeResponse!(parseJson("2"), ctx)).toBe(2);
    expect(writeJson(color.encodeRequest!(1, ctx))).toBe('"Red"');
    expect(writeJson(color.encodeRequest!(999, ctx))).toBe("999");
    expect(color.encodeKey!(1, ctx)).toBe("Red");
  });
  it("flags compose and undefined bits fall back to numbers", () => {
    const perm = enumCodec({
      id: "p",
      typeId: "p",
      underlying: "int32",
      flags: true,
      allowUndefinedInteger: true,
      stringForm: true,
      members: [
        { name: "None", value: 0n },
        { name: "Read", value: 1n },
        { name: "Write", value: 2n },
      ],
    });
    expect(writeJson(perm.encodeRequest!(3, ctx))).toBe('"Read, Write"');
    expect(perm.decodeResponse!(parseJson('"Read,Write"'), ctx)).toBe(3);
    expect(writeJson(perm.encodeRequest!(9, ctx))).toBe("9");
    expect(writeJson(perm.encodeRequest!(0, ctx))).toBe('"None"');
  });
  it("numeric enums keep undefined integers and 64-bit bases as bigint", () => {
    const big = enumCodec({ id: "b", typeId: "b", underlying: "uint64", flags: false, allowUndefinedInteger: true, stringForm: false, members: [{ name: "Max", value: 18446744073709551615n }] });
    expect(big.decodeResponse!(parseJson("18446744073709551615"), ctx)).toBe(18446744073709551615n);
    expect(() => big.decodeResponse!(parseJson('"Max"'), ctx)).toThrow(CodecError);
    const closed = enumCodec({ id: "c", typeId: "c", underlying: "int32", flags: false, allowUndefinedInteger: false, stringForm: false, members: [{ name: "A", value: 1n }] });
    expect(() => closed.decodeResponse!(parseJson("2"), ctx)).toThrow(/undefined/);
  });
});

describe("tagged union", () => {
  it("dispatches on the discriminator literal only", () => {
    const circle = objectCodec<{ $type: "circle"; r: number }>({
      id: "circle",
      typeId: "circle",
      properties: [
        { name: "$type", codec: scalarCodec("string"), presence: "required", nullable: false },
        { name: "r", codec: scalarCodec("float64"), presence: "required", nullable: false },
      ],
      nameMatching: "ordinal",
      duplicates: "reject",
      readAdditional: "ignore",
      writeAdditional: "ignore",
      request: true,
      response: true,
    });
    const shape = taggedUnionCodec<{ $type: "circle"; r: number }>({ id: "shape", typeId: "shape", discriminator: "$type", tagProperty: "$type", variants: [{ tag: "circle", codec: circle }] });
    expect(shape.decodeResponse!(parseJson('{"r":1,"$type":"circle"}'), ctx)).toEqual({ $type: "circle", r: 1 });
    expect(() => shape.decodeResponse!(parseJson('{"$type":"nope","r":1}'), ctx)).toThrow(/discriminator/);
    expect(() => shape.decodeResponse!(parseJson('{"r":1}'), ctx)).toThrow(/discriminator/);
    expect(writeJson(shape.encodeRequest!({ $type: "circle", r: 1.5 }, ctx))).toBe('{"$type":"circle","r":1.5}');
    // Explorer input: the discriminator picks the variant, which reads the whole object
    const input = shape.parseRequestInput!(parseJson('{"r":2.5,"$type":"circle"}'), ctx);
    expect(input).toEqual({ $type: "circle", r: 2.5 });
    expect(writeJson(shape.encodeRequest!(input, ctx))).toBe('{"$type":"circle","r":2.5}');
    expect(() => shape.parseRequestInput!(parseJson('{"$type":"nope","r":1}'), ctx)).toThrow(/discriminator/);
    expect(() => shape.parseRequestInput!(parseJson('{"r":1}'), ctx)).toThrow(/discriminator/);
    expect(() => shape.parseRequestInput!(parseJson('"circle"'), ctx)).toThrow(/object required/);
  });
});

describe("JSON null as a value (lossless json-value)", () => {
  const json = scalarCodec("json-value");
  const holder = objectCodec<{ element: unknown; maybe?: unknown; text: string }>({
    id: "demo.Holder.codec",
    typeId: "demo.Holder",
    properties: [
      { name: "element", codec: json, presence: "required", nullable: false },
      { name: "maybe", codec: nullableCodec(json), presence: "optional", nullable: true },
      { name: "text", codec: scalarCodec("string"), presence: "required", nullable: false },
    ],
    nameMatching: "ordinal",
    duplicates: "last-wins",
    readAdditional: "ignore",
    writeAdditional: "ignore",
    request: true,
    response: true,
  });

  it("decodes null of a non-nullable json-value member as the JSON value null, and of a nullable one as semantic null", () => {
    expect(holder.decodeResponse!(parseJson('{"element":null,"maybe":null,"text":"t"}'), ctx)).toEqual({ element: { kind: "null" }, maybe: null, text: "t" });
    expect(writeJson(holder.encodeRequest!({ element: { kind: "null" }, text: "t" }, ctx))).toBe('{"element":null,"text":"t"}');
    // any other non-nullable type still rejects the token
    expect(() => holder.decodeResponse!(parseJson('{"element":1,"text":null}'), ctx)).toThrow(/text/);
  });

  it("keeps JSON null items and values of json-value arrays and maps", () => {
    const items = arrayCodec<unknown>({ id: "demo.Items.codec", typeId: "demo.Items", element: json, elementNullable: false });
    expect(items.decodeResponse!(parseJson("[null,1]"), ctx)).toEqual([{ kind: "null" }, { kind: "number", text: "1" }]);
    const values = mapCodec<string, unknown>({ id: "demo.Values.codec", typeId: "demo.Values", key: scalarCodec("string"), value: json, valueNullable: false, comparer: "ordinal" });
    expect(values.decodeResponse!(parseJson('{"a":null}'), ctx).get("a")).toEqual({ kind: "null" });
  });
});

describe("request maps", () => {
  const byName = mapCodec<string, number>({ id: "demo.ByName.codec", typeId: "demo.ByName", key: scalarCodec("string"), value: scalarCodec("int32", { numbers: webNumbers }), valueNullable: false, comparer: "ordinal-ignore-case" });

  it("accepts TisiliaMap.of and a plain Map, and still rejects keys that collide under the contract's comparer", () => {
    expect(writeJson(byName.encodeRequest!(TisiliaMap.of([["a", 1], ["b", 2]]), ctx))).toBe('{"a":1,"b":2}');
    expect(writeJson(byName.encodeRequest!(new Map([["x", 3]]) as never, ctx))).toBe('{"x":3}');
    // "A" and "a" are distinct JavaScript keys but one key under OrdinalIgnoreCase
    expect(() => byName.encodeRequest!(TisiliaMap.of([["A", 1], ["a", 2]]), ctx)).toThrow(/collide/);
    expect(() => byName.encodeRequest!(new Map([["A", 1], ["a", 2]]) as never, ctx)).toThrow(/collide/);
  });

  it("keys TisiliaMap.of structurally", () => {
    const dates = TisiliaMap.of<{ kind: string; year: number }, number>([[{ kind: "date-only", year: 2026 }, 1]]);
    expect(dates.get({ kind: "date-only", year: 2026 })).toBe(1);
    expect(TisiliaMap.of<bigint, string>([[9007199254740993n, "x"]]).get(9007199254740993n)).toBe("x");
    // JSON.stringify writes NaN, Infinity and -Infinity all as null: they must stay three keys; -0 and 0 are one, as in .NET
    const doubles = TisiliaMap.of<number, string>([[Number.NaN, "nan"], [Number.POSITIVE_INFINITY, "inf"], [Number.NEGATIVE_INFINITY, "-inf"], [-0, "zero"], [0, "zero again"], [1, "one"]]);
    expect([...doubles.values()]).toEqual(["nan", "inf", "-inf", "zero again", "one"]);
    expect(doubles.get(Number.NaN)).toBe("nan");
    expect(TisiliaMap.of<string | number, string>([["1", "text"], [1, "number"]]).size).toBe(2);
  });
});

describe("codec construction and boundaries", () => {
  it("string length limits apply when a root body is encoded, not only through validateDomain", () => {
    const c = scalarCodec<string>("string", { minUtf16Length: 2, maxUtf16Length: 3 });
    expect(writeJson(c.encodeRequest!("ab", ctx))).toBe('"ab"');
    expect(() => c.encodeRequest!("abcd", ctx)).toThrow(/longer/);
    expect(() => c.encodeRequest!("a", ctx)).toThrow(/shorter/);
  });

  it("brandCodec reads its base when a capability is used, so a brand may be built before its base exists", () => {
    let base: ReturnType<typeof scalarCodec<string>> | undefined;
    // generated code: `export const emailCodec = brandCodec(…, () => stringCodec)` may run while `stringCodec` is still in its TDZ
    const brand = brandCodec<string>("demo.Email.codec", "demo.Email", () => {
      if (base === undefined) {
        throw new ReferenceError("Cannot access 'stringCodec' before initialization");
      }
      return base;
    });
    base = scalarCodec<string>("string");
    expect(writeJson(brand.encodeRequest!("a@b", ctx))).toBe('"a@b"');
    expect(brand.decodeResponse!(parseJson('"x"'), ctx)).toBe("x");
    expect(brand.encodeKey!("k", ctx)).toBe("k");
    // a capability the base lacks reads as absent
    const bytes = brandCodec<Uint8Array>("demo.Blob.codec", "demo.Blob", () => scalarCodec("bytes") as never);
    expect(bytes.encodeKey).toBeUndefined();
    expect(bytes.decodeResponse).toBeDefined();
  });
});
