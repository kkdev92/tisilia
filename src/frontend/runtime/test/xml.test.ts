import { describe, expect, it } from "vitest";
import { createCodecContext } from "../src/codec/abi.js";
import { CodecError } from "../src/codec/errors.js";
import { readXmlBody, writeXmlBody, xmlElementCodec, xmlEnumCodec, xmlItemsCodec, xmlTextCodec, type XmlCodec } from "../src/codec/xml.js";
import { decimalFromString } from "../src/primitives/decimal.js";
import { xsiNamespace, type XmlElement } from "../src/xml/dom.js";
import { formatXmlDuration, formatXmlEnum, parseXmlDuration, parseXmlEnum, xmlLexical } from "../src/xml/lexical.js";
import { parseXml, parseXmlBytes, XmlParseError } from "../src/xml/parser.js";
import { writeXml } from "../src/xml/writer.js";

const ctx = createCodecContext({ profileId: "test" });

function parseError(text: string): string {
  try {
    parseXml(text);
  } catch (error) {
    if (error instanceof XmlParseError) {
      return error.code;
    }
    throw error;
  }
  throw new Error("parsed: " + text);
}

describe("XML parser", () => {
  it("reads names, namespaces, attributes and merged text", () => {
    const root = parseXml(`<?xml version="1.0" encoding="utf-8"?>\n<!-- c --><o:Order xmlns:o="urn:o" xmlns="urn:d" id="7" o:kind="x"><Line>a<![CDATA[<b>]]>&amp;&#x41;<!-- c -->z</Line><Empty/></o:Order>\n`);
    expect(root).toMatchObject({ local: "Order", ns: "urn:o", attributes: [{ local: "id", ns: "", value: "7" }, { local: "kind", ns: "urn:o", value: "x" }] });
    expect(root.children).toEqual([
      { kind: "element", local: "Line", ns: "urn:d", attributes: [], children: [{ kind: "text", value: "a<b>&Az" }] },
      { kind: "element", local: "Empty", ns: "urn:d", attributes: [], children: [] },
    ]);
  });

  it("normalizes line ends and attribute white space, but keeps character references", () => {
    // XML 1.0 §2.11 and §3.3.3, as MVC's XmlSerializer formatters read the same document on .NET 10
    const root = parseXml("<a b=\"x\ty\nz&#x9;&#xA;&#xD;\">1\r\n2\r3&#xD;</a>");
    expect(root.attributes[0]!.value).toBe("x y z\t\n\r");
    expect(root.children).toEqual([{ kind: "text", value: "1\n2\n3\r" }]);
  });

  it("reads a byte order mark and an undeclared default namespace", () => {
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode("<a xmlns=\"urn:x\"><b xmlns=\"\"/></a>")]);
    const root = parseXmlBytes(bytes);
    expect(root.ns).toBe("urn:x");
    expect((root.children[0] as XmlElement).ns).toBe("");
  });

  it("refuses what the server's formatters never write", () => {
    expect(parseError("<!DOCTYPE a><a/>")).toBe("doctype");
    expect(parseError("<a><?pi x?></a>")).toBe("processing-instruction");
    expect(parseError("<a>&nbsp;</a>")).toBe("entity");
    expect(parseError("<a>&#xD800;</a>")).toBe("entity");
    expect(parseError("<p:a/>")).toBe("namespace");
    expect(parseError("<a x=\"1\" x=\"2\"/>")).toBe("duplicate-attribute");
    expect(parseError("<a xmlns:p=\"urn:x\" xmlns:q=\"urn:x\" p:x=\"1\" q:x=\"2\"/>")).toBe("duplicate-attribute");
    expect(parseError("<a></b>")).toBe("malformed");
    expect(parseError("<a/><b/>")).toBe("trailing-content");
    expect(parseError("<?xml version=\"1.0\" encoding=\"utf-16\"?><a/>")).toBe("encoding");
    expect(parseError("<a>\u0001</a>")).toBe("character");
    expect(parseError("<a>]]></a>")).toBe("malformed");
    expect(parseError("<a><!-- a -- b --></a>")).toBe("malformed");
  });

  it("enforces the depth and token limits", () => {
    expect(() => parseXml("<a><a><a/></a></a>", { limits: { maxDepth: 2 } })).toThrow(XmlParseError);
    expect(parseXml("<a><a/></a>", { limits: { maxDepth: 2 } }).children).toHaveLength(1);
    expect(() => parseXml("<a><b/><b/><b/></a>", { limits: { maxTokens: 3 } })).toThrow(XmlParseError);
  });
});

describe("XML writer", () => {
  it("escapes what a reader would change and declares namespaces", () => {
    const element: XmlElement = {
      kind: "element",
      local: "Order",
      ns: "urn:o",
      attributes: [{ local: "note", ns: "", value: "a\tb\nc\rd\"<&>" }, { local: "nil", ns: xsiNamespace, value: "true" }, { local: "x", ns: "urn:other", value: "1" }],
      children: [
        { kind: "text", value: "a\r\nb\u0001￾]]>" },
        { kind: "element", local: "Inner", ns: "", attributes: [], children: [] },
      ],
    };
    const text = writeXml(element);
    expect(text).toBe("<Order xmlns=\"urn:o\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:p1=\"urn:other\" note=\"a&#x9;b&#xA;c&#xD;d&quot;&lt;&amp;&gt;\" xsi:nil=\"true\" p1:x=\"1\">a&#xD;\nb&#x1;&#xFFFE;]]&gt;<Inner xmlns=\"\"/></Order>");
    // the server reads character references to control characters (XmlDictionaryReader, CheckCharacters off); this parser refuses
    // them only raw, so the written document reads back as written
    expect(() => parseXml(text)).not.toThrow();
  });

  it("refuses lone surrogates", () => {
    expect(() => writeXml({ kind: "element", local: "a", ns: "", attributes: [], children: [{ kind: "text", value: "\ud800" }] })).toThrow(CodecError);
  });

  it("round-trips text through the parser", () => {
    const samples = ["", " ", "  x  ", "a\nb", "a\r\nb", "\r", "<&>\"'", "\t", "😀", "x]]>y"];
    for (const value of samples) {
      const back = parseXml(writeXml({ kind: "element", local: "a", ns: "", attributes: [{ local: "v", ns: "", value }], children: value === "" ? [] : [{ kind: "text", value }] }));
      expect(back.attributes[0]!.value).toBe(value);
      expect(back.children.map((c) => (c.kind === "text" ? c.value : "")).join("")).toBe(value);
    }
  });
});

describe("XML lexical forms", () => {
  it("writes and reads xs:duration as XmlConvert does", () => {
    // what XmlConvert.ToString(TimeSpan) writes on .NET 10
    const cases: [bigint, string][] = [
      [0n, "PT0S"],
      [1n, "PT0.0000001S"],
      [-1_296_000_000_000n, "-P1DT12H"],
      [9223372036854775807n, "P10675199DT2H48M5.4775807S"],
      [-9223372036854775808n, "-P10675199DT2H48M5.4775808S"],
      [5_000_000n, "PT0.5S"],
      [900_000_000_000n, "P1DT1H"],
    ];
    for (const [ticks, text] of cases) {
      expect(formatXmlDuration({ kind: "duration", ticks })).toBe(text);
      expect(parseXmlDuration(text, "")).toEqual({ kind: "duration", ticks });
    }
    expect(parseXmlDuration(" PT36H ", "")).toEqual({ kind: "duration", ticks: 1_296_000_000_000n });
    for (const bad of ["P1Y", "P1M", "P", "PT", "P1DT", "PT1.12345678S", "1.02:03:04", "P10675199DT2H48M5.4775808S"]) {
      expect(() => parseXmlDuration(bad, "")).toThrow(CodecError);
    }
  });

  it("writes INF, -INF and NaN and reads the server's numbers", () => {
    const double = xmlLexical("xml-float", "float64");
    expect([Infinity, -Infinity, Number.NaN, -0, 1e21, 0.1].map((v) => double.format(v, ""))).toEqual(["INF", "-INF", "NaN", "-0", "1e+21", "0.1"]);
    expect(double.parse("1E+21", "")).toBe(1e21);
    expect(double.parse(" -INF ", "")).toBe(-Infinity);
    expect(Object.is(double.parse("-0", ""), -0)).toBe(true);
    expect(() => double.parse("Infinity", "")).toThrow(CodecError);
    const decimal = xmlLexical("xml-decimal", "decimal");
    expect(decimal.parse("1.50", "")).toEqual(decimalFromString("1.50"));
    expect(decimal.format(decimalFromString("-0.0"), "")).toBe("0.0");
    expect(() => decimal.parse("1E2", "")).toThrow(CodecError);
    const int = xmlLexical("xml-integer", "int32");
    expect(int.parse(" +42 ", "")).toBe(42);
    expect(() => int.parse("2147483648", "")).toThrow(CodecError);
    expect(xmlLexical("xml-integer", "int64").parse("-9223372036854775808", "")).toBe(-9223372036854775808n);
    const bool = xmlLexical("xml-boolean", "boolean");
    expect(["true", "1", " false ", "0"].map((t) => bool.parse(t, ""))).toEqual([true, true, false, false]);
    expect(() => bool.parse("True", "")).toThrow(CodecError);
  });

  it("writes char as a number, bytes as base64 or hex, offsets with Z and TimeOnly without trailing zeros", () => {
    expect(xmlLexical("xml-char", "char").format("A", "")).toBe("65");
    expect(xmlLexical("xml-char", "char").parse(" 66 ", "")).toBe("B");
    expect(() => xmlLexical("xml-char", "char").parse("55296", "")).toThrow(CodecError);
    expect(xmlLexical("xml-hex", "bytes").format(new Uint8Array([0xab, 1]), "")).toBe("AB01");
    expect(xmlLexical("xml-hex", "bytes").parse("ab01", "")).toEqual(new Uint8Array([0xab, 1]));
    expect(xmlLexical("xml-base64", "bytes").format(new Uint8Array([1, 2]), "")).toBe("AQI=");
    expect(xmlLexical("xml-datetime-offset", "datetime-offset").format({ kind: "datetime-offset", ticks: 638_712_864_000_000_000n, offsetMinutes: 0 }, "")).toBe("2025-01-01T00:00:00Z");
    expect(xmlLexical("xml-datetime-offset", "datetime-offset").format({ kind: "datetime-offset", ticks: 638_712_864_000_000_010n, offsetMinutes: -330 }, "")).toBe("2025-01-01T00:00:00.000001-05:30");
    expect(xmlLexical("xml-time-only", "time-only").format({ kind: "time-only", ticks: 37_231_000_000n }, "")).toBe("01:02:03.1");
  });

  it("names enum values as XmlSerializer does", () => {
    const names = [
      { value: 0n, name: "None" },
      { value: 1n, name: "Read" },
      { value: 2n, name: "Write" },
      { value: 3n, name: "All" },
      { value: 4n, name: "Delete" },
    ];
    // a defined value is its own name; another combination lists every constant inside the value (FromEnum)
    expect(formatXmlEnum(3n, names, true, "")).toBe("All");
    expect(formatXmlEnum(7n, names, true, "")).toBe("Read Write All Delete");
    expect(formatXmlEnum(0n, names, true, "")).toBe("None");
    expect(formatXmlEnum(0n, names.slice(1), true, "")).toBe("");
    expect(() => formatXmlEnum(8n, names, true, "")).toThrow(CodecError);
    expect(parseXmlEnum("Read  Write", names, true, "")).toBe(3n);
    expect(parseXmlEnum("", names, true, "")).toBe(0n);
    expect(() => parseXmlEnum("Read,Write", names, true, "")).toThrow(CodecError);
    expect(() => parseXmlEnum(" Read", names, false, "")).toThrow(CodecError);
  });
});

const line = xmlElementCodec<{ Sku?: string | null; Quantity?: number }>({
  id: "line",
  typeId: "line",
  attributes: [],
  elements: [
    { property: "Sku", name: "Sku", codec: xmlTextCodec({ id: "s", typeId: "std.string", scalar: "string", grammar: "xml-string" }), presence: "required", wirePresence: "optional", nullable: true },
    { property: "Quantity", name: "Quantity", codec: xmlTextCodec({ id: "i", typeId: "std.int32", scalar: "int32", grammar: "xml-integer" }), presence: "required", wirePresence: "required", nullable: false },
  ],
  request: true,
  response: true,
});
const string = xmlTextCodec({ id: "s", typeId: "std.string", scalar: "string", grammar: "xml-string" });
const int = xmlTextCodec({ id: "i", typeId: "std.int32", scalar: "int32", grammar: "xml-integer" });
const color = xmlEnumCodec({ id: "c", typeId: "c", underlying: "int32", flags: false, members: [{ name: "Red", value: 0n }, { name: "Blue", value: 2n }], names: [{ value: 0n, name: "Red" }, { value: 2n, name: "bleu" }] });
const order = xmlElementCodec({
  id: "order",
  typeId: "order",
  attributes: [{ property: "id", name: "id", codec: int, presence: "required", wirePresence: "required", nullable: false }],
  elements: [
    { property: "Note", name: "Note", ns: "urn:n", codec: string, presence: "required", wirePresence: "required", nullable: true, nillable: true },
    { property: "Lines", name: "Lines", codec: xmlItemsCodec({ id: "lines", typeId: "lines", item: { name: "Line", nillable: true }, element: line, elementNullable: true, request: true, response: true }), presence: "required", wirePresence: "optional", nullable: true },
    { property: "flat", name: "flat", codec: string, presence: "required", wirePresence: "optional", nullable: false, repeated: true },
    { property: "Count", name: "Count", codec: int, presence: "required", wirePresence: "optional", nullable: false, default: "7" },
    { property: "Color", name: "Color", codec: color, presence: "optional", wirePresence: "optional", nullable: false },
  ],
  request: true,
  response: true,
});

function body(xml: string): unknown {
  return readXmlBody(order, { name: "Order" }, parseXml(xml), false, ctx);
}

describe("XML codecs", () => {
  it("write a request as XmlSerializer reads it", () => {
    const value = { id: 7, Note: null, Lines: [{ Sku: "a", Quantity: 2 }, null], flat: ["x", "y"], Count: 7, Color: 2 };
    const written = writeXmlBody(order, { name: "Order" }, value, ctx, 32);
    expect(written.text).toBe('<Order id="7"><Note xmlns="urn:n" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/><Lines><Line><Sku>a</Sku><Quantity>2</Quantity></Line><Line xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/></Lines><flat>x</flat><flat>y</flat><Count>7</Count><Color>bleu</Color></Order>');
    expect(written.depth).toBe(4);
    expect(writeXmlBody(order, { name: "Order" }, value, ctx, 3).depth).toBe(4);
  });

  it("read a response as XmlSerializer writes it, with absent members as null, a default or no values", () => {
    const xml = '<Order xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" id="7">\n  <Note xmlns="urn:n" xsi:nil="true" />\n  <flat>b</flat>\n</Order>';
    expect(body(xml)).toEqual({ id: 7, Note: null, Lines: null, flat: ["b"], Count: 7 });
    expect(body('<Order id="1"><Note xmlns="urn:n">n</Note><Lines><Line><Quantity>1</Quantity></Line></Lines><Count>3</Count><Color>bleu</Color></Order>')).toEqual({ id: 1, Note: "n", Lines: [{ Sku: null, Quantity: 1 }], flat: [], Count: 3, Color: 2 });
  });

  it("refuse what the contract does not describe", () => {
    const fails = (xml: string): string => {
      try {
        body(xml);
      } catch (error) {
        return (error as CodecError).code;
      }
      return "decoded";
    };
    expect(fails('<Order id="1" extra="x"><Note xmlns="urn:n"/></Order>')).toBe("unexpected-property");
    expect(fails('<Order id="1"><Note xmlns="urn:n"/><Other/></Order>')).toBe("unexpected-property");
    expect(fails('<Order id="1"><Note/></Order>')).toBe("unexpected-property"); // the note is in urn:n
    expect(fails('<Order id="1"><Note xmlns="urn:n"/><Note xmlns="urn:n"/></Order>')).toBe("duplicate-property");
    expect(fails('<Order id="1"/>')).toBe("missing-required");
    expect(fails('<Order id="1"><Note xmlns="urn:n"/>text</Order>')).toBe("type-mismatch");
    expect(fails('<Order xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" id="1" xsi:type="Special"><Note xmlns="urn:n"/></Order>')).toBe("unsupported");
    expect(fails('<Order id="1"><Note xmlns="urn:n"/><Color>Blue</Color></Order>')).toBe("grammar");
    // a document that decodes as the value but for its root element: another name, or the name in another namespace
    const root = (xml: string, expected: { name: string; ns?: string }): string => {
      try {
        readXmlBody(order, expected, parseXml(xml), false, ctx);
      } catch (error) {
        return (error as CodecError).code;
      }
      return "decoded";
    };
    expect(root('<Order id="1"><Note xmlns="urn:n"/></Order>', { name: "Order" })).toBe("decoded");
    expect(root('<Other id="1"><Note xmlns="urn:n"/></Other>', { name: "Order" })).toBe("type-mismatch");
    expect(root('<Order xmlns="urn:o" id="1"><Note xmlns="urn:n"/></Order>', { name: "Order" })).toBe("type-mismatch");
    expect(root('<Order id="1"><Note xmlns="urn:n"/></Order>', { name: "Order", ns: "urn:o" })).toBe("type-mismatch");
    expect(() => readXmlBody(order, { name: "Order" }, parseXml('<Order xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/>'), false, ctx)).toThrow(CodecError);
    expect(readXmlBody(order, { name: "Order" }, parseXml('<Order xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/>'), true, ctx)).toBeNull();
  });

  it("validate the domain and refuse a value that contains itself", () => {
    expect(() => order.validateDomain({ id: 1, Note: null, Lines: null, flat: [], Count: 1, Color: 1 }, ctx)).toThrow(CodecError); // 1 is no defined color
    expect(() => order.validateDomain({ id: 1, Note: null, Lines: null, flat: [null], Count: 1 }, ctx)).toThrow(CodecError);
    const node: { Child?: unknown } = {};
    node.Child = node;
    const tree: XmlCodec<{ Child?: unknown }> = xmlElementCodec({ id: "node", typeId: "node", attributes: [], elements: [{ property: "Child", name: "Child", codec: () => tree, presence: "optional", wirePresence: "optional", nullable: false }], request: true, response: true });
    expect(() => tree.validateDomain(node, ctx)).toThrow(/contains itself/);
    // a value reached twice without being inside itself is written twice
    const shared = { Sku: "s", Quantity: 1 };
    const lines = xmlItemsCodec({ id: "lines", typeId: "lines", item: { name: "Line" }, element: line, elementNullable: false, request: true, response: true });
    expect(writeXmlBody(lines, { name: "ArrayOfLine" }, [shared, shared], ctx, 32).text).toBe("<ArrayOfLine><Line><Sku>s</Sku><Quantity>1</Quantity></Line><Line><Sku>s</Sku><Quantity>1</Quantity></Line></ArrayOfLine>");
  });

  it("read character content and empty content as null for a string", () => {
    const valued = xmlElementCodec<{ Unit?: string; Amount: unknown }>({
      id: "valued",
      typeId: "valued",
      attributes: [{ property: "Unit", name: "Unit", codec: string, presence: "optional", wirePresence: "optional", nullable: false }],
      elements: [],
      text: { property: "Amount", codec: xmlTextCodec({ id: "d", typeId: "std.decimal", scalar: "decimal", grammar: "xml-decimal" }), presence: "required", wirePresence: "required", nullable: false },
      request: true,
      response: true,
    });
    expect(readXmlBody(valued, { name: "Valued" }, parseXml('<Valued Unit="kg"> 2.5 </Valued>'), false, ctx)).toEqual({ Unit: "kg", Amount: decimalFromString("2.5") });
    expect(writeXmlBody(valued, { name: "Valued" }, { Unit: "kg", Amount: decimalFromString("1.50") }, ctx, 32).text).toBe('<Valued Unit="kg">1.50</Valued>');
    const labeled = xmlElementCodec<{ Text: string | null }>({ id: "labeled", typeId: "labeled", attributes: [], elements: [], text: { property: "Text", codec: string, presence: "required", wirePresence: "required", nullable: true }, request: false, response: true });
    expect(readXmlBody(labeled, { name: "L" }, parseXml("<L/>"), false, ctx)).toEqual({ Text: null });
    expect(readXmlBody(labeled, { name: "L" }, parseXml("<L>a\nb</L>"), false, ctx)).toEqual({ Text: "a\nb" });
  });
});
