using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tisilia;

/// <summary>
/// Shared constants and the strict <see cref="JsonSerializerOptions"/> used for every Tisilia control document
/// (contract, config, manifests, evidence, envelopes, runner messages).
/// </summary>
public static class TisiliaJson
{
    /// <summary>Unchanged codec ABI, portable DSL and non-contract control document version.</summary>
    public const string DraftVersion = "0.3";
    public const string ContractVersion = "0.4";

    /// <summary>Fixed builtin registry identifier.</summary>
    public const string BuiltinSet = "tisilia.builtins@0.3";

    /// <summary>The prefix each hashed document kind is tagged with. The trailing newline is a real LF byte.</summary>
    public const string ContractHashPrefix = "TISILIA-CONTRACT/0.4\n";
    public const string ProfileHashPrefix = "TISILIA-PROFILE/0.3\n";
    public const string ClosureHashPrefix = "TISILIA-CLOSURE/0.3\n";
    public const string RequestHashPrefix = "TISILIA-REQUEST/0.3\n";

    public static class Formats
    {
        public const string Contract = "tisilia.contract";
        public const string Config = "tisilia.config";
        public const string ClosureRecord = "tisilia.closure-record";
        public const string CodecManifest = "tisilia.codec-manifest";
        public const string ConformanceEvidence = "tisilia.conformance-evidence";
        public const string GenerationManifest = "tisilia.generation-manifest";
        public const string HydrationEnvelope = "tisilia.hydration-envelope";
        public const string PortableDefinition = "tisilia.portable-definition";
        public const string PortableProject = "tisilia.portable-project";
        public const string RequestIdentityRecord = "tisilia.request-identity-record";
        public const string RunnerMessage = "tisilia.runner-message";
        /// <summary>The trusted module registry of <c>tisilia explorer build</c>.</summary>
        public const string ExplorerRegistry = "tisilia.explorer-registry";
        /// <summary>The output of <c>tisilia explorer build</c>: the bundle's files and bundled modules with digests.</summary>
        public const string ExplorerBundle = "tisilia.explorer-bundle";
    }

    /// <summary>
    /// Strict options for control documents: camelCase names, case-sensitive matching, unknown members rejected,
    /// duplicates rejected, no comments/trailing commas, polymorphic discriminators accepted in any position.
    /// The <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> encoder is used only for readable output;
    /// hashing never goes through these options (it canonicalizes with RFC 8785).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: false);

    /// <summary>Same as <see cref="Options"/> but indented, for files written to disk.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(writeIndented: true);

    /// <summary>
    /// System.Text.Json's default settings with the reflection resolver named explicitly. Applications that publish with Native AOT or
    /// trimming turn reflection-based serialization off by default (the <c>JsonSerializerIsReflectionEnabledByDefault</c> feature
    /// switch, also under <c>dotnet run</c>), so <c>JsonSerializer.Serialize(value)</c> without options throws inside them; Tisilia's
    /// own serialization in the application process (export, runner) goes through these options instead.
    /// </summary>
    public static JsonSerializerOptions Plain { get; } = CreatePlainOptions();

    private static JsonSerializerOptions CreatePlainOptions()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        return options;
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = writeIndented,
            NewLine = "\n",
            IndentSize = 2,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // named explicitly: an application that disables reflection-based serialization by default still runs the export host
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
