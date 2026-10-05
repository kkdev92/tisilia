using System.Collections;
using System.Text.Json;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Validation;
using JsonValue = Tisilia.Contract.JsonValue;

namespace Tisilia.AspNetCore.Conformance;

/// <summary>
/// Executes the C# side of the runner protocol against the exported contract: adapter ids resolve to the exporter's
/// adapter table (codec id + profile), oracles resolve through the contract's equivalences. The profile named in
/// the request must be the one the adapter was exported under; nothing falls back to a process-global option.
/// </summary>
public sealed class DotnetRunnerCore
{
    private readonly ContractIndex _index;
    private readonly RunnerAdapterTable _adapters;
    private readonly IReadOnlyCollection<PairedCodecRegistration> _paired;
    private readonly IReadOnlyList<BehaviorRegistration> _behaviors;
    private readonly DomainProjector _projector;
    private readonly DomainFactory _factory;

    public DotnetRunnerCore(ContractIndex index, RunnerAdapterTable adapters, IReadOnlyCollection<PairedCodecRegistration> paired, IReadOnlyList<BehaviorRegistration>? behaviors = null)
    {
        _index = index;
        _adapters = adapters;
        _paired = paired;
        _behaviors = behaviors ?? [];
        _projector = new DomainProjector(index, adapters);
        _factory = new DomainFactory(index, paired);
    }

    public JsonValue Handle(RunnerRequest request)
    {
        switch (request.Action)
        {
            case RunnerAction.DotnetRead:
            {
                var (adapter, use) = Adapter(request);
                var value = Read(DomainAst.ToJsonText(request.Inputs[0]), adapter.ClrType, OptionsOf(adapter));
                return _projector.Project(value, use, adapter.Options, adapter.ProfileId);
            }

            case RunnerAction.DotnetWrite:
            {
                var (adapter, use) = Adapter(request);
                var value = _factory.Construct(request.Inputs[0], use, adapter);
                return DomainAst.ParseJsonText(Write(value, adapter.ClrType, OptionsOf(adapter)), EffectiveDepth(adapter.Options));
            }

            case RunnerAction.DotnetReadKey:
            {
                var (adapter, use) = Adapter(request);
                if (request.Inputs[0] is not JsonStringValue keyText)
                {
                    throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "key.not-a-string");
                }

                var dictType = typeof(Dictionary<,>).MakeGenericType(adapter.ClrType, typeof(int));
                var dict = (IDictionary)Read(DomainAst.ToJsonText(DomainAst.Object((keyText.Value, DomainAst.Number("0")))), dictType, adapter.Options)!;
                var key = dict.Keys.Cast<object>().Single();
                return _projector.Project(key, use, adapter.Options, adapter.ProfileId);
            }

            case RunnerAction.DotnetWriteKey:
            {
                var (adapter, use) = Adapter(request);
                var key = _factory.Construct(request.Inputs[0], use, adapter) ?? throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "key.null");
                var dictType = typeof(Dictionary<,>).MakeGenericType(adapter.ClrType, typeof(int));
                var dict = (IDictionary)Activator.CreateInstance(dictType)!;
                dict[key] = 0;
                var written = DomainAst.ParseJsonText(Write(dict, dictType, adapter.Options), EffectiveDepth(adapter.Options));
                return written is JsonObjectValue { Entries.Count: 1 } obj
                    ? DomainAst.String(obj.Entries[0].Name)
                    : throw new RunnerFailureException(RunnerFailureCode.Internal, "key.unexpected-write");
            }

            case RunnerAction.ValidateDomain:
            {
                var (adapter, use) = Adapter(request);
                _factory.Construct(request.Inputs[0], use, adapter);
                return DomainAst.Boolean(true);
            }

            case RunnerAction.Compare:
                return Compare(request);
            case RunnerAction.DotnetProject:
            {
                // a behavior's projection applied by the registered .NET implementation at a path of the domain AST
                if (!_index.Projections.TryGetValue(request.AdapterId, out var projection))
                {
                    throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.unknown");
                }

                var registration = _behaviors.FirstOrDefault(b => projection.DotnetImplementation is ModuleImpl m && m.ModuleId == b.ModuleId && m.ExportName == b.ProjectionDotnetExport)
                    ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.not-registered");
                if (registration.Project is null)
                {
                    throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.no-delegate");
                }

                if (request.Inputs[1] is not JsonStringValue pathText)
                {
                    throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "projection.path-not-a-string");
                }

                try
                {
                    return DomainAst.ApplyAt(request.Inputs[0], pathText.Value, registration.Project);
                }
                catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException or InvalidOperationException)
                {
                    throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "projection.rejected", pathText.Value);
                }
            }

            case RunnerAction.ParseRequestInput:
                throw new RunnerFailureException(RunnerFailureCode.Contract, "action.explorer-only");
            default:
                throw new RunnerFailureException(RunnerFailureCode.Contract, "action.wrong-runner");
        }
    }

    private JsonValue Compare(RunnerRequest request)
    {
        if (!_index.Equivalences.TryGetValue(request.AdapterId, out var equivalence))
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.unknown-equivalence");
        }

        var codec = (_index.CodecsByType.TryGetValue(equivalence.DomainTypeId, out var codecs) ? codecs : []).FirstOrDefault(c => EquivalenceFor(c, equivalence.Scope) == equivalence.Id)
            ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.no-codec-for-equivalence");
        var use = new TypeUse { TypeId = codec.TypeId, CodecId = codec.Id, SemanticNullable = false };
        return DomainAst.Boolean(CompareWith(equivalence, request.Inputs[0], request.Inputs[1], use, request.ProfileId, 0));
    }

    private static string? EquivalenceFor(Codec codec, EquivalenceScope scope) => scope switch
    {
        EquivalenceScope.Request => codec.Capabilities.Request?.EquivalenceId,
        EquivalenceScope.Response => codec.Capabilities.Response?.EquivalenceId,
        EquivalenceScope.Key => codec.Capabilities.RequestKey?.EquivalenceId ?? codec.Capabilities.ResponseKey?.EquivalenceId,
        _ => codec.Capabilities.Response?.EquivalenceId ?? codec.Capabilities.Request?.EquivalenceId,
    };

    /// <summary>
    /// The registered oracle of an equivalence. Builtin structural/numeric oracles walk the domain model and delegate
    /// every member to the member type's own equivalence in the same scope, so a paired member (e.g. Money) is
    /// compared by its module oracle; exact-wire compares the ASTs verbatim. The TypeScript runner implements the same rule.
    /// </summary>
    private bool CompareWith(Equivalence equivalence, JsonValue a, JsonValue b, TypeUse use, string profileId, int depth)
    {
        if (depth > 128)
        {
            throw new RunnerFailureException(RunnerFailureCode.Limit, "compare.depth");
        }

        switch (equivalence.DotnetOracle)
        {
            case ModuleImpl module:
            {
                if (a is JsonNullValue || b is JsonNullValue)
                {
                    return a is JsonNullValue && b is JsonNullValue;
                }

                var reg = _paired.FirstOrDefault(p => p.ModuleId == module.ModuleId && p.DotnetOracleExport == module.ExportName)
                    ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.unknown-module-oracle");
                if (reg.Oracle is null || reg.Construct is null)
                {
                    throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.oracle-not-registered");
                }

                var adapter = _adapters.Find(use.CodecId, profileId) ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.no-adapter-for-domain-type");
                var va = _factory.Construct(a, use, adapter) ?? throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "compare.null-input", "/0");
                var vb = _factory.Construct(b, use, adapter) ?? throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "compare.null-input", "/1");
                return reg.Oracle(va, vb);
            }

            case BuiltinImpl builtin when builtin.Id == Builtins.OracleExactWire:
                return AstOracles.ExactWire(a, b);
            case BuiltinImpl builtin when builtin.Id is Builtins.OracleStructural or Builtins.OracleNumeric:
                return CompareByModel(a, b, use, equivalence.Scope, builtin.Id == Builtins.OracleNumeric, profileId, depth);
            default:
                throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.unknown-oracle-kind");
        }
    }

    private bool CompareByModel(JsonValue a, JsonValue b, TypeUse use, EquivalenceScope scope, bool numeric, string profileId, int depth)
    {
        if (a is JsonNullValue || b is JsonNullValue)
        {
            return a is JsonNullValue && b is JsonNullValue;
        }

        var model = _index.Types.GetValueOrDefault(use.TypeId) ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "compare.unknown-type");
        switch (model.Shape)
        {
            case PrimitiveShape:
            case EnumShape:
                return numeric ? AstOracles.Numeric(a, b) : AstOracles.Structural(a, b);
            case ObjectShape shape:
            {
                if (a is not JsonObjectValue ao || b is not JsonObjectValue bo || ao.Entries.Count != bo.Entries.Count)
                {
                    return false;
                }

                if (ao.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != ao.Entries.Count || bo.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != bo.Entries.Count)
                {
                    return numeric ? AstOracles.Numeric(a, b) : AstOracles.Structural(a, b);
                }

                foreach (var entry in ao.Entries)
                {
                    var other = DomainAst.Entry(bo, entry.Name);
                    if (other is null)
                    {
                        return false;
                    }

                    var prop = shape.Properties.FirstOrDefault(p => p.Name == entry.Name);
                    if (prop is not null)
                    {
                        if (!CompareMember(entry.Value, other, prop.Use, scope, numeric, profileId, depth + 1))
                        {
                            return false;
                        }
                    }
                    else if (entry.Name == "extensions" && shape.Extension is CaptureExtension capture && entry.Value is JsonObjectValue ea && other is JsonObjectValue eb)
                    {
                        if (ea.Entries.Count != eb.Entries.Count)
                        {
                            return false;
                        }

                        foreach (var ext in ea.Entries)
                        {
                            var otherExt = DomainAst.Entry(eb, ext.Name);
                            if (otherExt is null || !CompareMember(ext.Value, otherExt, capture.Value, scope, numeric, profileId, depth + 1))
                            {
                                return false;
                            }
                        }
                    }
                    else if (!(numeric ? AstOracles.Numeric(entry.Value, other) : AstOracles.Structural(entry.Value, other)))
                    {
                        return false;
                    }
                }

                return true;
            }

            case ArrayShape array:
            {
                if (a is not JsonArrayValue aa || b is not JsonArrayValue ba || aa.Items.Count != ba.Items.Count)
                {
                    return false;
                }

                for (var i = 0; i < aa.Items.Count; i++)
                {
                    if (!CompareMember(aa.Items[i], ba.Items[i], array.Element, scope, numeric, profileId, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            case MapShape map:
            {
                if (a is not JsonObjectValue ma || b is not JsonObjectValue mb || ma.Entries.Count != mb.Entries.Count)
                {
                    return false;
                }

                foreach (var entry in ma.Entries)
                {
                    var other = DomainAst.Entry(mb, entry.Name);
                    if (other is null || !CompareMember(entry.Value, other, map.Value, scope, numeric, profileId, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            case BrandShape brand:
                return CompareMember(a, b, brand.Base, scope, numeric, profileId, depth + 1);
            case UnionShape union:
            {
                if (a is not JsonObjectValue ua || b is not JsonObjectValue ub)
                {
                    return false;
                }

                foreach (var variant in union.Variants)
                {
                    var vm = _index.Types[variant.Use.TypeId];
                    if (vm.Shape is ObjectShape vs && vs.Properties.Count > 0)
                    {
                        var ta = DomainAst.Entry(ua, vs.Properties[0].Name);
                        var tb = DomainAst.Entry(ub, vs.Properties[0].Name);
                        if (ta is not null && TagText(ta) == variant.Tag)
                        {
                            return tb is not null && TagText(tb) == variant.Tag && CompareMember(a, b, variant.Use, scope, numeric, profileId, depth + 1);
                        }
                    }
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>A member is compared by its own type's equivalence in the same scope when one exists, otherwise structurally by model.</summary>
    private bool CompareMember(JsonValue a, JsonValue b, TypeUse use, EquivalenceScope scope, bool numeric, string profileId, int depth)
    {
        if (_index.Codecs.TryGetValue(use.CodecId, out var codec) && EquivalenceFor(codec, scope) is { } eqId && _index.Equivalences.TryGetValue(eqId, out var eq))
        {
            return CompareWith(eq, a, b, use, profileId, depth);
        }

        return CompareByModel(a, b, use, scope, numeric, profileId, depth);
    }

    private static string? TagText(JsonValue value) => value switch
    {
        JsonStringValue s => s.Value,
        JsonNumberValue n => n.Text,
        _ => null,
    };

    private (RunnerAdapter Adapter, TypeUse Use) Adapter(RunnerRequest request)
    {
        if (!_index.Codecs.TryGetValue(request.AdapterId, out var codec))
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "adapter.not-a-codec");
        }

        var adapter = _adapters.Find(request.AdapterId, request.ProfileId)
            ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "adapter.unknown-for-profile");
        return (adapter, new TypeUse { TypeId = codec.TypeId, CodecId = codec.Id, SemanticNullable = codec.Id.EndsWith(".nullable", StringComparison.Ordinal) });
    }

    private static int EffectiveDepth(JsonSerializerOptions options) => (options.MaxDepth == 0 ? 64 : options.MaxDepth) + 1;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RunnerAdapter, JsonSerializerOptions> _memberOptions = new();

    /// <summary>
    /// The options a codec runs with: the profile options, or — for a member-level binding (property context) — a copy with the
    /// member's converter instance first, since options converters take precedence over the type's own converter.
    /// </summary>
    private JsonSerializerOptions OptionsOf(RunnerAdapter adapter)
    {
        if (adapter.Converter is null)
        {
            return adapter.Options;
        }

        return _memberOptions.GetValue(adapter, a =>
        {
            var o = new JsonSerializerOptions(a.Options);
            o.Converters.Insert(0, a.Converter!);
            o.MakeReadOnly(populateMissingResolver: true);
            return o;
        });
    }

    private static object? Read(string json, Type type, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type, options);
        }
        catch (JsonException e)
        {
            throw new RunnerFailureException(RunnerFailureCode.Codec, "codec.read", e.Path ?? "");
        }
        catch (NotSupportedException)
        {
            // System.Text.Json refuses the input with NotSupportedException rather than JsonException in some cases (a polymorphic base without
            // a discriminator, an abstract type): the server rejects the wire, which is the codec failure the suite expects
            throw new RunnerFailureException(RunnerFailureCode.Codec, "codec.read-unsupported");
        }
    }

    private static string Write(object? value, Type type, JsonSerializerOptions options)
    {
        try
        {
            try
            {
                return JsonSerializer.Serialize(value, type, options);
            }
            catch (NotSupportedException)
            {
                // IAsyncEnumerable<T> is written only by the asynchronous serializer — the one ASP.NET Core uses for responses
                using var stream = new MemoryStream();
                JsonSerializer.SerializeAsync(stream, value, type, options).GetAwaiter().GetResult();
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        catch (JsonException e)
        {
            throw new RunnerFailureException(RunnerFailureCode.Codec, "codec.write", e.Path ?? "");
        }
        catch (ArgumentException)
        {
            // e.g. NaN without named literal support, invalid surrogates
            throw new RunnerFailureException(RunnerFailureCode.Codec, "codec.write-argument");
        }
        catch (NotSupportedException)
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "codec.write-unsupported");
        }
    }
}
