using Tisilia.Contract;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Building;

/// <summary>Effective System.Text.Json profile records for the common presets, with every field written out.</summary>
public static class StandardProfile
{
    /// <summary>ASP.NET Core web defaults (observed on .NET 10: camelCase, case-insensitive, AllowReadingFromString, MaxDepth 0→64).</summary>
    public static Profile Web(string id, string dotnetVersion, string stjVersion, string nameMatchingBindingId)
        => Create(id, dotnetVersion, stjVersion, new StjOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicyId = "tisilia.naming.camel-case@0.3",
            DictionaryKeyPolicyId = Builtins.NamingNone,
            EncoderId = Builtins.EncoderDefault,
            NumberHandling = [NumberHandlingFlag.AllowReadingFromString],
            DefaultIgnoreCondition = IgnoreCondition.Never,
            IgnoreNullValues = false,
            IgnoreReadOnlyProperties = false,
            IgnoreReadOnlyFields = false,
            IncludeFields = false,
            RespectNullableAnnotations = false,
            RespectRequiredConstructorParameters = false,
            AllowDuplicateProperties = true,
            AllowOutOfOrderMetadataProperties = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = CommentHandling.Disallow,
            MaxDepthRaw = 0,
            MaxDepthEffective = 64,
            PreferredObjectCreationHandling = ObjectCreationHandling.Replace,
            UnmappedMemberHandling = UnmappedMemberHandling.Skip,
            ReferenceHandling = ReferenceHandling.None,
            ResolverIds = [Builtins.ResolverReflection],
            ConverterBindingIds = [],
        }, nameMatchingBindingId);

    public static Profile Create(string id, string dotnetVersion, string stjVersion, StjOptions options, string? nameMatchingBindingId = null, IReadOnlyList<Scope>? scopes = null, IReadOnlyList<Behavior>? behaviors = null)
        => new()
        {
            Id = id,
            DotnetVersion = dotnetVersion,
            StjVersion = stjVersion,
            Mode = ProfileMode.Reflection,
            Options = options,
            Scopes = scopes ?? [],
            Behaviors = behaviors ?? [],
            Fingerprint = "sha256:" + new string('0', 64),
        };

    /// <summary>Name matching binding for a profile: ordinal-ignore-case when PropertyNameCaseInsensitive, ordinal otherwise.</summary>
    public static Binding NameMatchingBinding(string profileId, bool caseInsensitive)
        => new()
        {
            Id = profileId + ".name-matching",
            Kind = BindingKind.NameMatching,
            Version = "0.3.0",
            Implementation = new BuiltinImpl { Id = caseInsensitive ? Builtins.NamesOrdinalIgnoreCase : Builtins.NamesOrdinal },
            SettingsDigest = Canonical.TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes("name-matching:" + (caseInsensitive ? "ordinal-ignore-case" : "ordinal"))),
            DependencyIds = [],
            Context = [],
        };

    public static string DuplicatePolicyFor(StjOptions options) => options.AllowDuplicateProperties ? Builtins.DuplicatesLastWins : Builtins.DuplicatesReject;

    public static NumberProfile NumberProfileFor(StjOptions options) => new(
        options.NumberHandling.Contains(NumberHandlingFlag.AllowReadingFromString),
        options.NumberHandling.Contains(NumberHandlingFlag.WriteAsString),
        options.NumberHandling.Contains(NumberHandlingFlag.AllowNamedFloatingPointLiterals));
}
