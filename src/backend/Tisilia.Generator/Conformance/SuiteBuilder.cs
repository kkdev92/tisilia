using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Additional;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Conformance;

public enum RunnerTarget
{
    [JsonStringEnumMemberName("dotnet")] Dotnet,
    [JsonStringEnumMemberName("node")] Node,
}

/// <summary>One deterministic test case of the standard suite. The case kind fixes the runner actions that are exchanged.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ResponseRoundTripCase), "response-round-trip")]
[JsonDerivedType(typeof(RequestRoundTripCase), "request-round-trip")]
[JsonDerivedType(typeof(KeyRoundTripCase), "key-round-trip")]
[JsonDerivedType(typeof(NegativeWireCase), "negative-wire")]
[JsonDerivedType(typeof(DomainValidationCase), "domain-validation")]
[JsonDerivedType(typeof(OracleDiscriminationCase), "oracle-discrimination")]
public abstract record ConformanceCase
{
    public required string Id { get; init; }
    /// <summary>Required-test category (<c>kind:type</c>); the set of categories is derived from the closure alone.</summary>
    public required string Category { get; init; }
    public required string ProfileId { get; init; }
}

/// <summary>
/// dotnet-write(domain) → wire; ts-decode-response(wire) → domain'; compare(expected, domain') on both runners. Write-side
/// normalized behaviors (should-serialize, getter) project <see cref="Expected"/> first, like the request case.
/// </summary>
public sealed record ResponseRoundTripCase : ConformanceCase
{
    public required string CodecId { get; init; }
    public required string EquivalenceId { get; init; }
    public required JsonValue Domain { get; init; }
    public required JsonValue Expected { get; init; }
    public IReadOnlyList<ProjectionStep>? Projections { get; init; }
}

/// <summary>A codec whose equivalence makes no round-trip claim (G1, an opaque behavior on its path): recorded so that the absence of cases is visible, never a hidden skip.</summary>
public sealed record NotApplicableEntry(string CodecId, string EquivalenceId, string Reason);

/// <summary>A behavior projection to apply to the expected value at a path of member names (<c>*</c> = every array item; empty = root).</summary>
public sealed record ProjectionStep(string Path, string ProjectionId);

/// <summary>
/// ts-encode-request(domain) → wire; dotnet-read(wire) → domain'; compare(expected, domain') on both runners. When the request
/// type carries normalized behaviors, <see cref="Projections"/> are applied to <see cref="Expected"/> by both runners first
/// (ts-project / dotnet-project), which must agree.
/// </summary>
public sealed record RequestRoundTripCase : ConformanceCase
{
    public required string CodecId { get; init; }
    public required string EquivalenceId { get; init; }
    public required JsonValue Domain { get; init; }
    public required JsonValue Expected { get; init; }
    public IReadOnlyList<ProjectionStep>? Projections { get; init; }
}

/// <summary>Request direction: ts-encode-key → dotnet-read-key; response direction: dotnet-write-key → ts-decode-key; then compare on both.</summary>
public sealed record KeyRoundTripCase : ConformanceCase
{
    public required string CodecId { get; init; }
    public required string EquivalenceId { get; init; }
    public required JsonValue Key { get; init; }
    public required bool RequestDirection { get; init; }

    /// <summary>What the decoding side must see; absent when the key round-trips unchanged (datetime-local-wire keys come back in the server's zone).</summary>
    public JsonValue? Expected { get; init; }
}

/// <summary>An invalid wire value that the decoder (node: ts-decode-response, dotnet: dotnet-read) must reject with a codec failure.</summary>
public sealed record NegativeWireCase : ConformanceCase
{
    public required string CodecId { get; init; }
    public required JsonValue Wire { get; init; }
    public required RunnerTarget Target { get; init; }
    public required string Reason { get; init; }
}

/// <summary>validate-domain on the TypeScript codec: valid values return true, invalid values fail with a codec failure.</summary>
public sealed record DomainValidationCase : ConformanceCase
{
    public required string CodecId { get; init; }
    public required JsonValue Domain { get; init; }
    public required bool ExpectValid { get; init; }
}

/// <summary>Two different values compared with the registered oracle must not be reported equal.</summary>
public sealed record OracleDiscriminationCase : ConformanceCase
{
    public required string EquivalenceId { get; init; }
    public required JsonValue A { get; init; }
    public required JsonValue B { get; init; }
}

/// <summary>The generated suite; its JCS digest and seed are recorded in the evidence.</summary>
public sealed record ConformanceSuite
{
    public const string StandardId = "tisilia.conformance.standard";
    public const string StandardVersion = "0.1.0";

    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Seed { get; init; }
    public required IReadOnlyList<string> OperationIds { get; init; }
    public required IReadOnlyList<string> RequiredTests { get; init; }
    public required IReadOnlyList<ConformanceCase> Cases { get; init; }

    /// <summary>Round trips the suite does not claim (G1 equivalences); listed, not silently skipped.</summary>
    public IReadOnlyList<NotApplicableEntry> NotApplicable { get; init; } = [];

    public string Digest()
    {
        var node = JsonSerializer.SerializeToNode(this, TisiliaJson.Options)!;
        return TisiliaHash.Sha256OfBytes(Jcs.Serialize(node));
    }
}

public sealed record SuiteOptions
{
    public ulong Seed { get; init; } = 1;
    /// <summary>Round-trip cases per codec and direction (boundary corpus first, then seeded random values).</summary>
    public int CasesPerCodec { get; init; } = 12;
    public int KeyCasesPerCodec { get; init; } = 6;

    /// <summary>
    /// The .NET runner's local time zone (IANA or Windows id): the fixed context under which <c>datetime-local-wire</c> is
    /// certified. The server reads an offset form into its own zone (<c>DateTimeOffset.LocalDateTime</c>) and
    /// writes that zone's offset back, so the expectations of those cases are computed for this zone; the evidence records it
    /// as <c>dotnet.timeZone</c>. The zone never changes which categories a closure requires.
    /// </summary>
    public string TimeZoneId { get; init; } = "UTC";
}

/// <summary>SplitMix64: a small, fully specified generator so that a seed reproduces the suite on every platform.</summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Next(int exclusiveMax) => exclusiveMax <= 0 ? 0 : (int)(Next() % (ulong)exclusiveMax);

    public bool Chance(int percent) => Next(100) < percent;

    public long NextInt64(long min, long max)
    {
        var range = (ulong)(max - min) + 1UL;
        return range == 0 ? (long)Next() : min + (long)(Next() % range);
    }
}

/// <summary>
/// Builds the standard suite from the qualification closure: boundary corpora, an invalid
/// corpus, seeded property-based values, oracle discrimination checks. Everything is derived from the contract, so
/// the same closure and seed give the same suite; nothing is executed here.
/// </summary>
public sealed class SuiteBuilder
{
    private readonly ContractIndex _index;
    private readonly ContractClosure _closure;
    private readonly SuiteOptions _options;
    private readonly TimeZoneInfo _zone;
    private SplitMix64 _rng;
    private readonly List<ConformanceCase> _cases = [];
    private readonly List<NotApplicableEntry> _notApplicable = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>A G1 equivalence makes no round-trip claim (an opaque behavior on the path, SV19): no round-trip or discrimination cases, recorded as not applicable.</summary>
    private bool ClaimsRoundTrip(string codecId, string equivalenceId)
    {
        if (_index.Equivalences.TryGetValue(equivalenceId, out var eq) && eq.Grade == Grade.G1)
        {
            _notApplicable.Add(new NotApplicableEntry(codecId, equivalenceId, "grade-g1-no-round-trip-claim"));
            return false;
        }

        return true;
    }

    private SuiteBuilder(ContractIndex index, ContractClosure closure, SuiteOptions options)
    {
        _index = index;
        _closure = closure;
        _options = options;
        _zone = ResolveTimeZone(options.TimeZoneId);
        _rng = new SplitMix64(options.Seed);
    }

    /// <summary>Resolves an IANA or Windows zone id on this host (.NET 6+ converts between the two when ICU is available).</summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"time zone '{id}' is not known to this host (IANA or Windows ids are accepted on .NET 6+); the datetime-local-wire expectations need the .NET runner's zone", nameof(id), e);
        }
    }

    public static ConformanceSuite Build(ContractIndex index, ContractClosure closure, SuiteOptions options)
    {
        var builder = new SuiteBuilder(index, closure, options);
        builder.BuildCases();
        return new ConformanceSuite
        {
            Id = ConformanceSuite.StandardId,
            Version = ConformanceSuite.StandardVersion,
            Seed = options.Seed.ToString(CultureInfo.InvariantCulture),
            OperationIds = closure.OperationIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            RequiredTests = builder._cases.Select(c => c.Category).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Cases = builder._cases,
            NotApplicable = builder._notApplicable,
        };
    }

    /// <summary>The categories a closure requires (seed-independent): what a valid evidence must list in <c>requiredTests</c>.</summary>
    public static IReadOnlyList<string> RequiredCategories(ContractIndex index, ContractClosure closure)
        => Build(index, closure, new SuiteOptions { Seed = 1, CasesPerCodec = 2, KeyCasesPerCodec = 1 }).RequiredTests;

    /// <summary>
    /// Generated values cannot anticipate the domain rules of module (paired/portable) codecs; inputs that the
    /// registered TypeScript domain rule rejects are not conformance failures but candidates outside the domain and
    /// are dropped here. Builtin-only codecs are never filtered: a builtin rejecting a generated value is a finding.
    /// Oracle-discrimination cases are rebuilt from the surviving samples; the required categories stay as derived
    /// from the closure, so a codec without any surviving sample cannot be claimed.
    /// </summary>
    public static async Task<ConformanceSuite> FilterAsync(ContractIndex index, ConformanceSuite suite, Func<string, string, JsonValue, Task<bool>> validate)
    {
        var moduleReach = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool NeedsFilter(string codecId)
        {
            if (moduleReach.TryGetValue(codecId, out var known))
            {
                return known;
            }

            moduleReach[codecId] = false;
            var codec = index.Codecs[codecId];
            var result = codec.Origin != CodecOrigin.Builtin || codec.Dependencies.Any(d => index.Codecs.ContainsKey(d) && NeedsFilter(d));
            moduleReach[codecId] = result;
            return result;
        }

        var kept = new List<ConformanceCase>();
        var samples = new Dictionary<string, (string ProfileId, string Tag, List<JsonValue> Values)>(StringComparer.Ordinal);
        void Sample(string equivalenceId, string profileId, string category, JsonValue value)
        {
            var tag = category[(category.IndexOf(':', StringComparison.Ordinal) + 1)..];
            if (!samples.TryGetValue(equivalenceId, out var entry))
            {
                entry = (profileId, tag, []);
                samples[equivalenceId] = entry;
            }

            entry.Values.Add(value);
        }

        foreach (var c in suite.Cases)
        {
            switch (c)
            {
                case ResponseRoundTripCase r:
                    if (NeedsFilter(r.CodecId) && !await validate(r.CodecId, r.ProfileId, r.Domain).ConfigureAwait(false))
                    {
                        continue;
                    }

                    kept.Add(r);
                    Sample(r.EquivalenceId, r.ProfileId, r.Category, r.Expected);
                    break;
                case RequestRoundTripCase r:
                    if (NeedsFilter(r.CodecId) && !await validate(r.CodecId, r.ProfileId, r.Domain).ConfigureAwait(false))
                    {
                        continue;
                    }

                    kept.Add(r);
                    Sample(r.EquivalenceId, r.ProfileId, r.Category, r.Expected);
                    break;
                case KeyRoundTripCase k:
                    if (NeedsFilter(k.CodecId) && !await validate(k.CodecId, k.ProfileId, k.Key).ConfigureAwait(false))
                    {
                        continue;
                    }

                    kept.Add(k);
                    break;
                case DomainValidationCase dv:
                    if (dv.ExpectValid && NeedsFilter(dv.CodecId) && !await validate(dv.CodecId, dv.ProfileId, dv.Domain).ConfigureAwait(false))
                    {
                        continue;
                    }

                    kept.Add(dv);
                    break;
                case OracleDiscriminationCase:
                    break; // rebuilt below
                default:
                    kept.Add(c);
                    break;
            }
        }

        foreach (var (equivalenceId, (profileId, tag, values)) in samples.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            for (var i = 1; i < values.Count; i++)
            {
                if (!AstOracles.Structural(values[0], values[i]))
                {
                    kept.Add(new OracleDiscriminationCase { Id = $"oracle.{equivalenceId}", Category = "oracle-discrimination:" + tag, ProfileId = profileId, EquivalenceId = equivalenceId, A = values[0], B = values[i] });
                    break;
                }
            }
        }

        return suite with { Cases = kept };
    }

    private void BuildCases()
    {
        var codecIds = _closure.RegistryRefs.Where(r => r.Registry == RegistryName.Codecs).Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var profileIds = _closure.ProfileIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var codecId in codecIds)
        {
            var codec = _index.Codecs[codecId];
            if (!_index.Types.TryGetValue(codec.TypeId, out var model) || !IsTestable(model))
            {
                continue;
            }

            // one stream per codec: the first samples (and therefore the categories) do not depend on how many cases other codecs consumed
            _rng = new SplitMix64(_options.Seed ^ Fnv1a64(codecId));

            var profileId = codec.ProfileIds.FirstOrDefault(profileIds.Contains) ?? profileIds.FirstOrDefault() ?? "";
            var tag = TypeTag(model, codec);
            // System.Text.Json applies DictionaryKeyPolicy on write only; the TypeScript key codecs do not model it, so key
            // cases are only meaningful (and generated) for profiles without a key policy.
            var keyPolicyNone = !_index.Profiles.TryGetValue(profileId, out var keyProfile) || keyProfile.Options.DictionaryKeyPolicyId == Builtins.NamingNone;
            var use = new TypeUse { TypeId = codec.TypeId, CodecId = codecId, SemanticNullable = codecId.EndsWith(".nullable", StringComparison.Ordinal) };
            var caps = codec.Capabilities;
            if (caps.Response is { } response)
            {
                if (ClaimsRoundTrip(codecId, response.EquivalenceId))
                {
                    var samples = new List<JsonValue>();
                    var responseProjections = ProjectionSteps(use);
                    for (var i = 0; i < _options.CasesPerCodec; i++)
                    {
                        var domain = Generate(use, GenMode.Response, i, 0, profileId);
                        var expected = ExpectedAfterWrite(domain, use, profileId);
                        samples.Add(expected);
                        Add(new ResponseRoundTripCase { Id = $"response.{codecId}.{i}", Category = (responseProjections is null ? "response-round-trip:" : "response-round-trip-projected:") + tag, ProfileId = profileId, CodecId = codecId, EquivalenceId = response.EquivalenceId, Domain = domain, Expected = expected, Projections = responseProjections });
                    }

                    AddDiscrimination(response.EquivalenceId, profileId, tag, samples);
                }

                foreach (var (wire, reason) in NegativeWires(response.Wire, 0))
                {
                    Add(new NegativeWireCase { Id = $"negative.node.{codecId}.{reason}", Category = "negative-wire:" + tag, ProfileId = profileId, CodecId = codecId, Wire = wire, Target = RunnerTarget.Node, Reason = reason });
                }
            }

            if (caps.Request is { } request)
            {
                if (ClaimsRoundTrip(codecId, request.EquivalenceId))
                {
                    var samples = new List<JsonValue>();
                    var projections = ProjectionSteps(use);
                    for (var i = 0; i < _options.CasesPerCodec; i++)
                    {
                        var domain = Generate(use, GenMode.Request, i, 0, profileId);
                        var expected = ExpectedAfterRead(domain, use, profileId);
                        samples.Add(expected);
                        Add(new RequestRoundTripCase { Id = $"request.{codecId}.{i}", Category = (projections is null ? "request-round-trip:" : "request-round-trip-projected:") + tag, ProfileId = profileId, CodecId = codecId, EquivalenceId = request.EquivalenceId, Domain = domain, Expected = expected, Projections = projections });
                    }

                    AddDiscrimination(request.EquivalenceId, profileId, tag, samples);
                }

                foreach (var (wire, reason) in NegativeWires(request.Wire, 0))
                {
                    Add(new NegativeWireCase { Id = $"negative.dotnet.{codecId}.{reason}", Category = "negative-wire:" + tag, ProfileId = profileId, CodecId = codecId, Wire = wire, Target = RunnerTarget.Dotnet, Reason = reason });
                }
            }

            if (caps.RequestKey is { } requestKey && keyPolicyNone && IsKeyShape(model))
            {
                for (var i = 0; i < _options.KeyCasesPerCodec; i++)
                {
                    var key = Generate(use with { SemanticNullable = false }, GenMode.Key, i, 0, profileId);
                    Add(new KeyRoundTripCase { Id = $"key.request.{codecId}.{i}", Category = "key-round-trip:" + tag, ProfileId = profileId, CodecId = codecId, EquivalenceId = requestKey.EquivalenceId, Key = key, Expected = KeyExpected(key, model), RequestDirection = true });
                }
            }

            if (caps.ResponseKey is { } responseKey && keyPolicyNone && IsKeyShape(model))
            {
                for (var i = 0; i < _options.KeyCasesPerCodec; i++)
                {
                    var key = Generate(use with { SemanticNullable = false }, GenMode.Key, i, 0, profileId);
                    Add(new KeyRoundTripCase { Id = $"key.response.{codecId}.{i}", Category = "key-round-trip:" + tag, ProfileId = profileId, CodecId = codecId, EquivalenceId = responseKey.EquivalenceId, Key = key, Expected = KeyExpected(key, model), RequestDirection = false });
                }
            }

            // validate-domain: one valid value, invalid values of every other AST kind
            var valid = Generate(use with { SemanticNullable = false }, GenMode.Response, 0, 0, profileId);
            Add(new DomainValidationCase { Id = $"validate.{codecId}.valid", Category = "domain-validation:" + tag, ProfileId = profileId, CodecId = codecId, Domain = valid, ExpectValid = true });
            foreach (var (invalid, reason) in InvalidDomains(model, use))
            {
                Add(new DomainValidationCase { Id = $"validate.{codecId}.{reason}", Category = "domain-validation:" + tag, ProfileId = profileId, CodecId = codecId, Domain = invalid, ExpectValid = false });
            }
        }
    }

    /// <summary>Key cases need a scalar-like domain: primitives, enums and brands over a primitive (module key codecs such as Int128's).</summary>
    private bool IsKeyShape(Model model)
        => model.Shape is PrimitiveShape or EnumShape || (model.Shape is BrandShape brand && _index.Types.TryGetValue(brand.Base.TypeId, out var baseModel) && baseModel.Shape is PrimitiveShape);

    private void Add(ConformanceCase c)
    {
        if (_ids.Add(c.Id))
        {
            _cases.Add(c);
        }
    }

    private static ulong Fnv1a64(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private void AddDiscrimination(string equivalenceId, string profileId, string tag, List<JsonValue> samples)
    {
        for (var i = 1; i < samples.Count; i++)
        {
            if (!AstOracles.Structural(samples[0], samples[i]))
            {
                Add(new OracleDiscriminationCase { Id = $"oracle.{equivalenceId}", Category = "oracle-discrimination:" + tag, ProfileId = profileId, EquivalenceId = equivalenceId, A = samples[0], B = samples[i] });
                return;
            }
        }
    }

    /// <summary>
    /// Types the standard suite can construct on both sides. The DateTime-kind scalars (utc/unspecified/local-wire) only reach a
    /// contract through portable definitions (raw <c>System.DateTime</c> members are SV03) and are generated like every other
    /// scalar; local-wire values carry the zone-dependent expectations of <see cref="SuiteOptions.TimeZoneId"/>.
    /// </summary>
    private bool IsTestable(Model model)
    {
        return Reachable(model, [], 0);

        bool Reachable(Model m, HashSet<string> visiting, int depth)
        {
            if (depth > 8 || !visiting.Add(m.Id))
            {
                return true; // recursion is fine: generation terminates through optional/null/empty branches
            }

            switch (m.Shape)
            {
                case PrimitiveShape:
                    return true;
                case EnumShape e:
                    return e.Members.Count > 0;
                case ObjectShape o:
                    return o.Properties.All(pr => _index.Types.TryGetValue(pr.Use.TypeId, out var t) && Reachable(t, visiting, depth + 1))
                        && (o.Extension is not CaptureExtension c || (_index.Types.TryGetValue(c.Value.TypeId, out var et) && Reachable(et, visiting, depth + 1)));
                case ArrayShape a:
                    return _index.Types.TryGetValue(a.Element.TypeId, out var at) && Reachable(at, visiting, depth + 1);
                case MapShape mp:
                    return _index.Types.TryGetValue(mp.Key.TypeId, out var kt) && kt.Shape is PrimitiveShape or EnumShape && Reachable(kt, visiting, depth + 1)
                        && _index.Types.TryGetValue(mp.Value.TypeId, out var vt) && Reachable(vt, visiting, depth + 1)
                        && BuiltinComparer(mp.ComparerId) is not null;
                case BrandShape br:
                    return _index.Types.TryGetValue(br.Base.TypeId, out var bt) && Reachable(bt, visiting, depth + 1);
                case UnionShape u:
                    return u.Variants.Count > 0 && u.Variants.All(v => _index.Types.TryGetValue(v.Use.TypeId, out var vt2) && Reachable(vt2, visiting, depth + 1));
                default:
                    return false;
            }
        }
    }

    private static string ScalarName(PrimitiveShape p)
    {
        var id = p.PrimitiveId;
        return id.StartsWith("tisilia.", StringComparison.Ordinal) && id.EndsWith("@0.1", StringComparison.Ordinal) ? id["tisilia.".Length..^"@0.1".Length] : id;
    }

    private static string TypeTag(Model model, Codec codec) => codec.Origin switch
    {
        CodecOrigin.Paired => "paired",
        CodecOrigin.Portable => "portable",
        _ => model.Shape switch
        {
            PrimitiveShape p => "std." + ScalarName(p),
            EnumShape => "enum",
            ObjectShape => "object",
            ArrayShape => "array",
            MapShape => "map",
            BrandShape => "brand",
            UnionShape => "union",
            _ => "unknown",
        },
    };

    // ------------------------------------------------------------------ generation

    private enum GenMode
    {
        Request,
        Response,
        Key,
    }

    private JsonValue Generate(TypeUse use, GenMode mode, int index, int depth, string profileId)
    {
        if (use.SemanticNullable && mode != GenMode.Key && (index == 1 || (index > 1 && _rng.Chance(20))))
        {
            return DomainAst.Null;
        }

        var model = _index.Types[use.TypeId];
        switch (model.Shape)
        {
            case PrimitiveShape p:
                return Scalar(ScalarName(p), index, mode);
            case EnumShape e:
                return EnumValue(e, index);
            case ObjectShape o:
                return ObjectValue(model, o, use.CodecId, mode, index, depth, profileId);
            case ArrayShape a:
            {
                var count = depth > 3 ? 0 : index == 0 ? 2 : index == 1 ? 0 : _rng.Next(5);
                if (ReadNormalizesItems(use.CodecId))
                {
                    count = Math.Min(count, 1);
                }

                return DomainAst.Array(Enumerable.Range(0, count).Select(i => Generate(a.Element, mode, index + i + 1, depth + 1, profileId)).ToList());
            }

            case MapShape m:
                return MapValue(m, mode, index, depth, profileId);
            case BrandShape b:
                // the domain of a module brand is narrower than its base scalar (an Int128 is not any string): the module's own samples
                return AdditionalModule.Sample(b.BrandId, index, _rng.Next, FixedStrings(use.CodecId)) ?? Generate(b.Base with { SemanticNullable = false }, mode, index, depth, profileId);
            case UnionShape u:
            {
                var variant = u.Variants[index % u.Variants.Count];
                var value = Generate(variant.Use with { SemanticNullable = false }, mode, index, depth + 1, profileId);
                return WithDiscriminator(value, model, variant);
            }

            default:
                throw new InvalidOperationException("unknown shape");
        }
    }

    private JsonValue WithDiscriminator(JsonValue value, Model unionModel, UnionVariant variant)
    {
        if (value is not JsonObjectValue obj)
        {
            return value;
        }

        // the wire discriminator name is on the union's write/read wire; the variant model carries it as its first property
        var variantModel = _index.Types[variant.Use.TypeId];
        if (variantModel.Shape is not ObjectShape shape || shape.Properties.Count == 0)
        {
            return value;
        }

        var discriminator = shape.Properties[0];
        var isNumber = _index.Types.TryGetValue(discriminator.Use.TypeId, out var dt) && dt.Shape is PrimitiveShape dp && ScalarName(dp) != "string";
        JsonValue tag = isNumber ? DomainAst.Number(variant.Tag) : DomainAst.String(variant.Tag);
        var entries = obj.Entries.Where(e => e.Name != discriminator.Name).Select(e => (e.Name, e.Value)).ToList();
        entries.Insert(0, (discriminator.Name, tag));
        _ = unionModel;
        return DomainAst.Object(entries);
    }

    /// <summary>
    /// Module codecs declare non-secret binding context; an entry whose name is a string property of
    /// the domain shape fixes that property's generated value (e.g. currency=JPY), which keeps generated samples
    /// inside the module's domain rule. Everything else is generated.
    /// </summary>
    private IReadOnlyDictionary<string, string> FixedStrings(string codecId)
    {
        if (!_index.Codecs.TryGetValue(codecId, out var codec) || codec.Origin == CodecOrigin.Builtin || !_index.Bindings.TryGetValue(codec.BindingId, out var binding))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return binding.Context.Where(e => !e.Confidential).GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The server's read of this collection drops duplicates or reorders its items (sets, stacks: the codec's request equivalence lists
    /// them as notPreserved). The runner builds response values through that read, so generated values keep at most one item.
    /// </summary>
    private bool ReadNormalizesItems(string codecId)
        => _index.Codecs.TryGetValue(codecId, out var codec)
            && codec.Capabilities.Request is { } request
            && _index.Equivalences.TryGetValue(request.EquivalenceId, out var eq)
            && eq.NotPreserved.Any(n => n is "duplicates" or "order");

    /// <summary>
    /// Members whose wire is a literal — the discriminator a polymorphic variant carries: the domain value is that
    /// literal, because the server reads and writes the variant only through its base type.
    /// </summary>
    private Dictionary<string, JsonValue> LiteralMembers(string codecId, GenMode mode)
    {
        var literals = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        if (_index.Codecs.TryGetValue(codecId, out var codec)
            && (mode == GenMode.Response ? codec.Capabilities.Response : codec.Capabilities.Request) is { } capability
            && _index.Wires.TryGetValue(capability.Wire.WireId, out var wire) && wire.Shape is ObjectWire objectWire)
        {
            foreach (var property in objectWire.Properties)
            {
                if (_index.Wires.TryGetValue(property.Wire.WireId, out var propertyWire) && propertyWire.Shape is LiteralWire literal)
                {
                    literals[property.Name] = literal.Value;
                }
            }
        }

        return literals;
    }

    private JsonValue ObjectValue(Model model, ObjectShape shape, string codecId, GenMode mode, int index, int depth, string profileId)
    {
        var entries = new List<(string, JsonValue)>();
        var fixedStrings = FixedStrings(codecId);
        var literals = LiteralMembers(codecId, mode);
        var i = 0;
        foreach (var prop in shape.Properties)
        {
            if (literals.TryGetValue(prop.Name, out var literal))
            {
                entries.Add((prop.Name, literal));
                i++;
                continue;
            }

            if (fixedStrings.TryGetValue(prop.Name, out var fixedValue) && _index.Types.TryGetValue(prop.Use.TypeId, out var propModel) && propModel.Shape is PrimitiveShape fp && ScalarName(fp) == "string")
            {
                entries.Add((prop.Name, DomainAst.String(fixedValue)));
                i++;
                continue;
            }

            var omit = false;
            if (prop.Presence == Presence.Optional)
            {
                // request: the server reads a missing optional member as its CLR default; only nullable members have a
                // representable expectation (null), so only those are omitted
                omit = mode == GenMode.Request && prop.Use.SemanticNullable && (index == 1 || (index > 1 && _rng.Chance(30)));
                // recursion terminates through optional members
                if (depth > 4 && prop.Use.SemanticNullable)
                {
                    omit = mode == GenMode.Request;
                }
            }

            if (omit)
            {
                continue;
            }

            var childIndex = index == 0 ? 0 : index + i + 1;
            JsonValue value;
            if (depth > 4 && prop.Use.SemanticNullable)
            {
                value = DomainAst.Null;
            }
            else if (mode == GenMode.Response && WriteIgnore(model, prop.Name, profileId) == IgnoreCondition.WhenWritingDefault)
            {
                value = NonDefault(prop.Use, mode, childIndex, depth, profileId);
            }
            else
            {
                value = Generate(prop.Use, mode, childIndex, depth + 1, profileId);
            }

            entries.Add((prop.Name, value));
            i++;
        }

        if (shape.Extension is CaptureExtension capture && index % 3 == 2 && depth < 3)
        {
            var count = 1 + _rng.Next(2);
            var ext = new List<(string, JsonValue)>();
            for (var k = 0; k < count; k++)
            {
                var name = "ext" + (index + k).ToString(CultureInfo.InvariantCulture);
                if (shape.Properties.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                ext.Add((name, Generate(capture.Value with { SemanticNullable = false }, mode, index + k, depth + 1, profileId)));
            }

            if (ext.Count > 0)
            {
                entries.Add(("extensions", DomainAst.Object(ext)));
            }
        }

        return DomainAst.Object(entries);
    }

    private JsonValue NonDefault(TypeUse use, GenMode mode, int index, int depth, string profileId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var v = Generate(use with { SemanticNullable = false }, mode, index + attempt + 2, depth + 1, profileId);
            if (!IsDefaultLike(v))
            {
                return v;
            }
        }

        return Generate(use with { SemanticNullable = false }, mode, 0, depth + 1, profileId);
    }

    private string? BuiltinComparer(string id)
    {
        if (!_index.Comparers.TryGetValue(id, out var comparer)) { return null; }
        if (Builtins.Is(comparer.BindingId, BuiltinKind.Comparer)) { return comparer.BindingId; }
        return _index.Bindings.TryGetValue(comparer.BindingId, out var binding) && binding.Implementation is BuiltinImpl b ? b.Id : null;
    }

    private JsonValue MapValue(MapShape m, GenMode mode, int index, int depth, string profileId)
    {
        var keyModel = _index.Types[m.Key.TypeId];
        var comparer = BuiltinComparer(m.ComparerId) ?? Builtins.ComparerOrdinal;
        var ignoreCase = comparer == Builtins.ComparerOrdinalIgnoreCase;
        var keyPolicy = _index.Profiles.TryGetValue(profileId, out var prof) && prof.Options.DictionaryKeyPolicyId != Builtins.NamingNone;
        var mixedDateTimeKeys = keyModel.Shape is PrimitiveShape primitive && ScalarName(primitive) == "datetime";
        var count = depth > 3 || keyPolicy ? 0 : index == 0 ? 2 : index == 1 ? 0 : index == 2 && mixedDateTimeKeys ? 1 : _rng.Next(4);
        var entries = new List<(string, JsonValue)>();
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var k = 0; k < count * 3 && entries.Count < count; k++)
        {
            // Multiple mixed DateTime keys must avoid Local: server-zone conversion and DST can collapse distinct instants.
            var keyAst = index == 2 && mixedDateTimeKeys ? Scalar("datetime-local-wire", 4, GenMode.Key)
                : count > 1 && mixedDateTimeKeys
                ? Scalar(k % 2 == 0 ? "datetime-utc" : "datetime-unspecified", index + k + 1, GenMode.Key)
                : Generate(m.Key with { SemanticNullable = false }, GenMode.Key, index + k + 1, depth + 1, profileId);
            var encoded = KeyText(keyAst, keyModel);
            if (encoded is null || !seen.Add(encoded))
            {
                continue;
            }

            entries.Add((encoded, Generate(m.Value, mode, index + k + 1, depth + 1, profileId)));
        }

        return DomainAst.Object(entries);
    }

    /// <summary>Canonical key text of a projected key value (the key codecs write exactly this).</summary>
    private static string? KeyText(JsonValue key, Model keyModel) => key switch
    {
        JsonStringValue s => s.Value,
        JsonNumberValue n => n.Text,
        JsonBooleanValue b => b.Value ? "True" : "False",
        _ => null,
    };

    private JsonValue EnumValue(EnumShape e, int index)
    {
        var members = e.Members;
        if (e.Flags && index >= members.Count && members.Count > 1)
        {
            var a = BigInteger.Parse(members[_rng.Next(members.Count)].Value, CultureInfo.InvariantCulture);
            var b = BigInteger.Parse(members[_rng.Next(members.Count)].Value, CultureInfo.InvariantCulture);
            return DomainAst.Number((a | b).ToString(CultureInfo.InvariantCulture));
        }

        var member = members[index < members.Count ? index : _rng.Next(members.Count)];
        return DomainAst.Number(member.Value);
    }

    private static readonly string[] StringCorpus =
    [
        "", "a", "hello world", "héllo wörld", "日本語テキスト", "emoji 😀 🎉", "\"quoted\" \\ backslash / slash", "line\nbreak\ttab", "control \u0001\u001f", "<script>&amp;</script>",
        "ﬃ ligature ǅ", "𝔘𝔫𝔦𝔠𝔬𝔡𝔢 surrogates", "   padded   ", new string('x', 300),
    ];

    private JsonValue Scalar(string name, int index, GenMode mode)
    {
        switch (name)
        {
            case "string":
                return DomainAst.String(index < StringCorpus.Length ? StringCorpus[index] : RandomString());
            case "char":
            {
                var corpus = new[] { "a", "Z", "0", " ", "é", "日", "\"", "\\", "\u0000" };
                return DomainAst.String(index < corpus.Length ? corpus[index] : ((char)(0x20 + _rng.Next(0x7e - 0x20))).ToString());
            }

            case "boolean":
                return DomainAst.Boolean(index % 2 == 0);
            case "guid":
            {
                var corpus = new[] { "00000000-0000-0000-0000-000000000000", "550e8400-e29b-41d4-a716-446655440000", "ffffffff-ffff-ffff-ffff-ffffffffffff" };
                return DomainAst.String(index < corpus.Length ? corpus[index] : DomainAst.FormatGuid(RandomGuid()));
            }

            case "bytes":
            {
                var corpus = new[] { "", "AA==", "AAE=", "AAEC", "/+8=", "TWFuIGlzIGRpc3Rpbmd1aXNoZWQ=" };
                if (index < corpus.Length)
                {
                    return DomainAst.String(corpus[index]);
                }

                var bytes = new byte[_rng.Next(40)];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)_rng.Next(256);
                }

                return DomainAst.String(Convert.ToBase64String(bytes));
            }

            case "json-value":
                return JsonValueSample(index);
            case "int8":
                return Integer(index, sbyte.MinValue, sbyte.MaxValue);
            case "uint8":
                return Integer(index, byte.MinValue, byte.MaxValue);
            case "int16":
                return Integer(index, short.MinValue, short.MaxValue);
            case "uint16":
                return Integer(index, ushort.MinValue, ushort.MaxValue);
            case "int32":
                return Integer(index, int.MinValue, int.MaxValue);
            case "uint32":
                return Integer(index, uint.MinValue, uint.MaxValue);
            case "int64":
                return Integer(index, long.MinValue, long.MaxValue);
            case "uint64":
                return UInt64Value(index);
            case "decimal":
                return DecimalValue(index);
            case "float64":
                return Float64Value(index);
            case "float32":
                return Float32Value(index);
            case "date-only":
            {
                var corpus = new[] { new DateOnly(2026, 9, 30), new DateOnly(1, 1, 1), new DateOnly(9999, 12, 31), new DateOnly(2024, 2, 29), new DateOnly(1900, 2, 28) };
                var d = index < corpus.Length ? corpus[index] : DateOnly.FromDayNumber((int)_rng.NextInt64(0, DateOnly.MaxValue.DayNumber));
                return DomainAst.String(DomainAst.StjText(d));
            }

            case "time-only":
            {
                var corpus = new[] { new TimeOnly(15, 4, 5), TimeOnly.MinValue, TimeOnly.MaxValue, new TimeOnly(0, 0, 0, 0, 1), new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9999999)) };
                var t = index < corpus.Length ? corpus[index] : new TimeOnly(_rng.NextInt64(0, TimeOnly.MaxValue.Ticks));
                return DomainAst.String(DomainAst.StjText(t));
            }

            case "datetime-offset":
            {
                var corpus = new[]
                {
                    new DateTimeOffset(2026, 9, 30, 15, 4, 5, TimeSpan.FromHours(9)).AddTicks(1234567),
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue,
                    new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero),
                    new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(-14)),
                    new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(14)),
                    new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.FromMinutes(345)).AddTicks(5),
                };
                DateTimeOffset v;
                if (index < corpus.Length)
                {
                    v = corpus[index];
                }
                else
                {
                    var offset = TimeSpan.FromMinutes(_rng.NextInt64(-14 * 60, 14 * 60));
                    var ticks = _rng.NextInt64(DateTime.MinValue.Ticks + 15 * TimeSpan.TicksPerHour, DateTime.MaxValue.Ticks - 15 * TimeSpan.TicksPerHour);
                    v = new DateTimeOffset(ticks, offset);
                }

                return DomainAst.String(DomainAst.StjText(v));
            }

            case "datetime":
                return Scalar(new[] { "datetime-utc", "datetime-unspecified", "datetime-local-wire" }[index % 3], index / 3, mode);

            case "datetime-utc":
            case "datetime-unspecified":
            {
                // Kind decides the suffix System.Text.Json writes ("O" format: Utc → Z, Unspecified → none); both round-trip unchanged
                var kind = name == "datetime-utc" ? DateTimeKind.Utc : DateTimeKind.Unspecified;
                var corpus = new[]
                {
                    new DateTime(2026, 9, 30, 6, 4, 5, kind).AddTicks(1234567),
                    DateTime.SpecifyKind(DateTime.MinValue, kind),
                    DateTime.SpecifyKind(DateTime.MaxValue, kind),
                    new DateTime(2024, 2, 29, 23, 59, 59, kind),
                    new DateTime(2000, 1, 1, 0, 0, 0, kind).AddTicks(5),
                };
                var v = index < corpus.Length ? corpus[index] : new DateTime(_rng.NextInt64(0, DateTime.MaxValue.Ticks), kind);
                return DomainAst.String(DomainAst.StjText(v));
            }

            case "datetime-local-wire":
            {
                // the server reads the offset form into its own zone (DateTimeOffset.LocalDateTime) and writes that zone's offset
                // back; instants whose local time is ambiguous there (DST fall-back) and the extremes of the range are not
                // generated, so LocalWireRoundTrip is the exact expectation (fixed server zone)
                var corpus = new[]
                {
                    new DateTimeOffset(2026, 9, 30, 15, 4, 5, TimeSpan.FromHours(9)).AddTicks(1234567),
                    new DateTimeOffset(DateTime.MinValue.Ticks + 14 * TimeSpan.TicksPerHour, TimeSpan.Zero),
                    new DateTimeOffset(DateTime.MaxValue.Ticks - 14 * TimeSpan.TicksPerHour, TimeSpan.Zero),
                    new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero),
                    new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(-14)),
                    new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(14)),
                    new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.FromMinutes(345)).AddTicks(5),
                };
                DateTimeOffset v;
                if (index < corpus.Length)
                {
                    v = corpus[index];
                }
                else
                {
                    var offset = TimeSpan.FromMinutes(_rng.NextInt64(-14 * 60, 14 * 60));
                    var ticks = _rng.NextInt64(DateTime.MinValue.Ticks + 15 * TimeSpan.TicksPerHour, DateTime.MaxValue.Ticks - 15 * TimeSpan.TicksPerHour);
                    v = new DateTimeOffset(ticks, offset);
                }

                for (var attempt = 0; attempt < 48 && _zone.IsAmbiguousTime(TimeZoneInfo.ConvertTime(v, _zone).DateTime); attempt++)
                {
                    v = v.AddHours(1);
                }

                return DomainAst.String(DomainAst.StjText(v));
            }

            case "duration":
            {
                var corpus = new[] { TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.MinValue, TimeSpan.MaxValue, new TimeSpan(1, 2, 3, 4, 5), TimeSpan.FromDays(-3).Add(TimeSpan.FromMilliseconds(-1.5)) };
                var v = index < corpus.Length ? corpus[index] : TimeSpan.FromTicks(_rng.NextInt64(long.MinValue, long.MaxValue));
                return DomainAst.String(DomainAst.StjText(v));
            }

            default:
                throw new InvalidOperationException("scalar not supported by the suite: " + name);
        }
    }

    private JsonValue Integer(int index, long min, long max)
    {
        var corpus = new List<long> { 0, 1, -1, max, min, 42 };
        if (max > 9007199254740993L)
        {
            corpus.AddRange([9007199254740991L, 9007199254740992L, 9007199254740993L, -9007199254740993L]);
        }

        corpus = corpus.Where(v => v >= min && v <= max).Distinct().ToList();
        return DomainAst.Number(DomainAst.FormatInteger(index < corpus.Count ? corpus[index] : _rng.NextInt64(min, max)));
    }

    private JsonValue UInt64Value(int index)
    {
        var corpus = new ulong[] { 0, 1, ulong.MaxValue, 9007199254740993UL, 9223372036854775808UL, 42 };
        return DomainAst.Number(DomainAst.FormatInteger(index < corpus.Length ? corpus[index] : _rng.Next()));
    }

    private JsonValue DecimalValue(int index)
    {
        var corpus = new[] { 123.4500m, 0m, -1m, decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, 1.0m, 79228162514264337593543950335m, -0.5m, 100m, 1E-28m };
        decimal v;
        if (index < corpus.Length)
        {
            v = corpus[index];
        }
        else
        {
            var lo = (int)_rng.Next();
            var mid = (int)_rng.Next();
            var hi = _rng.Chance(30) ? (int)_rng.Next() : 0;
            v = new decimal(lo, mid, hi, _rng.Chance(50), (byte)_rng.Next(29));
        }

        return DomainAst.Number(DomainAst.FormatDecimal(v));
    }

    private JsonValue Float64Value(int index)
    {
        var corpus = new[] { 0d, -0d, 1d, 0.1d, -2.5d, 1e300, 5e-324, double.MaxValue, double.Epsilon, 123456789012345680000d, 0.000001d, 1e-5, 1e16, 1e17, 3.141592653589793 };
        double v;
        if (index < corpus.Length)
        {
            v = corpus[index];
        }
        else
        {
            do
            {
                v = BitConverter.Int64BitsToDouble((long)_rng.Next());
            }
            while (!double.IsFinite(v));
        }

        return DomainAst.Float64(v);
    }

    private JsonValue Float32Value(int index)
    {
        var corpus = new[] { 0f, -0f, 1f, 0.1f, -2.5f, float.MaxValue, float.Epsilon, 1e8f, 1e9f, 16777217f, 3.1415927f };
        float v;
        if (index < corpus.Length)
        {
            v = corpus[index];
        }
        else
        {
            do
            {
                v = BitConverter.Int32BitsToSingle((int)_rng.Next());
            }
            while (!float.IsFinite(v));
        }

        return DomainAst.Float32(v);
    }

    private JsonValue JsonValueSample(int index)
    {
        var corpus = new JsonValue[]
        {
            DomainAst.Object(("a", DomainAst.Number("1")), ("b", DomainAst.Array([DomainAst.Boolean(true), DomainAst.Null, DomainAst.String("x")]))),
            DomainAst.Number("12345678901234567890123.4567890000e-3"),
            DomainAst.String("日本語 \"quoted\" \\"),
            DomainAst.Array([]),
            DomainAst.Object(),
            DomainAst.Null,
            DomainAst.Number("-0.0"),
            DomainAst.Object(("dup", DomainAst.Number("1")), ("other", DomainAst.Number("2"))),
        };
        return index < corpus.Length ? corpus[index] : DomainAst.Object(("n", DomainAst.Number(DomainAst.FormatInteger(_rng.NextInt64(long.MinValue, long.MaxValue)))), ("s", DomainAst.String(RandomString())));
    }

    private string RandomString()
    {
        var length = _rng.Next(24);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            var pick = _rng.Next(10);
            chars[i] = pick switch
            {
                < 6 => (char)('a' + _rng.Next(26)),
                6 => ' ',
                7 => (char)(0x3040 + _rng.Next(0x60)),
                8 => (char)(0x00C0 + _rng.Next(0x3F)),
                _ => "\"\\/\n\t{}[]:,"[_rng.Next(11)],
            };
        }

        return new string(chars);
    }

    private Guid RandomGuid()
    {
        var bytes = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            bytes[i] = (byte)_rng.Next(256);
        }

        return new Guid(bytes);
    }

    // ------------------------------------------------------------------ expectations

    private IgnoreCondition WriteIgnore(Model model, string propertyName, string profileId)
    {
        if (!_index.Profiles.TryGetValue(profileId, out var profile))
        {
            return IgnoreCondition.Never;
        }

        var clrPath = model.ClrIdentity + "." + propertyName;
        var scope = profile.Scopes.FirstOrDefault(s => s.Kind == ScopeKind.Member && s.ClrPath == clrPath);
        return scope?.EffectiveIgnoreCondition ?? IgnoreCondition.Never;
    }

    /// <summary>
    /// The text the server produces for a datetime-local-wire value: System.Text.Json reads an offset form as
    /// <c>DateTimeOffset.LocalDateTime</c> (Kind Local in the server's zone) and writes it with the "O" format, i.e. that zone's
    /// local calendar time and offset. Computed for <see cref="SuiteOptions.TimeZoneId"/>; other texts are returned unchanged.
    /// </summary>
    private JsonValue LocalWireRoundTrip(JsonValue domain)
    {
        if (domain is not JsonStringValue s || s.Value.Length < 16 || s.Value[^6] is not ('+' or '-'))
        {
            return domain;
        }

        DateTimeOffset parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(s.Value, TisiliaJson.Plain), TisiliaJson.Plain);
        }
        catch (JsonException)
        {
            return domain;
        }

        return DomainAst.String(DomainAst.StjText(TimeZoneInfo.ConvertTime(parsed, _zone)));
    }

    private string ExpectedMapKey(string text, TypeUse use)
        => _index.Types[use.TypeId].Shape is PrimitiveShape p && ScalarName(p) is "datetime" or "datetime-local-wire"
            ? ((JsonStringValue)LocalWireRoundTrip(DomainAst.String(text))).Value : text;

    private JsonValue? KeyExpected(JsonValue key, Model model) => model.Shape is PrimitiveShape p && ScalarName(p) is "datetime" or "datetime-local-wire" ? LocalWireRoundTrip(key) : null;

    /// <summary>
    /// The behavior projections reachable from a request type: a projection declared on a type is applied where
    /// that type sits in the case root (object members, array items). Null when the closure has none.
    /// </summary>
    private IReadOnlyList<ProjectionStep>? ProjectionSteps(TypeUse root)
    {
        var steps = new List<ProjectionStep>();
        Walk(root.TypeId, "", 0, []);
        return steps.Count == 0 ? null : steps;

        void Walk(string typeId, string path, int depth, HashSet<string> visiting)
        {
            if (depth > 8 || !visiting.Add(typeId) || !_index.Types.TryGetValue(typeId, out var model))
            {
                return;
            }

            switch (model.Shape)
            {
                case ObjectShape o:
                    foreach (var p in o.Properties)
                    {
                        Walk(p.Use.TypeId, path.Length == 0 ? p.Name : path + "/" + p.Name, depth + 1, visiting);
                    }

                    break;
                case ArrayShape a:
                    Walk(a.Element.TypeId, path.Length == 0 ? "*" : path + "/*", depth + 1, visiting);
                    break;
                case BrandShape b:
                    Walk(b.Base.TypeId, path, depth + 1, visiting);
                    break;
            }

            visiting.Remove(typeId);
            // the type's own projection applies after its members were projected (inner first, outer last)
            foreach (var projection in _index.Projections.Values.Where(p => p.SourceTypeId == typeId && p.TargetTypeId == typeId).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (_index.Behaviors.Values.Any(b => b.Behavior.ProjectionId == projection.Id && b.Behavior.Effect == BehaviorEffect.Normalized))
                {
                    steps.Add(new ProjectionStep(path, projection.Id));
                }
            }
        }
    }

    /// <summary>What the TypeScript decoder must see after the server wrote the value: members ignored by the profile disappear.</summary>
    public JsonValue ExpectedAfterWrite(JsonValue domain, TypeUse use, string profileId)
    {
        if (domain is JsonNullValue || !_index.Types.TryGetValue(use.TypeId, out var model))
        {
            return domain;
        }

        switch (model.Shape)
        {
            case PrimitiveShape p when ScalarName(p) is "datetime" or "datetime-local-wire":
                return LocalWireRoundTrip(domain);
            case ObjectShape shape when domain is JsonObjectValue obj:
            {
                var entries = new List<(string, JsonValue)>();
                foreach (var entry in obj.Entries)
                {
                    var prop = shape.Properties.FirstOrDefault(p => p.Name == entry.Name);
                    if (prop is null)
                    {
                        if (entry.Name == "extensions" && shape.Extension is CaptureExtension capture && entry.Value is JsonObjectValue ext)
                        {
                            entries.Add((entry.Name, DomainAst.Object(ext.Entries.Select(e => (e.Name, ExpectedAfterWrite(e.Value, capture.Value, profileId))))));
                        }

                        continue;
                    }

                    var ignore = WriteIgnore(model, prop.Name, profileId);
                    if (ignore == IgnoreCondition.WhenWriting || ignore == IgnoreCondition.Always)
                    {
                        continue;
                    }

                    if (entry.Value is JsonNullValue && ignore is IgnoreCondition.WhenWritingNull or IgnoreCondition.WhenWritingDefault)
                    {
                        continue;
                    }

                    if (ignore == IgnoreCondition.WhenWritingDefault && IsDefaultLike(entry.Value))
                    {
                        continue;
                    }

                    entries.Add((entry.Name, ExpectedAfterWrite(entry.Value, prop.Use, profileId)));
                }

                return DomainAst.Object(entries);
            }

            case ArrayShape a when domain is JsonArrayValue arr:
                return DomainAst.Array(arr.Items.Select(i => ExpectedAfterWrite(i, a.Element, profileId)));
            case MapShape m when domain is JsonObjectValue mobj:
                return DomainAst.Object(mobj.Entries.Select(e => (ExpectedMapKey(e.Name, m.Key), ExpectedAfterWrite(e.Value, m.Value, profileId))));
            case BrandShape b:
                return ExpectedAfterWrite(domain, b.Base, profileId);
            case UnionShape u when domain is JsonObjectValue uo:
            {
                foreach (var variant in u.Variants)
                {
                    var vm = _index.Types[variant.Use.TypeId];
                    if (vm.Shape is ObjectShape vs && vs.Properties.Count > 0 && DomainAst.Entry(uo, vs.Properties[0].Name) is { } tagValue && TagMatches(tagValue, variant.Tag))
                    {
                        return ExpectedAfterWrite(domain, variant.Use, profileId);
                    }
                }

                return domain;
            }

            default:
                return domain;
        }
    }

    private static bool TagMatches(JsonValue value, string tag) => value switch
    {
        JsonStringValue s => s.Value == tag,
        JsonNumberValue n => n.Text == tag,
        _ => false,
    };

    /// <summary>
    /// What the C# projection shows after the server read the value: missing nullable members are their CLR default, null —
    /// except populated members and members with a registered initializer/constructor behavior, whose constructed
    /// value stays and is what the behavior projection describes.
    /// </summary>
    public JsonValue ExpectedAfterRead(JsonValue domain, TypeUse use, string profileId = "")
    {
        if (domain is JsonNullValue || !_index.Types.TryGetValue(use.TypeId, out var model))
        {
            return domain;
        }

        switch (model.Shape)
        {
            case PrimitiveShape p when ScalarName(p) is "datetime" or "datetime-local-wire":
                return LocalWireRoundTrip(domain);
            case ObjectShape shape when domain is JsonObjectValue obj:
            {
                var entries = new List<(string, JsonValue)>();
                foreach (var prop in shape.Properties)
                {
                    var present = DomainAst.Entry(obj, prop.Name);
                    if (present is null)
                    {
                        if (prop.Use.SemanticNullable && !KeepsConstructedValue(model, prop.Name, profileId))
                        {
                            entries.Add((prop.Name, DomainAst.Null));
                        }

                        continue;
                    }

                    entries.Add((prop.Name, ExpectedAfterRead(present, prop.Use, profileId)));
                }

                if (DomainAst.Entry(obj, "extensions") is { } ext && shape.Extension is CaptureExtension)
                {
                    entries.Add(("extensions", ext));
                }

                return DomainAst.Object(entries);
            }

            case ArrayShape a when domain is JsonArrayValue arr:
                return DomainAst.Array(arr.Items.Select(i => ExpectedAfterRead(i, a.Element, profileId)));
            case MapShape m when domain is JsonObjectValue mobj:
                return DomainAst.Object(mobj.Entries.Select(e => (ExpectedMapKey(e.Name, m.Key), ExpectedAfterRead(e.Value, m.Value, profileId))));
            case BrandShape b:
                return ExpectedAfterRead(domain, b.Base, profileId);
            case UnionShape u when domain is JsonObjectValue uo:
            {
                foreach (var variant in u.Variants)
                {
                    var vm = _index.Types[variant.Use.TypeId];
                    if (vm.Shape is ObjectShape vs && vs.Properties.Count > 0 && DomainAst.Entry(uo, vs.Properties[0].Name) is { } tagValue && TagMatches(tagValue, variant.Tag))
                    {
                        return ExpectedAfterRead(domain, variant.Use, profileId);
                    }
                }

                return domain;
            }

            default:
                return domain;
        }
    }

    /// <summary>
    /// A member whose absent value is what the server constructs, not null: its effective object creation handling is Populate, or
    /// the member or its type carries a registered initializer/constructor behavior (profile scopes).
    /// </summary>
    private bool KeepsConstructedValue(Model model, string propertyName, string profileId)
    {
        if (!_index.Profiles.TryGetValue(profileId, out var profile))
        {
            return false;
        }

        var clrPath = model.ClrIdentity + "." + propertyName;
        bool Constructs(Scope s) => s.BehaviorIds.Any(id => profile.Behaviors.FirstOrDefault(b => b.Id == id)?.Kind is BehaviorKind.Initializer or BehaviorKind.Constructor);
        return profile.Scopes.Any(s => (s.Kind == ScopeKind.Member && s.ClrPath == clrPath && (s.EffectiveObjectCreationHandling == ObjectCreationHandling.Populate || Constructs(s)))
            || (s.Kind == ScopeKind.Type && s.ClrPath == model.ClrIdentity && Constructs(s)));
    }

    /// <summary>default(T) as System.Text.Json's WhenWritingDefault sees it, on the projected AST.</summary>
    public static bool IsDefaultLike(JsonValue value) => value switch
    {
        JsonNullValue => true,
        JsonBooleanValue b => !b.Value,
        JsonNumberValue n => AstOracles.Normalize(n.Text) is { Mantissa.IsZero: true },
        JsonStringValue s => s.Value is "" or "\0" or "00000000-0000-0000-0000-000000000000" or "0001-01-01" or "00:00:00" or "0001-01-01T00:00:00+00:00",
        _ => false,
    };

    // ------------------------------------------------------------------ negative corpus (by wire shape)

    private IEnumerable<(JsonValue Wire, string Reason)> NegativeWires(WireRef wireRef, int depth)
    {
        if (!_index.Wires.TryGetValue(wireRef.WireId, out var wire) || depth > 2)
        {
            yield break;
        }

        switch (wire.Shape)
        {
            case NumberWire n:
            {
                yield return (DomainAst.String("12"), "string-token");
                yield return (DomainAst.Boolean(true), "boolean-token");
                yield return (DomainAst.Array([]), "array-token");
                // the lexeme heuristics below describe builtin grammars; a module grammar (a binding) gets the token negatives only
                var grammar = Builtins.TryGet(n.GrammarId, out _) ? n.GrammarId : "";
                if (grammar.Contains("int", StringComparison.Ordinal) || grammar.Contains("enum-number", StringComparison.Ordinal))
                {
                    yield return (DomainAst.Number("1.5"), "fraction");
                    yield return (DomainAst.Number("99999999999999999999999"), "overflow");
                    if (grammar.Contains("uint", StringComparison.Ordinal))
                    {
                        yield return (DomainAst.Number("-1"), "negative");
                    }
                }
                else if (grammar.Contains("decimal", StringComparison.Ordinal))
                {
                    yield return (DomainAst.Number("1e30"), "decimal-range");
                    yield return (DomainAst.Number("79228162514264337593543950336"), "decimal-overflow");
                }

                break;
            }

            case StringWire s:
            {
                yield return (DomainAst.Number("1"), "number-token");
                yield return (DomainAst.Boolean(true), "boolean-token");
                yield return (DomainAst.Object(), "object-token");
                var g = Builtins.TryGet(s.GrammarId, out _) ? s.GrammarId : "";
                if (g.Contains("guid", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("not-a-guid"), "guid-text");
                    yield return (DomainAst.String("550e8400e29b41d4a716446655440000"), "guid-n-format");
                }
                else if (g.Contains("base64", StringComparison.Ordinal) || g.Contains("bytes", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("abc"), "base64-length");
                    yield return (DomainAst.String("/wB="), "base64-nonzero-padding-bits");
                    yield return (DomainAst.String("AA=A"), "base64-padding-position");
                }
                else if (g.Contains("date-only", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("2024-02-30"), "date-invalid-day");
                    yield return (DomainAst.String("2024-2-1"), "date-short");
                }
                else if (g.Contains("time-only", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("25:00:00"), "time-hour");
                    if (wire.Direction == WireDirection.ServerWrite)
                    {
                        // the .NET 10 TimeOnly reader accepts the short "h:m" aliases of the TimeSpan 'c' parser (TimeOnlyConverter: minimum length 3),
                        // so a short time is only invalid as a server write (the canonical form is HH:mm:ss[.fffffff])
                        yield return (DomainAst.String("12:00"), "time-short");
                    }
                }
                else if (g.Contains("datetime-offset", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("2026-13-01T00:00:00Z"), "datetime-month");
                    if (wire.Direction == WireDirection.ServerWrite)
                    {
                        // observed on .NET 10: the DateTimeOffset reader accepts a date-only ISO string (local midnight), so it is only invalid as a server write
                        yield return (DomainAst.String("2026-09-30"), "datetime-no-time");
                    }

                    yield return (DomainAst.String("2026-09-30T25:00:00+09:00"), "datetime-hour");
                }
                else if (g.Contains(".datetime@", StringComparison.Ordinal) || g.Contains(".datetime-key@", StringComparison.Ordinal) || g.Contains("datetime-utc", StringComparison.Ordinal) || g.Contains("datetime-unspecified", StringComparison.Ordinal) || g.Contains("datetime-local-wire", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("2026-13-01T00:00:00Z"), "datetime-month");
                    yield return (DomainAst.String("2026-09-30T25:00:00"), "datetime-hour");
                    if (wire.Direction == WireDirection.ServerWrite)
                    {
                        // System.Text.Json reads every ISO 8601 offset form into a DateTime (Z → Utc, ±hh:mm → Local, none → Unspecified),
                        // so the forms of the other kinds are only invalid as server writes: the decoder must refuse what the declared
                        // kind never produces
                        if (g.Contains("datetime-utc", StringComparison.Ordinal))
                        {
                            yield return (DomainAst.String("2026-09-30T15:04:05+09:00"), "datetime-utc-offset-form");
                            yield return (DomainAst.String("2026-09-30T15:04:05"), "datetime-utc-no-suffix");
                        }
                        else if (g.Contains("datetime-unspecified", StringComparison.Ordinal))
                        {
                            yield return (DomainAst.String("2026-09-30T15:04:05Z"), "datetime-unspecified-z");
                            yield return (DomainAst.String("2026-09-30T15:04:05+09:00"), "datetime-unspecified-offset");
                        }
                        else if (g.Contains("datetime-local-wire", StringComparison.Ordinal))
                        {
                            yield return (DomainAst.String("2026-09-30T15:04:05Z"), "datetime-local-z");
                            yield return (DomainAst.String("2026-09-30T15:04:05"), "datetime-local-no-offset");
                        }
                    }
                }
                else if (g.Contains("duration", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("1 day"), "duration-words");
                    yield return (DomainAst.String("00:60:00"), "duration-minutes");
                }
                else if (g.Contains("char", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("ab"), "char-length");
                }
                else if (g.Contains("decimal-string", StringComparison.Ordinal))
                {
                    yield return (DomainAst.String("abc"), "decimal-string-text");
                }

                break;
            }

            case BooleanWire:
                yield return (DomainAst.String("true"), "string-token");
                yield return (DomainAst.Number("1"), "number-token");
                break;
            case NullWire:
                yield return (DomainAst.Number("0"), "number-token");
                break;
            case LiteralWire lit:
                yield return (lit.Value is JsonStringValue ls ? DomainAst.String(ls.Value + "-other") : DomainAst.String("other"), "literal-mismatch");
                break;
            case ArrayWire:
                yield return (DomainAst.Object(), "object-token");
                yield return (DomainAst.String("x"), "string-token");
                break;
            case ObjectWire o:
            {
                yield return (DomainAst.Array([]), "array-token");
                yield return (DomainAst.String("x"), "string-token");
                if (o.Properties.Any(p => p.Presence == Presence.Required) && WireSample(wireRef, 0) is JsonObjectValue sample)
                {
                    var required = o.Properties.First(p => p.Presence == Presence.Required);
                    yield return (DomainAst.Object(sample.Entries.Where(e => e.Name != required.Name).Select(e => (e.Name, e.Value))), "missing-required");
                    if (o.Additional is RejectAdditional)
                    {
                        yield return (DomainAst.Object(sample.Entries.Select(e => (e.Name, e.Value)).Append(("tisiliaUnknownMember", DomainAst.Number("1")))), "unknown-member");
                    }

                    if (o.DuplicatePolicyId == Builtins.DuplicatesReject && sample.Entries.Count > 0)
                    {
                        yield return (DomainAst.Object(sample.Entries.Select(e => (e.Name, e.Value)).Append((sample.Entries[0].Name, sample.Entries[0].Value))), "duplicate-member");
                    }
                }

                break;
            }

            case TokenUnionWire tu:
            {
                var tokens = tu.Branches.Select(b => b.Token).ToHashSet();
                var candidates = new (JsonToken, JsonValue)[] { (JsonToken.Number, DomainAst.Number("1")), (JsonToken.String, DomainAst.String("x")), (JsonToken.Boolean, DomainAst.Boolean(true)), (JsonToken.Array, DomainAst.Array([])), (JsonToken.Object, DomainAst.Object()) };
                foreach (var (token, value) in candidates)
                {
                    if (!tokens.Contains(token))
                    {
                        yield return (value, "token-" + token.ToString().ToLowerInvariant());
                        break;
                    }
                }

                foreach (var branch in tu.Branches.Where(b => b.Token != JsonToken.Null))
                {
                    foreach (var (inner, reason) in NegativeWires(branch.Wire, depth + 1))
                    {
                        if (!tokens.Contains(inner.Token) || inner.Token == branch.Token)
                        {
                            yield return (inner, branch.Token.ToString().ToLowerInvariant() + "-" + reason);
                        }
                    }
                }

                break;
            }

            case TaggedUnionWire tg:
                yield return (DomainAst.Object((tg.Discriminator, DomainAst.String("tisilia-unknown-variant"))), "unknown-tag");
                yield return (DomainAst.Object(), "missing-discriminator");
                break;
            case LosslessJsonWire:
                break;
        }
    }

    /// <summary>A structurally valid wire value built from the wire shape alone (used to derive negative variants).</summary>
    private JsonValue? WireSample(WireRef wireRef, int depth)
    {
        if (!_index.Wires.TryGetValue(wireRef.WireId, out var wire) || depth > 6)
        {
            return null;
        }

        switch (wire.Shape)
        {
            case NullWire:
                return DomainAst.Null;
            case BooleanWire:
                return DomainAst.Boolean(true);
            case LiteralWire lit:
                return lit.Value;
            case NumberWire n:
                return DomainAst.Number(n.GrammarId.Contains("uint", StringComparison.Ordinal) ? "1" : "1");
            case StringWire s:
            {
                var g = s.GrammarId;
                var text = g.Contains("guid", StringComparison.Ordinal) ? "550e8400-e29b-41d4-a716-446655440000"
                    : g.Contains("date-only", StringComparison.Ordinal) ? "2026-09-30"
                    : g.Contains("time-only", StringComparison.Ordinal) ? "15:04:05"
                    : g.Contains("datetime-utc", StringComparison.Ordinal) ? "2026-09-30T06:04:05Z"
                    : g.Contains("datetime-unspecified", StringComparison.Ordinal) ? "2026-09-30T15:04:05"
                    : g.Contains("datetime", StringComparison.Ordinal) ? "2026-09-30T15:04:05+09:00"
                    : g.Contains("duration", StringComparison.Ordinal) ? "00:00:01"
                    : g.Contains("base64", StringComparison.Ordinal) || g.Contains("bytes", StringComparison.Ordinal) ? "AAE="
                    : g.Contains("decimal", StringComparison.Ordinal) ? "1.5"
                    : g.Contains("float", StringComparison.Ordinal) ? "1.5"
                    : g.Contains("int", StringComparison.Ordinal) ? "1"
                    : g.Contains("char", StringComparison.Ordinal) ? "a"
                    : g.Contains("enum", StringComparison.Ordinal) ? "" : "sample";
                return DomainAst.String(text);
            }

            case ArrayWire a:
            {
                var item = WireSample(a.Element, depth + 1);
                return DomainAst.Array(item is null ? [] : [item]);
            }

            case ObjectWire o:
            {
                var entries = new List<(string, JsonValue)>();
                foreach (var p in o.Properties)
                {
                    if (p.Presence == Presence.Required || depth == 0)
                    {
                        var v = WireSample(p.Wire, depth + 1);
                        if (v is null)
                        {
                            if (p.Presence == Presence.Required)
                            {
                                return null;
                            }

                            continue;
                        }

                        entries.Add((p.Name, v));
                    }
                }

                return DomainAst.Object(entries);
            }

            case TokenUnionWire tu:
            {
                var branch = tu.Branches.FirstOrDefault(b => b.Token != JsonToken.Null) ?? tu.Branches[0];
                return WireSample(branch.Wire, depth + 1);
            }

            case TaggedUnionWire tg:
            {
                var variant = tg.Variants[0];
                return WireSample(variant.Wire, depth + 1);
            }

            case LosslessJsonWire:
                return DomainAst.Object();
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ invalid domain values

    private IEnumerable<(JsonValue Domain, string Reason)> InvalidDomains(Model model, TypeUse use)
    {
        // a brand's values have the AST kind of its base
        var shape = model.Shape is BrandShape brand && _index.Types.TryGetValue(brand.Base.TypeId, out var baseModel) ? baseModel.Shape : model.Shape;
        var valid = shape switch
        {
            PrimitiveShape p => ScalarName(p) switch
            {
                "string" or "char" or "guid" or "bytes" or "date-only" or "time-only" or "datetime" or "datetime-utc" or "datetime-unspecified" or "datetime-local-wire" or "datetime-offset" or "duration" => JsonToken.String,
                "boolean" => JsonToken.Boolean,
                "json-value" => (JsonToken?)null,
                _ => JsonToken.Number,
            },
            EnumShape => JsonToken.Number,
            ObjectShape or MapShape or UnionShape => JsonToken.Object,
            ArrayShape => JsonToken.Array,
            _ => (JsonToken?)null,
        };
        if (valid is null)
        {
            yield break;
        }

        var candidates = new (JsonToken, JsonValue)[] { (JsonToken.String, DomainAst.String("x")), (JsonToken.Number, DomainAst.Number("1")), (JsonToken.Boolean, DomainAst.Boolean(true)), (JsonToken.Array, DomainAst.Array([])), (JsonToken.Object, DomainAst.Object()) };
        foreach (var (token, value) in candidates)
        {
            if (token != valid.Value)
            {
                yield return (value, "kind-" + token.ToString().ToLowerInvariant());
            }
        }

        if (!use.SemanticNullable)
        {
            yield return (DomainAst.Null, "null");
        }

        if (model.Shape is PrimitiveShape ps)
        {
            switch (ScalarName(ps))
            {
                case "guid":
                    yield return (DomainAst.String("not-a-guid"), "guid-text");
                    break;
                case "int32":
                    yield return (DomainAst.Number("2147483648"), "range");
                    yield return (DomainAst.Number("1.5"), "fraction");
                    break;
                case "int64":
                    yield return (DomainAst.Number("9223372036854775808"), "range");
                    break;
                case "uint64":
                    yield return (DomainAst.Number("-1"), "negative");
                    break;
                case "decimal":
                    yield return (DomainAst.Number("79228162514264337593543950336"), "range");
                    break;
                case "char":
                    yield return (DomainAst.String("ab"), "length");
                    break;
                case "date-only":
                    yield return (DomainAst.String("2024-02-30"), "invalid-day");
                    break;
                case "datetime-offset":
                    yield return (DomainAst.String("2026-09-30"), "no-time");
                    break;
                case "datetime":
                    yield return (DomainAst.String("2026-02-30T00:00:00Z"), "invalid-day");
                    break;
                case "datetime-utc":
                    yield return (DomainAst.String("2026-09-30T15:04:05+09:00"), "offset-form");
                    break;
                case "datetime-unspecified":
                    yield return (DomainAst.String("2026-09-30T15:04:05Z"), "utc-suffix");
                    break;
                case "datetime-local-wire":
                    yield return (DomainAst.String("2026-09-30T15:04:05"), "no-offset");
                    break;
            }
        }

        if (model.Shape is BrandShape moduleBrand)
        {
            foreach (var invalid in AdditionalModule.InvalidSamples(moduleBrand.BrandId))
            {
                yield return invalid;
            }
        }
    }
}
