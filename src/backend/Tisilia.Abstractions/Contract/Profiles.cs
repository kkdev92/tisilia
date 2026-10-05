using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public enum BehaviorKind
{
    [JsonStringEnumMemberName("constructor")] Constructor,
    [JsonStringEnumMemberName("initializer")] Initializer,
    [JsonStringEnumMemberName("setter")] Setter,
    [JsonStringEnumMemberName("getter")] Getter,
    [JsonStringEnumMemberName("callback")] Callback,
    [JsonStringEnumMemberName("should-serialize")] ShouldSerialize,
    [JsonStringEnumMemberName("populate")] Populate,
    [JsonStringEnumMemberName("nested-options")] NestedOptions,
    [JsonStringEnumMemberName("sourcegen-handler")] SourcegenHandler,
    [JsonStringEnumMemberName("other")] Other,
}

public enum BehaviorEffect
{
    [JsonStringEnumMemberName("identity")] Identity,
    [JsonStringEnumMemberName("normalized")] Normalized,
    [JsonStringEnumMemberName("opaque")] Opaque,
}

public sealed record Behavior
{
    public required string Id { get; init; }
    public required BehaviorKind Kind { get; init; }
    public required BehaviorEffect Effect { get; init; }
    public required Impl Implementation { get; init; }
    public string? ProjectionId { get; init; }
    public required string SettingsDigest { get; init; }
}

public enum NumberHandlingFlag
{
    AllowReadingFromString,
    WriteAsString,
    AllowNamedFloatingPointLiterals,
}

public enum IgnoreCondition
{
    Never,
    Always,
    WhenWritingDefault,
    WhenWritingNull,
    WhenWriting,
    WhenReading,
}

public enum CommentHandling
{
    Disallow,
    Skip,
}

public enum ObjectCreationHandling
{
    Replace,
    Populate,
}

public enum UnmappedMemberHandling
{
    Skip,
    Disallow,
}

public enum ReferenceHandling
{
    None,
    IgnoreCycles,
    Preserve,
}

public enum ProfileMode
{
    [JsonStringEnumMemberName("reflection")] Reflection,
    [JsonStringEnumMemberName("sourcegen-metadata")] SourcegenMetadata,
    [JsonStringEnumMemberName("sourcegen-fast-path")] SourcegenFastPath,
}

public enum ScopeKind
{
    [JsonStringEnumMemberName("type")] Type,
    [JsonStringEnumMemberName("member")] Member,
    [JsonStringEnumMemberName("constructor-parameter")] ConstructorParameter,
}

public enum Nullability
{
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>Every effective <c>JsonSerializerOptions</c> value that influences the wire. All fields are always written.</summary>
public sealed record StjOptions
{
    public required bool PropertyNameCaseInsensitive { get; init; }
    public required string PropertyNamingPolicyId { get; init; }
    public required string DictionaryKeyPolicyId { get; init; }
    public required string EncoderId { get; init; }
    public required IReadOnlyList<NumberHandlingFlag> NumberHandling { get; init; }
    public required IgnoreCondition DefaultIgnoreCondition { get; init; }
    public required bool IgnoreNullValues { get; init; }
    public required bool IgnoreReadOnlyProperties { get; init; }
    public required bool IgnoreReadOnlyFields { get; init; }
    public required bool IncludeFields { get; init; }
    public required bool RespectNullableAnnotations { get; init; }
    public required bool RespectRequiredConstructorParameters { get; init; }
    public required bool AllowDuplicateProperties { get; init; }
    public required bool AllowOutOfOrderMetadataProperties { get; init; }
    public required bool AllowTrailingCommas { get; init; }
    public required CommentHandling ReadCommentHandling { get; init; }
    public required int MaxDepthRaw { get; init; }
    public required int MaxDepthEffective { get; init; }
    public required ObjectCreationHandling PreferredObjectCreationHandling { get; init; }
    public required UnmappedMemberHandling UnmappedMemberHandling { get; init; }
    public required ReferenceHandling ReferenceHandling { get; init; }
    public required IReadOnlyList<string> ResolverIds { get; init; }
    public required IReadOnlyList<string> ConverterBindingIds { get; init; }
}

public sealed record Scope
{
    public required string Id { get; init; }
    public required ScopeKind Kind { get; init; }
    public required string ClrPath { get; init; }
    public string? ReadCodecId { get; init; }
    public string? WriteCodecId { get; init; }
    public required bool ReadRequired { get; init; }
    public required Nullability GetterNullable { get; init; }
    public required Nullability SetterNullable { get; init; }
    public required ObjectCreationHandling EffectiveObjectCreationHandling { get; init; }
    public required IgnoreCondition EffectiveIgnoreCondition { get; init; }
    public required IReadOnlyList<string> BehaviorIds { get; init; }
}

public sealed record Profile
{
    public required string Id { get; init; }
    public required string DotnetVersion { get; init; }
    public required string StjVersion { get; init; }
    public required ProfileMode Mode { get; init; }
    public required StjOptions Options { get; init; }
    public required IReadOnlyList<Scope> Scopes { get; init; }
    public required IReadOnlyList<Behavior> Behaviors { get; init; }
    public required string Fingerprint { get; init; }
}
