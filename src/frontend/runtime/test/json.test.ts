import { describe, expect, it } from "vitest";
import { fromNative, jsonEquals } from "../src/json/ast.js";
import { canonicalize } from "../src/json/jcs.js";
import { JsonParseError, parseJson, parseJsonBytes } from "../src/json/parser.js";
import { writeJson } from "../src/json/writer.js";

describe("lossless parser", () => {
  it("keeps number lexemes and duplicate entries", () => {
    const v = parseJson('{"a":9007199254740993,"a":1.2300e+2,"b":[-0,0.10]}');
    expect(v).toEqual({
      kind: "object",
      entries: [
        { name: "a", value: { kind: "number", text: "9007199254740993" } },
        { name: "a", value: { kind: "number", text: "1.2300e+2" } },
        { name: "b", value: { kind: "array", items: [{ kind: "number", text: "-0" }, { kind: "number", text: "0.10" }] } },
      ],
    });
  });

  it("rejects RFC 8259 grammar violations", () => {
    for (const bad of ["01", "1.", ".5", "1e", "-", "[1,]", "{\"a\":1,}", "{a:1}", "'x'", "tru", "\"\u0001\"", "\"\\x\"", "[1] 2", "", "  ", "{\"a\":1}}", "\"\\ud83d"]) {
      expect(() => parseJson(bad), JSON.stringify(bad)).toThrow(JsonParseError);
    }
  });

  it("keeps escaped lone surrogates in the AST for codecs to decide", () => {
    const v = parseJson('"\\ud83d"');
    expect(v).toEqual({ kind: "string", value: "\ud83d" });
    expect(parseJson('"\\ud83d\\ude00"')).toEqual({ kind: "string", value: "😀" });
  });

  it("enforces depth, token and number-length limits", () => {
    expect(() => parseJson("[".repeat(65) + "]".repeat(65), { limits: { maxDepth: 64 } })).toThrow(/depth/);
    expect(parseJson("[".repeat(64) + "]".repeat(64), { limits: { maxDepth: 64 } }).kind).toBe("array");
    expect(() => parseJson("[1,2,3,4,5]", { limits: { maxTokens: 3 } })).toThrow(/token/);
    expect(() => parseJson("1".repeat(20), { limits: { maxNumberCharacters: 10 } })).toThrow(/maxNumberCharacters/);
  });

  it("rejects invalid UTF-8 and a BOM", () => {
    expect(() => parseJsonBytes(new Uint8Array([0x22, 0xff, 0x22]))).toThrow(/UTF-8/);
    expect(() => parseJsonBytes(new Uint8Array([0xef, 0xbb, 0xbf, 0x31]))).toThrow(/byte order mark/);
    expect(() => parseJsonBytes(new Uint8Array([0x22, 0xc0, 0x80, 0x22]))).toThrow(/UTF-8/); // overlong
    expect(() => parseJsonBytes(new Uint8Array([0x22, 0xed, 0xa0, 0x80, 0x22]))).toThrow(/UTF-8/); // encoded surrogate
    expect(parseJsonBytes(new TextEncoder().encode('{"é":"😀"}'))).toEqual({ kind: "object", entries: [{ name: "é", value: { kind: "string", value: "😀" } }] });
  });

  it("supports cooperative cancellation checkpoints", () => {
    let calls = 0;
    const big = "[" + Array.from({ length: 5000 }, () => "1").join(",") + "]";
    expect(() =>
      parseJson(big, {
        checkpoint: () => {
          if (++calls > 2) {
            throw new Error("cancelled");
          }
        },
      }),
    ).toThrow("cancelled");
  });
});

describe("writer", () => {
  it("round-trips and uses only the required escapes", () => {
    const text = '{"a":[1,-0.5e-7,"x\\"y\\\\z",true,null],"é":"\\u0001\\n","\\ud83d":""}';
    const ast = parseJson(text);
    expect(writeJson(ast)).toBe('{"a":[1,-0.5e-7,"x\\"y\\\\z",true,null],"é":"\\u0001\\n","\\ud83d":""}');
    expect(jsonEquals(parseJson(writeJson(ast)), ast)).toBe(true);
  });

  it("converts native values without going through Number for bigints", () => {
    expect(writeJson(fromNative({ a: 9007199254740993n, b: [1, "x", null], c: -0 }))).toBe('{"a":9007199254740993,"b":[1,"x",null],"c":-0}');
    expect(() => fromNative(Number.NaN)).toThrow(TypeError);
  });
});

describe("JCS", () => {
  it("matches RFC 8785 examples", () => {
    const sorted = parseJson('{"\\u20ac":"Euro Sign","\\r":"Carriage Return","\\ufb33":"Hebrew Letter Dalet With Dagesh","1":"One","\\ud83d\\ude00":"Emoji: Grinning Face","\\u0080":"Control","\\u00f6":"Latin Small Letter O With Diaeresis"}');
    expect(canonicalize(sorted)).toBe('{"\\r":"Carriage Return","1":"One","\u0080":"Control","ö":"Latin Small Letter O With Diaeresis","€":"Euro Sign","😀":"Emoji: Grinning Face","\ufb33":"Hebrew Letter Dalet With Dagesh"}');
    const prim = parseJson('{"numbers":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],"string":"\\u20ac$\\u000F\\u000aA\'\\u0042\\u0022\\u005c\\\\\\"\\/","literals":[null,true,false]}');
    expect(canonicalize(prim)).toBe('{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\\u000f\\nA\'B\\"\\\\\\\\\\"/"}');
  });

  it("rejects lone surrogates", () => {
    expect(() => canonicalize(parseJson('"\\ud83d"'))).toThrow(/surrogate/);
  });
});
