using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Contract;
using Tisilia.Generator.Additional;
using Tisilia.Generator.Building;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Validation;
using JsonValue = Tisilia.Contract.JsonValue;

namespace Tisilia.AspNetCore.Codecs;

/// <summary>
/// The additional codecs Tisilia ships (types outside the builtin scalar set are used through certified additional
/// codecs, never rounded to string/number). Each is a paired codec of the module <c>tisilia-additional</c>: the
/// converter System.Text.Json uses on the server and the hand-written TypeScript codec of the module, qualified by conformance.
/// <list type="table">
/// <item><term>Int128, UInt128</term><description>System.Text.Json's converters: number tokens (strings under JsonNumberHandling); TypeScript: canonical decimal strings.</description></item>
/// <item><term>BigInteger</term><description><see cref="BigIntegerJsonConverter"/>: number tokens of at most 4096 characters; TypeScript: canonical decimal strings.</description></item>
/// <item><term>Half</term><description>System.Text.Json's converter (number handling included); TypeScript: numbers exactly representable in binary16.</description></item>
/// <item><term>Uri</term><description>System.Text.Json's converter (<see cref="Uri.OriginalString"/>); TypeScript: strings, requests limited to the RFC 3986 URI references .NET's parser keeps. No dictionary keys: <see cref="Uri.Equals(object?)"/> merges keys that differ in case, escaping or fragment.</description></item>
/// <item><term>Version</term><description>System.Text.Json's converter; TypeScript: canonical "major.minor[.build[.revision]]".</description></item>
/// <item><term>IPAddress</term><description><see cref="IPAddressJsonConverter"/>: the canonical <see cref="IPAddress.ToString()"/> text.</description></item>
/// <item><term>Rune</term><description><see cref="RuneJsonConverter"/>: a string of one Unicode scalar value.</description></item>
/// <item><term>IPNetwork</term><description><see cref="IPNetworkJsonConverter"/>: the canonical CIDR text (no host bits).</description></item>
/// <item><term>Index, Range</term><description><see cref="IndexJsonConverter"/>, <see cref="RangeJsonConverter"/>: C# index syntax ("^3", "1..^2").</description></item>
/// <item><term>Complex</term><description><see cref="ComplexJsonConverter"/>: {real, imaginary} with finite doubles; TypeScript: an object.</description></item>
/// <item><term>JsonValue</term><description><see cref="JsonValueJsonConverter"/>: one JSON string, number (lexeme kept) or boolean; TypeScript: the JSON AST (<c>JsonScalar</c>).</description></item>
/// <item><term>TimeZoneInfo, CultureInfo</term><description><see cref="TimeZoneInfoJsonConverter"/>, <see cref="CultureInfoJsonConverter"/>: the id / name; environment-bound — the ids the exporting server accepts are the binding context (zones, cultures) requests are checked against.</description></item>
/// </list>
/// Registering changes no JSON setting: BigInteger, IPAddress, Rune, IPNetwork, Index, Range, Complex and JsonValue need their converter in the application's
/// JSON options (<see cref="AdditionalJsonConverters.AddTisiliaAdditionalConverters"/>), and the TypeScript module is installed next to
/// the application with <c>tisilia codec install-additional --project &lt;dir&gt;</c>.
/// </summary>
public static class AdditionalCodecs
{
    /// <summary>
    /// Registers every additional codec. <paramref name="contentRoot"/> is the directory the contract is exported to (artifact paths are
    /// relative to it); <paramref name="typeNamePrefix"/> prefixes the generated TypeScript type names (Int128, Half, Uri, Version, …)
    /// when the application has models of those names.
    /// </summary>
    public static CodecBindingCollection AddAdditionalCodecs(this CodecBindingCollection codecs, string contentRoot, string typeNamePrefix = "")
    {
        foreach (var registration in Create(contentRoot, typeNamePrefix))
        {
            codecs.AddPaired(registration);
        }

        return codecs;
    }

    /// <summary>The registrations of the additional codecs (one per CLR type).</summary>
    public static IReadOnlyList<PairedCodecRegistration> Create(string contentRoot, string typeNamePrefix = "")
    {
        var artifacts = Artifacts(contentRoot);
        return
        [
            Int128Registration(artifacts, typeNamePrefix),
            UInt128Registration(artifacts, typeNamePrefix),
            BigIntegerRegistration(artifacts, typeNamePrefix),
            HalfRegistration(artifacts, typeNamePrefix),
            UriRegistration(artifacts, typeNamePrefix),
            VersionRegistration(artifacts, typeNamePrefix),
            IPAddressRegistration(artifacts, typeNamePrefix),
            RuneRegistration(artifacts, typeNamePrefix),
            IPNetworkRegistration(artifacts, typeNamePrefix),
            IndexRegistration(artifacts, typeNamePrefix),
            RangeRegistration(artifacts, typeNamePrefix),
            ComplexRegistration(artifacts, typeNamePrefix),
            JsonScalarRegistration(artifacts, typeNamePrefix),
            TimeZoneRegistration(artifacts, typeNamePrefix),
            CultureRegistration(artifacts, typeNamePrefix),
        ];
    }

    // ------------------------------------------------------------------ artifacts

    private static IReadOnlyList<ModuleArtifactSpec> Artifacts(string contentRoot)
    {
        var assembly = typeof(AdditionalCodecs).Assembly;
        return
        [
            // this assembly holds the registrations, the projections and the converters System.Text.Json lacks
            new ModuleArtifactSpec(ArtifactTarget.Dotnet, AssemblyArtifactPath(contentRoot, assembly), Bytes: () => File.ReadAllBytes(assembly.Location)),
            // the digest is the module Tisilia ships; generate and conformance check the installed copy against it (SV44)
            new ModuleArtifactSpec(ArtifactTarget.Browser, AdditionalModule.ScriptPath, Bytes: AdditionalModule.Script),
            new ModuleArtifactSpec(ArtifactTarget.Node, AdditionalModule.ScriptPath, Bytes: AdditionalModule.Script),
        ];
    }

    /// <summary>
    /// The path of this assembly relative to the content root, the same whichever host loaded it (the path is hashed into the contract):
    /// the application's build output <c>bin/&lt;configuration&gt;/&lt;tfm&gt;/</c> holding identical bytes (ordinal-first configuration),
    /// else the loaded file's own path below the content root, else <c>bin/&lt;file&gt;</c> (conformance then reports it missing, SV44).
    /// </summary>
    internal static string AssemblyArtifactPath(string contentRoot, Assembly assembly)
    {
        var name = Path.GetFileName(assembly.Location);
        var framework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName;
        var bin = Path.Combine(contentRoot, "bin");
        if (framework is not null && Directory.Exists(bin))
        {
            var version = new System.Runtime.Versioning.FrameworkName(framework).Version;
            var tfm = $"net{version.Major}.{version.Minor}";
            byte[]? loaded = null;
            foreach (var configuration in Directory.GetDirectories(bin).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(bin, configuration, tfm, name);
                if (File.Exists(candidate))
                {
                    loaded ??= File.ReadAllBytes(assembly.Location);
                    if (File.ReadAllBytes(candidate).AsSpan().SequenceEqual(loaded))
                    {
                        return $"bin/{configuration}/{tfm}/{name}";
                    }
                }
            }
        }

        var relative = Path.GetRelativePath(contentRoot, assembly.Location).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? "bin/" + name : relative;
    }

    // ------------------------------------------------------------------ wires

    private static string Dir(WireDirection direction) => direction == WireDirection.ServerRead ? "read" : "write";

    /// <summary>
    /// Integer wires under the position's number handling, as System.Text.Json's Int128/UInt128 converters read and write them:
    /// AllowReadingFromString adds a string branch to what the server reads, WriteAsString makes the server write strings.
    /// </summary>
    private static WireRef IntegerWire(ContractWireFactory f, string modelId, string grammar, WireDirection direction, NumberProfile numbers)
    {
        var prefix = modelId + "." + Dir(direction);
        if (direction == WireDirection.ServerWrite && numbers.WriteAsString)
        {
            return f.StringWire(prefix + ".string", direction, AdditionalModule.Grammar(grammar + "-string"));
        }

        var number = f.NumberWire(prefix, direction, AdditionalModule.Grammar(grammar));
        if (direction == WireDirection.ServerRead && numbers.ReadFromString)
        {
            var text = f.StringWire(prefix + ".string", direction, AdditionalModule.Grammar(grammar + "-string"));
            return f.TokenUnionWire(prefix + ".r", direction, [new TokenBranch { Token = JsonToken.Number, Wire = number }, new TokenBranch { Token = JsonToken.String, Wire = text }]);
        }

        return number;
    }

    /// <summary>
    /// Half wires as System.Text.Json's HalfConverter reads and writes them: under AllowReadingFromString the server reads numeric
    /// strings and the named literals, under AllowNamedFloatingPointLiterals alone only the named literals; WriteAsString writes every
    /// value as a string (non-finite ones as their names), AllowNamedFloatingPointLiterals writes the non-finite ones as names.
    /// </summary>
    private static WireRef HalfWire(ContractWireFactory f, string modelId, WireDirection direction, NumberProfile numbers)
    {
        var prefix = modelId + "." + Dir(direction);
        if (direction == WireDirection.ServerWrite && numbers.WriteAsString)
        {
            return f.StringWire(prefix + ".string", direction, AdditionalModule.Grammar("half-string-named"));
        }

        var number = f.NumberWire(prefix, direction, AdditionalModule.Grammar("half"));
        var readsStrings = direction == WireDirection.ServerRead && numbers.ReadFromString;
        if (readsStrings || numbers.NamedLiterals)
        {
            var text = readsStrings
                ? f.StringWire(prefix + ".string", direction, AdditionalModule.Grammar("half-string-named"))
                : f.StringWire(prefix + ".named", direction, AdditionalModule.Grammar("half-named"));
            return f.TokenUnionWire(prefix + (readsStrings ? ".r" : ".n"), direction, [new TokenBranch { Token = JsonToken.Number, Wire = number }, new TokenBranch { Token = JsonToken.String, Wire = text }]);
        }

        return number;
    }

    /// <summary>Grammar bindings implemented by module exports (a custom grammar is a grammar binding, never a disguised domain rule).</summary>
    private static IReadOnlyList<PairedGrammar> Grammars(params (string Name, string Export)[] grammars)
        => grammars.Select(g => new PairedGrammar(AdditionalModule.Grammar(g.Name), g.Export)).ToList();

    // ------------------------------------------------------------------ registrations

    /// <summary>The fields every additional registration shares; <paramref name="export"/> is the module's export prefix (int128, half, …).</summary>
    private static PairedCodecRegistration Registration(
        IReadOnlyList<ModuleArtifactSpec> artifacts, string typeNamePrefix, Type clrType, Type converterType, string name, string export, string baseScalar,
        string settings, Func<ContractWireFactory, NumberProfile, WireRef> requestWire, Func<ContractWireFactory, NumberProfile, WireRef> responseWire,
        PairedKey? key, IReadOnlyList<PairedGrammar> grammars, Func<object, JsonValue> project, Func<JsonValue, object> construct, Func<object, object, bool> oracle,
        bool numberHandling = false, IReadOnlyList<string>? notPreserved = null, string? parameterGrammar = null, Func<ContractWireFactory, DomainShape>? shape = null,
        IReadOnlyList<BindingContextEntry>? context = null)
    {
        var modelId = AdditionalModule.ModelId(name);
        return new PairedCodecRegistration
        {
            ClrType = clrType,
            ConverterType = converterType,
            ModelId = modelId,
            TsName = typeNamePrefix + name,
            // a brand over a builtin scalar unless the registration describes its own shape (Complex: an object of two doubles)
            Shape = shape ?? (f => new BrandShape { BrandId = modelId, Base = f.Builder.Scalar(baseScalar) }),
            ModuleId = AdditionalModule.ModuleId,
            ModuleVersion = AdditionalModule.Version,
            License = AdditionalModule.License,
            Artifacts = artifacts,
            DotnetConverterExport = converterType.Name,
            DomainRuleExport = export + "DomainRule",
            DotnetOracleExport = name + "Oracle",
            TypescriptOracleExport = export + "Oracle",
            Request = new PairedDirection(f => requestWire(f, NumberProfile.Strict), export + "RequestEncode", export + "Validate", export + "RequestInput") { NumberWire = numberHandling ? requestWire : null },
            Response = new PairedDirection(f => responseWire(f, NumberProfile.Strict), export + "ResponseDecode", export + "Validate") { NumberWire = numberHandling ? responseWire : null },
            ConverterContext = context ?? [],
            SettingsText = settings,
            Preserved = ["value"],
            NotPreserved = notPreserved ?? [],
            NumberHandling = numberHandling,
            Key = key,
            Grammars = grammars,
            ParameterGrammarId = parameterGrammar is null ? null : AdditionalModule.Grammar(parameterGrammar),
            Project = project,
            Construct = construct,
            Oracle = oracle,
        };
    }

    private static Type StjConverter(Type type) => TisiliaJson.Plain.GetConverter(type).GetType();

    private static string Text(JsonValue ast, string type) => ast is JsonStringValue s ? s.Value : throw new FormatException($"the {type} domain AST is a string");

    private static bool IsCanonicalInteger(string text) => text.Length > 0 && text != "-0" && (text[0] == '-' ? text.Length > 1 && IsDigits(text.AsSpan(1)) : IsDigits(text)) && (text.TrimStart('-').Length == 1 || text.TrimStart('-')[0] != '0');

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    private static BigInteger CanonicalInteger(JsonValue ast, string type)
    {
        var text = Text(ast, type);
        return IsCanonicalInteger(text) ? BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : throw new FormatException($"the {type} domain AST is a canonical decimal integer");
    }

    private static PairedCodecRegistration Int128Registration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Int128), StjConverter(typeof(Int128)), "Int128", "int128", "string",
        "System.Text.Json Int128Converter: number token (string under the position's JsonNumberHandling), invariant digits",
        (f, n) => IntegerWire(f, AdditionalModule.ModelId("Int128"), "int128", WireDirection.ServerRead, n),
        (f, n) => IntegerWire(f, AdditionalModule.ModelId("Int128"), "int128", WireDirection.ServerWrite, n),
        new PairedKey("int128Key", "int128Key", AdditionalModule.Grammar("int128-string")),
        Grammars(("int128", "int128Grammar"), ("int128-string", "int128StringGrammar")),
        value => DomainAst.String(((Int128)value).ToString(CultureInfo.InvariantCulture)),
        ast => CanonicalInteger(ast, "Int128") is var n && n >= (BigInteger)Int128.MinValue && n <= (BigInteger)Int128.MaxValue ? (object)(Int128)n : throw new OverflowException("Int128 range"),
        (a, b) => (Int128)a == (Int128)b,
        numberHandling: true, parameterGrammar: "int128-string");

    private static PairedCodecRegistration UInt128Registration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(UInt128), StjConverter(typeof(UInt128)), "UInt128", "uint128", "string",
        "System.Text.Json UInt128Converter: number token (string under the position's JsonNumberHandling), invariant digits",
        (f, n) => IntegerWire(f, AdditionalModule.ModelId("UInt128"), "uint128", WireDirection.ServerRead, n),
        (f, n) => IntegerWire(f, AdditionalModule.ModelId("UInt128"), "uint128", WireDirection.ServerWrite, n),
        new PairedKey("uint128Key", "uint128Key", AdditionalModule.Grammar("uint128-string")),
        Grammars(("uint128", "uint128Grammar"), ("uint128-string", "uint128StringGrammar")),
        value => DomainAst.String(((UInt128)value).ToString(CultureInfo.InvariantCulture)),
        ast => CanonicalInteger(ast, "UInt128") is var n && n >= 0 && n <= (BigInteger)UInt128.MaxValue ? (object)(UInt128)n : throw new OverflowException("UInt128 range"),
        (a, b) => (UInt128)a == (UInt128)b,
        numberHandling: true, parameterGrammar: "uint128-string");

    private static PairedCodecRegistration BigIntegerRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(BigInteger), typeof(BigIntegerJsonConverter), "BigInteger", "bigInteger", "string",
        "Tisilia BigIntegerJsonConverter: number token, canonical integer text of at most 4096 characters",
        (f, _) => f.NumberWire(AdditionalModule.ModelId("BigInteger") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("big-integer")),
        (f, _) => f.NumberWire(AdditionalModule.ModelId("BigInteger") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("big-integer")),
        new PairedKey("bigIntegerKey", "bigIntegerKey", AdditionalModule.Grammar("big-integer-string")),
        Grammars(("big-integer", "bigIntegerGrammar"), ("big-integer-string", "bigIntegerStringGrammar")),
        value => DomainAst.String(((BigInteger)value).ToString(CultureInfo.InvariantCulture)),
        ast => Text(ast, "BigInteger").Length <= BigIntegerJsonConverter.MaxLength ? CanonicalInteger(ast, "BigInteger") : throw new OverflowException("BigInteger longer than 4096 characters"),
        (a, b) => (BigInteger)a == (BigInteger)b, parameterGrammar: "big-integer-string");

    private static PairedCodecRegistration HalfRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Half), StjConverter(typeof(Half)), "Half", "half", "float64",
        "System.Text.Json HalfConverter: shortest round-trip number token (strings and named literals under the position's JsonNumberHandling)",
        (f, n) => HalfWire(f, AdditionalModule.ModelId("Half"), WireDirection.ServerRead, n),
        (f, n) => HalfWire(f, AdditionalModule.ModelId("Half"), WireDirection.ServerWrite, n),
        new PairedKey("halfKey", "halfKey", AdditionalModule.Grammar("half-string-named")),
        Grammars(("half", "halfGrammar"), ("half-named", "halfNamedGrammar"), ("half-string-named", "halfStringOrNamedGrammar")),
        // the domain AST of a brand over float64 is the float64 projection of the value (the TypeScript runner projects the same number)
        value => DomainAst.Float64((double)(Half)value),
        ConstructHalf,
        (a, b) => BitConverter.HalfToUInt16Bits((Half)a) == BitConverter.HalfToUInt16Bits((Half)b) || (Half.IsNaN((Half)a) && Half.IsNaN((Half)b)),
        numberHandling: true,
        notPreserved: ["nan-payload"], parameterGrammar: "half-string-named");

    private static object ConstructHalf(JsonValue ast)
    {
        switch (ast)
        {
            case JsonStringValue { Value: "NaN" }:
                return Half.NaN;
            case JsonStringValue { Value: "Infinity" }:
                return Half.PositiveInfinity;
            case JsonStringValue { Value: "-Infinity" }:
                return Half.NegativeInfinity;
            case JsonNumberValue number:
            {
                var d = double.Parse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                var h = (Half)d;
                // the domain holds binary16 values only: a double that is not one is outside it (never rounded silently)
                return Half.IsFinite(h) && ((double)h).Equals(d) ? h : throw new FormatException("the number is not exactly representable as a Half");
            }

            default:
                throw new FormatException("the Half domain AST is a number or a named literal");
        }
    }

    private static PairedCodecRegistration UriRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Uri), StjConverter(typeof(Uri)), "Uri", "uri", "string",
        "System.Text.Json UriConverter: Uri.TryCreate(text, UriKind.RelativeOrAbsolute) on read, Uri.OriginalString on write",
        (f, _) => f.StringWire(AdditionalModule.ModelId("Uri") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("uri-reference")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("Uri") + ".write", WireDirection.ServerWrite, Builtins.Grammar("string")),
        // no key capability: a Dictionary<Uri, T> compares keys with Uri.Equals, which merges "HTTP://A/" with "http://a/" and ignores
        // fragments, while the TypeScript map keeps every distinct text (a collision the client could not see)
        null,
        Grammars(("uri-reference", "uriReferenceGrammar")),
        value => DomainAst.String(((Uri)value).OriginalString),
        ast => new Uri(Text(ast, "Uri"), UriKind.RelativeOrAbsolute),
        // the domain is the text the server holds and writes back (OriginalString), not the Uri normalized by the parser
        (a, b) => string.Equals(((Uri)a).OriginalString, ((Uri)b).OriginalString, StringComparison.Ordinal), parameterGrammar: "uri-reference");

    private static PairedCodecRegistration VersionRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Version), StjConverter(typeof(Version)), "Version", "version", "string",
        "System.Text.Json VersionConverter: Version.TryParse on read (digits first and last), Version.ToString() on write",
        (f, _) => f.StringWire(AdditionalModule.ModelId("Version") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("version")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("Version") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("version")),
        new PairedKey("versionKey", "versionKey", AdditionalModule.Grammar("version")),
        Grammars(("version", "versionGrammar")),
        value => DomainAst.String(((Version)value).ToString()),
        ast => Version.Parse(Text(ast, "Version")) is var v && v.ToString() == Text(ast, "Version") ? v : throw new FormatException("the Version domain AST is the canonical Version.ToString() text"),
        (a, b) => ((Version)a).Equals((Version)b), parameterGrammar: "version");

    private static PairedCodecRegistration IPAddressRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(IPAddress), typeof(IPAddressJsonConverter), "IPAddress", "ipAddress", "string",
        "Tisilia IPAddressJsonConverter: the canonical IPAddress.ToString() text in both directions",
        (f, _) => f.StringWire(AdditionalModule.ModelId("IPAddress") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("ip-address")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("IPAddress") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("ip-address")),
        new PairedKey("ipAddressKey", "ipAddressKey", AdditionalModule.Grammar("ip-address")),
        Grammars(("ip-address", "ipAddressGrammar")),
        value => DomainAst.String(((IPAddress)value).ToString()),
        ast => IPAddressJsonConverter.ParseCanonical(Text(ast, "IPAddress")) ?? throw new FormatException("the IPAddress domain AST is the canonical IPAddress.ToString() text"),
        (a, b) => ((IPAddress)a).ToString() == ((IPAddress)b).ToString(), parameterGrammar: "ip-address");

    private static PairedCodecRegistration IPNetworkRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(IPNetwork), typeof(IPNetworkJsonConverter), "IPNetwork", "ipNetwork", "string",
        "Tisilia IPNetworkJsonConverter: the canonical IPNetwork.ToString() CIDR text in both directions",
        (f, _) => f.StringWire(AdditionalModule.ModelId("IPNetwork") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("ip-network")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("IPNetwork") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("ip-network")),
        new PairedKey("ipNetworkKey", "ipNetworkKey", AdditionalModule.Grammar("ip-network")),
        Grammars(("ip-network", "ipNetworkGrammar")),
        value => DomainAst.String(((IPNetwork)value).ToString()),
        ast => IPNetworkJsonConverter.ParseCanonical(Text(ast, "IPNetwork")) ?? throw new FormatException("the IPNetwork domain AST is the canonical IPNetwork.ToString() text"),
        (a, b) => ((IPNetwork)a).ToString() == ((IPNetwork)b).ToString(),
        parameterGrammar: "ip-network");

    private static PairedCodecRegistration IndexRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Index), typeof(IndexJsonConverter), "Index", "index", "string",
        "Tisilia IndexJsonConverter: C# index syntax (\"3\", \"^3\") in both directions",
        (f, _) => f.StringWire(AdditionalModule.ModelId("Index") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("index")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("Index") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("index")),
        new PairedKey("indexKey", "indexKey", AdditionalModule.Grammar("index")),
        Grammars(("index", "indexGrammar")),
        value => DomainAst.String(IndexJsonConverter.Format((Index)value)),
        ast => IndexJsonConverter.ParseCanonical(Text(ast, "Index")) ?? throw new FormatException("the Index domain AST is \"<n>\" or \"^<n>\""),
        (a, b) => ((Index)a).Equals((Index)b));

    private static PairedCodecRegistration RangeRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Range), typeof(RangeJsonConverter), "Range", "range", "string",
        "Tisilia RangeJsonConverter: C# range syntax with both ends (\"1..^2\") in both directions",
        (f, _) => f.StringWire(AdditionalModule.ModelId("Range") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("range")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("Range") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("range")),
        new PairedKey("rangeKey", "rangeKey", AdditionalModule.Grammar("range")),
        Grammars(("range", "rangeGrammar")),
        value => DomainAst.String(RangeJsonConverter.Format((Range)value)),
        ast => RangeJsonConverter.ParseCanonical(Text(ast, "Range")) ?? throw new FormatException("the Range domain AST is \"<index>..<index>\""),
        (a, b) => ((Range)a).Equals((Range)b));

    private static PairedCodecRegistration ComplexRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Complex), typeof(ComplexJsonConverter), "Complex", "complex", "float64",
        "Tisilia ComplexJsonConverter: {\"real\":double,\"imaginary\":double}, finite parts, exactly those members",
        (f, _) => ComplexWire(f, WireDirection.ServerRead),
        (f, _) => ComplexWire(f, WireDirection.ServerWrite),
        null,
        [],
        value => DomainAst.Object(("real", DomainAst.Float64(((Complex)value).Real)), ("imaginary", DomainAst.Float64(((Complex)value).Imaginary))),
        ConstructComplex,
        // the parts as IEEE values: -0 and 0 differ, as they do on the wire
        (a, b) => BitConverter.DoubleToInt64Bits(((Complex)a).Real) == BitConverter.DoubleToInt64Bits(((Complex)b).Real)
            && BitConverter.DoubleToInt64Bits(((Complex)a).Imaginary) == BitConverter.DoubleToInt64Bits(((Complex)b).Imaginary),
        shape: f => new ObjectShape
        {
            Properties =
            [
                new DomainProperty { Name = "real", Use = f.Builder.Scalar("float64"), Presence = Presence.Required },
                new DomainProperty { Name = "imaginary", Use = f.Builder.Scalar("float64"), Presence = Presence.Required },
            ],
            Extension = new NoExtension(),
        });

    private static WireRef ComplexWire(ContractWireFactory f, WireDirection direction) => f.ObjectWire(
        AdditionalModule.ModelId("Complex") + "." + Dir(direction), direction,
        [
            new WireProperty { Name = "real", Wire = f.Scalar("float64", direction), Presence = Presence.Required },
            new WireProperty { Name = "imaginary", Wire = f.Scalar("float64", direction), Presence = Presence.Required },
        ], new RejectAdditional(), Builtins.DuplicatesReject, Builtins.NamesOrdinal);

    private static object ConstructComplex(JsonValue ast)
    {
        if (ast is JsonObjectValue obj && DomainAst.Entry(obj, "real") is JsonNumberValue real && DomainAst.Entry(obj, "imaginary") is JsonNumberValue imaginary)
        {
            var r = double.Parse(real.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            var i = double.Parse(imaginary.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return double.IsFinite(r) && double.IsFinite(i) ? new Complex(r, i) : throw new FormatException("the parts of a Complex are finite");
        }

        throw new FormatException("the Complex domain AST is {real: number, imaginary: number}");
    }

    private static PairedCodecRegistration JsonScalarRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(System.Text.Json.Nodes.JsonValue), typeof(JsonValueJsonConverter), "JsonScalar", "jsonScalar", "json-value",
        "Tisilia JsonValueJsonConverter: one JSON string, number or boolean token into a JsonValue node, written back as read (a number keeps its lexeme); objects and arrays refused",
        (f, _) => JsonScalarWire(f, WireDirection.ServerRead),
        (f, _) => JsonScalarWire(f, WireDirection.ServerWrite),
        null,
        Grammars(("json-number", "jsonNumberGrammar")),
        value => DomainAst.ParseJsonText(((System.Text.Json.Nodes.JsonValue)value).ToJsonString(), 64),
        ast => ast is JsonStringValue or JsonNumberValue or JsonBooleanValue
            ? System.Text.Json.Nodes.JsonNode.Parse(DomainAst.ToJsonText(ast))!.AsValue()
            : throw new FormatException("the JsonScalar domain AST is a JSON string, number or boolean"),
        (a, b) => ((System.Text.Json.Nodes.JsonNode)a).ToJsonString() == ((System.Text.Json.Nodes.JsonNode)b).ToJsonString());

    private static PairedCodecRegistration TimeZoneRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(TimeZoneInfo), typeof(TimeZoneInfoJsonConverter), "TimeZoneId", "timeZoneId", "string",
        "Tisilia TimeZoneInfoJsonConverter: TimeZoneInfo.Id; reads ids FindSystemTimeZoneById resolves to a zone of that id (environment-bound: binding context zones)",
        (f, _) => f.StringWire(AdditionalModule.ModelId("TimeZoneId") + ".read", WireDirection.ServerRead, Builtins.Grammar("string")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("TimeZoneId") + ".write", WireDirection.ServerWrite, Builtins.Grammar("string")),
        null,
        [],
        value => DomainAst.String(((TimeZoneInfo)value).Id),
        ast => TimeZoneInfoJsonConverter.ParseKnown(Text(ast, "TimeZoneInfo")) ?? throw new FormatException("the TimeZoneInfo domain AST is a zone id this server knows"),
        (a, b) => ((TimeZoneInfo)a).Id == ((TimeZoneInfo)b).Id,
        context: new LazyContext(() => [new BindingContextEntry { Name = "zones", Value = JsonSerializer.Serialize(TimeZoneInfoJsonConverter.KnownIds(), TisiliaJson.Plain), Confidential = false }]));

    private static PairedCodecRegistration CultureRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(CultureInfo), typeof(CultureInfoJsonConverter), "CultureName", "cultureName", "string",
        "Tisilia CultureInfoJsonConverter: CultureInfo.Name; reads predefined culture names (environment-bound: binding context cultures)",
        (f, _) => f.StringWire(AdditionalModule.ModelId("CultureName") + ".read", WireDirection.ServerRead, Builtins.Grammar("string")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("CultureName") + ".write", WireDirection.ServerWrite, Builtins.Grammar("string")),
        null,
        [],
        value => DomainAst.String(((CultureInfo)value).Name),
        ast => CultureInfoJsonConverter.ParseKnown(Text(ast, "CultureInfo")) ?? throw new FormatException("the CultureInfo domain AST is a culture name this server knows"),
        (a, b) => ((CultureInfo)a).Name == ((CultureInfo)b).Name,
        context: new LazyContext(() => [new BindingContextEntry { Name = "cultures", Value = JsonSerializer.Serialize(CultureInfoJsonConverter.KnownNames(), TisiliaJson.Plain), Confidential = false }]));

    /// <summary>Binding context computed on first use (the export or a conformance run), not when the application registers the codecs.</summary>
    private sealed class LazyContext(Func<IReadOnlyList<BindingContextEntry>> create) : IReadOnlyList<BindingContextEntry>
    {
        private readonly Lazy<IReadOnlyList<BindingContextEntry>> _entries = new(create);

        public BindingContextEntry this[int index] => _entries.Value[index];

        public int Count => _entries.Value.Count;

        public IEnumerator<BindingContextEntry> GetEnumerator() => _entries.Value.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The tokens a JsonValue node holds: a string, a number (any JSON number lexeme) or a boolean — never an object, array or null.</summary>
    private static WireRef JsonScalarWire(ContractWireFactory f, WireDirection direction)
    {
        var prefix = AdditionalModule.ModelId("JsonScalar") + "." + Dir(direction);
        return f.TokenUnionWire(prefix, direction,
        [
            new TokenBranch { Token = JsonToken.String, Wire = f.Scalar("string", direction) },
            new TokenBranch { Token = JsonToken.Number, Wire = f.NumberWire(prefix + ".number", direction, AdditionalModule.Grammar("json-number")) },
            new TokenBranch { Token = JsonToken.Boolean, Wire = f.Scalar("boolean", direction) },
        ]);
    }

    private static PairedCodecRegistration RuneRegistration(IReadOnlyList<ModuleArtifactSpec> artifacts, string prefix) => Registration(
        artifacts, prefix, typeof(Rune), typeof(RuneJsonConverter), "Rune", "rune", "string",
        "Tisilia RuneJsonConverter: a JSON string holding exactly one Unicode scalar value",
        (f, _) => f.StringWire(AdditionalModule.ModelId("Rune") + ".read", WireDirection.ServerRead, AdditionalModule.Grammar("rune")),
        (f, _) => f.StringWire(AdditionalModule.ModelId("Rune") + ".write", WireDirection.ServerWrite, AdditionalModule.Grammar("rune")),
        new PairedKey("runeKey", "runeKey", AdditionalModule.Grammar("rune")),
        Grammars(("rune", "runeGrammar")),
        value => DomainAst.String(((Rune)value).ToString()),
        ast => RuneJsonConverter.ParseScalar(Text(ast, "Rune")) ?? throw new FormatException("the Rune domain AST is a string of one Unicode scalar value"),
        (a, b) => (Rune)a == (Rune)b);
}
