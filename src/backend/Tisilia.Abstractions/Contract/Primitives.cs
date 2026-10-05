using System.Text.Json.Serialization;

namespace Tisilia.Contract;

/// <summary>JSON token direction relative to the server.</summary>
public enum WireDirection
{
    [JsonStringEnumMemberName("server-read")] ServerRead,
    [JsonStringEnumMemberName("server-write")] ServerWrite,
}

public enum Presence
{
    [JsonStringEnumMemberName("required")] Required,
    [JsonStringEnumMemberName("optional")] Optional,
}

public enum JsonToken
{
    [JsonStringEnumMemberName("null")] Null,
    [JsonStringEnumMemberName("boolean")] Boolean,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("array")] Array,
    [JsonStringEnumMemberName("object")] Object,
}

public enum ArtifactTarget
{
    [JsonStringEnumMemberName("dotnet")] Dotnet,
    [JsonStringEnumMemberName("node")] Node,
    [JsonStringEnumMemberName("browser")] Browser,
}

/// <summary>Type usage position: root, member, element, key/value, variant, brand base or extension value.</summary>
public sealed record TypeUse
{
    public required string TypeId { get; init; }
    public required string CodecId { get; init; }
    public required bool SemanticNullable { get; init; }
}

/// <summary>Finite reference to a wire definition.</summary>
public sealed record WireRef
{
    public required string WireId { get; init; }
    public required WireDirection Direction { get; init; }
}

/// <summary>Implementation reference: a fixed builtin identifier or an export of a registered module.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BuiltinImpl), "builtin")]
[JsonDerivedType(typeof(ModuleImpl), "module")]
public abstract record Impl;

public sealed record BuiltinImpl : Impl
{
    public required string Id { get; init; }
}

public sealed record ModuleImpl : Impl
{
    public required string ModuleId { get; init; }
    public required string ExportName { get; init; }
}

/// <summary>Lossless JSON value used inside control documents (literals, defaults, runner payloads).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JsonNullValue), "null")]
[JsonDerivedType(typeof(JsonBooleanValue), "boolean")]
[JsonDerivedType(typeof(JsonStringValue), "string")]
[JsonDerivedType(typeof(JsonNumberValue), "number")]
[JsonDerivedType(typeof(JsonArrayValue), "array")]
[JsonDerivedType(typeof(JsonObjectValue), "object")]
public abstract record JsonValue
{
    [JsonIgnore]
    public abstract JsonToken Token { get; }
}

public sealed record JsonNullValue : JsonValue
{
    [JsonIgnore]
    public override JsonToken Token => JsonToken.Null;
}

public sealed record JsonBooleanValue : JsonValue
{
    public required bool Value { get; init; }
    [JsonIgnore]
    public override JsonToken Token => JsonToken.Boolean;
}

public sealed record JsonStringValue : JsonValue
{
    public required string Value { get; init; }
    [JsonIgnore]
    public override JsonToken Token => JsonToken.String;
}

/// <summary>Number kept as its RFC 8259 lexeme; never converted to a binary floating point value here.</summary>
public sealed record JsonNumberValue : JsonValue
{
    public required string Text { get; init; }
    [JsonIgnore]
    public override JsonToken Token => JsonToken.Number;
}

public sealed record JsonArrayValue : JsonValue
{
    public required IReadOnlyList<JsonValue> Items { get; init; }
    [JsonIgnore]
    public override JsonToken Token => JsonToken.Array;
}

public sealed record JsonObjectEntry
{
    public required string Name { get; init; }
    public required JsonValue Value { get; init; }
}

public sealed record JsonObjectValue : JsonValue
{
    public required IReadOnlyList<JsonObjectEntry> Entries { get; init; }
    [JsonIgnore]
    public override JsonToken Token => JsonToken.Object;
}

public sealed record NameValue
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed record Artifact
{
    public required ArtifactTarget Target { get; init; }
    public required string Path { get; init; }
    public required string Digest { get; init; }
}

public sealed record IdentifiedArtifact
{
    public required string ModuleId { get; init; }
    public required Artifact Artifact { get; init; }
}

public sealed record Limits
{
    public required long MaxBodyBytes { get; init; }
    public required int MaxDepth { get; init; }
    public required long MaxTokens { get; init; }
    public required int MaxNumberCharacters { get; init; }
    public required int TimeoutMs { get; init; }
    public required long MaxDiagnosticBytes { get; init; }

    /// <summary>The default limits.</summary>
    public static Limits Default { get; } = new()
    {
        MaxBodyBytes = 16_777_216,
        MaxDepth = 64,
        MaxTokens = 1_000_000,
        MaxNumberCharacters = 4096,
        TimeoutMs = 30_000,
        MaxDiagnosticBytes = 262_144,
    };
}

public sealed record RuntimeMatrix
{
    public required string Dotnet { get; init; }
    public required string Aspnetcore { get; init; }
    public required string Stj { get; init; }
    public required string Typescript { get; init; }
    public string? Node { get; init; }
    public string? Nuxt { get; init; }
    public required string Os { get; init; }
    public required string Architecture { get; init; }
    public string? BrowserName { get; init; }
    public string? BrowserVersion { get; init; }
}
