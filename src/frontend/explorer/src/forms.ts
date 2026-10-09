// Request forms. The editable structure follows the request wire the server reads — what the generated request
// encoder writes — enriched with the domain types it carries (enum members, union variant names, scalar kinds, map keys). A form
// edits a JSON AST: in typed mode that AST goes through the codec's request-input factory and the request encoder like any other
// input, in raw mode it is the body sent. Nothing here formats a request on its own.
import { parseJson, writeJson, type ContractDocument, type ContractTypeUse, type ContractWireRef, type JsonValue } from "@kkdev92/tisilia-runtime";

type WireShape = ContractDocument["wires"][number]["shape"];
type DomainShape = ContractDocument["types"][number]["shape"];

/** Input control for a scalar: decides the editor, the placeholder and the helpers (now, generate, pickers). */
export type ScalarWidget =
  | "text"
  | "guid"
  | "date"
  | "time"
  | "datetime-offset"
  | "datetime-utc"
  | "datetime"
  | "duration"
  | "bytes"
  | "char"
  | "integer"
  | "decimal"
  | "float"
  | "boolean"
  | "enum"
  | "flags";

export interface Choice {
  readonly label: string;
  /** The JSON the choice stands for on the wire (a name string or a number). */
  readonly value: JsonValue;
  /** The enum member's name in the contract (what its documentation is listed under). */
  readonly member?: string;
  /** The enum member's integer value: the domain value a server default names. */
  readonly number?: string;
}

export interface ScalarNode {
  readonly kind: "scalar";
  /** JSON token the wire carries: a string or a number (enum names and numbers-as-strings are strings). */
  readonly token: "string" | "number";
  readonly widget: ScalarWidget;
  readonly grammarId: string;
  /** What the value looks like, for placeholders and tooltips. */
  readonly hint: string;
  /** A valid value for examples, where the kind has a fixed format (a Version, an IP address). */
  readonly example?: string;
  readonly typeLabel: string;
  /** The contract type (an enum's), for its documentation. */
  readonly typeId?: string;
  readonly choices?: readonly Choice[];
  readonly minLength?: number;
  readonly maxLength?: number;
}

export interface ObjectProperty {
  readonly name: string;
  readonly required: boolean;
  readonly node: InputNode;
}

export interface ObjectNode {
  readonly kind: "object";
  readonly typeLabel: string;
  /** The contract type, for its documentation (absent for an anonymous shape). */
  readonly typeId?: string;
  readonly properties: readonly ObjectProperty[];
  /** Entries beyond the declared properties: a dictionary's values, or captured extension data. */
  readonly additional?: { readonly key: ScalarNode; readonly value: InputNode };
  /** Properties the server does not know are rejected (true) or ignored (false). */
  readonly closed: boolean;
}

export interface VariantOption {
  readonly label: string;
  readonly tag: JsonValue;
  readonly node: InputNode;
}

export type InputNode =
  | ScalarNode
  | ObjectNode
  | { readonly kind: "boolean" }
  | { readonly kind: "null" }
  | { readonly kind: "literal"; readonly value: JsonValue }
  | { readonly kind: "array"; readonly typeLabel: string; readonly element: InputNode }
  | { readonly kind: "nullable"; readonly inner: InputNode }
  | { readonly kind: "choice"; readonly branches: readonly { readonly token: string; readonly node: InputNode }[] }
  | { readonly kind: "tagged"; readonly typeLabel: string; readonly typeId?: string; readonly discriminator: string; readonly variants: readonly VariantOption[] }
  | { readonly kind: "json" };

// ---------------------------------------------------------------- scalars

const integerRanges: Readonly<Record<string, readonly [string, string]>> = {
  int8: ["-128", "127"],
  uint8: ["0", "255"],
  int16: ["-32768", "32767"],
  uint16: ["0", "65535"],
  int32: ["-2147483648", "2147483647"],
  uint32: ["0", "4294967295"],
  int64: ["-9223372036854775808", "9223372036854775807"],
  uint64: ["0", "18446744073709551615"],
  int128: ["-170141183460469231731687303715884105728", "170141183460469231731687303715884105727"],
  uint128: ["0", "340282366920938463463374607431768211455"],
};

/** `tisilia.grammar.int64-string@0.1` → `int64`: the grammar's kind without namespace, version and key/string form. */
export function grammarKind(grammarId: string): string {
  const last = grammarId.slice(grammarId.lastIndexOf(".grammar.") >= 0 ? grammarId.lastIndexOf(".grammar.") + ".grammar.".length : 0).replace(/@.*$/, "");
  return last.replace(/-(key|string|named|string-named)$/, "").replace(/-string$/, "");
}

const primitiveGrammar: Readonly<Record<string, string>> = {
  "tisilia.boolean@0.1": "boolean",
  "tisilia.string@0.1": "string",
  "tisilia.char@0.1": "char",
  "tisilia.guid@0.1": "guid",
  "tisilia.bytes@0.1": "bytes",
  "tisilia.decimal@0.1": "decimal",
  "tisilia.float32@0.1": "float32",
  "tisilia.float64@0.1": "float64",
  "tisilia.date-only@0.1": "date-only",
  "tisilia.time-only@0.1": "time-only",
  "tisilia.datetime@0.1": "datetime",
  "tisilia.datetime-utc@0.1": "datetime-utc",
  "tisilia.datetime-unspecified@0.1": "datetime-unspecified",
  "tisilia.datetime-local-wire@0.1": "datetime-local-wire",
  "tisilia.datetime-offset@0.1": "datetime-offset",
  "tisilia.duration@0.1": "duration",
  "tisilia.json-value@0.1": "json",
};

/** The builtin scalars whose request input is a JSON number (an XML text of them is edited as one). */
const numberPrimitives: ReadonlySet<string> = new Set(["int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "decimal", "float32", "float64"].map((n) => `tisilia.${n}@0.1`));

function widgetOf(kind: string): { widget: ScalarWidget; hint: string; example?: string } {
  if (kind in integerRanges) {
    const [min, max] = integerRanges[kind]!;
    return { widget: "integer", hint: `${kind} · ${min} … ${max}` };
  }
  switch (kind) {
    case "guid":
      return { widget: "guid", hint: "e.g. 550e8400-e29b-41d4-a716-446655440000" };
    case "date-only":
      return { widget: "date", hint: "yyyy-MM-dd, e.g. 2026-10-02" };
    case "time-only":
      return { widget: "time", hint: "HH:mm:ss[.fffffff], e.g. 13:45:30" };
    case "datetime-offset":
      return { widget: "datetime-offset", hint: "e.g. 2026-10-02T13:45:30+09:00" };
    case "datetime":
      return { widget: "datetime", hint: "ISO date/time; Z = UTC, ±HH:mm = local wire, no suffix = unspecified" };
    case "datetime-utc":
      return { widget: "datetime-utc", hint: "e.g. 2026-10-02T04:45:30Z" };
    case "datetime-local-wire":
      // a DateTime of kind Local travels with the machine's offset
      return { widget: "datetime-offset", hint: "local time with offset, e.g. 2026-10-02T13:45:30+09:00" };
    case "datetime-unspecified":
      return { widget: "datetime", hint: "e.g. 2026-10-02T13:45:30" };
    case "duration":
      return { widget: "duration", hint: "[d.]HH:mm:ss[.fffffff], e.g. 1.02:03:04" };
    case "bytes":
      return { widget: "bytes", hint: "base64, e.g. AQID" };
    case "char":
      return { widget: "char", hint: "one character" };
    case "decimal":
      return { widget: "decimal", hint: "decimal, e.g. 12.34" };
    case "float32":
    case "float64":
    case "half":
      return { widget: "float", hint: `${kind}, e.g. 1.5` };
    case "big-integer":
      return { widget: "integer", hint: "integer of any size" };
    case "json-number":
      return { widget: "decimal", hint: "a JSON number" };
    case "boolean":
      return { widget: "boolean", hint: "true or false" };
    case "version":
      return { widget: "text", hint: "e.g. 1.2.3.4", example: "1.2.3.4" };
    case "ip-address":
      return { widget: "text", hint: "e.g. 192.0.2.1 or 2001:db8::1", example: "192.0.2.1" };
    case "ip-network":
      return { widget: "text", hint: "e.g. 192.0.2.0/24", example: "192.0.2.0/24" };
    case "uri-reference":
      return { widget: "text", hint: "a URI, e.g. https://example.com/a", example: "https://example.com/a" };
    case "rune":
      return { widget: "char", hint: "one Unicode scalar" };
    case "index":
      return { widget: "text", hint: "e.g. 3 or ^1", example: "3" };
    case "range":
      return { widget: "text", hint: "e.g. 1..^2", example: "1..^2" };
    default:
      return { widget: "text", hint: "text" };
  }
}

// ---------------------------------------------------------------- schema

/** Builds input nodes from a contract; recursive types resolve to the same node object (nodes are created before they are filled). */
export class FormSchema {
  private readonly wires: ReadonlyMap<string, WireShape>;
  private readonly types: ReadonlyMap<string, { readonly tsName: string; readonly clrIdentity: string; readonly shape: DomainShape }>;
  private readonly cache = new Map<string, InputNode>();

  constructor(private readonly document: ContractDocument) {
    this.wires = new Map(document.wires.map((w) => [w.id + "|" + w.direction, w.shape]));
    this.types = new Map(document.types.map((t) => [t.id, t]));
  }

  /** The form of a request body: the request wire of the body codec. */
  body(use: ContractTypeUse): InputNode {
    const codec = this.document.codecs.find((c) => c.id === use.codecId);
    const wire = codec?.capabilities.request?.wire;
    return wire === undefined ? { kind: "json" } : this.node(wire, use);
  }

  /** The shape of a response body: the response wire of the case's codec (what the server writes). */
  response(use: ContractTypeUse): InputNode {
    const codec = this.document.codecs.find((c) => c.id === use.codecId);
    const wire = codec?.capabilities.response?.wire;
    return wire === undefined ? { kind: "json" } : this.node(wire, use);
  }

  /** A route/query/header value: one scalar (enum members, kinds and ranges from the domain type, the format from the binder's grammar). */
  parameter(use: ContractTypeUse, grammarId?: string): ScalarNode {
    return this.scalarOfType(use.typeId, "string", grammarId);
  }

  /** Readable name of a type for labels (`UserPutRequest`, `int64[]`). */
  typeLabel(typeId: string): string {
    const type = this.types.get(typeId);
    if (type === undefined) {
      return typeId.replace(/^std\./, "");
    }
    if (type.shape.kind === "primitive") {
      return (primitiveGrammar[type.shape.primitiveId] ?? type.shape.primitiveId.replace(/^tisilia\./, "").replace(/@.*$/, "")).replace(/^json$/, "JSON");
    }
    if (type.shape.kind === "array") {
      return this.typeLabel(type.shape.element.typeId) + (type.shape.element.semanticNullable ? " | null" : "") + "[]";
    }
    return type.tsName;
  }

  private node(ref: ContractWireRef, use: ContractTypeUse | undefined): InputNode {
    const key = ref.wireId + "|" + ref.direction + "|" + (use?.typeId ?? "");
    const cached = this.cache.get(key);
    if (cached !== undefined) {
      return cached;
    }
    const shape = this.wires.get(ref.wireId + "|" + ref.direction);
    if (shape === undefined) {
      return { kind: "json" };
    }
    const domain = use === undefined ? undefined : this.unbrand(this.types.get(use.typeId)?.shape);
    switch (shape.kind) {
      case "literal":
        return this.remember(key, { kind: "literal", value: shape.value });
      case "null":
        return this.remember(key, { kind: "null" });
      case "boolean":
        return this.remember(key, { kind: "boolean" });
      case "lossless-json":
        return this.remember(key, { kind: "json" });
      case "string":
      case "number": {
        const scalar = use === undefined ? this.scalarOfGrammar(shape.grammarId, shape.kind) : this.scalarOfType(use.typeId, shape.kind, shape.grammarId);
        const lengths = shape.kind === "string" ? { ...(shape.minUtf16Length !== undefined ? { minLength: shape.minUtf16Length } : {}), ...(shape.maxUtf16Length !== undefined ? { maxLength: shape.maxUtf16Length } : {}) } : {};
        return this.remember(key, { ...scalar, ...lengths });
      }
      case "array": {
        const elementUse = domain?.kind === "array" ? domain.element : undefined;
        const node = { kind: "array", typeLabel: use === undefined ? "array" : this.typeLabel(use.typeId), element: undefined as unknown as InputNode } as { kind: "array"; typeLabel: string; element: InputNode };
        this.cache.set(key, node);
        node.element = this.node(shape.element, elementUse);
        return node;
      }
      case "object": {
        const node: { kind: "object"; typeLabel: string; typeId?: string; properties: ObjectProperty[]; additional?: { readonly key: ScalarNode; readonly value: InputNode }; closed: boolean } = {
          kind: "object",
          typeLabel: use === undefined ? "object" : this.typeLabel(use.typeId),
          ...(use !== undefined ? { typeId: use.typeId } : {}),
          properties: [],
          closed: shape.additional.kind === "reject",
        };
        this.cache.set(key, node);
        const domainProps = domain?.kind === "object" ? domain.properties : [];
        for (const p of shape.properties) {
          const domainProp = domainProps.find((d) => d.name === p.name);
          node.properties.push({ name: p.name, required: p.presence === "required", node: this.node(p.wire, domainProp?.use) });
        }
        if (shape.additional.kind === "capture" && shape.additional.wire !== undefined) {
          const valueUse = domain?.kind === "map" ? domain.value : domain?.kind === "object" && domain.extension.kind === "capture" ? domain.extension.value : undefined;
          const keyNode = domain?.kind === "map" ? this.keyOfType(domain.key) : this.scalarOfGrammar("tisilia.grammar.string@0.1", "string");
          node.additional = { key: keyNode, value: this.node(shape.additional.wire, valueUse) };
        }
        return node;
      }
      case "token-union": {
        const nonNull = shape.branches.filter((b) => b.token !== "null");
        const hasNull = nonNull.length < shape.branches.length;
        // numbers the profile also reads from strings are one number field; null is a toggle, not a branch
        const preferred = nonNull.find((b) => b.token === "number" && nonNull.some((o) => o.token === "string")) ?? (nonNull.length === 1 ? nonNull[0] : undefined);
        if (preferred !== undefined) {
          const inner = this.node(preferred.wire, use === undefined ? undefined : this.withoutNull(use));
          return this.remember(key, hasNull ? { kind: "nullable", inner } : inner);
        }
        const choice = { kind: "choice", branches: [] as { token: string; node: InputNode }[] } as { kind: "choice"; branches: { token: string; node: InputNode }[] };
        const node: InputNode = hasNull ? { kind: "nullable", inner: choice } : choice;
        this.cache.set(key, node);
        for (const b of nonNull) {
          choice.branches.push({ token: b.token, node: this.node(b.wire, use) });
        }
        return node;
      }
      // an XML body is edited as its value, which the codec writes as XML: a class's members are its properties, a collection's items
      // its elements, and a text the scalar or enum it carries
      case "xml-text": {
        if (domain?.kind === "primitive" && domain.primitiveId === "tisilia.boolean@0.1") {
          return this.remember(key, { kind: "boolean" });
        }
        const token = domain?.kind === "primitive" && numberPrimitives.has(domain.primitiveId) ? "number" : "string";
        return this.remember(key, use === undefined ? this.scalarOfGrammar(shape.grammarId, token) : this.scalarOfType(use.typeId, token));
      }
      case "xml-element": {
        const node: { kind: "object"; typeLabel: string; typeId?: string; properties: ObjectProperty[]; closed: boolean } = {
          kind: "object",
          typeLabel: use === undefined ? "object" : this.typeLabel(use.typeId),
          ...(use !== undefined ? { typeId: use.typeId } : {}),
          properties: [],
          closed: true,
        };
        this.cache.set(key, node);
        const domainProps = domain?.kind === "object" ? domain.properties : [];
        for (const m of [...shape.attributes, ...shape.elements, ...(shape.text === undefined ? [] : [shape.text])]) {
          const domainProp = domainProps.find((d) => d.name === m.property);
          let child: InputNode;
          if (m.repeated === true) {
            const collection = domainProp === undefined ? undefined : this.unbrand(this.types.get(domainProp.use.typeId)?.shape);
            const itemUse = collection?.kind === "array" ? collection.element : undefined;
            const item = this.node(m.wire, itemUse === undefined ? undefined : this.withoutNull(itemUse));
            child = { kind: "array", typeLabel: domainProp === undefined ? "array" : this.typeLabel(domainProp.use.typeId), element: itemUse?.semanticNullable === true ? { kind: "nullable", inner: item } : item };
          } else {
            const inner = this.node(m.wire, domainProp === undefined ? undefined : this.withoutNull(domainProp.use));
            child = domainProp?.use.semanticNullable === true ? { kind: "nullable", inner } : inner;
          }
          node.properties.push({ name: m.property, required: domainProp?.presence === "required", node: child });
        }
        return node;
      }
      case "xml-items": {
        const elementUse = domain?.kind === "array" ? domain.element : undefined;
        const node = { kind: "array", typeLabel: use === undefined ? "array" : this.typeLabel(use.typeId), element: undefined as unknown as InputNode } as { kind: "array"; typeLabel: string; element: InputNode };
        this.cache.set(key, node);
        const item = this.node(shape.item.wire, elementUse === undefined ? undefined : this.withoutNull(elementUse));
        node.element = elementUse?.semanticNullable === true ? { kind: "nullable", inner: item } : item;
        return node;
      }
      case "tagged-union": {
        const variantsOfDomain = domain?.kind === "union" ? domain.variants : [];
        const node = { kind: "tagged", typeLabel: use === undefined ? "union" : this.typeLabel(use.typeId), ...(use !== undefined ? { typeId: use.typeId } : {}), discriminator: shape.discriminator, variants: [] as VariantOption[] } as { kind: "tagged"; typeLabel: string; typeId?: string; discriminator: string; variants: VariantOption[] };
        this.cache.set(key, node);
        shape.variants.forEach((v, i) => {
          const tag: JsonValue = v.tag.kind === "string" ? { kind: "string", value: v.tag.value } : { kind: "number", text: v.tag.text };
          const domainVariant = variantsOfDomain[i];
          const label = domainVariant === undefined ? (v.tag.kind === "string" ? v.tag.value : v.tag.text) : `${v.tag.kind === "string" ? v.tag.value : v.tag.text} · ${this.typeLabel(domainVariant.use.typeId)}`;
          node.variants.push({ label, tag, node: this.node(v.wire, domainVariant?.use) });
        });
        return node;
      }
    }
  }

  private remember(key: string, node: InputNode): InputNode {
    this.cache.set(key, node);
    return node;
  }

  private withoutNull(use: ContractTypeUse): ContractTypeUse {
    return use.semanticNullable ? { ...use, semanticNullable: false } : use;
  }

  private unbrand(shape: DomainShape | undefined): DomainShape | undefined {
    let s = shape;
    for (let i = 0; i < 8 && s?.kind === "brand"; i++) {
      s = this.types.get(s.base.typeId)?.shape;
    }
    return s;
  }

  /** A dictionary key: always a JSON property name, in the key grammar .NET writes (`True`/`False` for booleans, a Version's text). */
  private keyOfType(use: ContractTypeUse): ScalarNode {
    const codec = this.document.codecs.find((c) => c.id === use.codecId);
    const grammarId = codec?.capabilities.requestKey?.grammarId ?? codec?.capabilities.responseKey?.grammarId;
    const node = this.scalarOfType(use.typeId, "string", grammarId);
    if (node.widget === "boolean") {
      return { ...node, widget: "enum", hint: "True or False", choices: [{ label: "True", value: { kind: "string", value: "True" } }, { label: "False", value: { kind: "string", value: "False" } }] };
    }
    return node;
  }

  private scalarOfType(typeId: string, token: "string" | "number", grammarId?: string): ScalarNode {
    const shape = this.unbrand(this.types.get(typeId)?.shape);
    const typeLabel = this.typeLabel(typeId);
    if (shape?.kind === "enum") {
      // names on a string wire (`enum-name`), values on a number wire; a flags enum combines them
      const byName = token === "string";
      const choices = shape.members.map((m) => ({ label: `${m.serializedName ?? m.name} (${m.value})`, value: byName ? ({ kind: "string", value: m.serializedName ?? m.name } as JsonValue) : ({ kind: "number", text: m.value } as JsonValue), member: m.name, number: m.value }));
      return { kind: "scalar", token, widget: shape.flags ? "flags" : "enum", grammarId: grammarId ?? "", hint: shape.flags ? "one or more members" : "one member", typeLabel, typeId, choices };
    }
    if (shape?.kind === "primitive") {
      const kind = primitiveGrammar[shape.primitiveId] ?? grammarKind(shape.primitiveId);
      const integer = /^tisilia\.(u?int(8|16|32|64))@/.exec(shape.primitiveId)?.[1];
      const { widget, hint, example } = widgetOf(integer ?? (kind === "string" && grammarId !== undefined ? grammarKind(grammarId) : kind));
      return { kind: "scalar", token, widget: kind === "json" ? "text" : widget, grammarId: grammarId ?? "", hint, typeLabel, ...(example !== undefined ? { example } : {}) };
    }
    return grammarId !== undefined ? { ...this.scalarOfGrammar(grammarId, token), typeLabel } : { kind: "scalar", token, widget: "text", grammarId: "", hint: "text", typeLabel };
  }

  private scalarOfGrammar(grammarId: string, token: "string" | "number"): ScalarNode {
    const kind = grammarKind(grammarId);
    const { widget, hint, example } = widgetOf(kind === "enum-name" || kind === "enum-number" || kind === "enum" ? "text" : kind);
    return { kind: "scalar", token, widget, grammarId, hint, typeLabel: kind, ...(example !== undefined ? { example } : {}) };
  }
}

// ---------------------------------------------------------------- values

/** Mutable JSON AST the editors work on; structurally a runtime `JsonValue`. */
export type FormJson =
  | { kind: "null" }
  | { kind: "boolean"; value: boolean }
  | { kind: "string"; value: string }
  | { kind: "number"; text: string }
  | { kind: "array"; items: FormJson[] }
  | { kind: "object"; entries: { name: string; value: FormJson }[] };

export function toFormJson(value: JsonValue): FormJson {
  switch (value.kind) {
    case "null":
      return { kind: "null" };
    case "boolean":
      return { kind: "boolean", value: value.value };
    case "string":
      return { kind: "string", value: value.value };
    case "number":
      return { kind: "number", text: value.text };
    case "array":
      return { kind: "array", items: value.items.map(toFormJson) };
    case "object":
      return { kind: "object", entries: value.entries.map((e) => ({ name: e.name, value: toFormJson(e.value) })) };
  }
}

const numberLexeme = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$/;

/** The AST as JSON a parser accepts: a number field whose text is not a number lexeme becomes a string (the codec then says why). */
export function sanitize(value: FormJson): JsonValue {
  switch (value.kind) {
    case "number":
      return numberLexeme.test(value.text) ? value : { kind: "string", value: value.text };
    case "array":
      return { kind: "array", items: value.items.map(sanitize) };
    case "object":
      return { kind: "object", entries: value.entries.map((e) => ({ name: e.name, value: sanitize(e.value) })) };
    default:
      return value;
  }
}

/** Indented JSON text of an AST; numbers keep their exact lexeme (no rounding through `number`). */
export function prettyJson(value: JsonValue, indent = "  "): string {
  const write = (v: JsonValue, depth: number): string => {
    switch (v.kind) {
      case "array":
        if (v.items.length === 0) {
          return "[]";
        }
        return "[\n" + v.items.map((item) => indent.repeat(depth + 1) + write(item, depth + 1)).join(",\n") + "\n" + indent.repeat(depth) + "]";
      case "object":
        if (v.entries.length === 0) {
          return "{}";
        }
        return "{\n" + v.entries.map((e) => indent.repeat(depth + 1) + writeJson({ kind: "string", value: e.name }) + ": " + write(e.value, depth + 1)).join(",\n") + "\n" + indent.repeat(depth) + "}";
      default:
        return writeJson(v);
    }
  };
  return write(value, 0);
}

/** Parses editor text into a form value, or says where it is not JSON. */
export function parseFormJson(text: string): { value: FormJson } | { error: string } {
  try {
    return { value: toFormJson(parseJson(text)) };
  } catch (error) {
    return { error: error instanceof Error ? error.message : String(error) };
  }
}

// ---------------------------------------------------------------- examples

export interface ExampleOptions {
  /** Include optional properties down to this depth (deeper levels get required ones only). */
  readonly optionalDepth?: number;
  readonly now?: Date;
  readonly uuid?: () => string;
}

/** A value of the right shape for each field: today's date for dates, a fresh GUID, the first enum member, one array item. */
export function exampleOf(node: InputNode, options: ExampleOptions = {}, depth = 0): FormJson {
  const optionalDepth = options.optionalDepth ?? 2;
  switch (node.kind) {
    case "literal":
      return toFormJson(node.value);
    case "null":
      return { kind: "null" };
    case "boolean":
      return { kind: "boolean", value: false };
    case "json":
      return { kind: "object", entries: [] };
    case "nullable":
      // a required self-reference (a linked node's next) ends in null
      return depth >= 6 ? { kind: "null" } : exampleOf(node.inner, options, depth);
    case "choice":
      return node.branches.length === 0 || depth >= 8 ? { kind: "null" } : exampleOf(node.branches[0]!.node, options, depth);
    case "scalar":
      return exampleScalar(node, options);
    case "array":
      return depth >= 6 ? { kind: "array", items: [] } : { kind: "array", items: [exampleOf(node.element, options, depth + 1)] };
    case "tagged":
      return node.variants.length === 0 ? { kind: "object", entries: [] } : exampleOf(node.variants[0]!.node, options, depth);
    case "object": {
      const entries: { name: string; value: FormJson }[] = [];
      for (const p of node.properties) {
        if (p.required || (depth < optionalDepth && !cyclic(p.node))) {
          entries.push({ name: p.name, value: exampleOf(p.node, options, depth + 1) });
        }
      }
      if (node.properties.length === 0 && node.additional !== undefined && depth < 6) {
        const key = exampleScalar(node.additional.key, options);
        entries.push({ name: key.kind === "string" ? key.value : key.kind === "number" ? key.text : "key", value: exampleOf(node.additional.value, options, depth + 1) });
      }
      return { kind: "object", entries };
    }
  }
}

function childrenOf(node: InputNode): InputNode[] {
  switch (node.kind) {
    case "array":
      return [node.element];
    case "nullable":
      return [node.inner];
    case "object":
      return [...node.properties.map((p) => p.node), ...(node.additional !== undefined ? [node.additional.value] : [])];
    case "tagged":
      return node.variants.map((v) => v.node);
    case "choice":
      return node.branches.map((b) => b.node);
    default:
      return [];
  }
}

const cycles = new WeakMap<InputNode, boolean>();

/**
 * Whether a node reaches a cycle (a tree's children, a linked node's next): such optional properties are left out of examples.
 * Depth-first with the path on a stack, so a type used twice (two Money fields) is not mistaken for recursion.
 */
export function cyclic(start: InputNode): boolean {
  const known = cycles.get(start);
  if (known !== undefined) {
    return known;
  }
  const onPath = new Set<InputNode>();
  const visit = (node: InputNode): boolean => {
    if (onPath.has(node)) {
      return true;
    }
    const cached = cycles.get(node);
    if (cached !== undefined) {
      return cached;
    }
    onPath.add(node);
    const found = childrenOf(node).some(visit);
    onPath.delete(node);
    if (!found) {
      cycles.set(node, false);
    }
    return found;
  };
  const result = visit(start);
  cycles.set(start, result);
  return result;
}

export function newUuid(): string {
  const c = globalThis.crypto;
  if (typeof c?.randomUUID === "function") {
    return c.randomUUID();
  }
  // randomUUID is [SecureContext]; getRandomValues is not (an Explorer opened over plain http from a LAN address)
  const b = c.getRandomValues(new Uint8Array(16));
  b[6] = (b[6]! & 0x0f) | 0x40;
  b[8] = (b[8]! & 0x3f) | 0x80;
  const h = [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

const pad = (n: number, w = 2): string => String(n).padStart(w, "0");

/** Current time in the wire formats: `date`, `time`, `offset` (local), `utc`, `local` (no offset). */
export function nowText(widget: ScalarWidget, now: Date = new Date()): string {
  const date = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  const time = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
  switch (widget) {
    case "date":
      return date;
    case "time":
      return time;
    case "datetime-utc":
      return now.toISOString().replace(/\.\d{3}Z$/, "Z");
    case "datetime-offset": {
      const minutes = -now.getTimezoneOffset();
      const sign = minutes < 0 ? "-" : "+";
      return `${date}T${time}${sign}${pad(Math.floor(Math.abs(minutes) / 60))}:${pad(Math.abs(minutes) % 60)}`;
    }
    case "datetime":
      return `${date}T${time}`;
    default:
      return "";
  }
}

function exampleScalar(node: ScalarNode, options: ExampleOptions): FormJson {
  const text = (value: string): FormJson => (node.token === "number" ? { kind: "number", text: value } : { kind: "string", value });
  if (node.choices !== undefined && node.choices.length > 0) {
    return toFormJson(node.choices[0]!.value);
  }
  if (node.example !== undefined) {
    return text(node.example);
  }
  switch (node.widget) {
    case "guid":
      return text((options.uuid ?? newUuid)());
    case "date":
    case "time":
    case "datetime-offset":
    case "datetime-utc":
    case "datetime":
      return text(nowText(node.widget, options.now));
    case "duration":
      return text("00:01:00");
    case "bytes":
      return text("AQID");
    case "char":
      return text("a");
    case "integer":
    case "decimal":
    case "float":
      return text("0");
    case "boolean":
      return { kind: "boolean", value: false };
    default:
      return text(node.minLength !== undefined && node.minLength > 6 ? "x".repeat(node.minLength) : "string");
  }
}

// ---------------------------------------------------------------- editing helpers

export function findEntry(value: FormJson | undefined, name: string): { name: string; value: FormJson } | undefined {
  return value?.kind === "object" ? value.entries.find((e) => e.name === name) : undefined;
}

/** The variant an object currently is, by its discriminator (or the first variant). */
export function variantIndex(node: Extract<InputNode, { kind: "tagged" }>, value: FormJson | undefined): number {
  const tag = findEntry(value, node.discriminator)?.value;
  const index = node.variants.findIndex((v) => (v.tag.kind === "string" ? tag?.kind === "string" && tag.value === v.tag.value : v.tag.kind === "number" && tag?.kind === "number" && tag.text === v.tag.text));
  return index < 0 ? 0 : index;
}

/** Scalar editor text of a JSON value (what an input shows). */
export function scalarText(value: FormJson | undefined): string {
  if (value === undefined) {
    return "";
  }
  switch (value.kind) {
    case "string":
      return value.value;
    case "number":
      return value.text;
    case "boolean":
      return value.value ? "true" : "false";
    case "null":
      return "";
    default:
      return writeJson(sanitize(value));
  }
}

/** JSON value of a scalar editor's text: numbers stay lexemes, strings stay strings. */
export function scalarValue(node: ScalarNode, text: string): FormJson {
  return node.token === "number" ? { kind: "number", text: text.trim() } : { kind: "string", value: text };
}

/** Error messages by JSON pointer below the body (`/body/items/0/name` → `/items/0/name`). */
export function errorsByPath(errors: readonly { readonly path: string; readonly message: string }[], prefix = "/body"): Map<string, string> {
  const map = new Map<string, string>();
  for (const e of errors) {
    if (e.path === prefix || e.path.startsWith(prefix + "/")) {
      map.set(e.path.slice(prefix.length), e.message);
    }
  }
  return map;
}
