// Contract interpreter: builds the same codec registry and operation descriptors the generator emits, directly from a
// tisilia.contract 0.1 document (the Explorer runs the client's registry/codecs;
// modules are locally installed code bound by id and export name, never fetched from the contract).
import { createCodecContext, withContext, type Codec, type CodecContext } from "../codec/abi.js";
import { CodecError } from "../codec/errors.js";
import { CodecRegistry } from "../codec/registry.js";
import { scalarCodec, type NumberProfile, type ScalarName } from "../codec/scalars.js";
import { arrayCodec, brandCodec, enumCodec, mapCodec, nullableCodec, objectCodec, taggedUnionCodec, type AdditionalPolicy, type DuplicatePolicy, type EnumMember, type NameMatching, type Presence, type PropertyDescriptor } from "../codec/structural.js";
import type { KeyComparer } from "../codec/map.js";
import { codecBinder, enumBinder, standardBinder, type NullPolicy, type ParameterLocation } from "../http/binders.js";
import type { HttpMethod, OperationDescriptor, ParameterDescriptor, ResponseBodyDescriptor, ResponseCaseDescriptor } from "../http/client.js";
import type { FormFieldDescriptor } from "../http/forms.js";
import type { JsonValue } from "../json/ast.js";
import { validateRoutePlan } from "../http/routes.js";

// ---------------------------------------------------------------- contract document (the subset the runtime reads)

export interface ContractTypeUse {
  readonly typeId: string;
  readonly codecId: string;
  readonly semanticNullable: boolean;
}

export interface ContractWireRef {
  readonly wireId: string;
  readonly direction: "server-read" | "server-write";
}

export interface ContractImpl {
  readonly kind: "builtin" | "module";
  readonly id?: string;
  readonly moduleId?: string;
  readonly exportName?: string;
}

export interface ContractDomainProperty {
  readonly name: string;
  readonly use: ContractTypeUse;
  readonly presence: Presence;
}

export type ContractShape =
  | { readonly kind: "primitive"; readonly primitiveId: string }
  | { readonly kind: "enum"; readonly underlyingPrimitiveId: string; readonly flags: boolean; readonly allowUndefinedInteger: boolean; readonly members: readonly { readonly name: string; readonly value: string; readonly serializedName?: string }[] }
  | { readonly kind: "object"; readonly properties: readonly ContractDomainProperty[]; readonly extension: { readonly kind: "none" } | { readonly kind: "capture"; readonly value: ContractTypeUse; readonly collision: string } }
  | { readonly kind: "array"; readonly element: ContractTypeUse }
  | { readonly kind: "map"; readonly key: ContractTypeUse; readonly value: ContractTypeUse; readonly comparerId: string }
  | { readonly kind: "brand"; readonly brandId: string; readonly base: ContractTypeUse }
  | { readonly kind: "union"; readonly variants: readonly { readonly tag: string; readonly use: ContractTypeUse }[] };

export interface ContractModel {
  readonly id: string;
  readonly tsName: string;
  readonly clrIdentity: string;
  readonly shape: ContractShape;
}

export interface ContractWireProperty {
  readonly name: string;
  readonly wire: ContractWireRef;
  readonly presence: Presence;
}

export type ContractWireShape =
  | { readonly kind: "literal"; readonly value: JsonValue }
  | { readonly kind: "null" }
  | { readonly kind: "boolean" }
  | { readonly kind: "string"; readonly grammarId: string; readonly minUtf16Length?: number; readonly maxUtf16Length?: number }
  | { readonly kind: "number"; readonly grammarId: string }
  | { readonly kind: "array"; readonly element: ContractWireRef }
  | { readonly kind: "object"; readonly properties: readonly ContractWireProperty[]; readonly additional: { readonly kind: AdditionalPolicy; readonly wire?: ContractWireRef }; readonly duplicatePolicyId: string; readonly nameMatchingId: string }
  | { readonly kind: "token-union"; readonly branches: readonly { readonly token: string; readonly wire: ContractWireRef }[] }
  | { readonly kind: "tagged-union"; readonly discriminator: string; readonly variants: readonly { readonly tag: { readonly kind: "string"; readonly value: string } | { readonly kind: "number"; readonly text: string }; readonly wire: ContractWireRef }[] }
  | { readonly kind: "lossless-json"; readonly grammarId: string };

export interface ContractWire {
  readonly id: string;
  readonly direction: "server-read" | "server-write";
  readonly shape: ContractWireShape;
}

export interface ContractValueCapability {
  readonly wire: ContractWireRef;
  readonly implementation: ContractImpl;
  readonly nullBehavior: string;
  readonly equivalenceId: string;
  readonly domainRuleId: string;
}

export interface ContractKeyCapability {
  readonly implementation: ContractImpl;
  readonly grammarId: string;
  readonly equivalenceId: string;
  readonly collision: string;
}

export interface ContractInputCapability {
  readonly implementation: ContractImpl;
  readonly inputKind: "text" | "json-value";
  readonly editorId: string;
}

export interface ContractCodec {
  readonly id: string;
  readonly typeId: string;
  readonly origin: "builtin" | "portable" | "paired";
  readonly bindingId: string;
  readonly validateDomain: ContractImpl;
  readonly capabilities: {
    readonly request?: ContractValueCapability;
    readonly response?: ContractValueCapability;
    readonly requestKey?: ContractKeyCapability;
    readonly responseKey?: ContractKeyCapability;
    readonly requestInput?: ContractInputCapability;
  };
  readonly dependencies: readonly string[];
  readonly profileIds: readonly string[];
}

export interface ContractBinding {
  readonly id: string;
  readonly kind: string;
  readonly implementation: ContractImpl;
  /** Declared non-secret context of the binding (converter instance settings such as a scale); confidential entries are never shipped. */
  readonly context?: readonly { readonly name: string; readonly value: string; readonly confidential: boolean }[];
}

export interface ContractProjection {
  readonly id: string;
  readonly sourceTypeId: string;
  readonly targetTypeId: string;
  readonly dotnetImplementation: ContractImpl;
  readonly typescriptImplementation: ContractImpl;
}

export interface ContractComparer {
  readonly id: string;
  readonly bindingId: string;
}

export interface ContractBinder {
  readonly id: string;
  readonly location: ParameterLocation;
  readonly typeId: string;
  /** The binder grammar: for an enum, `tisilia.grammar.enum-name@0.1` when the server binds defined values only (MVC). */
  readonly grammarId?: string;
  readonly cardinality: "single" | "repeated";
  readonly nullPolicy: NullPolicy;
  readonly nullLiteral?: string;
  readonly emptyPolicy: "reject" | "allow";
}

export interface ContractParameter {
  readonly id: string;
  readonly name: string;
  readonly location: ParameterLocation;
  readonly binderId: string;
  readonly use: ContractTypeUse;
  readonly presence: Presence;
  readonly serverDefault?: JsonValue;
}

export interface ContractFormField {
  readonly name: string;
  readonly kind: "value" | "file" | "object";
  readonly use?: ContractTypeUse;
  readonly repeated: boolean;
  readonly rejectBlank?: boolean;
  readonly indexed?: boolean;
  readonly wireName?: "";
  readonly fields?: readonly ContractFormField[];
  readonly enumDefinedOnly?: boolean;
  readonly presence: Presence;
}

export type ContractRequestBody = { readonly kind: "none" } | { readonly kind: "json"; readonly mediaType: string; readonly profileId: string; readonly use: ContractTypeUse; readonly presence: Presence }
  | { readonly kind: "form"; readonly mediaType: string; readonly presence: Presence; readonly fields: readonly ContractFormField[] }
  | { readonly kind: "binary"; readonly mediaType: string; readonly presence: Presence };

export type ContractResponseBody =
  | { readonly kind: "sse"; readonly mediaType: string; readonly dataFormat: "text" | "json"; readonly profileId?: string; readonly use: ContractTypeUse }
  | { readonly kind: "none" }
  | { readonly kind: "json"; readonly mediaType: string; readonly profileId: string; readonly use: ContractTypeUse }
  | { readonly kind: "text"; readonly mediaType: string; readonly use: ContractTypeUse }
  | { readonly kind: "binary"; readonly mediaType: string };

export interface ContractResponse {
  readonly id: string;
  readonly status: number;
  readonly body: ContractResponseBody;
  readonly resultAdapterId: string;
  readonly hydration: "server-only" | "browser-safe";
  readonly exposedHeaders: readonly string[];
}

export interface ContractOperation {
  readonly id: string;
  readonly method: HttpMethod;
  readonly route: string;
  readonly routePlan: import("../http/routes.js").RoutePlan;
  readonly tags: readonly string[];
  readonly parameters: readonly ContractParameter[];
  readonly requestBody: ContractRequestBody;
  readonly responses: readonly ContractResponse[];
  readonly pipelineBindingIds: readonly string[];
  readonly security: {
    readonly authPolicyId: string;
    readonly requestExecution: "browser-allowed" | "server-only";
    readonly requestHeaderAllowlist: readonly string[];
    readonly csrfPolicyId: string;
    readonly redaction?: { readonly default: "mask"; readonly rules: readonly { readonly direction: "request" | "response"; readonly selector: { readonly kind: "header" | "query" | "path"; readonly name: string } | { readonly kind: "body-path"; readonly segments: readonly ({ readonly property: string } | { readonly index: number } | { readonly each: true })[] }; readonly action: "mask" | "omit" | "show" }[] };
  };
}

export interface ContractModule {
  readonly id: string;
  readonly version: string;
  readonly artifacts: readonly { readonly target: "dotnet" | "node" | "browser"; readonly path: string; readonly digest: string }[];
  readonly exports: readonly { readonly name: string; readonly role: string; readonly targets: readonly string[] }[];
}

export interface ContractDocument {
  readonly format: "tisilia.contract";
  readonly version: "0.1";
  readonly apiId: string;
  readonly semanticHash: string;
  readonly profiles: readonly { readonly id: string; readonly options?: { readonly maxDepthEffective?: number } }[];
  readonly types: readonly ContractModel[];
  readonly wires: readonly ContractWire[];
  readonly codecs: readonly ContractCodec[];
  readonly bindings: readonly ContractBinding[];
  readonly comparers: readonly ContractComparer[];
  readonly binders: readonly ContractBinder[];
  readonly operations: readonly ContractOperation[];
  readonly modules: readonly ContractModule[];
  readonly projections?: readonly ContractProjection[];
}

// ---------------------------------------------------------------- interpreter

export interface ContractRegistryOptions {
  /** Exports of locally installed modules by module id; a paired codec without its module fails on use. */
  readonly modules?: ReadonlyMap<string, Readonly<Record<string, unknown>>>;
}

export interface ContractRegistry {
  readonly apiId: string;
  readonly semanticHash: string;
  readonly registry: CodecRegistry;
  readonly operations: ReadonlyMap<string, OperationDescriptor>;
  readonly document: ContractDocument;
}

function scalarNameOf(primitiveId: string): ScalarName {
  const name = primitiveId.startsWith("tisilia.") && primitiveId.endsWith("@0.1") ? primitiveId.slice("tisilia.".length, -"@0.1".length) : primitiveId;
  return name as ScalarName;
}

/** Mirror of the generator's number-profile suffix (`.codec[.r][w][n][.nullable]`). */
export function numbersOf(codecId: string): NumberProfile {
  const marker = codecId.indexOf(".codec.");
  const suffix = marker < 0 ? "" : codecId.slice(marker + ".codec.".length).split(".")[0]!;
  return { readFromString: suffix.includes("r"), writeAsString: suffix.includes("w"), namedLiterals: suffix.includes("n") };
}

/** The server profile's effective MaxDepth for a request body (absent when the contract does not record it). */
function maxDepthOf(document: ContractDocument, profileId: string | undefined): { maxDepth?: number } {
  const depth = document.profiles.find((p) => p.id === profileId)?.options?.maxDepthEffective;
  return depth === undefined ? {} : { maxDepth: depth };
}

function builtinName(id: string): string | undefined {
  const m = /^tisilia\.[a-z-]+\.([a-z0-9-]+)@0\.1$/.exec(id);
  return m === null ? undefined : m[1];
}

/**
 * Builds the registry lazily: every codec id resolves on first use, so recursive types and module codecs work the
 * same way as in generated code. Structural rules (presence per wire, name matching, duplicates, comparers, enum
 * string form, union discriminators) are read from the contract exactly as the generator reads them.
 */
export function createContractRegistry(document: ContractDocument, options: ContractRegistryOptions = {}): ContractRegistry {
  if (document.format !== "tisilia.contract" || document.version !== "0.1") {
    throw new Error("expected tisilia.contract 0.1: re-export with Tisilia 0.1.0-alpha; other contract versions are not supported");
  }
  const types = new Map(document.types.map((t) => [t.id, t] as const));
  const wires = new Map(document.wires.map((w) => [w.id, w] as const));
  const codecs = new Map(document.codecs.map((c) => [c.id, c] as const));
  const bindings = new Map(document.bindings.map((b) => [b.id, b] as const));
  const comparers = new Map(document.comparers.map((c) => [c.id, c] as const));
  const binders = new Map(document.binders.map((b) => [b.id, b] as const));
  const modules = options.modules ?? new Map<string, Readonly<Record<string, unknown>>>();
  const registry = new CodecRegistry();

  const moduleExport = (impl: ContractImpl, role: string): unknown => {
    if (impl.kind !== "module" || impl.moduleId === undefined || impl.exportName === undefined) {
      throw new CodecError("unsupported", "", `a ${role} capability of a module codec must name a module export`);
    }
    const exports = modules.get(impl.moduleId);
    if (exports === undefined) {
      throw new CodecError("unsupported", "", `Tisilia module '${impl.moduleId}' is not installed`, impl.moduleId);
    }
    const value = exports[impl.exportName];
    if (value === undefined) {
      throw new CodecError("unsupported", "", `Tisilia module '${impl.moduleId}' has no export '${impl.exportName}'`, impl.moduleId);
    }
    return value;
  };

  const nameMatchingOf = (id: string | undefined): NameMatching => {
    if (id === undefined) {
      return "ordinal";
    }
    const direct = builtinName(id);
    const viaBinding = direct ?? (bindings.get(id)?.implementation.kind === "builtin" ? builtinName(bindings.get(id)!.implementation.id ?? "") : undefined);
    return viaBinding === "ordinal-ignore-case" ? "ordinal-ignore-case" : "ordinal";
  };
  const duplicatesOf = (id: string | undefined): DuplicatePolicy => {
    if (id === undefined) {
      return "reject";
    }
    const direct = builtinName(id);
    const viaBinding = direct ?? (bindings.get(id)?.implementation.kind === "builtin" ? builtinName(bindings.get(id)!.implementation.id ?? "") : undefined);
    return viaBinding === "last-wins" ? "last-wins" : "reject";
  };
  const comparerOf = (comparerId: string): KeyComparer => {
    const comparer = comparers.get(comparerId);
    if (comparer?.bindingId === "tisilia.comparer.ordinal-ignore-case@0.1") {
      return "ordinal-ignore-case";
    }
    if (comparer?.bindingId === "tisilia.comparer.structural@0.1") {
      return "structural";
    }
    return "ordinal";
  };
  const wireShape = (ref: ContractWireRef | undefined): ContractWireShape | undefined => (ref === undefined ? undefined : wires.get(ref.wireId)?.shape);

  // the binding's declared non-secret context (the converter instance's settings) reaches the module through
  // the codec context, exactly as the generated client and the conformance runner pass it
  const boundContext = (codec: ContractCodec): ((context: CodecContext) => CodecContext) => {
    const entries = (bindings.get(codec.bindingId)?.context ?? []).filter((e) => !e.confidential);
    if (entries.length === 0) {
      return (context) => context;
    }
    const record: Record<string, string> = {};
    for (const entry of entries) {
      record[entry.name] = entry.value;
    }
    return (context) => withContext(context, record);
  };

  const buildPaired = (codec: ContractCodec): Codec<unknown> => {
    const caps = codec.capabilities;
    const bound = boundContext(codec);
    const validator = moduleExport(codec.validateDomain, "validator") as { validateDomain(value: unknown, context: CodecContext): unknown };
    const built: { -readonly [K in keyof Codec<unknown>]: Codec<unknown>[K] } = {
      id: codec.id,
      typeId: codec.typeId,
      validateDomain: (value, context) => validator.validateDomain(value, bound(context)),
    };
    if (caps.request !== undefined) {
      const impl = moduleExport(caps.request.implementation, "codec") as { encodeRequest(value: unknown, context: CodecContext): JsonValue };
      built.encodeRequest = (value, context) => impl.encodeRequest(value, bound(context));
    }
    if (caps.response !== undefined) {
      const impl = moduleExport(caps.response.implementation, "codec") as { decodeResponse(wire: JsonValue, context: CodecContext): unknown };
      built.decodeResponse = (wire, context) => impl.decodeResponse(wire, bound(context));
    }
    if (caps.requestKey !== undefined) {
      const impl = moduleExport(caps.requestKey.implementation, "key-codec") as { encodeKey(value: unknown, context: CodecContext): string };
      built.encodeKey = (value, context) => impl.encodeKey(value, bound(context));
    }
    if (caps.responseKey !== undefined) {
      const impl = moduleExport(caps.responseKey.implementation, "key-codec") as { decodeKey(value: string, context: CodecContext): unknown };
      built.decodeKey = (value, context) => impl.decodeKey(value, bound(context));
    }
    if (caps.requestInput !== undefined) {
      const impl = moduleExport(caps.requestInput.implementation, "request-input") as { parseRequestInput(value: string | JsonValue, context: CodecContext): unknown };
      built.parseRequestInput = (value, context) => impl.parseRequestInput(value, bound(context));
    }
    return built;
  };

  const build = (codec: ContractCodec): Codec<unknown> => {
    // the nullable wrapper of any codec — a module codec's too, which keeps the module's origin — passes null by itself (null behavior
    // bypass) and hands every other value to the codec it wraps, as the generated client does
    if (codec.id.endsWith(".nullable")) {
      const inner = codec.id.slice(0, -".nullable".length);
      if (codecs.has(inner)) {
        return nullableCodec(() => registry.get(inner), codec.id) as Codec<unknown>;
      }
    }
    if (codec.origin !== "builtin") {
      return buildPaired(codec);
    }
    const model = types.get(codec.typeId);
    if (model === undefined) {
      throw new CodecError("unsupported", "", `codec '${codec.id}' references unknown type '${codec.typeId}'`, codec.id);
    }
    const caps = codec.capabilities;
    const shape = model.shape;
    switch (shape.kind) {
      case "primitive": {
        const wire = wireShape((caps.response ?? caps.request)?.wire);
        const lengths = wire?.kind === "string" ? { ...(wire.minUtf16Length !== undefined ? { minUtf16Length: wire.minUtf16Length } : {}), ...(wire.maxUtf16Length !== undefined ? { maxUtf16Length: wire.maxUtf16Length } : {}) } : {};
        return scalarCodec(scalarNameOf(shape.primitiveId), { id: codec.id, typeId: codec.typeId, numbers: numbersOf(codec.id), ...lengths });
      }
      case "object": {
        const readWire = wireShape(caps.request?.wire);
        const writeWire = wireShape(caps.response?.wire);
        const readObject = readWire?.kind === "object" ? readWire : undefined;
        const writeObject = writeWire?.kind === "object" ? writeWire : undefined;
        const properties: PropertyDescriptor[] = shape.properties.map((p) => ({
          name: p.name,
          codec: () => registry.get(p.use.codecId),
          presence: p.presence,
          readPresence: readObject?.properties.find((x) => x.name === p.name)?.presence ?? p.presence,
          writePresence: writeObject?.properties.find((x) => x.name === p.name)?.presence ?? p.presence,
          nullable: p.use.semanticNullable,
        }));
        const any = readObject ?? writeObject;
        const extension = shape.extension.kind === "capture" ? shape.extension.value.codecId : undefined;
        return objectCodec({
          id: codec.id,
          typeId: codec.typeId,
          properties,
          nameMatching: nameMatchingOf(any?.nameMatchingId),
          duplicates: duplicatesOf(any?.duplicatePolicyId),
          readAdditional: readObject?.additional.kind ?? "ignore",
          writeAdditional: writeObject?.additional.kind ?? "ignore",
          ...(extension !== undefined ? { extension: () => registry.get(extension), extensionProperty: "extensions" } : {}),
          request: caps.request !== undefined,
          response: caps.response !== undefined,
        });
      }
      case "array":
        return arrayCodec({ id: codec.id, typeId: codec.typeId, element: () => registry.get(shape.element.codecId), elementNullable: shape.element.semanticNullable }) as Codec<unknown>;
      case "map":
        return mapCodec({ id: codec.id, typeId: codec.typeId, key: () => registry.get(shape.key.codecId), value: () => registry.get(shape.value.codecId), valueNullable: shape.value.semanticNullable, comparer: comparerOf(shape.comparerId) }) as Codec<unknown>;
      case "enum": {
        const stringForm = wireShape(caps.response?.wire)?.kind === "token-union";
        const members: EnumMember[] = shape.members.map((m) => ({ name: m.name, value: BigInt(m.value), ...(m.serializedName !== undefined ? { serializedName: m.serializedName } : {}) }));
        return enumCodec({
          id: codec.id,
          typeId: codec.typeId,
          underlying: scalarNameOf(shape.underlyingPrimitiveId) as EnumMember extends never ? never : "int8" | "uint8" | "int16" | "uint16" | "int32" | "uint32" | "int64" | "uint64",
          flags: shape.flags,
          allowUndefinedInteger: shape.allowUndefinedInteger,
          stringForm,
          members,
        }) as Codec<unknown>;
      }
      case "union": {
        const wire = wireShape((caps.response ?? caps.request)?.wire);
        if (wire?.kind !== "tagged-union") {
          throw new CodecError("unsupported", "", `union codec '${codec.id}' has no tagged-union wire`, codec.id);
        }
        return taggedUnionCodec({
          id: codec.id,
          typeId: codec.typeId,
          discriminator: wire.discriminator,
          tagProperty: wire.discriminator,
          variants: shape.variants.map((v, i) => {
            const wireTag = wire.variants[i]?.tag;
            const tag: string | bigint = wireTag?.kind === "number" ? BigInt(wireTag.text) : wireTag?.kind === "string" ? wireTag.value : v.tag;
            return { tag, codec: () => registry.get(v.use.codecId) };
          }),
        }) as Codec<unknown>;
      }
      case "brand":
        return brandCodec(codec.id, codec.typeId, () => registry.get(shape.base.codecId));
    }
  };

  for (const codec of document.codecs) {
    registry.registerLazy(codec.id, () => build(codec));
  }

  const profileOf = (op: ContractOperation): string => {
    if (op.requestBody.kind === "json") {
      return op.requestBody.profileId;
    }
    for (const r of op.responses) {
      if (r.body.kind === "sse" && r.body.profileId !== undefined) { return r.body.profileId; }
      if (r.body.kind === "json") {
        return r.body.profileId;
      }
    }
    return "";
  };

  const operations = new Map<string, OperationDescriptor>();
  for (const op of document.operations) {
    validateRoutePlan(op.routePlan, op.route, op.parameters);
    const parameters: ParameterDescriptor[] = op.parameters.map((p) => {
      const binder = binders.get(p.binderId);
      if (binder === undefined) {
        throw new Error(`operation '${op.id}': unknown binder '${p.binderId}'`);
      }
      const model = types.get(binder.typeId);
      const binderOptions = { id: binder.id, cardinality: binder.cardinality, nullPolicy: binder.nullPolicy, emptyPolicy: binder.emptyPolicy, ...(binder.nullLiteral !== undefined ? { nullLiteral: binder.nullLiteral } : {}) };
      // a module type's parameter is written with its request codec's canonical text
      const valueCodec = p.use.codecId.endsWith(".nullable") ? p.use.codecId.slice(0, -".nullable".length) : p.use.codecId;
      return {
        id: p.id,
        name: p.name,
        location: p.location,
        binder:
          model?.shape.kind === "primitive"
            ? standardBinder(scalarNameOf(model.shape.primitiveId), p.location, binderOptions)
            : model?.shape.kind === "enum"
              ? enumBinder(() => registry.get(valueCodec), model.shape.members.map((m) => [m.name, BigInt(m.value)] as const), p.location, { ...binderOptions, flags: model.shape.flags, definedOnly: binder.grammarId === "tisilia.grammar.enum-name@0.1" })
              : codecBinder(() => registry.get(valueCodec), p.location, binderOptions),
        presence: p.presence,
        nullable: p.use.semanticNullable,
        get: (args) => (args as Readonly<Record<string, unknown>>)[p.name],
      };
    });
    const responses: ResponseCaseDescriptor[] = op.responses.map((r) => {
      let body: ResponseBodyDescriptor;
      if (r.body.kind === "none") {
        body = { kind: "none" };
      } else if (r.body.kind === "text") {
        body = { kind: "text", mediaType: r.body.mediaType };
      } else if (r.body.kind === "binary") {
        body = { kind: "binary", mediaType: r.body.mediaType };
      } else if (r.body.kind === "sse") {
        const codecId = r.body.use.codecId;
        body = { kind: "sse", mediaType: r.body.mediaType, dataFormat: r.body.dataFormat, codec: () => registry.get(codecId), nullable: r.body.use.semanticNullable, ...(r.body.profileId === undefined ? {} : { profileId: r.body.profileId }) };
      } else if (r.body.kind === "json") {
        const codecId = r.body.use.codecId;
        body = { kind: "json", profileId: r.body.profileId, mediaType: r.body.mediaType, codec: () => registry.get(codecId), nullable: r.body.use.semanticNullable };
      } else {
        throw new Error("unknown response body kind; re-export with matching Tisilia tooling");
      }
      return { caseId: r.id, status: r.status, body, hydration: r.hydration, exposedHeaders: r.exposedHeaders };
    });
    const formDescriptor = (f: ContractFormField): FormFieldDescriptor => {
      const shape = f.use === undefined ? undefined : types.get(f.use.typeId)?.shape;
      if (f.kind === "value" && shape?.kind !== "primitive" && shape?.kind !== "enum") { throw new Error("form field requires a builtin scalar or enum"); }
      return { name: f.name, kind: f.kind, repeated: f.repeated, ...(f.rejectBlank === true ? { rejectBlank: true } : {}), presence: f.presence, ...(f.indexed === true ? { indexed: true } : {}), ...(f.wireName === undefined ? {} : { wireName: f.wireName }), ...(f.fields === undefined ? {} : { fields: f.fields.map(formDescriptor) }), ...(shape?.kind === "primitive" ? { scalar: scalarNameOf(shape.primitiveId) } : shape?.kind === "enum" ? { format: enumBinder(() => registry.get(f.use!.codecId), shape.members.map(m => [m.name, BigInt(m.value)] as const), "query", { flags: shape.flags, definedOnly: f.enumDefinedOnly === true }).format } : {}) };
    };
    const requestBody = op.requestBody;
    operations.set(op.id, {
      id: op.id,
      method: op.method,
      route: op.route,
      routePlan: op.routePlan,
      ...(profileOf(op) === "" ? {} : { profileId: profileOf(op) }),
      parameters,
      ...(requestBody.kind === "form" ? { requestBody: { kind: "form" as const, mediaType: requestBody.mediaType, presence: requestBody.presence, fields: requestBody.fields.map(formDescriptor), get: (args: unknown) => (args as { body?: unknown }).body } } : {}),
      ...(requestBody.kind === "binary" ? { requestBody: { kind: "binary" as const, mediaType: requestBody.mediaType, presence: requestBody.presence, get: (args: unknown) => (args as { body?: unknown }).body } } : {}),
      ...(requestBody.kind === "json"
        ? { requestBody: { mediaType: requestBody.mediaType, codec: () => registry.get(requestBody.use.codecId), presence: requestBody.presence, nullable: requestBody.use.semanticNullable, get: (args: unknown) => (args as { body?: unknown }).body, ...maxDepthOf(document, requestBody.profileId) } }
        : {}),
      responses,
      requestExecution: op.security.requestExecution,
      requestHeaderAllowlist: op.security.requestHeaderAllowlist,
    });
  }

  return { apiId: document.apiId, semanticHash: document.semanticHash, registry, operations, document };
}

/** A codec context for interpreter callers (Explorer, runner) that carries the operation's profile id. */
export function contextFor(operation: OperationDescriptor, options: { readonly context?: Record<string, string>; readonly signal?: AbortSignal } = {}): CodecContext {
  return createCodecContext({ ...(operation.profileId === undefined ? {} : { profileId: operation.profileId }), ...(options.context !== undefined ? { context: options.context } : {}), ...(options.signal !== undefined ? { signal: options.signal } : {}) });
}
