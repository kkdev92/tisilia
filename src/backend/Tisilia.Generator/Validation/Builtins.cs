using System.Numerics;

namespace Tisilia.Generator.Validation;

/// <summary>Kinds of fixed builtin identifiers in <c>tisilia.builtins@0.3</c>.</summary>
public enum BuiltinKind
{
    Scalar,
    Structure,
    Grammar,
    KeyGrammar,
    DomainRule,
    Normalization,
    Equality,
    NameMatching,
    DuplicatePolicy,
    Editor,
    NamingPolicy,
    Encoder,
    Resolver,
    ServerAcceptance,
    AuthPolicy,
    CsrfPolicy,
    Pipeline,
    Result,
    Binder,
    Behavior,
    Oracle,
    CodecImpl,
    Comparer,
    Binding,
    Projection,
    JsonGrammar,
}

public sealed record BuiltinEntry(string Id, BuiltinKind Kind, string Name);

/// <summary>
/// The closed builtin registry <c>tisilia.builtins@0.3</c>. Every identifier is enumerated explicitly; the
/// <c>tisilia.</c> prefix alone never resolves anything.
/// </summary>
public static class Builtins
{
    public const string Version = "0.3";
    public const string Prefix = "tisilia.";
    public const string JsonRfc8259 = "tisilia.json-rfc8259@0.3";

    public static readonly string[] ScalarNames =
    [
        "string", "boolean", "char", "guid", "bytes", "json-value",
        "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64",
        "decimal", "float32", "float64",
        "date-only", "time-only", "datetime-utc", "datetime-unspecified", "datetime-local-wire", "datetime-offset", "duration",
    ];

    public static readonly string[] IntegerScalarNames = ["int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64"];

    public static readonly string[] StructureNames = ["object", "array", "map", "brand", "union", "enum"];

    /// <summary>Scalars whose canonical wire token is a JSON number.</summary>
    public static readonly string[] NumberTokenScalars = ["int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "decimal", "float32", "float64"];

    public static IReadOnlyDictionary<string, BuiltinEntry> All => Entries;

    public static bool TryGet(string id, out BuiltinEntry entry) => Entries.TryGetValue(id, out entry!);

    public static bool Is(string id, BuiltinKind kind) => Entries.TryGetValue(id, out var e) && e.Kind == kind;

    public static string Scalar(string name) => $"tisilia.{name}@0.3";
    public static string Grammar(string name) => $"tisilia.grammar.{name}@0.3";
    public static string KeyGrammar(string name) => $"tisilia.grammar.{name}-key@0.3";
    public static string DomainRule(string name) => $"tisilia.domain.{name}@0.3";
    public static string BindingFor(string name) => $"tisilia.binding.{name}@0.3";
    public static string CodecImpl(string name, string method) => $"tisilia.codec.{name}.{method}@0.3";

    public const string NormalizeIdentity = "tisilia.normalize.identity@0.3";
    public const string EqualityStructural = "tisilia.equality.structural@0.3";
    public const string EqualityNumeric = "tisilia.equality.numeric@0.3";
    public const string EqualityExactWire = "tisilia.equality.exact-wire@0.3";
    public const string NamesOrdinal = "tisilia.names.ordinal@0.3";
    public const string NamesOrdinalIgnoreCase = "tisilia.names.ordinal-ignore-case@0.3";
    public const string DuplicatesReject = "tisilia.duplicates.reject@0.3";
    public const string DuplicatesLastWins = "tisilia.duplicates.last-wins@0.3";
    public const string EditorText = "tisilia.editor.text@0.3";
    public const string EditorJson = "tisilia.editor.json@0.3";
    public const string NamingNone = "tisilia.naming.none@0.3";
    public const string EncoderDefault = "tisilia.encoder.default@0.3";
    public const string EncoderUnsafeRelaxed = "tisilia.encoder.unsafe-relaxed@0.3";
    public const string ResolverReflection = "tisilia.resolver.reflection@0.3";
    /// <summary>The framework's internal <c>Microsoft.AspNetCore.Http.ProblemDetailsJsonContext</c> inserted by <c>AddProblemDetails()</c>; its metadata for ProblemDetails is shape-identical to reflection.</summary>
    public const string ResolverAspNetCoreProblemDetails = "tisilia.resolver.aspnetcore-problem-details@0.3";
    /// <summary>
    /// <c>Microsoft.AspNetCore.OpenApi.OpenApiJsonSchemaContext</c>, inserted by <c>AddOpenApi()</c> (the webapi template): metadata for its own
    /// schema type, <c>string</c> and <c>JsonNode</c> — the same built-in converters reflection uses for the latter two.
    /// </summary>
    public const string ResolverAspNetCoreOpenApi = "tisilia.resolver.aspnetcore-openapi@0.3";
    /// <summary><c>Microsoft.AspNetCore.Identity.Data.IdentityEndpointsJsonSerializerContext</c>, inserted by <c>AddIdentityApiEndpoints()</c>: metadata for the Identity endpoint DTOs only.</summary>
    public const string ResolverAspNetCoreIdentityEndpoints = "tisilia.resolver.aspnetcore-identity-endpoints@0.3";
    /// <summary><c>Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenJsonSerializerContext</c>, inserted by <c>AddBearerToken()</c>: metadata for AccessTokenResponse only.</summary>
    public const string ResolverAspNetCoreBearerToken = "tisilia.resolver.aspnetcore-bearer-token@0.3";

    /// <summary>
    /// A source-generated <c>JsonSerializerContext</c> of the application: System.Text.Json's generated metadata, with the
    /// context, the options it was generated with and its fast-path types recorded on the binding.
    /// </summary>
    public const string ResolverStjSourceGenerated = "tisilia.resolver.stj-source-generated@0.3";
    public const string AcceptCanonical = "tisilia.accept.canonical@0.3";
    public const string AcceptDotnetTryParseInvariant = "tisilia.accept.dotnet-tryparse-invariant@0.3";
    public const string AuthAnonymous = "tisilia.auth.anonymous@0.3";
    public const string AuthAuthenticated = "tisilia.auth.authenticated@0.3";
    public const string CsrfNone = "tisilia.csrf.none@0.3";
    public const string CsrfAntiforgery = "tisilia.csrf.antiforgery@0.3";
    public const string BehaviorIdentity = "tisilia.behavior.identity@0.3";
    public const string OracleStructural = "tisilia.oracle.structural@0.3";
    public const string OracleNumeric = "tisilia.oracle.numeric@0.3";
    public const string OracleExactWire = "tisilia.oracle.exact-wire@0.3";
    public const string ComparerOrdinal = "tisilia.comparer.ordinal@0.3";
    public const string ComparerOrdinalIgnoreCase = "tisilia.comparer.ordinal-ignore-case@0.3";
    public const string ComparerStructural = "tisilia.comparer.structural@0.3";
    public const string ProjectionIdentity = "tisilia.projection.identity@0.3";
    public const string ResultMinimalJson = "tisilia.result.minimal-json@0.3";
    public const string ResultMinimalResult = "tisilia.result.minimal-result@0.3";
    public const string ResultMvcObject = "tisilia.result.mvc-object@0.3";
    public const string ResultMvcJson = "tisilia.result.mvc-json@0.3";
    public const string ResultBodyless = "tisilia.result.bodyless@0.3";
    public const string ResultTextUtf8 = "tisilia.result.text-utf8@0.3";
    public const string ResultBinaryBuffered = "tisilia.result.binary-buffered@0.4";
    public const string BinderPathSegment = "tisilia.binder.path-segment@0.3";
    public const string BinderQueryComponent = "tisilia.binder.query-component@0.3";
    public const string BinderHeaderText = "tisilia.binder.header-text@0.3";

    public static readonly string[] NamingPolicies = ["none", "camel-case", "snake-case-lower", "snake-case-upper", "kebab-case-lower", "kebab-case-upper"];

    public static readonly string[] Pipelines =
    [
        "problem-details", "exception-handler", "developer-exception-page", "status-code-pages", "authentication", "authorization",
        "api-controller-validation", "minimal-validation", "cors", "antiforgery",
    ];

    // Declared after every array it reads: static initializers run in textual order.
    private static readonly Dictionary<string, BuiltinEntry> Entries = Build();

    private static Dictionary<string, BuiltinEntry> Build()
    {
        var map = new Dictionary<string, BuiltinEntry>(StringComparer.Ordinal);
        void Add(string id, BuiltinKind kind, string name) => map.Add(id, new BuiltinEntry(id, kind, name));

        foreach (var name in ScalarNames)
        {
            Add(Scalar(name), BuiltinKind.Scalar, name);
            Add(Grammar(name), BuiltinKind.Grammar, name);
            Add(KeyGrammar(name), BuiltinKind.KeyGrammar, name);
            Add(DomainRule(name), BuiltinKind.DomainRule, name);
            Add(BindingFor(name), BuiltinKind.Binding, name);
            foreach (var method in new[] { "validate", "encode", "decode", "encode-key", "decode-key", "parse-input" })
            {
                Add(CodecImpl(name, method), BuiltinKind.CodecImpl, name);
            }
        }

        foreach (var name in StructureNames)
        {
            Add(Scalar(name), BuiltinKind.Structure, name);
            Add(DomainRule(name), BuiltinKind.DomainRule, name);
            Add(BindingFor(name), BuiltinKind.Binding, name);
            foreach (var method in new[] { "validate", "encode", "decode", "parse-input" })
            {
                Add(CodecImpl(name, method), BuiltinKind.CodecImpl, name);
            }
        }

        // enum keys and string-form enum grammar
        Add(KeyGrammar("enum"), BuiltinKind.KeyGrammar, "enum");
        Add(Grammar("enum-name"), BuiltinKind.Grammar, "enum-name");
        Add(Grammar("enum-number"), BuiltinKind.Grammar, "enum-number");
        Add(CodecImpl("enum", "encode-key"), BuiltinKind.CodecImpl, "enum");
        Add(CodecImpl("enum", "decode-key"), BuiltinKind.CodecImpl, "enum");
        // string forms of number scalars (WriteAsString / AllowReadingFromString profiles)
        foreach (var name in NumberTokenScalars)
        {
            Add(Grammar(name + "-string"), BuiltinKind.Grammar, name + "-string");
        }

        Add(Grammar("float64-named"), BuiltinKind.Grammar, "float64-named");
        Add(Grammar("float32-named"), BuiltinKind.Grammar, "float32-named");
        Add(Grammar("text"), BuiltinKind.Grammar, "text");
        Add(JsonRfc8259, BuiltinKind.JsonGrammar, "json-rfc8259");

        Add(NormalizeIdentity, BuiltinKind.Normalization, "identity");
        Add(EqualityStructural, BuiltinKind.Equality, "structural");
        Add(EqualityNumeric, BuiltinKind.Equality, "numeric");
        Add(EqualityExactWire, BuiltinKind.Equality, "exact-wire");
        Add(NamesOrdinal, BuiltinKind.NameMatching, "ordinal");
        Add(NamesOrdinalIgnoreCase, BuiltinKind.NameMatching, "ordinal-ignore-case");
        Add(DuplicatesReject, BuiltinKind.DuplicatePolicy, "reject");
        Add(DuplicatesLastWins, BuiltinKind.DuplicatePolicy, "last-wins");
        Add(EditorText, BuiltinKind.Editor, "text");
        Add(EditorJson, BuiltinKind.Editor, "json");
        foreach (var name in NamingPolicies)
        {
            Add($"tisilia.naming.{name}@0.3", BuiltinKind.NamingPolicy, name);
        }

        Add(EncoderDefault, BuiltinKind.Encoder, "default");
        Add(EncoderUnsafeRelaxed, BuiltinKind.Encoder, "unsafe-relaxed");
        Add(ResolverReflection, BuiltinKind.Resolver, "reflection");
        Add(ResolverAspNetCoreProblemDetails, BuiltinKind.Resolver, "aspnetcore-problem-details");
        Add(ResolverAspNetCoreOpenApi, BuiltinKind.Resolver, "aspnetcore-openapi");
        Add(ResolverAspNetCoreIdentityEndpoints, BuiltinKind.Resolver, "aspnetcore-identity-endpoints");
        Add(ResolverAspNetCoreBearerToken, BuiltinKind.Resolver, "aspnetcore-bearer-token");
        Add(ResolverStjSourceGenerated, BuiltinKind.Resolver, "stj-source-generated");
        Add(AcceptCanonical, BuiltinKind.ServerAcceptance, "canonical");
        Add(AcceptDotnetTryParseInvariant, BuiltinKind.ServerAcceptance, "dotnet-tryparse-invariant");
        Add(AuthAnonymous, BuiltinKind.AuthPolicy, "anonymous");
        Add(AuthAuthenticated, BuiltinKind.AuthPolicy, "authenticated");
        Add(CsrfNone, BuiltinKind.CsrfPolicy, "none");
        Add(CsrfAntiforgery, BuiltinKind.CsrfPolicy, "antiforgery");
        foreach (var name in Pipelines)
        {
            Add($"tisilia.pipeline.{name}@0.3", BuiltinKind.Pipeline, name);
        }

        Add(ResultMinimalJson, BuiltinKind.Result, "minimal-json");
        Add(ResultMinimalResult, BuiltinKind.Result, "minimal-result");
        Add(ResultMvcObject, BuiltinKind.Result, "mvc-object");
        Add(ResultMvcJson, BuiltinKind.Result, "mvc-json");
        Add(ResultBodyless, BuiltinKind.Result, "bodyless");
        Add(ResultTextUtf8, BuiltinKind.Result, "text-utf8");
        Add(ResultBinaryBuffered, BuiltinKind.Result, "binary-buffered");
        Add(BinderPathSegment, BuiltinKind.Binder, "path-segment");
        Add(BinderQueryComponent, BuiltinKind.Binder, "query-component");
        Add(BinderHeaderText, BuiltinKind.Binder, "header-text");
        Add(BehaviorIdentity, BuiltinKind.Behavior, "identity");
        Add(OracleStructural, BuiltinKind.Oracle, "structural");
        Add(OracleNumeric, BuiltinKind.Oracle, "numeric");
        Add(OracleExactWire, BuiltinKind.Oracle, "exact-wire");
        Add(ComparerOrdinal, BuiltinKind.Comparer, "ordinal");
        Add(ComparerOrdinalIgnoreCase, BuiltinKind.Comparer, "ordinal-ignore-case");
        Add(ComparerStructural, BuiltinKind.Comparer, "structural");
        Add(ProjectionIdentity, BuiltinKind.Projection, "identity");
        return map;
    }

    /// <summary>Inclusive ranges of the builtin integer scalars.</summary>
    public static bool TryGetIntegerRange(string scalarName, out BigInteger min, out BigInteger max)
    {
        switch (scalarName)
        {
            case "int8": min = -128; max = 127; return true;
            case "uint8": min = 0; max = 255; return true;
            case "int16": min = -32768; max = 32767; return true;
            case "uint16": min = 0; max = 65535; return true;
            case "int32": min = int.MinValue; max = int.MaxValue; return true;
            case "uint32": min = 0; max = uint.MaxValue; return true;
            case "int64": min = long.MinValue; max = long.MaxValue; return true;
            case "uint64": min = 0; max = ulong.MaxValue; return true;
            default: min = 0; max = 0; return false;
        }
    }

    /// <summary>JSON token consumed by the canonical wire of a scalar (string unless listed in <see cref="NumberTokenScalars"/>).</summary>
    public static bool IsNumberTokenScalar(string scalarName) => Array.IndexOf(NumberTokenScalars, scalarName) >= 0;
}
