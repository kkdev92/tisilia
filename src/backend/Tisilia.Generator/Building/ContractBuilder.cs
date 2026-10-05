using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Building;

/// <summary>Effective number handling of one usage position (global options merged with type/member attributes).</summary>
public readonly record struct NumberProfile(bool ReadFromString, bool WriteAsString, bool NamedLiterals)
{
    public static NumberProfile Strict => new(false, false, false);
    public static NumberProfile Web => new(true, false, false);

    public string Suffix => (ReadFromString, WriteAsString, NamedLiterals) switch
    {
        (false, false, false) => "",
        _ => "." + (ReadFromString ? "r" : "") + (WriteAsString ? "w" : "") + (NamedLiterals ? "n" : ""),
    };
}

public enum Direction
{
    Request,
    Response,
}

/// <summary>One property of an object model as seen by the builder: domain use/presence and per-direction wire presence.</summary>
public sealed record PropertySpec(string Name, TypeUse Use, Presence DomainPresence, Presence ReadPresence, Presence WritePresence);

/// <summary>
/// Assembles a <c>tisilia.contract</c> document from standard building blocks. Used by the ASP.NET Core exporter and by
/// tests; it only produces data and computes the contract's digests at the end.
/// </summary>
public sealed class ContractBuilder
{
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Model> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Wire> _wires = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Codec> _codecs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Equivalence> _equivalences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Projection> _projections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Contract.Comparer> _comparers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binder> _binders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResultAdapter> _resultAdapters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);
    private readonly List<DocumentationEntry> _documentation = [];

    public ContractBuilder(string apiId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiId);
        ApiId = apiId;
    }

    public string ApiId { get; }

    public const string StdPrefix = "std.";

    // ------------------------------------------------------------------ registries

    public void AddProfile(Profile profile) => _profiles[profile.Id] = profile;
    public void AddType(Model model) => _types[model.Id] = model;
    public void AddWire(Wire wire) => _wires[wire.Id] = wire;
    public void AddCodec(Codec codec)
    {
        if (_pendingProfileTags.Remove(codec.Id, out var tags))
        {
            codec = codec with { ProfileIds = [.. codec.ProfileIds, .. tags.Where(t => !codec.ProfileIds.Contains(t))] };
        }

        _codecs[codec.Id] = codec;
        if (_pendingNullable.Remove(codec.Id))
        {
            MaterializeNullable(codec);
        }
    }

    private readonly Dictionary<string, HashSet<string>> _pendingProfileTags = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that a profile uses a codec (the exporter calls it for every codec it maps in a profile, builtin ones included), so
    /// that <c>profileIds</c> names the profiles whose serializer options the codec was derived from and the conformance suite
    /// runs the codec under a profile the runner can serve. A codec that does not exist yet (recursion) is tagged when added.
    /// </summary>
    public void TagCodecProfile(string codecId, string profileId)
    {
        if (_codecs.TryGetValue(codecId, out var codec))
        {
            if (!codec.ProfileIds.Contains(profileId))
            {
                _codecs[codecId] = codec with { ProfileIds = [.. codec.ProfileIds, profileId] };
            }

            return;
        }

        if (!_pendingProfileTags.TryGetValue(codecId, out var set))
        {
            _pendingProfileTags[codecId] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(profileId);
    }
    public void AddBinding(Binding binding) => _bindings[binding.Id] = binding;

    /// <summary>
    /// An existing binding with the same kind, implementation, settings digest and context — the SV24 claim key — or null.
    /// Profiles with identical settings share such a binding instead of claiming the same scope twice.
    /// </summary>
    public Binding? FindEquivalentBinding(Binding binding)
    {
        foreach (var existing in _bindings.Values)
        {
            if (existing.Kind == binding.Kind && existing.SettingsDigest == binding.SettingsDigest && ImplEquals(existing.Implementation, binding.Implementation)
                && existing.Context.Count == binding.Context.Count
                && existing.Context.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name + "=" + c.Value)
                    .SequenceEqual(binding.Context.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name + "=" + c.Value), StringComparer.Ordinal))
            {
                return existing;
            }
        }

        return null;

        static bool ImplEquals(Impl a, Impl b) => (a, b) switch
        {
            (BuiltinImpl x, BuiltinImpl y) => x.Id == y.Id,
            (ModuleImpl x, ModuleImpl y) => x.ModuleId == y.ModuleId && x.ExportName == y.ExportName,
            _ => false,
        };
    }
    public void AddEquivalence(Equivalence equivalence) => _equivalences[equivalence.Id] = equivalence;
    public void AddProjection(Projection projection) => _projections[projection.Id] = projection;
    public void AddComparer(Contract.Comparer comparer) => _comparers[comparer.Id] = comparer;
    public void AddBinder(Binder binder) => _binders[binder.Id] = binder;
    public void AddResultAdapter(ResultAdapter adapter) => _resultAdapters[adapter.Id] = adapter;
    public void AddOperation(Operation operation) => _operations[operation.Id] = operation;
    public void AddModule(Module module) => _modules[module.Id] = module;
    public Module? GetModule(string id) => _modules.GetValueOrDefault(id);
    public void AddDocumentation(string targetId, string summary, string description = "") => _documentation.Add(new DocumentationEntry { TargetId = targetId, Summary = summary, Description = description });

    public bool HasType(string id) => _types.ContainsKey(id);

    /// <summary>Whether another model already uses this TypeScript name (SV52: names differing only by case collide too).</summary>
    /// <remarks>Builtin scalar models do not count: they are never declared in the generated models (they map to runtime types).</remarks>
    public bool IsTsNameTaken(string tsName, string exceptModelId) => TypesNamed(tsName).Any(t => t.Id != exceptModelId);

    /// <summary>The declared (non-scalar) models that use this TypeScript name, compared like SV52 (case-insensitive).</summary>
    public IEnumerable<Model> TypesNamed(string tsName) => _types.Values.Where(t => t.Shape is not PrimitiveShape && string.Equals(t.TsName, tsName, StringComparison.OrdinalIgnoreCase));
    public bool HasCodec(string id) => _codecs.ContainsKey(id);
    public Model? GetType(string id) => _types.GetValueOrDefault(id);
    public Codec? GetCodec(string id) => _codecs.GetValueOrDefault(id);
    public Equivalence? GetEquivalence(string id) => _equivalences.GetValueOrDefault(id);
    public IEnumerable<Profile> Profiles => _profiles.Values;

    // ------------------------------------------------------------------ standard scalars

    /// <summary>Ensures model, wires, codec and equivalences for a builtin scalar exist and returns a type use.</summary>
    public TypeUse Scalar(string name, NumberProfile numberProfile = default, bool nullable = false)
    {
        if (Array.IndexOf(Builtins.ScalarNames, name) < 0)
        {
            throw new ArgumentException($"'{name}' is not a builtin scalar", nameof(name));
        }

        var isNumber = Builtins.IsNumberTokenScalar(name);
        var isFloat = name is "float32" or "float64";
        var np = isNumber ? numberProfile : NumberProfile.Strict;
        if (!isFloat)
        {
            np = np with { NamedLiterals = false };
        }

        var typeId = StdPrefix + name;
        if (!_types.ContainsKey(typeId))
        {
            _types[typeId] = new Model
            {
                Id = typeId,
                TsName = TsNameOf(name),
                ClrIdentity = ClrIdentityOf(name),
                Shape = new PrimitiveShape { PrimitiveId = Builtins.Scalar(name) },
            };
            EnsureScalarEquivalences(name, typeId);
        }

        var codecId = typeId + ".codec" + np.Suffix;
        if (!_codecs.ContainsKey(codecId))
        {
            var readWire = EnsureScalarWire(name, np, WireDirection.ServerRead);
            var writeWire = EnsureScalarWire(name, np, WireDirection.ServerWrite);
            var caps = new Capabilities
            {
                Request = new ValueCapability
                {
                    Wire = new WireRef { WireId = readWire, Direction = WireDirection.ServerRead },
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(name, "encode") },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = typeId + ".request",
                    DomainRuleId = Builtins.DomainRule(name),
                },
                Response = new ValueCapability
                {
                    Wire = new WireRef { WireId = writeWire, Direction = WireDirection.ServerWrite },
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(name, "decode") },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = typeId + ".response",
                    DomainRuleId = Builtins.DomainRule(name),
                },
                // json-value is not a key; byte[] has no System.Text.Json key converter (ByteArrayConverter lacks Read/WriteAsPropertyName)
                RequestKey = name is "json-value" or "bytes" ? null : new KeyCapability
                {
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(name, "encode-key") },
                    GrammarId = Builtins.KeyGrammar(name),
                    EquivalenceId = typeId + ".key",
                    Collision = "reject",
                },
                ResponseKey = name is "json-value" or "bytes" ? null : new KeyCapability
                {
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(name, "decode-key") },
                    GrammarId = Builtins.KeyGrammar(name),
                    EquivalenceId = typeId + ".key",
                    Collision = "reject",
                },
                RequestInput = new InputCapability
                {
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(name, "parse-input") },
                    InputKind = name == "json-value" ? InputKind.JsonValue : InputKind.Text,
                    EditorId = name == "json-value" ? Builtins.EditorJson : Builtins.EditorText,
                },
            };
            _codecs[codecId] = new Codec
            {
                Id = codecId,
                TypeId = typeId,
                Origin = CodecOrigin.Builtin,
                BindingId = Builtins.BindingFor(name),
                ValidateDomain = new BuiltinImpl { Id = Builtins.CodecImpl(name, "validate") },
                Capabilities = caps,
                Dependencies = [],
                ProfileIds = [],
            };
        }

        return new TypeUse { TypeId = typeId, CodecId = codecId, SemanticNullable = nullable };
    }

    /// <summary>A scalar use whose wire is nullable in the given direction (token-union with a null branch); the codec passes null through (bypass).</summary>
    public TypeUse NullableScalar(string name, NumberProfile numberProfile = default)
    {
        var plain = Scalar(name, numberProfile);
        var codecId = plain.CodecId + ".nullable";
        if (!_codecs.ContainsKey(codecId))
        {
            var baseCodec = _codecs[plain.CodecId];
            var readWire = EnsureNullableWire(baseCodec.Capabilities.Request!.Wire.WireId, WireDirection.ServerRead);
            var writeWire = EnsureNullableWire(baseCodec.Capabilities.Response!.Wire.WireId, WireDirection.ServerWrite);
            _codecs[codecId] = baseCodec with
            {
                Id = codecId,
                Capabilities = baseCodec.Capabilities with
                {
                    Request = baseCodec.Capabilities.Request with { Wire = new WireRef { WireId = readWire, Direction = WireDirection.ServerRead }, NullBehavior = NullBehavior.Bypass },
                    Response = baseCodec.Capabilities.Response with { Wire = new WireRef { WireId = writeWire, Direction = WireDirection.ServerWrite }, NullBehavior = NullBehavior.Bypass },
                    RequestKey = null,
                    ResponseKey = null,
                },
            };
        }

        return new TypeUse { TypeId = plain.TypeId, CodecId = codecId, SemanticNullable = true };
    }

    /// <summary>
    /// Wraps a codec's value wires in a null branch and returns a nullable use of the same type. When the base codec is still
    /// being registered (a paired type that refers to itself), the wrapper is created as soon as the codec is added.
    /// </summary>
    public TypeUse Nullable(TypeUse use)
    {
        if (use.SemanticNullable)
        {
            return use;
        }

        if (_codecs.TryGetValue(use.CodecId, out var baseCodec))
        {
            MaterializeNullable(baseCodec);
        }
        else
        {
            _pendingNullable.Add(use.CodecId);
        }

        return use with { CodecId = use.CodecId + ".nullable", SemanticNullable = true };
    }

    private readonly HashSet<string> _pendingNullable = new(StringComparer.Ordinal);

    private void MaterializeNullable(Codec baseCodec)
    {
        var codecId = baseCodec.Id + ".nullable";
        if (!_codecs.ContainsKey(codecId))
        {
            var caps = baseCodec.Capabilities;
            var request = caps.Request is null ? null : caps.Request with
            {
                Wire = new WireRef { WireId = EnsureNullableWire(caps.Request.Wire.WireId, WireDirection.ServerRead), Direction = WireDirection.ServerRead },
                NullBehavior = NullBehavior.Bypass,
            };
            var response = caps.Response is null ? null : caps.Response with
            {
                Wire = new WireRef { WireId = EnsureNullableWire(caps.Response.Wire.WireId, WireDirection.ServerWrite), Direction = WireDirection.ServerWrite },
                NullBehavior = NullBehavior.Bypass,
            };
            // the wrapper reaches the same child codecs as the codec it wraps; a recursive type reaches its own wrapper, which is never listed (SV14)
            _codecs[codecId] = baseCodec with
            {
                Id = codecId,
                Capabilities = caps with { Request = request, Response = response, RequestKey = null, ResponseKey = null },
                Dependencies = baseCodec.Dependencies.Where(d => d != codecId).ToList(),
            };
        }
    }

    private string EnsureNullableWire(string innerWireId, WireDirection direction)
    {
        // a lossless JSON wire (json-value) already admits the null token: the nullable wrapper keeps the wire and the codec's
        // NullBehavior.Bypass maps that token to the semantic null (JsonElement?, JsonNode?, object? read JSON null as null)
        if (_wires[innerWireId].Shape is LosslessJsonWire)
        {
            return innerWireId;
        }

        var id = innerWireId + ".or-null";
        if (!_wires.ContainsKey(id))
        {
            var nullId = StdPrefix + "null." + (direction == WireDirection.ServerRead ? "read" : "write");
            _wires.TryAdd(nullId, new Wire { Id = nullId, Direction = direction, Shape = new NullWire() });
            var inner = _wires[innerWireId];
            var branches = new List<TokenBranch> { new() { Token = JsonToken.Null, Wire = new WireRef { WireId = nullId, Direction = direction } } };
            if (inner.Shape is TokenUnionWire tu)
            {
                branches.AddRange(tu.Branches);
            }
            else
            {
                branches.Add(new TokenBranch { Token = RootTokenOf(inner), Wire = new WireRef { WireId = innerWireId, Direction = direction } });
            }

            _wires[id] = new Wire { Id = id, Direction = direction, Shape = new TokenUnionWire { Branches = branches } };
        }

        return id;
    }

    private static JsonToken RootTokenOf(Wire wire) => wire.Shape switch
    {
        NullWire => JsonToken.Null,
        BooleanWire => JsonToken.Boolean,
        StringWire => JsonToken.String,
        NumberWire => JsonToken.Number,
        LiteralWire lit => lit.Value.Token,
        ArrayWire => JsonToken.Array,
        ObjectWire or TaggedUnionWire => JsonToken.Object,
        _ => throw new InvalidOperationException($"wire '{wire.Id}' has no single root token"),
    };

    private string EnsureScalarWire(string name, NumberProfile np, WireDirection direction)
    {
        var dir = direction == WireDirection.ServerRead ? "read" : "write";
        var isNumber = Builtins.IsNumberTokenScalar(name);
        var isFloat = name is "float32" or "float64";
        if (!isNumber)
        {
            var id = $"{StdPrefix}{name}.{dir}";
            if (!_wires.ContainsKey(id))
            {
                WireShape shape = name switch
                {
                    "boolean" => new BooleanWire(),
                    "json-value" => new LosslessJsonWire { GrammarId = Builtins.JsonRfc8259 },
                    "char" => new StringWire { GrammarId = Builtins.Grammar(name), MinUtf16Length = 1, MaxUtf16Length = 1 },
                    _ => new StringWire { GrammarId = Builtins.Grammar(name) },
                };
                _wires[id] = new Wire { Id = id, Direction = direction, Shape = shape };
            }

            return id;
        }

        var numberId = $"{StdPrefix}{name}.{dir}";
        _wires.TryAdd(numberId, new Wire { Id = numberId, Direction = direction, Shape = new NumberWire { GrammarId = Builtins.Grammar(name) } });
        var stringId = $"{StdPrefix}{name}.{dir}.string";
        var namedId = $"{StdPrefix}{name}.{dir}.named";
        var readFromString = direction == WireDirection.ServerRead && np.ReadFromString;
        var writeAsString = direction == WireDirection.ServerWrite && np.WriteAsString;
        var named = isFloat && np.NamedLiterals;
        if (writeAsString && !named)
        {
            _wires.TryAdd(stringId, new Wire { Id = stringId, Direction = direction, Shape = new StringWire { GrammarId = Builtins.Grammar(name + "-string") } });
            return stringId;
        }

        if (!readFromString && !named && !writeAsString)
        {
            return numberId;
        }

        // token-union of number and string forms
        var unionId = $"{StdPrefix}{name}.{dir}{np.Suffix}";
        if (!_wires.ContainsKey(unionId))
        {
            var stringGrammar = named && !readFromString ? Builtins.Grammar(name + "-named") : Builtins.Grammar(name + "-string");
            var branchStringId = named && !readFromString ? namedId : stringId;
            _wires.TryAdd(branchStringId, new Wire { Id = branchStringId, Direction = direction, Shape = new StringWire { GrammarId = stringGrammar } });
            var branches = new List<TokenBranch>();
            if (!writeAsString)
            {
                branches.Add(new TokenBranch { Token = JsonToken.Number, Wire = new WireRef { WireId = numberId, Direction = direction } });
            }

            branches.Add(new TokenBranch { Token = JsonToken.String, Wire = new WireRef { WireId = branchStringId, Direction = direction } });
            if (branches.Count == 1)
            {
                return branchStringId;
            }

            _wires[unionId] = new Wire { Id = unionId, Direction = direction, Shape = new TokenUnionWire { Branches = branches } };
        }

        return unionId;
    }

    private void EnsureScalarEquivalences(string name, string typeId)
    {
        var numeric = name is "decimal";
        var oracle = numeric ? Builtins.OracleNumeric : Builtins.OracleStructural;
        var notPreserved = name switch
        {
            "decimal" => new[] { "scale", "negative-zero-sign" },
            "float32" or "float64" => ["nan-payload"],
            "guid" => ["hex-case"],
            "datetime-utc" or "datetime-unspecified" or "datetime-local-wire" or "datetime-offset" or "time-only" or "duration" => ["fraction-digits-beyond-7"],
            _ => Array.Empty<string>(),
        };
        foreach (var (suffix, scope) in new[] { ("request", EquivalenceScope.Request), ("response", EquivalenceScope.Response), ("key", EquivalenceScope.Key) })
        {
            var id = typeId + "." + suffix;
            _equivalences.TryAdd(id, new Equivalence
            {
                Id = id,
                Version = "0.1.0",
                DomainTypeId = typeId,
                Scope = scope,
                Grade = Grade.G2,
                DomainRuleId = Builtins.DomainRule(name),
                DotnetOracle = new BuiltinImpl { Id = oracle },
                TypescriptOracle = new BuiltinImpl { Id = oracle },
                NormalizationId = Builtins.NormalizeIdentity,
                Preserved = ["value"],
                NotPreserved = notPreserved,
            });
        }
    }

    private static string TsNameOf(string scalar) => scalar switch
    {
        "string" => "string",
        "boolean" => "boolean",
        "char" => "Char",
        "guid" => "Guid",
        "bytes" => "Bytes",
        "json-value" => "JsonValue",
        "int8" => "Int8",
        "uint8" => "UInt8",
        "int16" => "Int16",
        "uint16" => "UInt16",
        "int32" => "Int32",
        "uint32" => "UInt32",
        "int64" => "Int64",
        "uint64" => "UInt64",
        "decimal" => "Decimal",
        "float32" => "Float32",
        "float64" => "Float64",
        "date-only" => "DateOnly",
        "time-only" => "TimeOnly",
        "datetime-utc" => "DateTimeUtc",
        "datetime-unspecified" => "DateTimeUnspecified",
        "datetime-local-wire" => "DateTimeLocalWire",
        "datetime-offset" => "DateTimeOffset",
        "duration" => "Duration",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
    };

    private static string ClrIdentityOf(string scalar) => scalar switch
    {
        "string" => "System.String",
        "boolean" => "System.Boolean",
        "char" => "System.Char",
        "guid" => "System.Guid",
        "bytes" => "System.Byte[]",
        "json-value" => "System.Text.Json.JsonElement",
        "int8" => "System.SByte",
        "uint8" => "System.Byte",
        "int16" => "System.Int16",
        "uint16" => "System.UInt16",
        "int32" => "System.Int32",
        "uint32" => "System.UInt32",
        "int64" => "System.Int64",
        "uint64" => "System.UInt64",
        "decimal" => "System.Decimal",
        "float32" => "System.Single",
        "float64" => "System.Double",
        "date-only" => "System.DateOnly",
        "time-only" => "System.TimeOnly",
        "datetime-utc" => "System.DateTime(Utc)",
        "datetime-unspecified" => "System.DateTime(Unspecified)",
        "datetime-local-wire" => "System.DateTime(Local)",
        "datetime-offset" => "System.DateTimeOffset",
        "duration" => "System.TimeSpan",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
    };

    // ------------------------------------------------------------------ composite standard codecs

    public TypeUse ArrayOf(string id, string tsName, string clrIdentity, TypeUse element, bool nullable = false)
    {
        if (!_types.ContainsKey(id))
        {
            _types[id] = new Model { Id = id, TsName = tsName, ClrIdentity = clrIdentity, Shape = new ArrayShape { Element = element } };
            var elementRead = ChildWireId(element, WireDirection.ServerRead);
            var elementWrite = ChildWireId(element, WireDirection.ServerWrite);
            var readWire = elementRead is null ? null : EnsureArrayWire(id, elementRead, WireDirection.ServerRead);
            var writeWire = elementWrite is null ? null : EnsureArrayWire(id, elementWrite, WireDirection.ServerWrite);
            AddStructuralCodec(id, "array", readWire, writeWire, [element.CodecId]);
        }

        return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = nullable };
    }

    private string EnsureArrayWire(string typeId, string elementWireId, WireDirection direction)
    {
        var id = typeId + (direction == WireDirection.ServerRead ? ".read" : ".write");
        _wires.TryAdd(id, new Wire { Id = id, Direction = direction, Shape = new ArrayWire { Element = new WireRef { WireId = elementWireId, Direction = direction } } });
        return id;
    }

    public TypeUse MapOf(string id, string tsName, string clrIdentity, TypeUse key, TypeUse value, string comparerId, bool nullable = false)
    {
        if (!_types.ContainsKey(id))
        {
            _types[id] = new Model { Id = id, TsName = tsName, ClrIdentity = clrIdentity, Shape = new MapShape { Key = key, Value = value, ComparerId = comparerId } };
            var keyCodec = _codecs[key.CodecId];
            var valueRead = ChildWireId(value, WireDirection.ServerRead);
            var valueWrite = ChildWireId(value, WireDirection.ServerWrite);
            string? readWire = null;
            string? writeWire = null;
            if (valueRead is not null && keyCodec.Capabilities.RequestKey is { } rk)
            {
                readWire = EnsureMapWire(id, valueRead, rk.GrammarId, WireDirection.ServerRead);
            }

            if (valueWrite is not null && keyCodec.Capabilities.ResponseKey is { } sk)
            {
                writeWire = EnsureMapWire(id, valueWrite, sk.GrammarId, WireDirection.ServerWrite);
            }

            AddStructuralCodec(id, "map", readWire, writeWire, [key.CodecId, value.CodecId]);
        }

        return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = nullable };
    }

    /// <summary>
    /// Wire used by a child type in a direction. Codecs that do not exist yet (recursive types under construction) fall back to
    /// the structural naming convention <c>&lt;typeId&gt;.read|write</c>; the semantic validator verifies the final graph.
    /// </summary>
    private string? ChildWireId(TypeUse use, WireDirection direction)
    {
        if (_codecs.TryGetValue(use.CodecId, out var codec))
        {
            return direction == WireDirection.ServerRead ? codec.Capabilities.Request?.Wire.WireId : codec.Capabilities.Response?.Wire.WireId;
        }

        // a model under construction is direction-specific when its id carries the direction (an exporter object model `X.request`
        // only ever gets a read wire): the other direction has no wire, so a collection of it gets no codec capability for it either
        var directed = use.TypeId.EndsWith(".request", StringComparison.Ordinal) ? WireDirection.ServerRead
            : use.TypeId.EndsWith(".response", StringComparison.Ordinal) ? WireDirection.ServerWrite
            : (WireDirection?)null;
        if (directed is not null && directed != direction)
        {
            return null;
        }

        if (use.CodecId == use.TypeId + ".codec")
        {
            return use.TypeId + (direction == WireDirection.ServerRead ? ".read" : ".write");
        }

        // the nullable wrapper of a codec under construction (a type that refers to itself through a nullable member, e.g. a tree
        // node's left child): the wrapper and its `.or-null` wires are materialized as soon as the base codec is added (see Add)
        if (use.CodecId == use.TypeId + ".codec.nullable")
        {
            return use.TypeId + (direction == WireDirection.ServerRead ? ".read" : ".write") + ".or-null";
        }

        throw new InvalidOperationException($"codec '{use.CodecId}' is not registered and does not follow the structural naming convention");
    }

    /// <summary>A map is an object wire with no fixed properties whose additional members are captured with the value wire.</summary>
    private string EnsureMapWire(string typeId, string valueWireId, string keyGrammarId, WireDirection direction)
    {
        var id = typeId + (direction == WireDirection.ServerRead ? ".read" : ".write");
        _wires.TryAdd(id, new Wire
        {
            Id = id,
            Direction = direction,
            Shape = new ObjectWire
            {
                Properties = [],
                Additional = new CaptureAdditional { Wire = new WireRef { WireId = valueWireId, Direction = direction } },
                DuplicatePolicyId = Builtins.DuplicatesReject,
                NameMatchingId = Builtins.NamesOrdinal,
            },
        });
        return id;
    }

    public string StandardComparer(string keyScalarName, bool ignoreCase = false)
    {
        var key = Scalar(keyScalarName);
        var id = StdPrefix + "comparer." + keyScalarName + (ignoreCase ? ".ignore-case" : "");
        if (!_comparers.ContainsKey(id))
        {
            _comparers[id] = new Contract.Comparer
            {
                Id = id,
                BindingId = keyScalarName == "string" ? (ignoreCase ? Builtins.ComparerOrdinalIgnoreCase : Builtins.ComparerOrdinal) : Builtins.ComparerStructural,
                EquivalenceId = key.TypeId + ".key",
                Collision = "reject",
            };
        }

        return id;
    }

    /// <summary>
    /// Object model + wires + builtin object codec. Wire property names equal domain property names (effective JSON names).
    /// Directions without any use can be suppressed with <paramref name="emitRead"/>/<paramref name="emitWrite"/>.
    /// </summary>
    public TypeUse ObjectOf(string id, string tsName, string clrIdentity, IReadOnlyList<PropertySpec> properties, string nameMatchingId, string duplicatePolicyId,
        AdditionalPolicy? readAdditional = null, AdditionalPolicy? writeAdditional = null, Extension? extension = null, bool emitRead = true, bool emitWrite = true, bool nullable = false)
    {
        if (!_types.ContainsKey(id))
        {
            _types[id] = new Model
            {
                Id = id,
                TsName = tsName,
                ClrIdentity = clrIdentity,
                Shape = new ObjectShape
                {
                    Properties = properties.Select(p => new DomainProperty { Name = p.Name, Use = p.Use, Presence = p.DomainPresence }).ToList(),
                    Extension = extension ?? new NoExtension(),
                },
            };
            string? readWire = null;
            string? writeWire = null;
            if (emitRead)
            {
                readWire = id + ".read";
                _wires[readWire] = new Wire
                {
                    Id = readWire,
                    Direction = WireDirection.ServerRead,
                    Shape = new ObjectWire
                    {
                        Properties = properties.Select(p => new WireProperty
                        {
                            Name = p.Name,
                            Wire = new WireRef { WireId = ChildWireId(p.Use, WireDirection.ServerRead) ?? throw new InvalidOperationException($"codec '{p.Use.CodecId}' has no request capability"), Direction = WireDirection.ServerRead },
                            Presence = p.ReadPresence,
                        }).ToList(),
                        Additional = readAdditional ?? new IgnoreAdditional(),
                        DuplicatePolicyId = duplicatePolicyId,
                        NameMatchingId = nameMatchingId,
                    },
                };
            }

            if (emitWrite)
            {
                writeWire = id + ".write";
                _wires[writeWire] = new Wire
                {
                    Id = writeWire,
                    Direction = WireDirection.ServerWrite,
                    Shape = new ObjectWire
                    {
                        Properties = properties.Select(p => new WireProperty
                        {
                            Name = p.Name,
                            Wire = new WireRef { WireId = ChildWireId(p.Use, WireDirection.ServerWrite) ?? throw new InvalidOperationException($"codec '{p.Use.CodecId}' has no response capability"), Direction = WireDirection.ServerWrite },
                            Presence = p.WritePresence,
                        }).ToList(),
                        Additional = writeAdditional ?? new IgnoreAdditional(),
                        DuplicatePolicyId = Builtins.DuplicatesReject,
                        NameMatchingId = Builtins.NamesOrdinal,
                    },
                };
            }

            var deps = properties.Select(p => p.Use.CodecId).ToList();
            if (extension is CaptureExtension capture)
            {
                deps.Add(capture.Value.CodecId);
            }

            AddStructuralCodec(id, "object", readWire, writeWire, deps.Distinct(StringComparer.Ordinal).ToList());
        }

        return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = nullable };
    }

    public TypeUse EnumOf(string id, string tsName, string clrIdentity, string underlying, IReadOnlyList<EnumMember> members, bool flags, bool stringForm, bool nullable = false)
    {
        if (!_types.ContainsKey(id))
        {
            _types[id] = new Model
            {
                Id = id,
                TsName = tsName,
                ClrIdentity = clrIdentity,
                Shape = new EnumShape { UnderlyingPrimitiveId = Builtins.Scalar(underlying), Flags = flags, AllowUndefinedInteger = true, Members = members },
            };
            string readWire, writeWire;
            if (stringForm)
            {
                // JsonStringEnumConverter reads names (case-insensitive), integer strings and integers; writes names or numbers for undefined values.
                var readNum = id + ".read.number";
                var readStr = id + ".read.string";
                _wires[readNum] = new Wire { Id = readNum, Direction = WireDirection.ServerRead, Shape = new NumberWire { GrammarId = Builtins.Grammar("enum-number") } };
                _wires[readStr] = new Wire { Id = readStr, Direction = WireDirection.ServerRead, Shape = new StringWire { GrammarId = Builtins.Grammar("enum-name") } };
                readWire = id + ".read";
                _wires[readWire] = new Wire
                {
                    Id = readWire,
                    Direction = WireDirection.ServerRead,
                    Shape = new TokenUnionWire
                    {
                        Branches =
                        [
                            new TokenBranch { Token = JsonToken.String, Wire = new WireRef { WireId = readStr, Direction = WireDirection.ServerRead } },
                            new TokenBranch { Token = JsonToken.Number, Wire = new WireRef { WireId = readNum, Direction = WireDirection.ServerRead } },
                        ],
                    },
                };
                var writeNum = id + ".write.number";
                var writeStr = id + ".write.string";
                _wires[writeNum] = new Wire { Id = writeNum, Direction = WireDirection.ServerWrite, Shape = new NumberWire { GrammarId = Builtins.Grammar("enum-number") } };
                _wires[writeStr] = new Wire { Id = writeStr, Direction = WireDirection.ServerWrite, Shape = new StringWire { GrammarId = Builtins.Grammar("enum-name") } };
                writeWire = id + ".write";
                _wires[writeWire] = new Wire
                {
                    Id = writeWire,
                    Direction = WireDirection.ServerWrite,
                    Shape = new TokenUnionWire
                    {
                        Branches =
                        [
                            new TokenBranch { Token = JsonToken.String, Wire = new WireRef { WireId = writeStr, Direction = WireDirection.ServerWrite } },
                            new TokenBranch { Token = JsonToken.Number, Wire = new WireRef { WireId = writeNum, Direction = WireDirection.ServerWrite } },
                        ],
                    },
                };
            }
            else
            {
                readWire = id + ".read";
                writeWire = id + ".write";
                _wires[readWire] = new Wire { Id = readWire, Direction = WireDirection.ServerRead, Shape = new NumberWire { GrammarId = Builtins.Grammar(underlying) } };
                _wires[writeWire] = new Wire { Id = writeWire, Direction = WireDirection.ServerWrite, Shape = new NumberWire { GrammarId = Builtins.Grammar(underlying) } };
            }

            AddStructuralCodec(id, "enum", readWire, writeWire, [], keys: true);
        }

        return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = nullable };
    }

    private void AddStructuralCodec(string id, string structure, string? readWire, string? writeWire, IReadOnlyList<string> dependencies, bool keys = false)
    {
        EnsureStructuralEquivalences(id, structure);
        AddCodec(new Codec
        {
            Id = id + ".codec",
            TypeId = id,
            Origin = CodecOrigin.Builtin,
            BindingId = Builtins.BindingFor(structure),
            ValidateDomain = new BuiltinImpl { Id = Builtins.CodecImpl(structure, "validate") },
            Capabilities = new Capabilities
            {
                Request = readWire is null ? null : new ValueCapability
                {
                    Wire = new WireRef { WireId = readWire, Direction = WireDirection.ServerRead },
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(structure, "encode") },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = id + ".request",
                    DomainRuleId = Builtins.DomainRule(structure),
                },
                Response = writeWire is null ? null : new ValueCapability
                {
                    Wire = new WireRef { WireId = writeWire, Direction = WireDirection.ServerWrite },
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(structure, "decode") },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = id + ".response",
                    DomainRuleId = Builtins.DomainRule(structure),
                },
                RequestKey = keys ? new KeyCapability { Implementation = new BuiltinImpl { Id = Builtins.CodecImpl("enum", "encode-key") }, GrammarId = Builtins.KeyGrammar("enum"), EquivalenceId = id + ".key", Collision = "reject" } : null,
                ResponseKey = keys ? new KeyCapability { Implementation = new BuiltinImpl { Id = Builtins.CodecImpl("enum", "decode-key") }, GrammarId = Builtins.KeyGrammar("enum"), EquivalenceId = id + ".key", Collision = "reject" } : null,
                RequestInput = new InputCapability { Implementation = new BuiltinImpl { Id = Builtins.CodecImpl(structure, "parse-input") }, InputKind = InputKind.JsonValue, EditorId = Builtins.EditorJson },
            },
            Dependencies = dependencies,
            ProfileIds = [],
        });
    }

    private void EnsureStructuralEquivalences(string id, string structure)
    {
        foreach (var (suffix, scope) in new[] { ("request", EquivalenceScope.Request), ("response", EquivalenceScope.Response), ("key", EquivalenceScope.Key) })
        {
            if (scope == EquivalenceScope.Key && structure != "enum")
            {
                continue;
            }

            _equivalences.TryAdd(id + "." + suffix, new Equivalence
            {
                Id = id + "." + suffix,
                Version = "0.1.0",
                DomainTypeId = id,
                Scope = scope,
                Grade = Grade.G2,
                DomainRuleId = Builtins.DomainRule(structure),
                DotnetOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
                TypescriptOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
                NormalizationId = Builtins.NormalizeIdentity,
                Preserved = ["structure", "member-values"],
                NotPreserved = structure == "object" ? ["property-order", "reference-identity"] : structure == "map" ? ["entry-order", "reference-identity"] : ["reference-identity"],
            });
        }
    }

    public Wire? GetWire(string id) => _wires.GetValueOrDefault(id);

    // ------------------------------------------------------------------ binders / result adapters

    /// <summary>Standard binder for a scalar parameter at a location; the server acceptance is the invariant TryParse of the CLR type.</summary>
    /// <summary>
    /// A binder for a parameter of a module type: the client writes the canonical text of the parameter's request codec
    /// (its wire's string value or number lexeme) through the builtin path/query/header encoder, and the server reads it with its
    /// TryParse-invariant binding. The binder's type is the module model; <paramref name="grammarId"/> is the module's text grammar.
    /// </summary>
    public string CodecBinder(TypeUse use, string grammarId, ParameterLocation location, Cardinality cardinality = Cardinality.Single, NullPolicy nullPolicy = NullPolicy.Reject, EmptyPolicy emptyPolicy = EmptyPolicy.Reject)
    {
        var id = $"{use.CodecId}.binder.{location.ToString().ToLowerInvariant()}{(cardinality == Cardinality.Repeated ? ".repeated" : "")}{(nullPolicy == NullPolicy.Omit ? ".omit-null" : "")}{(emptyPolicy == EmptyPolicy.Allow ? ".allow-empty" : "")}";
        if (!_binders.ContainsKey(id))
        {
            var (impl, encoding) = location switch
            {
                ParameterLocation.Path => (Builtins.BinderPathSegment, BinderEncoding.PathSegment),
                ParameterLocation.Query => (Builtins.BinderQueryComponent, BinderEncoding.QueryComponent),
                _ => (Builtins.BinderHeaderText, BinderEncoding.HeaderText),
            };
            _binders[id] = new Binder
            {
                Id = id,
                BindingId = impl,
                Location = location,
                TypeId = use.TypeId,
                Implementation = new BuiltinImpl { Id = impl },
                Cardinality = cardinality,
                NullPolicy = nullPolicy,
                EmptyPolicy = emptyPolicy,
                Encoding = encoding,
                GrammarId = grammarId,
                NormalizationId = Builtins.NormalizeIdentity,
                ServerAcceptanceId = Builtins.AcceptDotnetTryParseInvariant,
                OrderSensitive = cardinality == Cardinality.Repeated,
            };
        }

        return id;
    }

    /// <summary>
    /// A binder for an enum parameter. ASP.NET Core binds enums with <c>Enum.TryParse</c> (minimal APIs: case-sensitive
    /// member names, integers, comma-separated flags) or MVC's enum model binder (the same texts, but only defined values). The client
    /// writes a defined member's C# name, and the integer otherwise; <paramref name="definedOnly"/> (MVC) makes undefined values a
    /// client-side refusal — grammar <c>enum-name</c> instead of the enum key grammar.
    /// </summary>
    public string EnumBinder(TypeUse use, bool definedOnly, ParameterLocation location, Cardinality cardinality = Cardinality.Single, NullPolicy nullPolicy = NullPolicy.Reject, EmptyPolicy emptyPolicy = EmptyPolicy.Reject)
    {
        var id = $"{use.TypeId}.binder.{location.ToString().ToLowerInvariant()}{(definedOnly ? ".defined" : "")}{(cardinality == Cardinality.Repeated ? ".repeated" : "")}{(nullPolicy == NullPolicy.Omit ? ".omit-null" : "")}{(emptyPolicy == EmptyPolicy.Allow ? ".allow-empty" : "")}";
        if (!_binders.ContainsKey(id))
        {
            var (impl, encoding) = location switch
            {
                ParameterLocation.Path => (Builtins.BinderPathSegment, BinderEncoding.PathSegment),
                ParameterLocation.Query => (Builtins.BinderQueryComponent, BinderEncoding.QueryComponent),
                _ => (Builtins.BinderHeaderText, BinderEncoding.HeaderText),
            };
            _binders[id] = new Binder
            {
                Id = id,
                BindingId = impl,
                Location = location,
                TypeId = use.TypeId,
                Implementation = new BuiltinImpl { Id = impl },
                Cardinality = cardinality,
                NullPolicy = nullPolicy,
                EmptyPolicy = emptyPolicy,
                Encoding = encoding,
                GrammarId = definedOnly ? Builtins.Grammar("enum-name") : Builtins.KeyGrammar("enum"),
                NormalizationId = Builtins.NormalizeIdentity,
                ServerAcceptanceId = Builtins.AcceptDotnetTryParseInvariant,
                OrderSensitive = cardinality == Cardinality.Repeated,
            };
        }

        return id;
    }

    public string StandardBinder(string scalarName, ParameterLocation location, Cardinality cardinality = Cardinality.Single, NullPolicy nullPolicy = NullPolicy.Reject, EmptyPolicy emptyPolicy = EmptyPolicy.Reject)
    {
        var use = Scalar(scalarName);
        var id = $"{StdPrefix}binder.{scalarName}.{location.ToString().ToLowerInvariant()}{(cardinality == Cardinality.Repeated ? ".repeated" : "")}{(nullPolicy == NullPolicy.Omit ? ".omit-null" : "")}{(emptyPolicy == EmptyPolicy.Allow ? ".allow-empty" : "")}";
        if (!_binders.ContainsKey(id))
        {
            var (impl, encoding) = location switch
            {
                ParameterLocation.Path => (Builtins.BinderPathSegment, BinderEncoding.PathSegment),
                ParameterLocation.Query => (Builtins.BinderQueryComponent, BinderEncoding.QueryComponent),
                _ => (Builtins.BinderHeaderText, BinderEncoding.HeaderText),
            };
            _binders[id] = new Binder
            {
                Id = id,
                BindingId = impl,
                Location = location,
                TypeId = use.TypeId,
                Implementation = new BuiltinImpl { Id = impl },
                Cardinality = cardinality,
                NullPolicy = nullPolicy,
                EmptyPolicy = emptyPolicy,
                Encoding = encoding,
                GrammarId = Builtins.KeyGrammar(scalarName),
                NormalizationId = Builtins.NormalizeIdentity,
                ServerAcceptanceId = scalarName == "string" ? Builtins.AcceptCanonical : Builtins.AcceptDotnetTryParseInvariant,
                OrderSensitive = cardinality == Cardinality.Repeated,
            };
        }

        return id;
    }

    public string StandardResultAdapter(ResultAdapterKind kind, IReadOnlyList<string>? profileIds = null)
    {
        var impl = kind switch
        {
            ResultAdapterKind.MinimalJson => Builtins.ResultMinimalJson,
            ResultAdapterKind.MinimalResult => Builtins.ResultMinimalResult,
            ResultAdapterKind.MvcObject => Builtins.ResultMvcObject,
            ResultAdapterKind.MvcJson => Builtins.ResultMvcJson,
            ResultAdapterKind.Bodyless => Builtins.ResultBodyless,
            ResultAdapterKind.Text => Builtins.ResultTextUtf8,
            ResultAdapterKind.Binary => Builtins.ResultBinaryBuffered,
            _ => throw new ArgumentException("custom adapters need explicit bindings", nameof(kind)),
        };
        var suffix = kind switch
        {
            ResultAdapterKind.MinimalJson => "minimal-json",
            ResultAdapterKind.MinimalResult => "minimal-result",
            ResultAdapterKind.MvcObject => "mvc-object",
            ResultAdapterKind.MvcJson => "mvc-json",
            ResultAdapterKind.Bodyless => "bodyless",
            ResultAdapterKind.Binary => "binary",
            _ => "text",
        };
        var id = StdPrefix + "result." + suffix + (profileIds is { Count: > 0 } ? "." + string.Join("+", profileIds) : "");
        _resultAdapters.TryAdd(id, new ResultAdapter
        {
            Id = id,
            BindingId = impl,
            Kind = kind,
            Implementation = new BuiltinImpl { Id = impl },
            ProfileIds = profileIds ?? [],
            BehaviorIds = [],
        });
        return id;
    }

    // ------------------------------------------------------------------ build

    /// <summary>Serializes the registries in insertion order, then computes profile fingerprints and the semantic hash.</summary>
    /// <summary>
    /// The guarantee unit is the codec dependency closure: a codec cannot claim more than the weakest codec it
    /// depends on in the same direction. A G1 dependency — a normalizing collection, keys renamed on write, an opaque behavior —
    /// makes every equivalence that contains it G1 as well, with the dependency's notPreserved aspects added.
    /// </summary>
    private void PropagateG1()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var codec in _codecs.Values)
            {
                foreach (var pick in new Func<Capabilities, ValueCapability?>[] { c => c.Request, c => c.Response })
                {
                    if (pick(codec.Capabilities) is not { } own || !_equivalences.TryGetValue(own.EquivalenceId, out var eq) || eq.Grade == Grade.G1)
                    {
                        continue;
                    }

                    var weak = codec.Dependencies
                        .Select(d => _codecs.TryGetValue(d, out var dependency) ? pick(dependency.Capabilities) : null)
                        .Select(c => c is not null && _equivalences.TryGetValue(c.EquivalenceId, out var de) && de.Grade == Grade.G1 ? de : null)
                        .OfType<Equivalence>()
                        .ToList();
                    if (weak.Count == 0)
                    {
                        continue;
                    }

                    _equivalences[eq.Id] = eq with { Grade = Grade.G1, NotPreserved = [.. eq.NotPreserved.Concat(weak.SelectMany(w => w.NotPreserved)).Distinct(StringComparer.Ordinal)] };
                    changed = true;
                }
            }
        }
        while (changed);
    }

    public JsonObject BuildJson()
    {
        PropagateG1();
        var document = new ContractDocument
        {
            Format = TisiliaJson.Formats.Contract,
            Version = TisiliaJson.ContractVersion,
            ApiId = ApiId,
            Profiles = _profiles.Values.ToList(),
            Types = _types.Values.ToList(),
            Wires = _wires.Values.ToList(),
            Codecs = _codecs.Values.ToList(),
            Bindings = _bindings.Values.ToList(),
            Equivalences = _equivalences.Values.ToList(),
            Projections = _projections.Values.ToList(),
            Comparers = _comparers.Values.ToList(),
            Binders = _binders.Values.ToList(),
            ResultAdapters = _resultAdapters.Values.ToList(),
            Operations = _operations.Values.Select(op => op.RoutePlan is null ? op with { RoutePlan = RoutePlans.Parse(op.Route, op.Parameters) } : op).ToList(),
            Modules = _modules.Values.ToList(),
            Documentation = _documentation.ToList(),
            SemanticHash = "sha256:" + new string('0', 64),
        };
        var root = JsonSerializer.SerializeToNode(document, TisiliaJson.Options)!.AsObject();
        if (root["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                profile["fingerprint"] = TisiliaHash.ProfileFingerprint(profile);
            }
        }

        root["semanticHash"] = TisiliaHash.SemanticHash(root);
        return root;
    }

    public string BuildText() => BuildJson().ToJsonString(TisiliaJson.IndentedOptions) + "\n";
}
