using System.Globalization;
using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.TypeScript;

public sealed record TsGenerationOptions
{
    public required ModuleMode ModuleMode { get; init; }
    /// <summary>Import specifier of the runtime package.</summary>
    public string RuntimeImport { get; init; } = "@kkdev92/tisilia-runtime";
    /// <summary>Resolves a module artifact path (relative to the contract) to an import specifier from the output root; null when unavailable.</summary>
    public required Func<Module, Artifact, string?> ModuleImportResolver { get; init; }
    public required string GeneratorVersion { get; init; }
    public CoveragePolicy CoveragePolicy { get; init; } = CoveragePolicy.Development;
    /// <summary>The config's limits: the generated client's defaults when they differ from the runtime's.</summary>
    public Limits Limits { get; init; } = Limits.Default;
}

/// <summary>
/// TypeScript generation: <c>models/</c>, <c>codecs/</c>, <c>operations/</c>, <c>client.ts</c>, <c>registry.ts</c>,
/// <c>index.ts</c>. Output is deterministic (ids sorted ordinally, LF, UTF-8, no timestamps); the generator never
/// executes modules and never emits <c>any</c>/<c>unknown</c> for an undescribed type.
/// </summary>
public sealed class TsGenerator
{
    private readonly ContractIndex _index;
    private readonly ContractDocument _doc;
    private readonly TsGenerationOptions _options;
    private readonly DiagnosticBag _bag;
    private readonly Dictionary<string, string> _typeNames = new(StringComparer.Ordinal); // typeId → tsName
    private readonly Dictionary<string, string> _codecNames = new(StringComparer.Ordinal); // codecId → const name
    private readonly List<NameMapping> _nameMappings = [];
    private readonly Dictionary<string, (Module Module, Artifact Artifact, string Import, string Alias)> _moduleImports = new(StringComparer.Ordinal);
    private readonly List<(string Literal, string Name)> _boundContexts = []; // the codecs file's shared binding contexts, in first-use order

    private TsGenerator(ContractIndex index, TsGenerationOptions options, DiagnosticBag bag)
    {
        _index = index;
        _doc = index.Document;
        _options = options;
        _bag = bag;
    }

    private string Ext => ".js";

    public static IReadOnlyList<GeneratedFile>? Generate(ContractIndex index, TsGenerationOptions options, DiagnosticBag bag, out IReadOnlyList<NameMapping> nameMappings)
    {
        var generator = new TsGenerator(index, options, bag);
        var files = generator.Run();
        nameMappings = generator._nameMappings;
        return bag.HasErrors ? null : files;
    }

    private List<GeneratedFile> Run()
    {
        if (!AssignNames())
        {
            return [];
        }

        // each file imports only what it uses: generated code is compiled with the application's tsconfig (noUnusedLocals)
        var files = new List<GeneratedFile>
        {
            new("models/index.ts", TsImports.Prune(GenerateModels())),
            new("registry.ts", TsImports.Prune(GenerateRegistry())),
            new("codecs/index.ts", TsImports.Prune(GenerateCodecs())),
            new("operations/index.ts", TsImports.Prune(GenerateOperations())),
            new("client.ts", TsImports.Prune(GenerateClient())),
            new("index.ts", GenerateIndex()),
        };
        foreach (var file in files)
        {
            if (TsNames.IsWindowsReservedFileName(System.IO.Path.GetFileName(file.Path)))
            {
                _bag.Error(TisiliaCodes.OutputPath, "SV38", "", $"generated file name '{file.Path}' is a reserved Windows device name");
            }
        }

        return files;
    }

    // ------------------------------------------------------------------ names

    private bool AssignNames()
    {
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in _doc.Types.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var name = model.Shape is PrimitiveShape prim ? ScalarTsType(prim.PrimitiveId) : model.TsName;
            if (model.Shape is not PrimitiveShape && !TsNames.Claim(claimed, model, _bag))
            {
                continue;
            }

            _typeNames[model.Id] = name;
            if (model.Shape is not PrimitiveShape)
            {
                _nameMappings.Add(new NameMapping { Kind = NameMappingKind.Type, Id = model.Id, GeneratedName = name, Path = "models/index.ts" });
            }
        }

        var codecClaimed = new HashSet<string>(StringComparer.Ordinal);
        var typesById = _doc.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        bool IsScalarCodec(Codec codec) => typesById.TryGetValue(codec.TypeId, out var m) && m.Shape is PrimitiveShape;
        // builtin scalar codecs claim their names first (int32Codec, durationCodec): a model of the same name (an enum Duration) never takes
        // them over, and its own codec is numbered instead of failing generation
        foreach (var codec in _doc.Codecs.OrderBy(c => IsScalarCodec(c) ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            // scalar codecs are named after the scalar (int32Codec, float64Codec, charCodec): several scalars share one TypeScript type
            var typeBase = IsScalarCodec(codec) ? typesById[codec.TypeId].TsName : _typeNames.GetValueOrDefault(codec.TypeId, "codec");
            var baseName = TsNames.CamelCase(typeBase) + "Codec";
            var suffix = codec.Id[(codec.TypeId.Length)..].TrimStart('.');
            if (suffix.StartsWith("codec", StringComparison.Ordinal))
            {
                suffix = suffix["codec".Length..].TrimStart('.');
            }

            var stem = baseName + string.Concat(suffix.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries).Select(TsNames.Capitalize));
            var name = stem;
            for (var i = 2; !codecClaimed.Add(name); i++)
            {
                name = stem + i.ToString(CultureInfo.InvariantCulture);
            }

            _codecNames[codec.Id] = name;
        }

        foreach (var codec in _doc.Codecs.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            _nameMappings.Add(new NameMapping { Kind = NameMappingKind.Codec, Id = codec.Id, GeneratedName = _codecNames[codec.Id], Path = "codecs/index.ts" });
        }

        var opClaimed = new HashSet<string>(StringComparer.Ordinal);
        // names index.ts re-exports from operations/ and client.ts next to the models (`export *`): a model of the same name would make the
        // re-export ambiguous (TS2308), so the collision is reported here with the names involved
        var modelNames = _doc.Types.Where(t => t.Shape is not PrimitiveShape && _typeNames.ContainsKey(t.Id)).ToDictionary(t => _typeNames[t.Id], t => t.Id, StringComparer.Ordinal);
        var clientName = TsNames.Capitalize(TsNames.OperationMethodName(_doc.ApiId)) + "Client";
        foreach (var exported in new[] { clientName, "create" + clientName, "semanticHash", "apiId", "createRegistry", "codecs" })
        {
            if (modelNames.TryGetValue(exported, out var modelId))
            {
                _bag.Error(TisiliaCodes.NameCollision, "SV52", "/types", $"type '{modelId}': tsName '{exported}' is also a name the generated client exports", [modelId], "rename the type (a distinct CLR type name) or the API id");
            }
        }

        foreach (var op in _doc.Operations.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var name = TsNames.OperationMethodName(op.Id);
            if (!opClaimed.Add(name))
            {
                _bag.Error(TisiliaCodes.NameCollision, "SV52", "/operations", $"operation '{op.Id}': method name '{name}' collides with another operation", [op.Id]);
                continue;
            }
            if (op.Responses.Any(r => r.Body is SseResponseBody) && !opClaimed.Add(name + "Subscribe"))
            {
                _bag.Error(TisiliaCodes.NameCollision, "SV52", "/operations", $"operation '{op.Id}': subscription method name '{name}Subscribe' collides with another operation", [op.Id]);
            }

            foreach (var exported in new[] { TsNames.TypeName(op.Id, "Args"), TsNames.TypeName(op.Id, "Result"), name + "Operation" }.Concat(op.Responses.Any(r => r.Body is SseResponseBody) ? [TsNames.TypeName(op.Id, "EventData")] : []))
            {
                if (modelNames.TryGetValue(exported, out var modelId))
                {
                    _bag.Error(TisiliaCodes.NameCollision, "SV52", "/operations", $"operation '{op.Id}': the generated name '{exported}' is also the tsName of type '{modelId}'", [op.Id, modelId], "rename the operation id or the type");
                }
            }

            // the arguments object has one key per parameter name, plus `body` for a JSON body: the same name in two locations (a route `id`
            // and a query `id`) would be two members of one key
            var keys = op.Parameters.Select(p => (Name: p.Name, Where: Enum(p.Location) + " parameter")).Concat(op.RequestBody is not NoRequestBody ? [("body", "request body")] : []);
            foreach (var clash in keys.GroupBy(k => k.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                _bag.Error(TisiliaCodes.NameCollision, "SV52", "/operations", $"operation '{op.Id}': the generated arguments have one key '{clash.Key}' for {string.Join(" and ", clash.Select(c => c.Where))}", [op.Id],
                    "give one of them another name ([FromQuery(Name = \"…\")], [FromHeader(Name = \"…\")]) or rename the route value");
            }

            _nameMappings.Add(new NameMapping { Kind = NameMappingKind.Operation, Id = op.Id, GeneratedName = name, Path = "operations/index.ts" });
        }

        foreach (var module in _doc.Modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var artifact = module.Artifacts.FirstOrDefault(a => a.Target == ArtifactTarget.Browser) ?? module.Artifacts.FirstOrDefault(a => a.Target == ArtifactTarget.Node);
            if (artifact is null)
            {
                continue; // dotnet-only module (converter assembly); nothing to import
            }

            var errors = _bag.ErrorCount;
            var import = _options.ModuleImportResolver(module, artifact);
            if (import is null && _bag.ErrorCount > errors)
            {
                continue; // the resolver said why (a different digest, a path it rejects)
            }

            if (import is null)
            {
                _bag.Error(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"module '{module.Id}': artifact '{artifact.Path}' is not available locally; generated clients only bind pre-installed modules", [module.Id],
                    module.Id == Additional.AdditionalModule.ModuleId ? "install it next to the contract: tisilia codec install-additional --project <directory of the contract>" : null);
                continue;
            }

            var alias = "mod" + string.Concat(module.Id.Split(['.', '-', '@', '/'], StringSplitOptions.RemoveEmptyEntries).Select(TsNames.Capitalize));
            _moduleImports[module.Id] = (module, artifact, import, alias);
        }

        return !_bag.HasErrors;
    }

    private static string ScalarTsType(string primitiveId)
    {
        var name = primitiveId["tisilia.".Length..];
        name = name[..name.IndexOf('@', StringComparison.Ordinal)];
        return name switch
        {
            "string" => "string",
            "boolean" => "boolean",
            "char" => "string",
            "guid" => "Guid",
            "bytes" => "Uint8Array",
            "json-value" => "JsonValue",
            "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" => "number",
            "int64" => "Int64",
            "uint64" => "UInt64",
            "decimal" => "Decimal",
            "float32" or "float64" => "number",
            "date-only" => "DateOnly",
            "time-only" => "TimeOnly",
            "datetime" => "DateTime",
            "datetime-utc" => "DateTimeUtc",
            "datetime-unspecified" => "DateTimeUnspecified",
            "datetime-local-wire" => "DateTimeLocalWire",
            "datetime-offset" => "DateTimeOffset",
            "duration" => "Duration",
            _ => throw new InvalidOperationException("unknown scalar " + primitiveId),
        };
    }

    private static string ScalarName(string primitiveId)
    {
        var name = primitiveId["tisilia.".Length..];
        return name[..name.IndexOf('@', StringComparison.Ordinal)];
    }

    /// <summary>A type reference inside the models file: models by name, runtime types under their alias when a model shares their name.</summary>
    private string TypeRef(TypeUse use)
    {
        var name = _typeNames[use.TypeId];
        if (_index.Types[use.TypeId].Shape is PrimitiveShape)
        {
            name = _runtimeAliases.GetValueOrDefault(name, name);
        }

        return use.SemanticNullable ? name + " | null" : name;
    }

    /// <summary>Runtime type imports of the models file renamed because a model has their name (an enum Duration next to TimeSpan members).</summary>
    private readonly Dictionary<string, string> _runtimeAliases = new(StringComparer.Ordinal);

    private const string RuntimeTypeImports = "Guid, Int64, UInt64, Decimal, DateOnly, TimeOnly, DateTime, DateTimeUtc, DateTimeUnspecified, DateTimeLocalWire, DateTimeOffset, Duration, JsonValue, TisiliaMap";

    // ------------------------------------------------------------------ models

    private string GenerateModels()
    {
        // a model may carry the name of a runtime type (Duration, Guid, …): the models file then imports that runtime type under an alias;
        // every other generated file refers to models through the `models` namespace, so the bare runtime names stay unambiguous there
        var declared = _doc.Types.Where(t => t.Shape is not PrimitiveShape && _typeNames.ContainsKey(t.Id)).Select(t => _typeNames[t.Id]).ToHashSet(StringComparer.Ordinal);
        _runtimeAliases.Clear();
        var imports = new List<string>();
        foreach (var runtimeName in RuntimeTypeImports.Split(", "))
        {
            if (!declared.Contains(runtimeName))
            {
                imports.Add(runtimeName);
                continue;
            }

            var alias = "Tisilia" + runtimeName;
            while (declared.Contains(alias))
            {
                alias = "_" + alias;
            }

            _runtimeAliases[runtimeName] = alias;
            imports.Add(runtimeName + " as " + alias);
        }

        var w = new CodeWriter();
        w.Line("// Generated by Tisilia. Do not edit: owned by tisilia.generation-manifest.json.");
        w.Line($"import type {{ {string.Join(", ", imports)} }} from {TsNames.Quote(_options.RuntimeImport)};");
        w.Line();
        if (_doc.Types.Any(t => t.Shape is BrandShape))
        {
            w.Line("declare const brandTag: unique symbol;");
            w.Line();
        }

        var discriminators = DiscriminatorLiterals();
        foreach (var model in _doc.Types.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (model.Shape is PrimitiveShape)
            {
                continue;
            }

            var name = _typeNames[model.Id];
            var origin = $"{model.Id} ({model.ClrIdentity})";
            var doc = Documentation(model.Id);
            var (docText, memberDocs) = doc is null ? ("", new Dictionary<string, string>(StringComparer.Ordinal)) : DocumentationText.Split(doc.Description);
            JsDoc(w, doc?.Summary, docText, origin, Deprecated(docText));
            switch (model.Shape)
            {
                case ObjectShape obj:
                    w.Open($"export interface {name} {{");
                    foreach (var p in obj.Properties)
                    {
                        JsDoc(w, memberDocs.GetValueOrDefault(p.Name), Deprecated(memberDocs.GetValueOrDefault(p.Name)));
                        var type = TypeRef(p.Use);
                        if (discriminators.TryGetValue(model.Id, out var tag) && tag.Property == p.Name && p.Presence == Presence.Required && type == tag.Type)
                        {
                            type = tag.Literal;
                        }

                        w.Line($"readonly {TsNames.PropertyKey(p.Name)}{(p.Presence == Presence.Optional ? "?" : "")}: {type};");
                    }

                    if (obj.Extension is CaptureExtension capture)
                    {
                        w.Line($"/** JsonExtensionData: unknown members captured with their own codec. */");
                        w.Line($"readonly extensions?: ReadonlyMap<string, {TypeRef(capture.Value)}>;");
                    }

                    w.Close();
                    break;
                case ArrayShape arr:
                    w.Line($"export type {name} = readonly ({TypeRef(arr.Element)})[];");
                    break;
                case MapShape map:
                    w.Line($"export type {name} = {_runtimeAliases.GetValueOrDefault("TisiliaMap", "TisiliaMap")}<{TypeRef(map.Key)}, {TypeRef(map.Value)}>;");
                    break;
                case EnumShape en:
                {
                    var big = ScalarName(en.UnderlyingPrimitiveId) is "int64" or "uint64";
                    w.Line($"export type {name} = {(big ? "bigint" : "number")};");
                    // the const is a declaration of its own: a deprecated enum marks both
                    JsDoc(w, $"Known members of {name}; the value domain is the full {ScalarName(en.UnderlyingPrimitiveId)} range unless allowUndefinedInteger is false.", Deprecated(docText));
                    w.Open($"export const {name} = {{");
                    foreach (var m in en.Members)
                    {
                        JsDoc(w, memberDocs.GetValueOrDefault(m.Name), Deprecated(memberDocs.GetValueOrDefault(m.Name)));
                        w.Line($"{TsNames.ObjectLiteralKey(m.Name)}: {m.Value}{(big ? "n" : "")},");
                    }

                    w.Close("} as const;");
                    break;
                }

                case UnionShape union:
                    w.Line($"export type {name} = {string.Join(" | ", union.Variants.Select(v => TypeRef(v.Use)))};");
                    break;
                case BrandShape brand:
                    w.Line($"export type {name} = {TypeRef(brand.Base)} & {{ readonly [brandTag]: {TsNames.Quote(brand.BrandId)} }};");
                    break;
            }

            w.Line();
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ registry (module imports)

    private string GenerateRegistry()
    {
        var w = new CodeWriter();
        w.Line("// Generated by Tisilia. Execution registry: locally installed codec modules bound by id and export name.");
        w.Line("// Nothing is fetched from the contract; artifact digests are checked at generation time.");
        foreach (var (module, _, import, alias) in _moduleImports.Values.OrderBy(m => m.Module.Id, StringComparer.Ordinal))
        {
            w.Line($"import * as {alias} from {TsNames.Quote(import)};");
        }

        w.Line();
        // declared, not inferred: a namespace import's type cannot be inferred under isolatedDeclarations (TS9013)
        w.Open("export const modules: { readonly [moduleId: string]: { readonly version: string; readonly artifact: string; readonly digest: string; readonly exports: object } } = {");
        foreach (var (module, artifact, _, alias) in _moduleImports.Values.OrderBy(m => m.Module.Id, StringComparer.Ordinal))
        {
            w.Line($"{TsNames.Quote(module.Id)}: {{ version: {TsNames.Quote(module.Version)}, artifact: {TsNames.Quote(artifact.Path)}, digest: {TsNames.Quote(artifact.Digest)}, exports: {alias} }},");
        }

        w.Close("};");
        w.Line();
        w.Line("/** Resolves a module export; throws when the module or export is missing instead of guessing. */");
        w.Open("export function moduleExport(moduleId: string, exportName: string): unknown {");
        w.Line("const module = (modules as Record<string, { exports: Record<string, unknown> } | undefined>)[moduleId];");
        w.Line("if (module === undefined) throw new Error(`Tisilia module '${moduleId}' is not installed`);");
        w.Line("const value = module.exports[exportName];");
        w.Line("if (value === undefined) throw new Error(`Tisilia module '${moduleId}' has no export '${exportName}'`);");
        w.Line("return value;");
        w.Close();
        return w.ToString();
    }

    // ------------------------------------------------------------------ codecs

    private string GenerateCodecs()
    {
        _boundContexts.Clear();
        var w = new CodeWriter();
        foreach (var codec in _doc.Codecs.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var name = _codecNames[codec.Id];
            var model = _index.Types[codec.TypeId];
            var tsType = _typeNames[codec.TypeId];
            w.Line($"/** {codec.Id} ({codec.Origin}) for {codec.TypeId} */");
            if (codec.Origin != CodecOrigin.Builtin)
            {
                w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = {PairedCodecExpression(codec, tsType)};");
                w.Line();
                continue;
            }

            if (codec.Id.EndsWith(".nullable", StringComparison.Ordinal))
            {
                var inner = codec.Id[..^".nullable".Length];
                if (_codecNames.TryGetValue(inner, out var innerName))
                {
                    w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)} | null> = nullableCodec(() => {innerName}, {TsNames.Quote(codec.Id)});");
                    w.Line();
                    continue;
                }
            }

            switch (model.Shape)
            {
                case PrimitiveShape prim:
                    w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = scalarCodec<{TypeNameOf(codec.TypeId)}>({TsNames.Quote(ScalarName(prim.PrimitiveId))}, {{ id: {TsNames.Quote(codec.Id)}, typeId: {TsNames.Quote(codec.TypeId)}, numbers: {NumbersOf(codec)}{StringLengthOptions(codec)} }});");
                    break;
                case ObjectShape obj:
                    w.Open($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = objectCodec<{TypeNameOf(codec.TypeId)}>({{");
                    w.Line($"id: {TsNames.Quote(codec.Id)},");
                    w.Line($"typeId: {TsNames.Quote(codec.TypeId)},");
                    w.Open("properties: [");
                    var readWire = codec.Capabilities.Request is { } rq ? _index.Wires[rq.Wire.WireId].Shape as ObjectWire : null;
                    var writeWire = codec.Capabilities.Response is { } rs ? _index.Wires[rs.Wire.WireId].Shape as ObjectWire : null;
                    foreach (var p in obj.Properties)
                    {
                        var readPresence = readWire?.Properties.FirstOrDefault(x => x.Name == p.Name)?.Presence ?? p.Presence;
                        var writePresence = writeWire?.Properties.FirstOrDefault(x => x.Name == p.Name)?.Presence ?? p.Presence;
                        w.Line($"{{ name: {TsNames.Quote(p.Name)}, codec: () => {_codecNames[p.Use.CodecId]}, presence: {TsNames.Quote(Lower(p.Presence))}, readPresence: {TsNames.Quote(Lower(readPresence))}, writePresence: {TsNames.Quote(Lower(writePresence))}, nullable: {(p.Use.SemanticNullable ? "true" : "false")} }},");
                    }

                    w.Close("],");
                    var nameMatching = (readWire ?? writeWire)?.NameMatchingId;
                    var duplicates = (readWire ?? writeWire)?.DuplicatePolicyId;
                    w.Line($"nameMatching: {TsNames.Quote(ResolveNameMatching(nameMatching))},");
                    w.Line($"duplicates: {TsNames.Quote(ResolveDuplicates(duplicates))},");
                    w.Line($"readAdditional: {TsNames.Quote(readWire?.Additional.Kind ?? "ignore")},");
                    w.Line($"writeAdditional: {TsNames.Quote(writeWire?.Additional.Kind ?? "ignore")},");
                    if (obj.Extension is CaptureExtension capture)
                    {
                        w.Line($"extension: () => {_codecNames[capture.Value.CodecId]},");
                        w.Line("extensionProperty: \"extensions\",");
                    }

                    w.Line($"request: {(codec.Capabilities.Request is not null ? "true" : "false")},");
                    w.Line($"response: {(codec.Capabilities.Response is not null ? "true" : "false")},");
                    w.Close("});");
                    break;
                case ArrayShape arr:
                    // element types live in models/index.ts, so they are qualified like every other type reference of this file
                    w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = arrayCodec<{TypeRefModels(arr.Element with { SemanticNullable = false })}>({{ id: {TsNames.Quote(codec.Id)}, typeId: {TsNames.Quote(codec.TypeId)}, element: () => {_codecNames[arr.Element.CodecId]}, elementNullable: {(arr.Element.SemanticNullable ? "true" : "false")} }}) as Codec<{TypeNameOf(codec.TypeId)}>;");
                    break;
                case MapShape map:
                    w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = mapCodec<{TypeRefModels(map.Key)}, {TypeRefModels(map.Value with { SemanticNullable = false })}>({{ id: {TsNames.Quote(codec.Id)}, typeId: {TsNames.Quote(codec.TypeId)}, key: () => {_codecNames[map.Key.CodecId]}, value: () => {_codecNames[map.Value.CodecId]}, valueNullable: {(map.Value.SemanticNullable ? "true" : "false")}, comparer: {TsNames.Quote(ComparerOf(map.ComparerId))} }}) as Codec<{TypeNameOf(codec.TypeId)}>;");
                    break;
                case EnumShape en:
                {
                    var stringForm = codec.Capabilities.Response is { } cap && _index.Wires[cap.Wire.WireId].Shape is TokenUnionWire;
                    w.Open($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = enumCodec({{");
                    w.Line($"id: {TsNames.Quote(codec.Id)},");
                    w.Line($"typeId: {TsNames.Quote(codec.TypeId)},");
                    w.Line($"underlying: {TsNames.Quote(ScalarName(en.UnderlyingPrimitiveId))},");
                    w.Line($"flags: {(en.Flags ? "true" : "false")},");
                    w.Line($"allowUndefinedInteger: {(en.AllowUndefinedInteger ? "true" : "false")},");
                    w.Line($"stringForm: {(stringForm ? "true" : "false")},");
                    w.Line($"members: [{string.Join(", ", en.Members.Select(m => $"{{ name: {TsNames.Quote(m.Name)}, value: {m.Value}n{(m.SerializedName is null ? "" : ", serializedName: " + TsNames.Quote(m.SerializedName))} }}"))}],");
                    w.Close($"}}) as Codec<{TypeNameOf(codec.TypeId)}>;");
                    break;
                }

                case UnionShape union:
                {
                    var wire = (codec.Capabilities.Response ?? codec.Capabilities.Request) is { } cap ? _index.Wires[cap.Wire.WireId].Shape as TaggedUnionWire : null;
                    if (wire is null)
                    {
                        _bag.Error(TisiliaCodes.TaggedUnionInvalid, "SV13", "/codecs", $"union codec '{codec.Id}' has no tagged-union wire; token unions are only supported inside nullable branches", [codec.Id]);
                        break;
                    }

                    w.Open($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = taggedUnionCodec<{TypeNameOf(codec.TypeId)}>({{");
                    w.Line($"id: {TsNames.Quote(codec.Id)},");
                    w.Line($"typeId: {TsNames.Quote(codec.TypeId)},");
                    w.Line($"discriminator: {TsNames.Quote(wire.Discriminator)},");
                    w.Line($"tagProperty: {TsNames.Quote(wire.Discriminator)},");
                    w.Line($"variants: [{string.Join(", ", union.Variants.Select((v, i) => $"{{ tag: {TagLiteral(wire.Variants.ElementAtOrDefault(i)?.Tag, v.Tag)}, codec: () => {_codecNames[v.Use.CodecId]} }}"))}],");
                    w.Close("});");
                    break;
                }

                case BrandShape brand:
                    w.Line($"export const {name}: Codec<{TypeNameOf(codec.TypeId)}> = brandCodec<{TypeNameOf(codec.TypeId)}>({TsNames.Quote(codec.Id)}, {TsNames.Quote(codec.TypeId)}, () => {_codecNames[brand.Base.CodecId]});");
                    break;
            }

            w.Line();
        }

        w.Open("export function createRegistry(): CodecRegistry {");
        w.Line("const registry = new CodecRegistry();");
        foreach (var codec in _doc.Codecs.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            w.Line($"registry.register({_codecNames[codec.Id]} as Codec<unknown>);");
        }

        w.Line("return registry;");
        w.Close();
        w.Line();
        w.Line("export type { CodecContext, JsonValue };");

        var head = new CodeWriter();
        head.Line("// Generated by Tisilia. Codec instances built from the contract; recursion goes through lazy references.");
        head.Line($"import {{ scalarCodec, objectCodec, arrayCodec, mapCodec, nullableCodec, enumCodec, taggedUnionCodec, brandCodec, withContext, CodecRegistry }} from {TsNames.Quote(_options.RuntimeImport)};");
        head.Line($"import type {{ Codec, CodecContext, {RuntimeTypeImports} }} from {TsNames.Quote(_options.RuntimeImport)};");
        head.Line($"import type * as models from \"../models/index{Ext}\";");
        if (_moduleImports.Count > 0)
        {
            head.Line($"import {{ moduleExport }} from \"../registry{Ext}\";");
        }

        head.Line();
        // a binding's context entries are declared once and shared by every method of its codecs (an entry can be large: the zone ids
        // an environment-bound codec accepts)
        foreach (var (literal, name) in _boundContexts)
        {
            head.Line($"const {name}: Readonly<Record<string, string>> = {literal};");
        }

        if (_boundContexts.Count > 0)
        {
            head.Line();
        }

        return head.Raw(w.ToString()).ToString();
    }

    /// <summary>
    /// Type expression usable in codecs/operations files: a builtin scalar as its TypeScript primitive or runtime type (imported bare),
    /// every declared model through the `models` namespace import — decided by the model, never by the name, so that a model named like a
    /// runtime type (an enum Duration) is still `models.Duration`.
    /// </summary>
    private string TypeNameOf(string typeId) => _index.Types[typeId].Shape is PrimitiveShape ? _typeNames[typeId] : "models." + _typeNames[typeId];

    private string TypeRefModels(TypeUse use)
    {
        var name = TypeNameOf(use.TypeId);
        return use.SemanticNullable ? name + " | null" : name;
    }

    /// <summary>
    /// The discriminator of every union variant model as a literal type: a variant only ever holds its own tag, so `kind: "dog"` (or
    /// `$type: 1`) instead of `string` lets a union narrow by it (`if (animal.kind === "dog")`). A model reached with different tags
    /// keeps the declared type.
    /// </summary>
    private Dictionary<string, (string Property, string Literal, string Type)> DiscriminatorLiterals()
    {
        var literals = new Dictionary<string, (string Property, string Literal, string Type)>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var codec in _doc.Codecs)
        {
            if (!_index.Types.TryGetValue(codec.TypeId, out var model) || model.Shape is not UnionShape union)
            {
                continue;
            }

            foreach (var cap in new[] { codec.Capabilities.Response, codec.Capabilities.Request })
            {
                if (cap is null || !_index.Wires.TryGetValue(cap.Wire.WireId, out var wire) || wire.Shape is not TaggedUnionWire tagged)
                {
                    continue;
                }

                for (var i = 0; i < union.Variants.Count && i < tagged.Variants.Count; i++)
                {
                    (string Property, string Literal, string Type) literal = tagged.Variants[i].Tag switch
                    {
                        StringTag s => (tagged.Discriminator, TsNames.Quote(s.Value), "string"),
                        NumberTag n => (tagged.Discriminator, n.Text, "number"),
                        _ => (tagged.Discriminator, "", ""),
                    };
                    var variant = union.Variants[i].Use.TypeId;
                    if (literal.Literal.Length == 0 || (literals.TryGetValue(variant, out var known) && known != literal))
                    {
                        conflicting.Add(variant);
                    }
                    else
                    {
                        literals[variant] = literal;
                    }
                }
            }
        }

        foreach (var variant in conflicting)
        {
            literals.Remove(variant);
        }

        return literals;
    }

    private static string TagLiteral(DiscriminatorTag? wireTag, string fallback) => wireTag switch
    {
        NumberTag n => n.Text + "n",
        StringTag s => TsNames.Quote(s.Value),
        _ => TsNames.Quote(fallback),
    };

    private static string Lower(Presence p) => p == Presence.Required ? "required" : "optional";

    private string ResolveNameMatching(string? id)
    {
        if (id is null)
        {
            return "ordinal";
        }

        if (Builtins.TryGet(id, out var entry))
        {
            return entry.Name;
        }

        if (_index.Bindings.TryGetValue(id, out var binding) && binding.Implementation is BuiltinImpl b && Builtins.TryGet(b.Id, out var impl))
        {
            return impl.Name;
        }

        return "ordinal";
    }

    private string ResolveDuplicates(string? id)
    {
        if (id is null)
        {
            return "reject";
        }

        if (Builtins.TryGet(id, out var entry))
        {
            return entry.Name;
        }

        if (_index.Bindings.TryGetValue(id, out var binding) && binding.Implementation is BuiltinImpl b && Builtins.TryGet(b.Id, out var impl))
        {
            return impl.Name;
        }

        return "reject";
    }

    private string ComparerOf(string comparerId)
    {
        if (_index.Comparers.TryGetValue(comparerId, out var comparer))
        {
            if (comparer.BindingId == Builtins.ComparerOrdinalIgnoreCase)
            {
                return "ordinal-ignore-case";
            }

            if (comparer.BindingId == Builtins.ComparerStructural)
            {
                return "structural";
            }
        }

        return "ordinal";
    }

    private string NumbersOf(Codec codec)
    {
        var suffix = codec.Id.Contains(".codec.", StringComparison.Ordinal) ? codec.Id[(codec.Id.IndexOf(".codec.", StringComparison.Ordinal) + 7)..] : "";
        var flags = suffix.Split('.')[0];
        var r = flags.Contains('r') ? "true" : "false";
        var w = flags.Contains('w') ? "true" : "false";
        var n = flags.Contains('n') ? "true" : "false";
        return $"{{ readFromString: {r}, writeAsString: {w}, namedLiterals: {n} }}";
    }

    private string StringLengthOptions(Codec codec)
    {
        var cap = codec.Capabilities.Response ?? codec.Capabilities.Request;
        if (cap is null || _index.Wires[cap.Wire.WireId].Shape is not StringWire sw)
        {
            return "";
        }

        var parts = new List<string>();
        if (sw.MinUtf16Length is { } min)
        {
            parts.Add($"minUtf16Length: {min.ToString(CultureInfo.InvariantCulture)}");
        }

        if (sw.MaxUtf16Length is { } max)
        {
            parts.Add($"maxUtf16Length: {max.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
    }

    private string PairedCodecExpression(Codec codec, string tsType)
    {
        // the binding's non-secret context (the converter instance's settings) reaches the module through the codec context
        var ctx = BoundContext(codec);
        var parts = new List<string>
        {
            $"id: {TsNames.Quote(codec.Id)}",
            $"typeId: {TsNames.Quote(codec.TypeId)}",
            $"validateDomain: (value: unknown, context: CodecContext) => ({Impl(codec.ValidateDomain, "validator")} as {{ validateDomain(value: unknown, context: CodecContext): {TypeNameOf(codec.TypeId)} }}).validateDomain(value, {ctx})",
        };
        if (codec.Capabilities.Request is { } req)
        {
            parts.Add($"encodeRequest: (value: {TypeNameOf(codec.TypeId)}, context: CodecContext) => ({Impl(req.Implementation, "codec")} as {{ encodeRequest(value: {TypeNameOf(codec.TypeId)}, context: CodecContext): JsonValue }}).encodeRequest(value, {ctx})");
        }

        if (codec.Capabilities.Response is { } res)
        {
            parts.Add($"decodeResponse: (wire: JsonValue, context: CodecContext) => ({Impl(res.Implementation, "codec")} as {{ decodeResponse(wire: JsonValue, context: CodecContext): {TypeNameOf(codec.TypeId)} }}).decodeResponse(wire, {ctx})");
        }

        if (codec.Capabilities.RequestKey is { } rk)
        {
            parts.Add($"encodeKey: (value: {TypeNameOf(codec.TypeId)}, context: CodecContext) => ({Impl(rk.Implementation, "key-codec")} as {{ encodeKey(value: {TypeNameOf(codec.TypeId)}, context: CodecContext): string }}).encodeKey(value, {ctx})");
        }

        if (codec.Capabilities.ResponseKey is { } sk)
        {
            parts.Add($"decodeKey: (value: string, context: CodecContext) => ({Impl(sk.Implementation, "key-codec")} as {{ decodeKey(value: string, context: CodecContext): {TypeNameOf(codec.TypeId)} }}).decodeKey(value, {ctx})");
        }

        if (codec.Capabilities.RequestInput is { } input)
        {
            parts.Add($"parseRequestInput: (value: string | JsonValue, context: CodecContext) => ({Impl(input.Implementation, "request-input")} as {{ parseRequestInput(value: string | JsonValue, context: CodecContext): {TypeNameOf(codec.TypeId)} }}).parseRequestInput(value, {ctx})");
        }

        return "{\n  " + string.Join(",\n  ", parts) + ",\n}";
    }

    /// <summary>The codec context the module receives: the caller's context plus the binding's non-confidential entries (empty → the context itself).</summary>
    private string BoundContext(Codec codec)
    {
        if (!_index.Bindings.TryGetValue(codec.BindingId, out var binding))
        {
            return "context";
        }

        var entries = binding.Context.Where(e => !e.Confidential).ToList();
        if (entries.Count == 0)
        {
            return "context";
        }

        var literal = "{ " + string.Join(", ", entries.Select(e => TsNames.Quote(e.Name) + ": " + TsNames.Quote(e.Value))) + " }";
        var index = _boundContexts.FindIndex(c => c.Literal == literal);
        if (index < 0)
        {
            index = _boundContexts.Count;
            _boundContexts.Add((literal, "boundContext" + (index + 1).ToString(CultureInfo.InvariantCulture)));
        }

        return "withContext(context, " + _boundContexts[index].Name + ")";
    }

    private string Impl(Impl impl, string role)
    {
        switch (impl)
        {
            case ModuleImpl m:
                return $"moduleExport({TsNames.Quote(m.ModuleId)}, {TsNames.Quote(m.ExportName)})";
            case BuiltinImpl b:
                _bag.Error(TisiliaCodes.ModuleExportMismatch, "SV23", "/codecs", $"paired codec capability '{role}' cannot use builtin implementation '{b.Id}'", [b.Id]);
                return "undefined";
            default:
                return "undefined";
        }
    }

    // ------------------------------------------------------------------ operations

    private string GenerateOperations()
    {
        var w = new CodeWriter();
        w.Line("// Generated by Tisilia. Operation descriptors, argument types and case-narrowed result unions.");
        w.Line("// Evidence covers codec conformance only. HTTP routing and binary transfer are unobserved by the codec suite.");
        w.Line($"import {{ standardBinder, codecBinder, enumBinder }} from {TsNames.Quote(_options.RuntimeImport)};");
        w.Line($"import type {{ OperationDescriptor, RuntimeFailure, ResponseCaseResult, BodylessCaseResult, BufferedFile, ServerSentEvent, UploadFile, {RuntimeTypeImports} }} from {TsNames.Quote(_options.RuntimeImport)};");
        w.Line($"import type * as models from \"../models/index{Ext}\";");
        w.Line($"import * as codecs from \"../codecs/index{Ext}\";");
        w.Line();
        foreach (var op in _doc.Operations.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var method = TsNames.OperationMethodName(op.Id);
            var argsName = TsNames.TypeName(op.Id, "Args");
            var resultName = TsNames.TypeName(op.Id, "Result");
            var opDoc = Documentation(op.Id);
            JsDoc(w, opDoc?.Summary, opDoc?.Description, $"{op.Method} {op.Route} ({op.Id})", Deprecated(opDoc?.Description));
            w.Open($"export interface {argsName} {{");
            foreach (var p in op.Parameters)
            {
                JsDoc(w, Documentation(p.Id)?.Summary, Documentation(p.Id)?.Description, Deprecated(Documentation(p.Id)?.Description));
                var binder = _index.Binders[p.BinderId];
                var t = TypeRefModels(p.Use);
                if (binder.Cardinality == Cardinality.Repeated)
                {
                    t = $"readonly ({t})[]";
                }

                w.Line($"readonly {TsNames.PropertyKey(p.Name)}{(p.Presence == Presence.Optional ? "?" : "")}: {t};");
            }

            if (op.RequestBody is JsonRequestBody body)
            {
                w.Line($"readonly body{(body.Presence == Presence.Optional ? "?" : "")}: {TypeRefModels(body.Use)};");
            }
            else if (op.RequestBody is BinaryRequestBody binaryBody)
            {
                w.Line($"readonly body{(binaryBody.Presence == Presence.Optional ? "?" : "")}: Uint8Array;");
            }
            else if (op.RequestBody is FormRequestBody formBody)
            {
                w.Open($"readonly body{(formBody.Presence == Presence.Optional ? "?" : "")}: {{");
                void WriteFormType(IReadOnlyList<FormField> fields)
                {
                    foreach (var field in fields)
                    {
                        var key = $"readonly {TsNames.PropertyKey(field.Name)}{(field.Presence == Presence.Optional ? "?" : "")}: ";
                        if (field.Kind == "object")
                        {
                            w.Open(key + (field.Repeated ? "readonly {" : "{"));
                            WriteFormType(field.Fields!);
                            w.Close(field.Repeated ? "}[];" : "};");
                        }
                        else
                        {
                            var type = field.Kind == "file" ? "UploadFile" : TypeRefModels(field.Use!);
                            w.Line(key + (field.Repeated ? $"readonly ({type})[]" : type) + ";");
                        }
                    }
                }
                WriteFormType(formBody.Fields);
                w.Close("};");
            }

            w.Close();
            w.Line();
            var events = op.Responses.Select(r => r.Body).OfType<SseResponseBody>().ToList();
            if (events.Count > 0)
            {
                w.Line($"export type {TsNames.TypeName(op.Id, "EventData")} = {string.Join(" | ", events.Select(e => TypeRefModels(e.Use)).Distinct(StringComparer.Ordinal))};");
                w.Line();
            }
            var cases = op.Responses.Select(r => r.Body switch
            {
                NoResponseBody => $"BodylessCaseResult<{TsNames.Quote(r.Id)}, {r.Status}>",
                JsonResponseBody json => $"ResponseCaseResult<{TsNames.Quote(r.Id)}, {r.Status}, {TypeRefModels(json.Use)}>",
                TextResponseBody => $"ResponseCaseResult<{TsNames.Quote(r.Id)}, {r.Status}, string>",
                BinaryResponseBody => $"ResponseCaseResult<{TsNames.Quote(r.Id)}, {r.Status}, BufferedFile>",
                SseResponseBody sse => $"ResponseCaseResult<{TsNames.Quote(r.Id)}, {r.Status}, readonly ServerSentEvent<{TypeRefModels(sse.Use)}>[]>",
                _ => throw new InvalidOperationException(),
            });
            w.Line($"export type {resultName} =");
            foreach (var c in cases)
            {
                w.Line($"  | {c}");
            }

            w.Line("  | RuntimeFailure;");
            w.Line();
            w.Open($"export const {method}Operation: OperationDescriptor = {{");
            w.Line($"id: {TsNames.Quote(op.Id)},");
            w.Line($"method: {TsNames.Quote(op.Method.ToString())},");
            w.Line($"route: {TsNames.Quote(op.Route)},");
            w.Line($"routePlan: {JsonSerializer.Serialize(op.RoutePlan, TisiliaJson.Options)},");
            if (ProfileOf(op) is { Length: > 0 } profileId) { w.Line($"profileId: {TsNames.Quote(profileId)},"); }
            w.Open("parameters: [");
            foreach (var p in op.Parameters)
            {
                var binder = _index.Binders[p.BinderId];
                var primitiveBinder = _index.Types[binder.TypeId].Shape is PrimitiveShape;
                var scalar = _index.Types[binder.TypeId].Shape is PrimitiveShape prim ? ScalarName(prim.PrimitiveId) : "string";
                var binderOptions = new List<string> { $"id: {TsNames.Quote(binder.Id)}", $"cardinality: {TsNames.Quote(binder.Cardinality == Cardinality.Repeated ? "repeated" : "single")}", $"nullPolicy: {TsNames.Quote(Enum(binder.NullPolicy))}", $"emptyPolicy: {TsNames.Quote(Enum(binder.EmptyPolicy))}" };
                if (binder.NullLiteral is not null)
                {
                    binderOptions.Add($"nullLiteral: {TsNames.Quote(binder.NullLiteral)}");
                }

                // a module type's parameter is written with its request codec's canonical text (the binder of a nullable use wraps the codec itself)
                var valueCodec = p.Use.CodecId.EndsWith(".nullable", StringComparison.Ordinal) ? p.Use.CodecId[..^".nullable".Length] : p.Use.CodecId;
                if (_index.Types[binder.TypeId].Shape is EnumShape enumShape)
                {
                    // enum parameters: the C# member names Enum.TryParse reads, integers otherwise (MVC: defined values only)
                    binderOptions.Add($"flags: {(enumShape.Flags ? "true" : "false")}");
                    binderOptions.Add($"definedOnly: {(binder.GrammarId == Builtins.Grammar("enum-name") ? "true" : "false")}");
                }

                var binderExpression = primitiveBinder
                    ? $"standardBinder({TsNames.Quote(scalar)}, {TsNames.Quote(Enum(p.Location))}, {{ {string.Join(", ", binderOptions)} }})"
                    : _index.Types[binder.TypeId].Shape is EnumShape members
                        ? $"enumBinder(() => codecs.{_codecNames[valueCodec]}, [{string.Join(", ", members.Members.Select(m => $"[{TsNames.Quote(m.Name)}, {m.Value}n]"))}], {TsNames.Quote(Enum(p.Location))}, {{ {string.Join(", ", binderOptions)} }})"
                        : $"codecBinder(() => codecs.{_codecNames[valueCodec]}, {TsNames.Quote(Enum(p.Location))}, {{ {string.Join(", ", binderOptions)} }})";
                w.Line($"{{ id: {TsNames.Quote(p.Id)}, name: {TsNames.Quote(p.Name)}, location: {TsNames.Quote(Enum(p.Location))}, binder: {binderExpression}, presence: {TsNames.Quote(Lower(p.Presence))}, nullable: {(p.Use.SemanticNullable ? "true" : "false")}, get: (args) => (args as {argsName})[{TsNames.Quote(p.Name)}] }},");
            }

            w.Close("],");
            if (op.RequestBody is JsonRequestBody rb)
            {
                w.Line($"requestBody: {{ mediaType: {TsNames.Quote(rb.MediaType)}, codec: () => codecs.{_codecNames[rb.Use.CodecId]}, presence: {TsNames.Quote(Lower(rb.Presence))}, nullable: {(rb.Use.SemanticNullable ? "true" : "false")}, get: (args) => (args as {argsName}).body{(_index.Profiles.TryGetValue(rb.ProfileId, out var bodyProfile) ? ", maxDepth: " + bodyProfile.Options.MaxDepthEffective.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")} }},");
            }

            if (op.RequestBody is BinaryRequestBody rawBody)
            {
                w.Line($"requestBody: {{ kind: \"binary\", mediaType: {TsNames.Quote(rawBody.MediaType)}, presence: {TsNames.Quote(Lower(rawBody.Presence))}, get: (args) => (args as {argsName}).body }},");
            }
            if (op.RequestBody is FormRequestBody form)
            {
                w.Open($"requestBody: {{ kind: \"form\", mediaType: {TsNames.Quote(form.MediaType)}, presence: {TsNames.Quote(Lower(form.Presence))}, get: (args) => (args as {argsName}).body, fields: [");
                string FormDescriptor(FormField field)
                {
                    var scalar = field.Use is null ? "" : _index.Types[field.Use.TypeId].Shape is EnumShape en
                        ? $", format: enumBinder<unknown>(() => codecs.{_codecNames[field.Use.CodecId]}, [{string.Join(", ", en.Members.Select(m => $"[{TsNames.Quote(m.Name)}, {m.Value}n]"))}], \"query\", {{ flags: {(en.Flags ? "true" : "false")}, definedOnly: {(field.EnumDefinedOnly ? "true" : "false")} }}).format"
                        : $", scalar: {TsNames.Quote(ScalarName(((PrimitiveShape)_index.Types[field.Use.TypeId].Shape).PrimitiveId))}";
                    var children = field.Fields is null ? "" : $", fields: [{string.Join(", ", field.Fields.Select(FormDescriptor))}]";
                    var wire = field.WireName is null ? "" : $", wireName: {TsNames.Quote(field.WireName)}";
                    return $"{{ name: {TsNames.Quote(field.Name)}, kind: {TsNames.Quote(field.Kind)}, presence: {TsNames.Quote(Lower(field.Presence))}, repeated: {(field.Repeated ? "true" : "false")}{(field.RejectBlank ? ", rejectBlank: true" : "")}{(field.Indexed ? ", indexed: true" : "")}{wire}{children}{scalar} }}";
                }
                foreach (var field in form.Fields) { w.Line(FormDescriptor(field) + ","); }
                w.Close("] },");
            }
            w.Open("responses: [");
            foreach (var r in op.Responses)
            {
                var bodyExpr = r.Body switch
                {
                    NoResponseBody => "{ kind: \"none\" }",
                    JsonResponseBody json => $"{{ kind: \"json\", profileId: {TsNames.Quote(json.ProfileId)}, mediaType: {TsNames.Quote(json.MediaType)}, codec: () => codecs.{_codecNames[json.Use.CodecId]}, nullable: {(json.Use.SemanticNullable ? "true" : "false")} }}",
                    TextResponseBody text => $"{{ kind: \"text\", mediaType: {TsNames.Quote(text.MediaType)} }}",
                    BinaryResponseBody binary => $"{{ kind: \"binary\", mediaType: {TsNames.Quote(binary.MediaType)} }}",
                    SseResponseBody sse => $"{{ kind: \"sse\", mediaType: \"text/event-stream\", dataFormat: {TsNames.Quote(sse.DataFormat)}, codec: () => codecs.{_codecNames[sse.Use.CodecId]}, nullable: {(sse.Use.SemanticNullable ? "true" : "false")}{(sse.ProfileId is null ? "" : ", profileId: " + TsNames.Quote(sse.ProfileId))} }}",
                    _ => throw new InvalidOperationException(),
                };
                w.Line($"{{ caseId: {TsNames.Quote(r.Id)}, status: {r.Status}, body: {bodyExpr}, hydration: {TsNames.Quote(Enum(r.Hydration))}, exposedHeaders: [{string.Join(", ", r.ExposedHeaders.Select(TsNames.Quote))}] }},");
            }

            w.Close("],");
            w.Line($"requestExecution: {TsNames.Quote(Enum(op.Security.RequestExecution))},");
            w.Line($"requestHeaderAllowlist: [{string.Join(", ", op.Security.RequestHeaderAllowlist.Select(TsNames.Quote))}],");
            w.Close("};");
            w.Line();
        }

        return w.ToString();
    }

    private string ProfileOf(Operation op)
    {
        if (op.RequestBody is JsonRequestBody rb)
        {
            return rb.ProfileId;
        }

        foreach (var r in op.Responses)
        {
            if (r.Body is SseResponseBody { ProfileId: not null } sse) { return sse.ProfileId; }
            if (r.Body is JsonResponseBody json)
            {
                return json.ProfileId;
            }
        }

        return "";
    }

    private static string Enum<T>(T value) where T : struct, System.Enum
    {
        var member = typeof(T).GetField(value.ToString()!);
        var attr = member?.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute), false)
            .OfType<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>().FirstOrDefault();
        return attr?.Name ?? value.ToString()!;
    }

    // ------------------------------------------------------------------ client

    private string GenerateClient()
    {
        var w = new CodeWriter();
        var clientName = TsNames.Capitalize(TsNames.OperationMethodName(_doc.ApiId)) + "Client";
        w.Line("// Generated by Tisilia. One method per operation; the same runtime pipeline the Explorer uses.");
        w.Line($"import {{ execute, subscribe }} from {TsNames.Quote(_options.RuntimeImport)};");
        w.Line($"import type {{ ClientOptions, EventSink, SubscriptionResult }} from {TsNames.Quote(_options.RuntimeImport)};");
        w.Line($"import * as operations from \"./operations/index{Ext}\";");
        w.Line();
        w.Line($"export const semanticHash = {TsNames.Quote(_doc.SemanticHash)};");
        w.Line($"export const apiId = {TsNames.Quote(_doc.ApiId)};");
        w.Line();
        w.Open($"export interface {clientName} {{");
        foreach (var op in _doc.Operations.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var method = TsNames.OperationMethodName(op.Id);
            var opDoc = Documentation(op.Id);
            var caseDocs = op.Responses.Select(r => Documentation(r.Id) is { Summary.Length: > 0 } rd ? $"- `{r.Id}` ({r.Status}): {OneLine(rd.Summary)}" : null).OfType<string>().ToList();
            if (opDoc is not null || caseDocs.Count > 0)
            {
                JsDoc(w, opDoc?.Summary, opDoc?.Description, caseDocs.Count > 0 ? string.Join("\n", caseDocs) : null, $"{op.Method} {op.Route} ({op.Id})", Deprecated(opDoc?.Description));
            }

            // an operation without required arguments can be called without an arguments object (client.usersList())
            w.Line($"{method}(args{(ArgsOptional(op) ? "?" : "")}: operations.{TsNames.TypeName(op.Id, "Args")}, overrides?: Partial<ClientOptions>): Promise<operations.{TsNames.TypeName(op.Id, "Result")}>;");
            if (op.Responses.Any(r => r.Body is SseResponseBody))
            {
                w.Line($"{method}Subscribe(onEvent: EventSink<operations.{TsNames.TypeName(op.Id, "EventData")}>, args{(ArgsOptional(op) ? "?" : "")}: operations.{TsNames.TypeName(op.Id, "Args")}, overrides?: Partial<ClientOptions>): Promise<SubscriptionResult>;");
            }
        }

        w.Close();
        w.Line();
        var limits = _options.Limits;
        if (limits != Limits.Default)
        {
            w.Line("/** The limits of the generation config; the client's and each call's limits override them entry by entry. */");
            w.Line($"const configuredLimits = {{ maxBodyBytes: {Integer(limits.MaxBodyBytes)}, maxDepth: {Integer(limits.MaxDepth)}, maxTokens: {Integer(limits.MaxTokens)}, maxNumberCharacters: {Integer(limits.MaxNumberCharacters)}, timeoutMs: {Integer(limits.TimeoutMs)}, maxDiagnosticBytes: {Integer(limits.MaxDiagnosticBytes)} }};");
            w.Line();
        }

        w.Line("/** Creates a client bound to an explicitly allowed base origin. A call's overrides replace the client's options; limits merge entry by entry. */");
        w.Open($"export function create{clientName}(options: ClientOptions): {clientName} {{");
        w.Line($"const merged = (overrides: Partial<ClientOptions> | undefined): ClientOptions => ({{ ...options, ...overrides, limits: {{ {(limits != Limits.Default ? "...configuredLimits, " : "")}...options.limits, ...overrides?.limits }} }});");
        w.Open("return {");
        foreach (var op in _doc.Operations.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var method = TsNames.OperationMethodName(op.Id);
            w.Line($"{method}: (args, overrides) => execute(operations.{method}Operation, args{(ArgsOptional(op) ? " ?? {}" : "")}, merged(overrides)) as Promise<operations.{TsNames.TypeName(op.Id, "Result")}>,");
            if (op.Responses.Any(r => r.Body is SseResponseBody))
            {
                w.Line($"{method}Subscribe: (onEvent, args, overrides) => subscribe(operations.{method}Operation, args{(ArgsOptional(op) ? " ?? {}" : "")}, merged(overrides), onEvent),");
            }
        }

        w.Close("};");
        w.Close();
        return w.ToString();

        static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The contract's documentation entry for an id, if any.</summary>
    private DocumentationEntry? Documentation(string id)
    {
        _documentation ??= _doc.Documentation.GroupBy(d => d.TargetId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _documentation.GetValueOrDefault(id);
    }

    private Dictionary<string, DocumentationEntry>? _documentation;

    /// <summary>A JSDoc block of Markdown paragraphs, blank lines between them; one single-line paragraph stays a one-line comment.</summary>
    private static void JsDoc(CodeWriter w, params string?[] paragraphs)
    {
        var kept = paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => TsNames.CommentText(p!.Replace("\r\n", "\n", StringComparison.Ordinal).Trim())).ToList();
        if (kept.Count == 0)
        {
            return;
        }

        if (kept.Count == 1 && !kept[0].Contains('\n', StringComparison.Ordinal))
        {
            w.Line($"/** {kept[0]} */");
            return;
        }

        w.Line("/**");
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0)
            {
                w.Line(" *");
            }

            foreach (var line in kept[i].Split('\n'))
            {
                w.Line(line.Length == 0 ? " *" : " * " + line.TrimEnd());
            }
        }

        w.Line(" */");
    }

    private static string OneLine(string text) => string.Join(" ", text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The JSDoc tag of a deprecated target (its text starts with <see cref="DocumentationText.DeprecatedMark"/>): editors strike its uses through.</summary>
    private static string? Deprecated(string? text) => DocumentationText.IsDeprecated(text) ? "@deprecated" : null;

    /// <summary>No parameter and no request body is required: the arguments object may be omitted.</summary>
    private static bool ArgsOptional(Operation op) => op.Parameters.All(p => p.Presence == Presence.Optional) && op.RequestBody is not JsonRequestBody { Presence: Presence.Required } and not BinaryRequestBody { Presence: Presence.Required } and not FormRequestBody { Presence: Presence.Required };

    private string GenerateIndex()
    {
        var w = new CodeWriter();
        w.Line("// Generated by Tisilia.");
        w.Line($"export * from \"./models/index{Ext}\";");
        w.Line($"export * from \"./operations/index{Ext}\";");
        w.Line($"export * from \"./client{Ext}\";");
        w.Line($"export {{ createRegistry }} from \"./codecs/index{Ext}\";");
        w.Line($"export * as codecs from \"./codecs/index{Ext}\";");
        return w.ToString();
    }
}
