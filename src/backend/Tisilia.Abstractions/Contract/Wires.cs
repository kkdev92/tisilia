using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public sealed record WireProperty
{
    public required string Name { get; init; }
    public required WireRef Wire { get; init; }
    public required Presence Presence { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RejectAdditional), "reject")]
[JsonDerivedType(typeof(IgnoreAdditional), "ignore")]
[JsonDerivedType(typeof(CaptureAdditional), "capture")]
public abstract record AdditionalPolicy
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record RejectAdditional : AdditionalPolicy
{
    [JsonIgnore]
    public override string Kind => "reject";
}

public sealed record IgnoreAdditional : AdditionalPolicy
{
    [JsonIgnore]
    public override string Kind => "ignore";
}

public sealed record CaptureAdditional : AdditionalPolicy
{
    public required WireRef Wire { get; init; }
    [JsonIgnore]
    public override string Kind => "capture";
}

public sealed record TokenBranch
{
    public required JsonToken Token { get; init; }
    public required WireRef Wire { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StringTag), "string")]
[JsonDerivedType(typeof(NumberTag), "number")]
public abstract record DiscriminatorTag;

public sealed record StringTag : DiscriminatorTag
{
    public required string Value { get; init; }
}

public sealed record NumberTag : DiscriminatorTag
{
    public required string Text { get; init; }
}

public sealed record TaggedVariant
{
    public required DiscriminatorTag Tag { get; init; }
    public required WireRef Wire { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LiteralWire), "literal")]
[JsonDerivedType(typeof(NullWire), "null")]
[JsonDerivedType(typeof(BooleanWire), "boolean")]
[JsonDerivedType(typeof(StringWire), "string")]
[JsonDerivedType(typeof(NumberWire), "number")]
[JsonDerivedType(typeof(ArrayWire), "array")]
[JsonDerivedType(typeof(ObjectWire), "object")]
[JsonDerivedType(typeof(TokenUnionWire), "token-union")]
[JsonDerivedType(typeof(TaggedUnionWire), "tagged-union")]
[JsonDerivedType(typeof(LosslessJsonWire), "lossless-json")]
public abstract record WireShape
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record LiteralWire : WireShape
{
    public required JsonValue Value { get; init; }
    [JsonIgnore]
    public override string Kind => "literal";
}

public sealed record NullWire : WireShape
{
    [JsonIgnore]
    public override string Kind => "null";
}

public sealed record BooleanWire : WireShape
{
    [JsonIgnore]
    public override string Kind => "boolean";
}

public sealed record StringWire : WireShape
{
    public required string GrammarId { get; init; }
    public long? MinUtf16Length { get; init; }
    public long? MaxUtf16Length { get; init; }
    [JsonIgnore]
    public override string Kind => "string";
}

public sealed record NumberWire : WireShape
{
    public required string GrammarId { get; init; }
    [JsonIgnore]
    public override string Kind => "number";
}

public sealed record ArrayWire : WireShape
{
    public required WireRef Element { get; init; }
    [JsonIgnore]
    public override string Kind => "array";
}

public sealed record ObjectWire : WireShape
{
    public required IReadOnlyList<WireProperty> Properties { get; init; }
    public required AdditionalPolicy Additional { get; init; }
    public required string DuplicatePolicyId { get; init; }
    public required string NameMatchingId { get; init; }
    [JsonIgnore]
    public override string Kind => "object";
}

public sealed record TokenUnionWire : WireShape
{
    public required IReadOnlyList<TokenBranch> Branches { get; init; }
    [JsonIgnore]
    public override string Kind => "token-union";
}

public sealed record TaggedUnionWire : WireShape
{
    public required string Discriminator { get; init; }
    public required IReadOnlyList<TaggedVariant> Variants { get; init; }
    [JsonIgnore]
    public override string Kind => "tagged-union";
}

public sealed record LosslessJsonWire : WireShape
{
    public required string GrammarId { get; init; }
    [JsonIgnore]
    public override string Kind => "lossless-json";
}

public sealed record Wire
{
    public required string Id { get; init; }
    public required WireDirection Direction { get; init; }
    public required WireShape Shape { get; init; }
}
