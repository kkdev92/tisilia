/// <reference types="node" />
// Isolated conformance runner, TypeScript side: UTF-8 JSON Lines on stdin/stdout, one
// tisilia.runner-message per line, strictly one response per request. Adapter ids resolve to codecs of the
// generated registry; compare resolves through the contract's equivalences. Logs go to stderr only.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, renameSync } from "node:fs";
import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { createCodecContext, type Codec, type CodecContext } from "../codec/abi.js";
import { CodecError } from "../codec/errors.js";
import { TisiliaMap, type KeyComparer } from "../codec/map.js";
import type { CodecRegistry } from "../codec/registry.js";
import { type JsonEntry, type JsonValue, jsonNull } from "../json/ast.js";
import { parseJson, JsonParseError } from "../json/parser.js";
import { decimalFromString, formatDecimal, isDecimal, type Decimal } from "../primitives/decimal.js";
import { formatDotnetShortest, parseFloat32Lexeme, parseFloat64Lexeme } from "../primitives/float.js";
import { int64, uint64 } from "../primitives/integers.js";
import { decodeBase64, encodeBase64, guid } from "../primitives/text.js";
import {
  formatDateOnly,
  formatDateTimeLocalWire,
  formatDateTimeOffset,
  formatDateTimeUnspecified,
  formatDateTimeUtc,
  formatDuration,
  formatTimeOnly,
  parseDateOnly,
  parseDateTimeLocalWire,
  parseDateTimeOffset,
  parseDateTimeUnspecified,
  parseDateTimeUtc,
  parseDuration,
  parseTimeOnly,
} from "../primitives/datetime.js";

export interface RunnerOptions {
  readonly registry: CodecRegistry;
  /** Path of the exported contract the registry was generated from (drives the domain factory/projection). */
  readonly contractPath: string;
  /** Resolves module exports (generated registry.js `moduleExport`); needed for module oracles. */
  readonly moduleExport?: (moduleId: string, exportName: string) => unknown;
  /** Where to write the environment report before serving (TISILIA_RUNNER_ENV). */
  readonly environmentPath?: string;
  readonly sessionId?: string;
  readonly maxRecordBytes?: number;
  readonly input?: NodeJS.ReadableStream;
  readonly output?: NodeJS.WritableStream;
}

type Action =
  | "dotnet-read"
  | "dotnet-write"
  | "ts-encode-request"
  | "ts-decode-response"
  | "dotnet-read-key"
  | "dotnet-write-key"
  | "ts-encode-key"
  | "ts-decode-key"
  | "validate-domain"
  | "parse-request-input"
  | "compare"
  | "ts-project"
  | "dotnet-project";

const actions: ReadonlySet<string> = new Set<Action>([
  "dotnet-read",
  "dotnet-write",
  "ts-encode-request",
  "ts-decode-response",
  "dotnet-read-key",
  "dotnet-write-key",
  "ts-encode-key",
  "ts-decode-key",
  "validate-domain",
  "parse-request-input",
  "compare",
  "ts-project",
  "dotnet-project",
]);

type FailureCode = "invalid-input" | "contract" | "codec" | "limit" | "timeout" | "cancelled" | "internal";

class RunnerFailure extends Error {
  constructor(
    readonly code: FailureCode,
    readonly safeMessageId: string,
    readonly path = "",
    /** The request id when it was already known (protocol failures after the ids were read). */
    readonly requestId?: string,
  ) {
    super(safeMessageId);
    this.name = "RunnerFailure";
  }
}

interface Request {
  readonly sessionId: string;
  readonly requestId: string;
  readonly action: Action;
  readonly adapterId: string;
  readonly profileId: string;
  readonly context: ReadonlyMap<string, string>;
  readonly inputs: readonly JsonValue[];
}

const idPattern = /^[A-Za-z][A-Za-z0-9_.:@/\-]{0,159}$/;

function safeId(raw: string): string {
  let s = raw.replace(/[^A-Za-z0-9_.:@/\-]/g, "-");
  if (s.length === 0 || !/^[A-Za-z]/.test(s)) {
    s = "m." + s;
  }
  return s.length > 160 ? s.slice(0, 160) : s;
}

// ---------------------------------------------------------------- contract model (control document)

interface TypeUse {
  readonly typeId: string;
  readonly codecId: string;
  readonly semanticNullable: boolean;
}

interface DomainProperty {
  readonly name: string;
  readonly use: TypeUse;
  readonly presence: "required" | "optional";
}

type Shape =
  | { readonly kind: "primitive"; readonly primitiveId: string }
  | { readonly kind: "enum"; readonly underlyingPrimitiveId: string; readonly flags: boolean; readonly members: readonly { name: string; value: string; serializedName?: string }[] }
  | { readonly kind: "object"; readonly properties: readonly DomainProperty[]; readonly extension: { kind: "none" } | { kind: "capture"; value: TypeUse } }
  | { readonly kind: "array"; readonly element: TypeUse }
  | { readonly kind: "map"; readonly key: TypeUse; readonly value: TypeUse; readonly comparerId: string }
  | { readonly kind: "brand"; readonly base: TypeUse }
  | { readonly kind: "union"; readonly variants: readonly { tag: string; use: TypeUse }[] };

interface Model {
  readonly id: string;
  readonly shape: Shape;
}

interface Impl {
  readonly kind: "builtin" | "module";
  readonly id?: string;
  readonly moduleId?: string;
  readonly exportName?: string;
}

interface Equivalence {
  readonly id: string;
  readonly domainTypeId: string;
  readonly scope: string;
  readonly typescriptOracle: Impl;
}

/** A behavior projection: received domain AST → the value the server constructs, implemented on both sides. */
interface Projection {
  readonly id: string;
  readonly sourceTypeId: string;
  readonly targetTypeId: string;
  readonly typescriptImplementation: Impl;
}

interface Capabilities {
  readonly request?: { equivalenceId: string };
  readonly response?: { equivalenceId: string };
  readonly requestKey?: { equivalenceId: string };
  readonly responseKey?: { equivalenceId: string };
}

interface CodecEntry {
  readonly id: string;
  readonly typeId: string;
  readonly capabilities: Capabilities;
}

interface ContractView {
  readonly types: ReadonlyMap<string, Model>;
  readonly codecs: ReadonlyMap<string, CodecEntry>;
  readonly equivalences: ReadonlyMap<string, Equivalence>;
  readonly comparers: ReadonlyMap<string, { bindingId: string }>;
  readonly bindings: ReadonlyMap<string, { implementation: Impl }>;
  readonly projections: ReadonlyMap<string, Projection>;
}

function loadContract(path: string): ContractView {
  // control numbers only (SV01), so the native parser is exact here
  const doc = JSON.parse(readFileSync(path, "utf8")) as {
    types: Model[];
    codecs: CodecEntry[];
    equivalences: Equivalence[];
    comparers: { id: string; bindingId: string }[];
    bindings: { id: string; implementation: Impl }[];
    projections?: Projection[];
  };
  return {
    types: new Map(doc.types.map((t) => [t.id, t])),
    codecs: new Map(doc.codecs.map((c) => [c.id, c])),
    equivalences: new Map(doc.equivalences.map((e) => [e.id, e])),
    comparers: new Map(doc.comparers.map((c) => [c.id, c])),
    bindings: new Map(doc.bindings.map((b) => [b.id, b])),
    projections: new Map((doc.projections ?? []).map((p) => [p.id, p])),
  };
}

/**
 * Applies a projection to the value(s) at a slash-separated path of member names (`*` = every array item; "" = root); the
 * same rule as the C# runner, so both runners project the same locations of a case root.
 */
function applyAt(value: JsonValue, segments: readonly string[], index: number, transform: (v: JsonValue) => JsonValue): JsonValue {
  if (index === segments.length) {
    return transform(value);
  }
  const segment = segments[index]!;
  if (value.kind === "array" && segment === "*") {
    return { kind: "array", items: value.items.map((item) => applyAt(item, segments, index + 1, transform)) };
  }
  if (value.kind === "object") {
    return { kind: "object", entries: value.entries.map((e) => (e.name === segment ? { name: e.name, value: applyAt(e.value, segments, index + 1, transform) } : e)) };
  }
  return value;
}

function scalarName(primitiveId: string): string {
  return primitiveId.startsWith("tisilia.") && primitiveId.endsWith("@0.1") ? primitiveId.slice("tisilia.".length, -"@0.1".length) : primitiveId;
}

// ---------------------------------------------------------------- domain AST ⇄ TypeScript values (contract-driven)

class DomainBridge {
  constructor(
    private readonly contract: ContractView,
    private readonly registry: CodecRegistry,
  ) {}

  private model(typeId: string): Model {
    const m = this.contract.types.get(typeId);
    if (m === undefined) {
      throw new RunnerFailure("contract", "domain.unknown-type");
    }
    return m;
  }

  /** Builds the TypeScript value a codec expects. Mismatching AST kinds yield the natural JS value so that validators can reject them. */
  fromAst(ast: JsonValue, use: TypeUse): unknown {
    const model = this.model(use.typeId);
    const shape = model.shape;
    if (ast.kind === "null") {
      // a JSON null is a value of the lossless json-value domain, not an absent member
      return shape.kind === "primitive" && scalarName(shape.primitiveId) === "json-value" ? ast : null;
    }
    switch (shape.kind) {
      case "primitive":
        return this.scalarFromAst(ast, scalarName(shape.primitiveId));
      case "enum": {
        if (ast.kind === "number") {
          const big = shape.underlyingPrimitiveId.includes("int64");
          try {
            const v = BigInt(ast.text);
            return big ? v : Number(v);
          } catch {
            return natural(ast);
          }
        }
        return natural(ast);
      }
      case "object": {
        if (ast.kind !== "object") {
          return natural(ast);
        }
        const out: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
        for (const entry of ast.entries) {
          const prop = shape.properties.find((p) => p.name === entry.name);
          if (prop !== undefined) {
            out[entry.name] = this.fromAst(entry.value, prop.use);
          } else if (entry.name === "extensions" && shape.extension.kind === "capture" && entry.value.kind === "object") {
            const map = new Map<string, unknown>();
            for (const e of entry.value.entries) {
              map.set(e.name, this.fromAst(e.value, shape.extension.value));
            }
            out["extensions"] = map;
          } else {
            out[entry.name] = natural(entry.value);
          }
        }
        return out;
      }
      case "array":
        return ast.kind === "array" ? ast.items.map((i) => this.fromAst(i, shape.element)) : natural(ast);
      case "map": {
        if (ast.kind !== "object") {
          return natural(ast);
        }
        const keyCodec = this.registry.get(shape.key.codecId);
        const ctx = createCodecContext();
        const comparer = this.comparerOf(shape.comparerId);
        const encode = (k: unknown): string => {
          if (keyCodec.encodeKey === undefined) {
            throw new RunnerFailure("contract", "domain.key-codec-without-encode-key");
          }
          return keyCodec.encodeKey(k, ctx);
        };
        const map = new TisiliaMap<unknown, unknown>(comparer, encode);
        for (const entry of ast.entries) {
          if (keyCodec.decodeKey === undefined) {
            throw new RunnerFailure("contract", "domain.key-codec-without-decode-key");
          }
          map.set(keyCodec.decodeKey(entry.name, ctx), this.fromAst(entry.value, shape.value));
        }
        return map;
      }
      case "brand":
        return this.fromAst(ast, shape.base);
      case "union": {
        if (ast.kind !== "object") {
          return natural(ast);
        }
        for (const variant of shape.variants) {
          const vm = this.model(variant.use.typeId);
          if (vm.shape.kind === "object" && vm.shape.properties.length > 0) {
            const tag = findEntryValue(ast, vm.shape.properties[0]!.name);
            if (tag !== undefined && tagText(tag) === variant.tag) {
              return this.fromAst(ast, variant.use);
            }
          }
        }
        return natural(ast);
      }
    }
  }

  private comparerOf(comparerId: string): KeyComparer {
    const comparer = this.contract.comparers.get(comparerId);
    const binding = comparer === undefined ? undefined : this.contract.bindings.get(comparer.bindingId);
    const id = binding?.implementation.kind === "builtin" ? binding.implementation.id : undefined;
    if (id === "tisilia.comparer.ordinal-ignore-case@0.1") {
      return "ordinal-ignore-case";
    }
    if (id === "tisilia.comparer.structural@0.1") {
      return "structural";
    }
    return "ordinal";
  }

  private scalarFromAst(ast: JsonValue, name: string): unknown {
    try {
      switch (name) {
        case "string":
        case "char":
          return ast.kind === "string" ? ast.value : natural(ast);
        case "boolean":
          return ast.kind === "boolean" ? ast.value : natural(ast);
        case "guid":
          return ast.kind === "string" ? guid(ast.value) : natural(ast);
        case "bytes":
          return ast.kind === "string" ? decodeBase64(ast.value, "") : natural(ast);
        case "json-value":
          return ast;
        case "int8":
        case "uint8":
        case "int16":
        case "uint16":
        case "int32":
        case "uint32":
          return ast.kind === "number" ? Number(ast.text) : natural(ast);
        case "int64":
          return ast.kind === "number" ? int64(BigInt(ast.text)) : natural(ast);
        case "uint64":
          return ast.kind === "number" ? uint64(BigInt(ast.text)) : natural(ast);
        case "decimal":
          return ast.kind === "number" ? decimalFromString(ast.text) : natural(ast);
        case "float32":
          return ast.kind === "number" ? parseFloat32Lexeme(ast.text, "") : ast.kind === "string" ? namedFloat(ast.value) : natural(ast);
        case "float64":
          return ast.kind === "number" ? parseFloat64Lexeme(ast.text, "") : ast.kind === "string" ? namedFloat(ast.value) : natural(ast);
        case "date-only":
          return ast.kind === "string" ? parseDateOnly(ast.value, "") : natural(ast);
        case "time-only":
          return ast.kind === "string" ? parseTimeOnly(ast.value, "") : natural(ast);
        case "datetime-utc":
          return ast.kind === "string" ? parseDateTimeUtc(ast.value, "") : natural(ast);
        case "datetime-unspecified":
          return ast.kind === "string" ? parseDateTimeUnspecified(ast.value, "") : natural(ast);
        case "datetime-local-wire":
          return ast.kind === "string" ? parseDateTimeLocalWire(ast.value, "") : natural(ast);
        case "datetime-offset":
          return ast.kind === "string" ? parseDateTimeOffset(ast.value, "") : natural(ast);
        case "duration":
          return ast.kind === "string" ? parseDuration(ast.value, "") : natural(ast);
        default:
          throw new RunnerFailure("contract", "domain.unknown-scalar");
      }
    } catch (e) {
      if (e instanceof RunnerFailure) {
        throw e;
      }
      // an unparseable canonical text: hand the raw value to the validator, which is what must reject it
      return natural(ast);
    }
  }

  /** Projects a TypeScript value to the domain AST following the contract model. */
  toAst(value: unknown, use: TypeUse, path = ""): JsonValue {
    if (value === null || value === undefined) {
      return jsonNull;
    }
    const model = this.model(use.typeId);
    const shape = model.shape;
    switch (shape.kind) {
      case "primitive":
        return this.scalarToAst(value, scalarName(shape.primitiveId), path);
      case "enum":
        if (typeof value === "bigint" || (typeof value === "number" && Number.isInteger(value))) {
          return { kind: "number", text: value.toString() };
        }
        throw new RunnerFailure("contract", "projection.enum-not-integer", path);
      case "object": {
        if (typeof value !== "object" || Array.isArray(value)) {
          throw new RunnerFailure("contract", "projection.not-an-object", path);
        }
        const record = value as Record<string, unknown>;
        const entries: JsonEntry[] = [];
        for (const prop of shape.properties) {
          if (!Object.prototype.hasOwnProperty.call(record, prop.name) || record[prop.name] === undefined) {
            continue;
          }
          entries.push({ name: prop.name, value: this.toAst(record[prop.name], prop.use, path + "/" + prop.name) });
        }
        if (shape.extension.kind === "capture" && record["extensions"] instanceof Map && (record["extensions"] as Map<string, unknown>).size > 0) {
          const ext: JsonEntry[] = [];
          for (const [k, v] of record["extensions"] as Map<string, unknown>) {
            ext.push({ name: k, value: this.toAst(v, shape.extension.value, path + "/" + k) });
          }
          entries.push({ name: "extensions", value: { kind: "object", entries: ext } });
        }
        return { kind: "object", entries };
      }
      case "array": {
        if (!Array.isArray(value)) {
          throw new RunnerFailure("contract", "projection.not-an-array", path);
        }
        const items: JsonValue[] = [];
        for (let i = 0; i < value.length; i++) {
          items.push(this.toAst(value[i], shape.element, path + "/" + i));
        }
        return { kind: "array", items };
      }
      case "map": {
        if (!(value instanceof TisiliaMap)) {
          throw new RunnerFailure("contract", "projection.not-a-map", path);
        }
        const keyCodec = this.registry.get(shape.key.codecId);
        const ctx = createCodecContext();
        const entries: JsonEntry[] = [];
        for (const [k, v] of value.entries()) {
          const name = keyCodec.encodeKey !== undefined ? keyCodec.encodeKey(k, ctx) : keyText(this.toAst(k, shape.key, path));
          entries.push({ name, value: this.toAst(v, shape.value, path + "/" + name) });
        }
        return { kind: "object", entries };
      }
      case "brand":
        return this.toAst(value, shape.base, path);
      case "union": {
        if (typeof value !== "object") {
          throw new RunnerFailure("contract", "projection.not-an-object", path);
        }
        const record = value as Record<string, unknown>;
        for (const variant of shape.variants) {
          const vm = this.model(variant.use.typeId);
          if (vm.shape.kind === "object" && vm.shape.properties.length > 0) {
            const tag = record[vm.shape.properties[0]!.name];
            if ((typeof tag === "string" || typeof tag === "number" || typeof tag === "bigint") && String(tag) === variant.tag) {
              return this.toAst(value, variant.use, path);
            }
          }
        }
        throw new RunnerFailure("contract", "projection.unknown-variant", path);
      }
    }
  }

  private scalarToAst(value: unknown, name: string, path: string): JsonValue {
    switch (name) {
      case "string":
      case "char":
      case "guid":
        if (typeof value === "string") {
          return { kind: "string", value };
        }
        break;
      case "boolean":
        if (typeof value === "boolean") {
          return { kind: "boolean", value };
        }
        break;
      case "bytes":
        if (value instanceof Uint8Array) {
          return { kind: "string", value: encodeBase64(value) };
        }
        break;
      case "json-value":
        return value as JsonValue;
      case "int8":
      case "uint8":
      case "int16":
      case "uint16":
      case "int32":
      case "uint32":
      case "int64":
      case "uint64":
        if (typeof value === "bigint" || (typeof value === "number" && Number.isInteger(value))) {
          return { kind: "number", text: value.toString() };
        }
        break;
      case "decimal":
        if (isDecimal(value)) {
          return { kind: "number", text: formatDecimal(value as Decimal) };
        }
        break;
      case "float32":
      case "float64":
        if (typeof value === "number") {
          if (!Number.isFinite(value)) {
            return { kind: "string", value: Number.isNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity" };
          }
          return { kind: "number", text: formatDotnetShortest(value, name === "float32") };
        }
        break;
      case "date-only":
        return { kind: "string", value: formatDateOnly(value as never) };
      case "time-only":
        return { kind: "string", value: formatTimeOnly(value as never) };
      case "datetime-utc":
        return { kind: "string", value: formatDateTimeUtc(value as never) };
      case "datetime-unspecified":
        return { kind: "string", value: formatDateTimeUnspecified(value as never) };
      case "datetime-local-wire":
        return { kind: "string", value: formatDateTimeLocalWire(value as never) };
      case "datetime-offset":
        return { kind: "string", value: formatDateTimeOffset(value as never) };
      case "duration":
        return { kind: "string", value: formatDuration(value as never) };
      default:
        throw new RunnerFailure("contract", "projection.unknown-scalar", path);
    }
    throw new RunnerFailure("contract", "projection.value-type-mismatch", path);
  }
}

function natural(ast: JsonValue): unknown {
  switch (ast.kind) {
    case "null":
      return null;
    case "boolean":
      return ast.value;
    case "string":
      return ast.value;
    case "number":
      return Number(ast.text);
    case "array":
      return ast.items.map(natural);
    case "object": {
      const out: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
      for (const e of ast.entries) {
        out[e.name] = natural(e.value);
      }
      return out;
    }
  }
}

function namedFloat(text: string): unknown {
  return text === "NaN" ? Number.NaN : text === "Infinity" ? Number.POSITIVE_INFINITY : text === "-Infinity" ? Number.NEGATIVE_INFINITY : text;
}

function findEntryValue(obj: JsonValue & { kind: "object" }, name: string): JsonValue | undefined {
  for (const e of obj.entries) {
    if (e.name === name) {
      return e.value;
    }
  }
  return undefined;
}

function tagText(v: JsonValue): string | undefined {
  return v.kind === "string" ? v.value : v.kind === "number" ? v.text : undefined;
}

function keyText(v: JsonValue): string {
  switch (v.kind) {
    case "string":
      return v.value;
    case "number":
      return v.text;
    case "boolean":
      return v.value ? "True" : "False";
    default:
      throw new RunnerFailure("contract", "projection.key-not-scalar");
  }
}

// ---------------------------------------------------------------- builtin oracles (same rules as the C# AstOracles)

function normalizeNumber(lexeme: string): { mantissa: bigint; exponent: number } | undefined {
  const m = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(lexeme);
  if (m === null) {
    return undefined;
  }
  const frac = m[3] ?? "";
  let exponent = (m[4] === undefined ? 0 : Number.parseInt(m[4], 10)) - frac.length;
  if (!Number.isSafeInteger(exponent)) {
    return undefined;
  }
  let mantissa = BigInt(m[2]! + frac);
  if (mantissa === 0n) {
    return { mantissa: 0n, exponent: 0 };
  }
  while (mantissa % 10n === 0n) {
    mantissa /= 10n;
    exponent++;
  }
  return { mantissa: m[1] === "-" ? -mantissa : mantissa, exponent };
}

function astEqual(a: JsonValue, b: JsonValue, numeric: boolean, ordered: boolean): boolean {
  switch (a.kind) {
    case "null":
      return b.kind === "null";
    case "boolean":
      return b.kind === "boolean" && a.value === b.value;
    case "string":
      return b.kind === "string" && a.value === b.value;
    case "number": {
      if (b.kind !== "number") {
        return false;
      }
      if (a.text === b.text) {
        return true;
      }
      if (!numeric) {
        return false;
      }
      const x = normalizeNumber(a.text);
      const y = normalizeNumber(b.text);
      return x !== undefined && y !== undefined && x.mantissa === y.mantissa && x.exponent === y.exponent;
    }
    case "array":
      return b.kind === "array" && a.items.length === b.items.length && a.items.every((item, i) => astEqual(item, b.items[i]!, numeric, ordered));
    case "object": {
      if (b.kind !== "object" || a.entries.length !== b.entries.length) {
        return false;
      }
      const unique = new Set(a.entries.map((e) => e.name)).size === a.entries.length && new Set(b.entries.map((e) => e.name)).size === b.entries.length;
      if (ordered || !unique) {
        return a.entries.every((e, i) => e.name === b.entries[i]!.name && astEqual(e.value, b.entries[i]!.value, numeric, ordered));
      }
      const map = new Map(b.entries.map((e) => [e.name, e.value]));
      return a.entries.every((e) => {
        const other = map.get(e.name);
        return other !== undefined && astEqual(e.value, other, numeric, ordered);
      });
    }
  }
}

export function builtinOracle(id: string, a: JsonValue, b: JsonValue): boolean | undefined {
  switch (id) {
    case "tisilia.oracle.structural@0.1":
      return astEqual(a, b, false, false);
    case "tisilia.oracle.numeric@0.1":
      return astEqual(a, b, true, false);
    case "tisilia.oracle.exact-wire@0.1":
      return astEqual(a, b, false, true);
    default:
      return undefined;
  }
}

// ---------------------------------------------------------------- protocol records

function readRecord(line: string, maxRecordBytes: number): Request {
  if (Buffer.byteLength(line, "utf8") > maxRecordBytes) {
    throw new RunnerFailure("limit", "protocol.record-limit");
  }
  let doc: JsonValue;
  try {
    doc = parseJson(line, { limits: { maxDepth: 512, maxBodyBytes: maxRecordBytes } });
  } catch (e) {
    throw new RunnerFailure("invalid-input", e instanceof JsonParseError ? "protocol.malformed-json" : "protocol.malformed");
  }
  if (doc.kind !== "object") {
    throw new RunnerFailure("invalid-input", "protocol.not-an-object");
  }
  const seen = new Set<string>();
  const fields = new Map<string, JsonValue>();
  for (const e of doc.entries) {
    if (seen.has(e.name)) {
      throw new RunnerFailure("invalid-input", "protocol.duplicate-member");
    }
    seen.add(e.name);
    fields.set(e.name, e.value);
  }
  const str = (name: string): string => {
    const v = fields.get(name);
    if (v === undefined || v.kind !== "string") {
      throw new RunnerFailure("invalid-input", "protocol.missing-" + name);
    }
    return v.value;
  };
  if (str("format") !== "tisilia.runner-message" || str("version") !== "0.1") {
    throw new RunnerFailure("invalid-input", "protocol.format");
  }
  const sessionId = str("sessionId");
  const requestId = str("requestId");
  if (!idPattern.test(sessionId) || !idPattern.test(requestId)) {
    throw new RunnerFailure("invalid-input", "protocol.id-grammar");
  }
  const fail = (safeMessageId: string): RunnerFailure => new RunnerFailure("invalid-input", safeMessageId, "", requestId);
  if (str("kind") !== "request") {
    throw fail("protocol.not-a-request");
  }
  const action = str("action");
  if (!actions.has(action)) {
    throw fail("protocol.unknown-action");
  }
  const adapterId = str("adapterId");
  const profileId = str("profileId");
  const contextNode = fields.get("context");
  const inputsNode = fields.get("inputs");
  if (contextNode?.kind !== "array" || inputsNode?.kind !== "array") {
    throw fail("protocol.context-or-inputs");
  }
  const expected = new Set(["format", "version", "sessionId", "requestId", "kind", "action", "adapterId", "profileId", "context", "inputs"]);
  for (const name of fields.keys()) {
    if (!expected.has(name)) {
      throw fail("protocol.unknown-member");
    }
  }
  const context = new Map<string, string>();
  for (const item of contextNode.items) {
    if (item.kind !== "object") {
      throw fail("protocol.context-entry");
    }
    const name = findEntryValue(item, "name");
    const value = findEntryValue(item, "value");
    if (name?.kind !== "string" || value?.kind !== "string" || name.value.length === 0) {
      throw fail("protocol.context-entry");
    }
    context.set(name.value, value.value);
  }
  let inputs: JsonValue[];
  try {
    inputs = inputsNode.items.map(decodeAst);
  } catch (e) {
    throw e instanceof RunnerFailure ? new RunnerFailure(e.code, e.safeMessageId, e.path, requestId) : e;
  }
  const arity = action === "compare" || action === "ts-project" || action === "dotnet-project" ? 2 : 1;
  if (inputs.length !== arity) {
    throw fail("protocol.input-arity");
  }
  return { sessionId, requestId, action: action as Action, adapterId, profileId, context, inputs };
}

/** The message carries ASTs as documents ({kind, value|text|items|entries}); rebuild runtime JsonValue nodes strictly. */
function decodeAst(node: JsonValue): JsonValue {
  if (node.kind !== "object") {
    throw new RunnerFailure("invalid-input", "ast.not-an-object");
  }
  const kind = findEntryValue(node, "kind");
  if (kind?.kind !== "string") {
    throw new RunnerFailure("invalid-input", "ast.kind");
  }
  const count = node.entries.length;
  switch (kind.value) {
    case "null":
      if (count !== 1) {
        throw new RunnerFailure("invalid-input", "ast.null-members");
      }
      return jsonNull;
    case "boolean": {
      const v = findEntryValue(node, "value");
      if (count !== 2 || v?.kind !== "boolean") {
        throw new RunnerFailure("invalid-input", "ast.boolean");
      }
      return { kind: "boolean", value: v.value };
    }
    case "string": {
      const v = findEntryValue(node, "value");
      if (count !== 2 || v?.kind !== "string") {
        throw new RunnerFailure("invalid-input", "ast.string");
      }
      return { kind: "string", value: v.value };
    }
    case "number": {
      const t = findEntryValue(node, "text");
      if (count !== 2 || t?.kind !== "string" || !/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/.test(t.value) || t.value.length > 4096) {
        throw new RunnerFailure("invalid-input", "ast.number");
      }
      return { kind: "number", text: t.value };
    }
    case "array": {
      const items = findEntryValue(node, "items");
      if (count !== 2 || items?.kind !== "array") {
        throw new RunnerFailure("invalid-input", "ast.array");
      }
      return { kind: "array", items: items.items.map(decodeAst) };
    }
    case "object": {
      const entries = findEntryValue(node, "entries");
      if (count !== 2 || entries?.kind !== "array") {
        throw new RunnerFailure("invalid-input", "ast.object");
      }
      const out: JsonEntry[] = [];
      for (const item of entries.items) {
        if (item.kind !== "object" || item.entries.length !== 2) {
          throw new RunnerFailure("invalid-input", "ast.entry");
        }
        const name = findEntryValue(item, "name");
        const value = findEntryValue(item, "value");
        if (name?.kind !== "string" || value === undefined) {
          throw new RunnerFailure("invalid-input", "ast.entry");
        }
        out.push({ name: name.value, value: decodeAst(value) });
      }
      return { kind: "object", entries: out };
    }
    default:
      throw new RunnerFailure("invalid-input", "ast.unknown-kind");
  }
}

function encodeAst(v: JsonValue): unknown {
  switch (v.kind) {
    case "null":
      return { kind: "null" };
    case "boolean":
      return { kind: "boolean", value: v.value };
    case "string":
      return { kind: "string", value: v.value };
    case "number":
      return { kind: "number", text: v.text };
    case "array":
      return { kind: "array", items: v.items.map(encodeAst) };
    case "object":
      return { kind: "object", entries: v.entries.map((e) => ({ name: e.name, value: encodeAst(e.value) })) };
  }
}

function successRecord(sessionId: string, requestId: string, output: JsonValue): string {
  // records contain no JSON numbers (number lexemes travel as text), so the native serializer is lossless here
  return JSON.stringify({ format: "tisilia.runner-message", version: "0.1", sessionId, requestId, kind: "success", outputs: [encodeAst(output)] });
}

function failureRecord(sessionId: string, requestId: string, code: FailureCode, safeMessageId: string, path: string): string {
  return JSON.stringify({ format: "tisilia.runner-message", version: "0.1", sessionId, requestId, kind: "failure", code, safeMessageId: safeId(safeMessageId), path });
}

function toFailure(e: unknown): RunnerFailure {
  if (e instanceof RunnerFailure) {
    return e;
  }
  if (e instanceof CodecError) {
    return new RunnerFailure("codec", "codec." + e.code, e.path);
  }
  if (typeof e === "object" && e !== null && (e as { name?: unknown }).name === "CodecError") {
    const err = e as { code?: unknown; path?: unknown };
    return new RunnerFailure("codec", "codec." + (typeof err.code === "string" ? err.code : "module"), typeof err.path === "string" ? err.path : "");
  }
  if (e instanceof RangeError || e instanceof TypeError) {
    return new RunnerFailure("codec", "codec." + e.name);
  }
  return new RunnerFailure("internal", "internal." + (e instanceof Error ? e.name : "unknown"));
}

// ---------------------------------------------------------------- dispatch

class Dispatcher {
  private readonly bridge: DomainBridge;

  constructor(
    private readonly contract: ContractView,
    private readonly registry: CodecRegistry,
    private readonly moduleExport: ((moduleId: string, exportName: string) => unknown) | undefined,
  ) {
    this.bridge = new DomainBridge(contract, registry);
  }

  private codec(request: Request): { codec: Codec<unknown>; use: TypeUse } {
    const entry = this.contract.codecs.get(request.adapterId);
    if (entry === undefined) {
      throw new RunnerFailure("contract", "adapter.not-a-codec");
    }
    if (!this.registry.has(request.adapterId)) {
      throw new RunnerFailure("contract", "adapter.not-registered");
    }
    return { codec: this.registry.get(request.adapterId), use: { typeId: entry.typeId, codecId: entry.id, semanticNullable: entry.id.endsWith(".nullable") } };
  }

  private context(request: Request): CodecContext {
    return createCodecContext({ profileId: request.profileId, context: request.context });
  }

  handle(request: Request): JsonValue {
    switch (request.action) {
      case "ts-encode-request": {
        const { codec, use } = this.codec(request);
        if (codec.encodeRequest === undefined) {
          throw new RunnerFailure("contract", "adapter.no-request-capability");
        }
        return codec.encodeRequest(this.bridge.fromAst(request.inputs[0]!, use), this.context(request));
      }
      case "ts-decode-response": {
        const { codec, use } = this.codec(request);
        if (codec.decodeResponse === undefined) {
          throw new RunnerFailure("contract", "adapter.no-response-capability");
        }
        return this.bridge.toAst(codec.decodeResponse(request.inputs[0]!, this.context(request)), use);
      }
      case "ts-encode-key": {
        const { codec, use } = this.codec(request);
        if (codec.encodeKey === undefined) {
          throw new RunnerFailure("contract", "adapter.no-request-key-capability");
        }
        return { kind: "string", value: codec.encodeKey(this.bridge.fromAst(request.inputs[0]!, use), this.context(request)) };
      }
      case "ts-decode-key": {
        const { codec, use } = this.codec(request);
        const input = request.inputs[0]!;
        if (input.kind !== "string") {
          throw new RunnerFailure("invalid-input", "key.not-a-string");
        }
        if (codec.decodeKey === undefined) {
          throw new RunnerFailure("contract", "adapter.no-response-key-capability");
        }
        return this.bridge.toAst(codec.decodeKey(input.value, this.context(request)), use);
      }
      case "validate-domain": {
        const { codec, use } = this.codec(request);
        codec.validateDomain(this.bridge.fromAst(request.inputs[0]!, use), this.context(request));
        return { kind: "boolean", value: true };
      }
      case "parse-request-input": {
        const { codec, use } = this.codec(request);
        if (codec.parseRequestInput === undefined) {
          throw new RunnerFailure("contract", "adapter.no-request-input-capability");
        }
        const input = request.inputs[0]!;
        return this.bridge.toAst(codec.parseRequestInput(input.kind === "string" ? input.value : input, this.context(request)), use);
      }
      case "compare":
        return this.compare(request);
      case "ts-project": {
        // a behavior's projection by its TypeScript module export at a path of the domain AST
        const projection = this.contract.projections.get(request.adapterId);
        if (projection === undefined) {
          throw new RunnerFailure("contract", "projection.unknown");
        }
        const impl = projection.typescriptImplementation;
        if (impl.kind !== "module" || impl.moduleId === undefined || impl.exportName === undefined) {
          throw new RunnerFailure("contract", "projection.not-a-module-export");
        }
        if (this.moduleExport === undefined) {
          throw new RunnerFailure("contract", "projection.modules-unavailable");
        }
        const exported = this.moduleExport(impl.moduleId, impl.exportName) as { project?: (domain: JsonValue, context: CodecContext) => JsonValue } | undefined;
        if (exported === undefined || typeof exported.project !== "function") {
          throw new RunnerFailure("contract", "projection.export-missing");
        }
        const pathInput = request.inputs[1]!;
        if (pathInput.kind !== "string") {
          throw new RunnerFailure("invalid-input", "projection.path-not-a-string");
        }
        const segments = pathInput.value.length === 0 ? [] : pathInput.value.split("/");
        const context = this.context(request);
        try {
          return applyAt(request.inputs[0]!, segments, 0, (v) => exported.project!(v, context));
        } catch (e) {
          if (e instanceof RunnerFailure) {
            throw e;
          }
          throw new RunnerFailure("invalid-input", "projection.rejected", pathInput.value);
        }
      }
      default:
        throw new RunnerFailure("contract", "action.wrong-runner");
    }
  }

  private compare(request: Request): JsonValue {
    const equivalence = this.contract.equivalences.get(request.adapterId);
    if (equivalence === undefined) {
      throw new RunnerFailure("contract", "compare.unknown-equivalence");
    }
    const [a, b] = request.inputs as [JsonValue, JsonValue];
    const codecEntry = [...this.contract.codecs.values()].find((c) => c.typeId === equivalence.domainTypeId && equivalenceFor(c, equivalence.scope) === equivalence.id);
    if (codecEntry === undefined) {
      throw new RunnerFailure("contract", "compare.no-codec-for-equivalence");
    }
    const use: TypeUse = { typeId: codecEntry.typeId, codecId: codecEntry.id, semanticNullable: false };
    return { kind: "boolean", value: this.compareWith(equivalence, a, b, use, 0) };
  }

  /**
   * The registered oracle of an equivalence. Builtin structural/numeric oracles walk the domain model and delegate
   * every member to the member type's own equivalence in the same scope (a paired member is compared by its module
   * oracle); exact-wire compares the ASTs verbatim. The C# runner implements the same rule.
   */
  private compareWith(equivalence: Equivalence, a: JsonValue, b: JsonValue, use: TypeUse, depth: number): boolean {
    if (depth > 128) {
      throw new RunnerFailure("limit", "compare.depth");
    }
    const oracle = equivalence.typescriptOracle;
    if (oracle.kind === "module") {
      if (a.kind === "null" || b.kind === "null") {
        return a.kind === "null" && b.kind === "null";
      }
      if (this.moduleExport === undefined || oracle.moduleId === undefined || oracle.exportName === undefined) {
        throw new RunnerFailure("contract", "compare.module-oracle-unavailable");
      }
      const fn = this.moduleExport(oracle.moduleId, oracle.exportName);
      if (typeof fn !== "function") {
        throw new RunnerFailure("contract", "compare.oracle-not-a-function");
      }
      const result = (fn as (x: unknown, y: unknown) => unknown)(this.bridge.fromAst(a, use), this.bridge.fromAst(b, use));
      if (typeof result !== "boolean") {
        throw new RunnerFailure("contract", "compare.oracle-not-boolean");
      }
      return result;
    }
    switch (oracle.id) {
      case "tisilia.oracle.exact-wire@0.1":
        return astEqual(a, b, false, true);
      case "tisilia.oracle.structural@0.1":
        return this.compareByModel(a, b, use, equivalence.scope, false, depth);
      case "tisilia.oracle.numeric@0.1":
        return this.compareByModel(a, b, use, equivalence.scope, true, depth);
      default:
        throw new RunnerFailure("contract", "compare.unknown-builtin-oracle");
    }
  }

  private compareMember(a: JsonValue, b: JsonValue, use: TypeUse, scope: string, numeric: boolean, depth: number): boolean {
    const codec = this.contract.codecs.get(use.codecId);
    const eqId = codec === undefined ? undefined : equivalenceFor(codec, scope);
    const eq = eqId === undefined ? undefined : this.contract.equivalences.get(eqId);
    return eq === undefined ? this.compareByModel(a, b, use, scope, numeric, depth) : this.compareWith(eq, a, b, use, depth);
  }

  private compareByModel(a: JsonValue, b: JsonValue, use: TypeUse, scope: string, numeric: boolean, depth: number): boolean {
    if (a.kind === "null" || b.kind === "null") {
      return a.kind === "null" && b.kind === "null";
    }
    const model = this.contract.types.get(use.typeId);
    if (model === undefined) {
      throw new RunnerFailure("contract", "compare.unknown-type");
    }
    const shape = model.shape;
    switch (shape.kind) {
      case "primitive":
      case "enum":
        return astEqual(a, b, numeric, false);
      case "object": {
        if (a.kind !== "object" || b.kind !== "object" || a.entries.length !== b.entries.length) {
          return false;
        }
        if (new Set(a.entries.map((e) => e.name)).size !== a.entries.length || new Set(b.entries.map((e) => e.name)).size !== b.entries.length) {
          return astEqual(a, b, numeric, false);
        }
        for (const entry of a.entries) {
          const other = findEntryValue(b, entry.name);
          if (other === undefined) {
            return false;
          }
          const prop = shape.properties.find((p) => p.name === entry.name);
          if (prop !== undefined) {
            if (!this.compareMember(entry.value, other, prop.use, scope, numeric, depth + 1)) {
              return false;
            }
          } else if (entry.name === "extensions" && shape.extension.kind === "capture" && entry.value.kind === "object" && other.kind === "object") {
            if (entry.value.entries.length !== other.entries.length) {
              return false;
            }
            for (const ext of entry.value.entries) {
              const otherExt = findEntryValue(other, ext.name);
              if (otherExt === undefined || !this.compareMember(ext.value, otherExt, shape.extension.value, scope, numeric, depth + 1)) {
                return false;
              }
            }
          } else if (!astEqual(entry.value, other, numeric, false)) {
            return false;
          }
        }
        return true;
      }
      case "array":
        return a.kind === "array" && b.kind === "array" && a.items.length === b.items.length && a.items.every((item, i) => this.compareMember(item, b.items[i]!, shape.element, scope, numeric, depth + 1));
      case "map": {
        if (a.kind !== "object" || b.kind !== "object" || a.entries.length !== b.entries.length) {
          return false;
        }
        for (const entry of a.entries) {
          const other = findEntryValue(b, entry.name);
          if (other === undefined || !this.compareMember(entry.value, other, shape.value, scope, numeric, depth + 1)) {
            return false;
          }
        }
        return true;
      }
      case "brand":
        return this.compareMember(a, b, shape.base, scope, numeric, depth + 1);
      case "union": {
        if (a.kind !== "object" || b.kind !== "object") {
          return false;
        }
        for (const variant of shape.variants) {
          const vm = this.contract.types.get(variant.use.typeId);
          if (vm?.shape.kind === "object" && vm.shape.properties.length > 0) {
            const name = vm.shape.properties[0]!.name;
            const ta = findEntryValue(a, name);
            const tb = findEntryValue(b, name);
            if (ta !== undefined && tagText(ta) === variant.tag) {
              return tb !== undefined && tagText(tb) === variant.tag && this.compareMember(a, b, variant.use, scope, numeric, depth + 1);
            }
          }
        }
        return false;
      }
    }
  }
}

function equivalenceFor(codec: { capabilities?: Capabilities }, scope: string): string | undefined {
  const caps = codec.capabilities;
  if (caps === undefined) {
    return undefined;
  }
  switch (scope) {
    case "request":
      return caps.request?.equivalenceId;
    case "response":
      return caps.response?.equivalenceId;
    case "key":
      return caps.requestKey?.equivalenceId ?? caps.responseKey?.equivalenceId;
    default:
      return caps.response?.equivalenceId ?? caps.request?.equivalenceId;
  }
}

// ---------------------------------------------------------------- environment report

function typescriptVersion(): string | undefined {
  try {
    const require = createRequire(import.meta.url);
    const pkg = require("typescript/package.json") as { version?: unknown };
    return typeof pkg.version === "string" ? pkg.version : undefined;
  } catch {
    return undefined;
  }
}

function writeEnvironment(path: string): void {
  const self = fileURLToPath(import.meta.url);
  const digest = "sha256:" + createHash("sha256").update(readFileSync(self)).digest("hex");
  const matrix: Record<string, string> = { node: process.versions.node };
  const ts = typescriptVersion();
  if (ts !== undefined) {
    matrix["typescript"] = ts;
  }
  const report = {
    ready: true,
    runner: "node",
    runnerDigest: digest,
    matrix,
    context: [{ name: "node.timeZone", value: Intl.DateTimeFormat().resolvedOptions().timeZone }],
    applicationArtifacts: [],
  };
  writeFileSync(path + ".tmp", JSON.stringify(report) + "\n");
  renameSync(path + ".tmp", path);
}

// ---------------------------------------------------------------- main loop

export async function runRunner(options: RunnerOptions): Promise<void> {
  const contract = loadContract(options.contractPath);
  const sessionId = options.sessionId ?? process.env["TISILIA_RUNNER_SESSION"] ?? "";
  const maxRecordBytes = options.maxRecordBytes ?? Number(process.env["TISILIA_RUNNER_MAX_RECORD_BYTES"] ?? 16777216);
  const dispatcher = new Dispatcher(contract, options.registry, options.moduleExport);
  const output = options.output ?? process.stdout;
  const write = (line: string): Promise<void> =>
    new Promise((resolve, reject) => {
      output.write(line + "\n", (err) => (err ? reject(err) : resolve()));
    });
  const envPath = options.environmentPath ?? process.env["TISILIA_RUNNER_ENV"];
  if (envPath !== undefined && envPath.length > 0) {
    writeEnvironment(envPath);
  }
  const rl = createInterface({ input: options.input ?? process.stdin, crlfDelay: Number.POSITIVE_INFINITY });
  for await (const line of rl) {
    if (line.length === 0) {
      continue;
    }
    let request: Request | undefined;
    let record: string;
    try {
      request = readRecord(line, maxRecordBytes);
      if (request.sessionId !== sessionId) {
        record = failureRecord(sessionId, request.requestId, "invalid-input", "protocol.session-mismatch", "");
      } else {
        record = successRecord(sessionId, request.requestId, dispatcher.handle(request));
      }
    } catch (e) {
      const failure = toFailure(e);
      record = failureRecord(sessionId, failure.requestId ?? request?.requestId ?? "unknown", failure.code, failure.safeMessageId, failure.path);
    }
    await write(record);
  }
}
