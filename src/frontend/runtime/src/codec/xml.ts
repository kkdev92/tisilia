import type { JsonValue } from "../json/ast.js";
import { graphOf, type Codec, type CodecContext } from "./abi.js";
import { CodecError } from "./errors.js";
import { inGraph } from "./graph.js";
import { scalarCodec, type ScalarName } from "./scalars.js";
import { enumCodec, type CodecRef, type EnumMember, type Presence } from "./structural.js";
import { xsiNamespace, type XmlAttribute, type XmlChild, type XmlElement } from "../xml/dom.js";
import { formatXmlEnum, parseXmlEnum, xmlLexical, type XmlEnumName, type XmlScalarGrammar } from "../xml/lexical.js";
import { writeXml } from "../xml/writer.js";

/** What a value is written as inside an element: its attributes and children. */
export interface XmlContent {
  readonly attributes: readonly XmlAttribute[];
  readonly children: readonly XmlChild[];
}

/**
 * The XML surface of a codec of an XML wire (MVC's XmlSerializer formatters). `writeXml` gives an element's content for the request,
 * `readXml` reads one of the response; a text codec also gives and reads the text of an attribute or of character content.
 */
export interface XmlCodec<T = unknown> extends Codec<T> {
  readonly xml: "text" | "element" | "items";
  writeXml?(value: T, ctx: CodecContext): XmlContent;
  readXml?(element: XmlElement, ctx: CodecContext): T;
  formatXmlText?(value: T, ctx: CodecContext): string;
  parseXmlText?(text: string, ctx: CodecContext): T;
}

export function isXmlCodec(codec: Codec<unknown>): codec is XmlCodec<unknown> {
  return (codec as Partial<XmlCodec>).xml !== undefined;
}

function resolve(ref: CodecRef<unknown>): XmlCodec<unknown> {
  const codec = typeof ref === "function" ? ref() : ref;
  if (!isXmlCodec(codec)) {
    throw new CodecError("unsupported", "", `codec '${codec.id}' has no XML form`, codec.id);
  }
  return codec;
}

/** Null-prototype record so that XML names can never reach Object.prototype. */
function createRecord(): Record<string, unknown> {
  return Object.create(null) as Record<string, unknown>;
}

/** `xsi:nil="true"`: XML Schema's null, which XmlSerializer writes for a null nillable element (and reads as true or 1). */
export function isNil(element: XmlElement, path: string): boolean {
  const nil = element.attributes.find((a) => a.ns === xsiNamespace && a.local === "nil");
  if (nil === undefined) {
    return false;
  }
  switch (nil.value.trim()) {
    case "true":
    case "1":
      return true;
    case "false":
    case "0":
      return false;
    default:
      throw new CodecError("grammar", path, "xsi:nil must be true or false");
  }
}

/** An attribute of the XML Schema instance namespace other than xsi:nil is a type the server named for a derived value. */
function refuseXsi(attribute: XmlAttribute, path: string, codecId: string): void {
  if (attribute.local !== "nil") {
    throw new CodecError("unsupported", path, `the server wrote xsi:${attribute.local} (a derived type, which this codec does not describe)`, codecId);
  }
}

/** The character content of an element without child elements and attributes (the null of a nillable element is its parent's). */
function textContent(element: XmlElement, path: string, codecId: string): string {
  for (const attribute of element.attributes) {
    if (attribute.ns === xsiNamespace) {
      refuseXsi(attribute, path, codecId);
      continue;
    }
    throw new CodecError("unexpected-property", path, `unexpected attribute '${attribute.local}'`, codecId);
  }
  let text = "";
  for (const child of element.children) {
    if (child.kind === "element") {
      throw new CodecError("type-mismatch", path, `element '${child.local}' where text is expected`, codecId);
    }
    text += child.value;
  }
  return text;
}

function nilElement(local: string, ns: string): XmlElement {
  return { kind: "element", local, ns, attributes: [{ local: "nil", ns: xsiNamespace, value: "true" }], children: [] };
}

function elementOf(local: string, ns: string, content: XmlContent): XmlElement {
  return { kind: "element", local, ns, attributes: content.attributes, children: content.children };
}

function textChildren(text: string): readonly XmlChild[] {
  return text === "" ? [] : [{ kind: "text", value: text }];
}

// ---------------------------------------------------------------- text

export interface XmlTextCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly scalar: ScalarName;
  readonly grammar: XmlScalarGrammar;
}

/**
 * A builtin scalar in an XML text grammar. The domain is the scalar's own (floats include NaN and the infinities, which XML writes as
 * NaN, INF and -INF); the text is what XmlSerializer writes and reads for the CLR type.
 */
export function xmlTextCodec<T = unknown>(d: XmlTextCodecDescriptor): XmlCodec<T> {
  const domain = scalarCodec(d.scalar, { id: d.id, typeId: d.typeId, numbers: { readFromString: false, writeAsString: false, namedLiterals: true } });
  const lexical = xmlLexical(d.grammar, d.scalar);
  const format = (value: unknown, ctx: CodecContext): string => lexical.format(domain.validateDomain(value, ctx), ctx.path);
  const parse = (text: string, ctx: CodecContext): unknown => lexical.parse(text, ctx.path);
  return {
    id: d.id,
    typeId: d.typeId,
    xml: "text",
    validateDomain: domain.validateDomain,
    parseRequestInput: domain.parseRequestInput!,
    formatXmlText: format,
    parseXmlText: parse,
    writeXml: (value, ctx) => ({ attributes: [], children: textChildren(format(value, ctx)) }),
    readXml: (element, ctx) => parse(textContent(element, ctx.path, d.id), ctx),
  } as XmlCodec<unknown> as XmlCodec<T>;
}

// ---------------------------------------------------------------- enum

export interface XmlEnumCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly underlying: "int8" | "uint8" | "int16" | "uint16" | "int32" | "uint32" | "int64" | "uint64";
  readonly flags: boolean;
  readonly members: readonly EnumMember[];
  /** Each constant's XML name, in XmlSerializer's order. */
  readonly names: readonly XmlEnumName[];
}

/** An enum written as its constants' XML names; only defined values (and, for flags, combinations of them) exist, as XmlSerializer refuses others. */
export function xmlEnumCodec(d: XmlEnumCodecDescriptor): XmlCodec<number | bigint> {
  const domain = enumCodec({ id: d.id, typeId: d.typeId, underlying: d.underlying, flags: d.flags, allowUndefinedInteger: false, stringForm: false, members: d.members });
  const big = d.underlying === "int64" || d.underlying === "uint64";
  const format = (value: unknown, ctx: CodecContext): string => formatXmlEnum(BigInt(domain.validateDomain(value, ctx)), d.names, d.flags, ctx.path);
  const parse = (text: string, ctx: CodecContext): number | bigint => {
    const value = parseXmlEnum(text, d.names, d.flags, ctx.path);
    return big ? value : Number(value);
  };
  return {
    id: d.id,
    typeId: d.typeId,
    xml: "text",
    validateDomain: domain.validateDomain,
    parseRequestInput: (input, ctx) => {
      // an XML name is accepted as well as the CLR name or the number
      const text = typeof input === "string" ? input.trim() : input.kind === "string" ? input.value : undefined;
      const named = text === undefined ? undefined : d.names.find((n) => n.name === text);
      return named !== undefined ? (big ? named.value : Number(named.value)) : domain.parseRequestInput!(input, ctx);
    },
    formatXmlText: format,
    parseXmlText: parse,
    writeXml: (value, ctx) => ({ attributes: [], children: textChildren(format(value, ctx)) }),
    readXml: (element, ctx) => parse(textContent(element, ctx.path, d.id), ctx),
  };
}

// ---------------------------------------------------------------- element (a class)

export interface XmlMemberDescriptor {
  /** The domain property. */
  readonly property: string;
  /** The attribute or element local name; absent for character content. */
  readonly name?: string;
  readonly ns?: string;
  /** The value's codec; for a repeated member, each item's. */
  readonly codec: CodecRef<unknown>;
  /** Presence in the domain object. */
  readonly presence: Presence;
  /** Whether the member may be absent from the XML. */
  readonly wirePresence: Presence;
  /** The value (for a repeated member, each item) may be null. */
  readonly nullable: boolean;
  readonly nillable?: boolean;
  readonly repeated?: boolean;
  /** The text of the member's default value: the server leaves the member out when its value equals it. */
  readonly default?: string;
}

export interface XmlElementCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly attributes: readonly XmlMemberDescriptor[];
  readonly elements: readonly XmlMemberDescriptor[];
  readonly text?: XmlMemberDescriptor;
  readonly request: boolean;
  readonly response: boolean;
}

/** A class as XmlSerializer writes it: attributes, child elements in order, or character content. */
export function xmlElementCodec<T extends object>(d: XmlElementCodecDescriptor): XmlCodec<T> {
  const members = [...d.attributes, ...d.elements, ...(d.text === undefined ? [] : [d.text])];
  const known = new Set(members.map((m) => m.property));
  const validateValue = (m: XmlMemberDescriptor, v: unknown, ctx: CodecContext): unknown => {
    if (v === undefined) {
      throw new CodecError("undefined-not-allowed", ctx.path, `property '${m.property}' is undefined; omit it or set null`, d.id);
    }
    if (v === null) {
      if (!m.nullable) {
        throw new CodecError("null-not-allowed", ctx.path, `property '${m.property}' does not allow null`, d.id);
      }
      return null;
    }
    return resolve(m.codec).validateDomain(v, ctx);
  };
  const validate = (value: unknown, ctx: CodecContext): T => {
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      throw new CodecError("type-mismatch", ctx.path, "object required", d.id);
    }
    const graph = graphOf(ctx);
    return inGraph(graph, () => {
      if (graph.active.has(value)) {
        throw new CodecError("unsupported", ctx.path, "the value contains itself, and XML writes a tree", d.id);
      }
      graph.active.add(value);
      try {
        const source = value as Record<string, unknown>;
        const out = createRecord();
        for (const m of members) {
          const path = ctx.child(m.property);
          if (!Object.prototype.hasOwnProperty.call(source, m.property)) {
            if (m.presence === "required") {
              throw new CodecError("missing-required", path.path, `required property '${m.property}' is missing`, d.id);
            }
            continue;
          }
          const v = source[m.property];
          if (m.repeated === true) {
            if (!Array.isArray(v)) {
              throw new CodecError("type-mismatch", path.path, "array required", d.id);
            }
            if (graph.active.has(v)) {
              throw new CodecError("unsupported", path.path, "the value contains itself, and XML writes a tree", d.id);
            }
            const items: unknown[] = [];
            for (let i = 0; i < v.length; i++) {
              items.push(validateValue(m, i in v ? v[i] : undefined, path.child(i)));
            }
            out[m.property] = items;
            continue;
          }
          out[m.property] = validateValue(m, v, path);
        }
        return out as T;
      } finally {
        graph.active.delete(value);
      }
    });
  };

  const writeXml = (value: T, ctx: CodecContext): XmlContent => {
    ctx.checkpoint();
    const validated = validate(value, ctx) as Record<string, unknown>;
    const attributes: XmlAttribute[] = [];
    const children: XmlChild[] = [];
    const present = (m: XmlMemberDescriptor): boolean => {
      if (Object.prototype.hasOwnProperty.call(validated, m.property)) {
        return true;
      }
      if (m.wirePresence === "required") {
        throw new CodecError("missing-required", ctx.child(m.property).path, `the server requires '${m.property}'`, d.id);
      }
      return false;
    };
    for (const m of d.attributes) {
      if (!present(m)) {
        continue;
      }
      const v = validated[m.property];
      if (v === null) {
        throw new CodecError("null-not-allowed", ctx.child(m.property).path, "an attribute cannot be null; leave it out", d.id);
      }
      attributes.push({ local: m.name!, ns: m.ns ?? "", value: resolve(m.codec).formatXmlText!(v, ctx.child(m.property)) });
    }
    for (const m of d.elements) {
      if (!present(m)) {
        continue;
      }
      const codec = resolve(m.codec);
      const write = (v: unknown, path: CodecContext): XmlElement => {
        if (v === null) {
          if (m.nillable !== true) {
            throw new CodecError("null-not-allowed", path.path, "this element cannot be null; leave it out", d.id);
          }
          return nilElement(m.name!, m.ns ?? "");
        }
        return elementOf(m.name!, m.ns ?? "", codec.writeXml!(v, path));
      };
      const v = validated[m.property];
      if (m.repeated === true) {
        (v as readonly unknown[]).forEach((item, i) => children.push(write(item, ctx.child(m.property).child(i))));
      } else {
        children.push(write(v, ctx.child(m.property)));
      }
    }
    if (d.text !== undefined && present(d.text)) {
      const v = validated[d.text.property];
      if (v !== null) {
        children.push(...textChildren(resolve(d.text.codec).formatXmlText!(v, ctx.child(d.text.property))));
      }
    }
    return { attributes, children };
  };

  const readXml = (element: XmlElement, ctx: CodecContext): T => {
    ctx.checkpoint();
    const out = createRecord();
    for (const attribute of element.attributes) {
      if (attribute.ns === xsiNamespace) {
        refuseXsi(attribute, ctx.path, d.id);
        continue;
      }
      const m = d.attributes.find((x) => x.name === attribute.local && (x.ns ?? "") === attribute.ns);
      if (m === undefined) {
        throw new CodecError("unexpected-property", ctx.path, `unexpected attribute '${attribute.local}'`, d.id);
      }
      out[m.property] = resolve(m.codec).parseXmlText!(attribute.value, ctx.child(m.property));
    }
    const repeated = new Map<XmlMemberDescriptor, unknown[]>();
    if (d.text !== undefined) {
      // character content: empty content is null when the value may be null (XmlSerializer reads no text as null)
      const text = textContent({ ...element, attributes: [] }, ctx.path, d.id);
      if (text !== "" || !d.text.nullable) {
        out[d.text.property] = resolve(d.text.codec).parseXmlText!(text, ctx.child(d.text.property));
      } else {
        out[d.text.property] = null;
      }
    } else {
      for (const child of element.children) {
        if (child.kind === "text") {
          // white space between elements is layout (an indenting writer); other text is not part of an element-only content
          if (!/^[ \t\n\r]*$/.test(child.value)) {
            throw new CodecError("type-mismatch", ctx.path, "text where only elements are expected", d.id);
          }
          continue;
        }
        const m = d.elements.find((x) => x.name === child.local && (x.ns ?? "") === child.ns);
        if (m === undefined) {
          throw new CodecError("unexpected-property", ctx.path, `unexpected element '${child.local}'`, d.id);
        }
        const codec = resolve(m.codec);
        if (m.repeated === true) {
          const items = repeated.get(m) ?? [];
          repeated.set(m, items);
          const path = ctx.child(m.property).child(items.length);
          if (isNil(child, path.path)) {
            if (m.nillable !== true) {
              throw new CodecError("null-not-allowed", path.path, "the server wrote xsi:nil for a value that cannot be null", d.id);
            }
            items.push(null);
          } else {
            items.push(codec.readXml!(child, path));
          }
          continue;
        }
        const path = ctx.child(m.property);
        if (Object.prototype.hasOwnProperty.call(out, m.property)) {
          throw new CodecError("duplicate-property", path.path, `element '${child.local}' appears twice`, d.id);
        }
        if (isNil(child, path.path)) {
          if (m.nillable !== true) {
            throw new CodecError("null-not-allowed", path.path, "the server wrote xsi:nil for a value that cannot be null", d.id);
          }
          out[m.property] = null;
          continue;
        }
        out[m.property] = codec.readXml!(child, path);
      }
    }
    for (const m of members) {
      if (Object.prototype.hasOwnProperty.call(out, m.property)) {
        continue;
      }
      if (m.repeated === true) {
        out[m.property] = repeated.get(m) ?? [];
        continue;
      }
      if (m.default !== undefined) {
        out[m.property] = resolve(m.codec).parseXmlText!(m.default, ctx.child(m.property));
        continue;
      }
      if (m.wirePresence === "required" || (m.presence === "required" && !m.nullable)) {
        throw new CodecError("missing-required", ctx.child(m.property).path, `required ${m.name === undefined ? "text" : `'${m.name}'`} is missing`, d.id);
      }
      if (m.presence === "required") {
        // the server leaves a null member out
        out[m.property] = null;
      }
    }
    // the properties in the model's order, whatever order the elements came in
    const ordered = createRecord();
    for (const m of members) {
      if (Object.prototype.hasOwnProperty.call(out, m.property)) {
        ordered[m.property] = out[m.property];
      }
    }
    return ordered as T;
  };

  const codec: XmlCodec<T> = {
    id: d.id,
    typeId: d.typeId,
    xml: "element",
    validateDomain: validate,
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string") {
        throw new CodecError("unsupported", ctx.path, "object input requires a JSON value editor", d.id);
      }
      return validate(inputToDomain(input, ctx), ctx);
    },
  };
  if (d.request) {
    (codec as { writeXml?: typeof writeXml }).writeXml = writeXml;
  }
  if (d.response) {
    (codec as { readXml?: typeof readXml }).readXml = readXml;
  }
  return codec;

  function inputToDomain(input: JsonValue, ctx: CodecContext): unknown {
    if (input.kind !== "object") {
      throw new CodecError("type-mismatch", ctx.path, "object required", d.id);
    }
    const out = createRecord();
    for (const entry of input.entries) {
      const m = members.find((x) => x.property === entry.name);
      if (m === undefined || !known.has(entry.name)) {
        throw new CodecError("unexpected-property", ctx.child(entry.name).path, `unknown property '${entry.name}'`, d.id);
      }
      const path = ctx.child(entry.name);
      if (m.repeated === true) {
        if (entry.value.kind !== "array") {
          throw new CodecError("type-mismatch", path.path, "array required", d.id);
        }
        out[m.property] = entry.value.items.map((item, i) => (item.kind === "null" ? null : resolve(m.codec).parseRequestInput!(item, path.child(i))));
        continue;
      }
      out[m.property] = entry.value.kind === "null" ? null : resolve(m.codec).parseRequestInput!(entry.value, path);
    }
    return out;
  }
}

// ---------------------------------------------------------------- items (a collection)

export interface XmlItemsCodecDescriptor {
  readonly id: string;
  readonly typeId: string;
  readonly item: { readonly name: string; readonly ns?: string; readonly nillable?: boolean };
  readonly element: CodecRef<unknown>;
  readonly elementNullable: boolean;
  readonly request: boolean;
  readonly response: boolean;
}

/** A collection as XmlSerializer writes it: one item element per value inside the collection's element. */
export function xmlItemsCodec<E>(d: XmlItemsCodecDescriptor): XmlCodec<readonly (E | null)[]> {
  const validate = (value: unknown, ctx: CodecContext): readonly (E | null)[] => {
    if (!Array.isArray(value)) {
      throw new CodecError("type-mismatch", ctx.path, "array required", d.id);
    }
    const graph = graphOf(ctx);
    return inGraph(graph, () => {
      if (graph.active.has(value)) {
        throw new CodecError("unsupported", ctx.path, "the value contains itself, and XML writes a tree", d.id);
      }
      graph.active.add(value);
      try {
        const element = resolve(d.element);
        const out: (E | null)[] = [];
        for (let i = 0; i < value.length; i++) {
          const item: unknown = value[i];
          if (!(i in value) || item === undefined) {
            throw new CodecError("undefined-not-allowed", ctx.child(i).path, "array holes and undefined are not values", d.id);
          }
          if (item === null) {
            if (!d.elementNullable) {
              throw new CodecError("null-not-allowed", ctx.child(i).path, "array element does not allow null", d.id);
            }
            out.push(null);
            continue;
          }
          out.push(element.validateDomain(item, ctx.child(i)) as E);
        }
        return out;
      } finally {
        graph.active.delete(value);
      }
    });
  };
  const ns = d.item.ns ?? "";
  const codec: XmlCodec<readonly (E | null)[]> = {
    id: d.id,
    typeId: d.typeId,
    xml: "items",
    validateDomain: validate,
    parseRequestInput: (input, ctx) => {
      if (typeof input === "string" || input.kind !== "array") {
        throw new CodecError("type-mismatch", ctx.path, "array input requires a JSON array", d.id);
      }
      const element = resolve(d.element);
      return validate(input.items.map((item, i) => (item.kind === "null" ? null : element.parseRequestInput!(item, ctx.child(i)))), ctx);
    },
  };
  if (d.request) {
    (codec as XmlCodec<readonly (E | null)[]> & { writeXml: unknown }).writeXml = (value: readonly (E | null)[], ctx: CodecContext): XmlContent => {
      ctx.checkpoint();
      const items = validate(value, ctx);
      const element = resolve(d.element);
      return {
        attributes: [],
        children: items.map((item, i) => (item === null ? nilElement(d.item.name, ns) : elementOf(d.item.name, ns, element.writeXml!(item, ctx.child(i))))),
      };
    };
  }
  if (d.response) {
    (codec as XmlCodec<readonly (E | null)[]> & { readXml: unknown }).readXml = (wrapper: XmlElement, ctx: CodecContext): readonly (E | null)[] => {
      ctx.checkpoint();
      for (const attribute of wrapper.attributes) {
        if (attribute.ns === xsiNamespace) {
          refuseXsi(attribute, ctx.path, d.id);
          continue;
        }
        throw new CodecError("unexpected-property", ctx.path, `unexpected attribute '${attribute.local}'`, d.id);
      }
      const element = resolve(d.element);
      const out: (E | null)[] = [];
      for (const child of wrapper.children) {
        if (child.kind === "text") {
          if (!/^[ \t\n\r]*$/.test(child.value)) {
            throw new CodecError("type-mismatch", ctx.path, "text where only items are expected", d.id);
          }
          continue;
        }
        const path = ctx.child(out.length);
        if (child.local !== d.item.name || child.ns !== ns) {
          throw new CodecError("unexpected-property", path.path, `unexpected element '${child.local}' among the items`, d.id);
        }
        if (isNil(child, path.path)) {
          if (d.item.nillable !== true) {
            throw new CodecError("null-not-allowed", path.path, "the server wrote xsi:nil for an item that cannot be null", d.id);
          }
          out.push(null);
          continue;
        }
        out.push(element.readXml!(child, path) as E);
      }
      return out;
    };
  }
  return codec;
}

// ---------------------------------------------------------------- bodies

export interface XmlRoot {
  readonly name: string;
  readonly ns?: string;
}

function depthOf(element: XmlElement): number {
  let deepest = 0;
  for (const child of element.children) {
    if (child.kind === "element") {
      deepest = Math.max(deepest, depthOf(child));
    }
  }
  return deepest + 1;
}

/**
 * The XML document of a request body: the value's content in the root element. The server reads elements nested `maxDepth` levels
 * deep at most (the root is level 1); a deeper document is reported here instead of being sent.
 */
export function writeXmlBody(codec: Codec<unknown>, root: XmlRoot, value: unknown, ctx: CodecContext, maxDepth: number): { readonly text: string; readonly depth: number } {
  const xml = resolve(codec);
  if (xml.writeXml === undefined) {
    throw new CodecError("unsupported", ctx.path, `codec '${codec.id}' has no request capability`, codec.id);
  }
  const element = elementOf(root.name, root.ns ?? "", xml.writeXml(value, ctx));
  const depth = depthOf(element);
  if (depth > maxDepth) {
    return { text: "", depth };
  }
  return { text: writeXml(element), depth };
}

/** The value of a response body's root element; `xsi:nil="true"` on the root is null. */
export function readXmlBody(codec: Codec<unknown>, root: XmlRoot, element: XmlElement, nullable: boolean, ctx: CodecContext): unknown {
  const xml = resolve(codec);
  if (xml.readXml === undefined) {
    throw new CodecError("unsupported", ctx.path, `codec '${codec.id}' has no response capability`, codec.id);
  }
  if (element.local !== root.name || element.ns !== (root.ns ?? "")) {
    throw new CodecError("type-mismatch", ctx.path, `the root element is '${element.local}'${element.ns === "" ? "" : ` in '${element.ns}'`}, not '${root.name}'${root.ns === undefined ? "" : ` in '${root.ns}'`}`, codec.id);
  }
  if (isNil(element, ctx.path)) {
    if (!nullable) {
      throw new CodecError("null-not-allowed", ctx.path, "the server wrote a null body that the contract does not allow", codec.id);
    }
    return null;
  }
  return xml.readXml(element, ctx);
}
