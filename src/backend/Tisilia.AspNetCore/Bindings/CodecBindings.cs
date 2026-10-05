using System.Text.Json.Serialization;
using Tisilia.Contract;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// The settings of one converter instance (the binding key includes the effective instance's settings and
/// profile). An instance whose settings differ from the registration's defaults gets its own binding/codec ids (a digest
/// suffix) and is certified on its own; its non-secret context entries reach the TypeScript codec through the codec context.
/// </summary>
public sealed record PairedInstanceSettings(string SettingsText, IReadOnlyList<BindingContextEntry> Context)
{
    /// <summary>Aspects this instance does not preserve (replaces the registration's list when set).</summary>
    public IReadOnlyList<string>? NotPreserved { get; init; }
}

/// <summary>
/// Explicit description of a paired custom converter: the C# converter that runs on the server, the
/// TypeScript module that runs in the client, the wires each direction produces and the equivalence claimed.
/// Nothing is inferred by sampling; every field is declared by the module author.
/// </summary>
public sealed class PairedCodecRegistration
{
    public required Type ClrType { get; init; }
    public required Type ConverterType { get; init; }
    public required string ModelId { get; init; }
    public required string TsName { get; init; }
    /// <summary>Public domain shape; the factory lets it reference standard scalars/models of the exporter's registries.</summary>
    public required Func<ContractWireFactory, DomainShape> Shape { get; init; }
    public required string ModuleId { get; init; }
    public required string ModuleVersion { get; init; }
    public required string License { get; init; }
    public required IReadOnlyList<ModuleArtifactSpec> Artifacts { get; init; }
    public required string DotnetConverterExport { get; init; }
    public required string DomainRuleExport { get; init; }
    public required string DotnetOracleExport { get; init; }
    public required string TypescriptOracleExport { get; init; }
    public PairedDirection? Request { get; init; }
    public PairedDirection? Response { get; init; }
    public required IReadOnlyList<BindingContextEntry> ConverterContext { get; init; }
    /// <summary>Non-secret settings text hashed into <c>binding.settingsDigest</c>. Never include secrets.</summary>
    public required string SettingsText { get; init; }
    public Grade Grade { get; init; } = Grade.G2;
    public IReadOnlyList<string> Preserved { get; init; } = ["value"];
    public IReadOnlyList<string> NotPreserved { get; init; } = [];

    /// <summary>Codec origin recorded in the contract: <c>paired</c> for hand-written modules, <c>portable</c> for generated ones.</summary>
    public CodecOrigin Origin { get; init; } = CodecOrigin.Paired;

    /// <summary>When true the equivalences use the builtin structural oracle on the projected domain instead of module oracle exports (portable codecs).</summary>
    public bool BuiltinOracles { get; init; }

    /// <summary>Trusted projection of a CLR value to the projected domain AST; required for conformance runs.</summary>
    public Func<object, JsonValue>? Project { get; init; }

    /// <summary>Trusted factory building a CLR value from a projected domain AST; required for conformance runs. Not an HTTP body reader.</summary>
    public Func<JsonValue, object>? Construct { get; init; }

    /// <summary>The C# oracle named by <see cref="DotnetOracleExport"/>, applied to two CLR values built by <see cref="Construct"/>.</summary>
    public Func<object, object, bool>? Oracle { get; init; }

    /// <summary>
    /// CLR types of other paired registrations this codec's shape or wires reference (portable refs). The exporter registers
    /// them first, so a referenced definition reaches the contract even when no endpoint uses it directly.
    /// </summary>
    public IReadOnlyList<Type> Dependencies { get; init; } = [];

    /// <summary>
    /// Describes the settings of the converter instance in effect (constructor arguments, options); null means every instance
    /// has the registration's <see cref="SettingsText"/> and <see cref="ConverterContext"/>. Instances whose settings text differs
    /// from <see cref="SettingsText"/> get their own binding/codec ids and conformance cases.
    /// </summary>
    public Func<JsonConverter, PairedInstanceSettings>? DescribeInstance { get; init; }

    /// <summary>
    /// System.Text.Json applies <c>JsonNumberHandling</c> to this type (its built-in number converters: Int128, UInt128, Half), so the
    /// effective handling of each position is part of the binding: its own binding/codec ids, the wires of
    /// <see cref="PairedDirection.NumberWire"/>, and the non-secret context entry <c>numbers</c> (letters r = AllowReadingFromString,
    /// w = WriteAsString, n = AllowNamedFloatingPointLiterals; absent for strict handling) that reaches the TypeScript codec.
    /// </summary>
    public bool NumberHandling { get; init; }

    /// <summary>Dictionary key capability (the requestKey/responseKey capabilities): the converter's property-name methods and their TypeScript exports.</summary>
    public PairedKey? Key { get; init; }

    /// <summary>
    /// Wire grammars the module implements (a custom grammar is a grammar binding resolved to a module export, never a
    /// disguised domain rule). Wires and keys of the registration reference them by id.
    /// </summary>
    public IReadOnlyList<PairedGrammar> Grammars { get; init; } = [];

    /// <summary>
    /// Route, query and header parameters of this type: the grammar of the request codec's canonical text — the string
    /// value or the number lexeme of its wire — when ASP.NET Core's parameter binding (TryParse with the invariant culture,
    /// Uri.TryCreate) reads exactly that text. Null: the type is not a parameter type (an SV30 diagnostic).
    /// </summary>
    public string? ParameterGrammarId { get; init; }
}

public sealed record PairedDirection(
    Func<ContractWireFactory, WireRef> Wire,
    string CodecExport,
    string ValidateExport,
    string? RequestInputExport = null,
    NullBehavior NullBehavior = NullBehavior.Reject)
{
    /// <summary>The wire for a position's effective number handling (used instead of <see cref="Wire"/> by registrations with <see cref="PairedCodecRegistration.NumberHandling"/>).</summary>
    public Func<ContractWireFactory, Generator.Building.NumberProfile, WireRef>? NumberWire { get; init; }
}

/// <summary>
/// A paired codec's dictionary key exports (role key-codec) and the grammar of the property names the server reads
/// (<paramref name="GrammarId"/>) and writes (<paramref name="ResponseGrammarId"/>, the same grammar when null).
/// </summary>
public sealed record PairedKey(string EncodeKeyExport, string DecodeKeyExport, string GrammarId, string? ResponseGrammarId = null);

/// <summary>A grammar binding of a paired module: the id wires and keys reference, and the module export (role grammar) implementing it.</summary>
public sealed record PairedGrammar(string Id, string ExportName);

public sealed record ModuleArtifactSpec(ArtifactTarget Target, string Path, string? Digest = null, Func<byte[]>? Bytes = null);

/// <summary>Helper handed to registrations so that wires can be declared against the exporter's registries.</summary>
public sealed class ContractWireFactory
{
    private readonly Generator.Building.ContractBuilder _builder;

    public ContractWireFactory(Generator.Building.ContractBuilder builder) => _builder = builder;

    public Generator.Building.ContractBuilder Builder => _builder;

    public WireRef StringWire(string id, WireDirection direction, string grammarId)
    {
        _builder.AddWire(new Wire { Id = id, Direction = direction, Shape = new StringWire { GrammarId = grammarId } });
        return new WireRef { WireId = id, Direction = direction };
    }

    public WireRef NumberWire(string id, WireDirection direction, string grammarId)
    {
        _builder.AddWire(new Wire { Id = id, Direction = direction, Shape = new NumberWire { GrammarId = grammarId } });
        return new WireRef { WireId = id, Direction = direction };
    }

    /// <summary>A wire of several JSON token kinds (a number or a string, …), one branch per token.</summary>
    public WireRef TokenUnionWire(string id, WireDirection direction, IReadOnlyList<TokenBranch> branches)
    {
        _builder.AddWire(new Wire { Id = id, Direction = direction, Shape = new TokenUnionWire { Branches = branches } });
        return new WireRef { WireId = id, Direction = direction };
    }

    public WireRef ObjectWire(string id, WireDirection direction, IReadOnlyList<WireProperty> properties, AdditionalPolicy additional, string duplicatePolicyId, string nameMatchingId)
    {
        _builder.AddWire(new Wire { Id = id, Direction = direction, Shape = new ObjectWire { Properties = properties, Additional = additional, DuplicatePolicyId = duplicatePolicyId, NameMatchingId = nameMatchingId } });
        return new WireRef { WireId = id, Direction = direction };
    }

    /// <summary>Wire of a standard scalar in a direction (ensures the scalar's model/codec exist).</summary>
    public WireRef Scalar(string name, WireDirection direction, Generator.Building.NumberProfile numbers = default)
    {
        var use = _builder.Scalar(name, numbers);
        var codec = _builder.GetCodec(use.CodecId)!;
        var cap = direction == WireDirection.ServerRead ? codec.Capabilities.Request! : codec.Capabilities.Response!;
        return cap.Wire;
    }
}

/// <summary>
/// Registered codec bindings, looked up by closed CLR type and converter (exact registration wins). One CLR
/// type may be bound by several converters — a type-level one and member-level <c>[JsonConverter]</c>s (property context)
/// — each with its own model/codec id, so the contract never mixes their meanings.
/// </summary>
public sealed class CodecBindingCollection
{
    private readonly Dictionary<(Type Clr, Type Converter), PairedCodecRegistration> _paired = new();
    private readonly HashSet<string> _modelIds = new(StringComparer.Ordinal);

    public void AddPaired(PairedCodecRegistration registration)
    {
        var key = (registration.ClrType, registration.ConverterType);
        if (_paired.ContainsKey(key))
        {
            throw new InvalidOperationException($"a codec binding for '{registration.ClrType}' with converter '{registration.ConverterType}' is already registered; a scope cannot be claimed twice (SV24)");
        }

        if (!_modelIds.Add(registration.ModelId))
        {
            throw new InvalidOperationException($"model id '{registration.ModelId}' is already used by another paired codec; ids are unique across the contract (SV02)");
        }

        _paired[key] = registration;
    }

    /// <summary>The registration of a CLR type under the converter that is in effect at a position.</summary>
    public bool TryGetPaired(Type type, Type converterType, out PairedCodecRegistration registration) => _paired.TryGetValue((type, converterType), out registration!);

    /// <summary>The single registration of a CLR type; false when none or when several converters bind the type (then the converter decides).</summary>
    public bool TryGetPaired(Type type, out PairedCodecRegistration registration)
    {
        PairedCodecRegistration? found = null;
        var count = 0;
        foreach (var candidate in _paired.Values)
        {
            if (candidate.ClrType == type)
            {
                found = candidate;
                count++;
            }
        }

        registration = found!;
        return count == 1;
    }

    /// <summary>Every registration of a CLR type.</summary>
    public IEnumerable<PairedCodecRegistration> PairedFor(Type type) => _paired.Values.Where(r => r.ClrType == type);

    public IReadOnlyCollection<PairedCodecRegistration> Paired => _paired.Values;
}
