using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public sealed record DomainProperty
{
    public required string Name { get; init; }
    public required TypeUse Use { get; init; }
    public required Presence Presence { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoExtension), "none")]
[JsonDerivedType(typeof(CaptureExtension), "capture")]
public abstract record Extension;

public sealed record NoExtension : Extension;

public sealed record CaptureExtension : Extension
{
    public required TypeUse Value { get; init; }
    public required string Collision { get; init; }
}

public sealed record EnumMember
{
    public required string Name { get; init; }
    /// <summary>Decimal integer text, always inside the underlying primitive range (SV49).</summary>
    public required string Value { get; init; }
    public string? SerializedName { get; init; }
}

public sealed record UnionVariant
{
    public required string Tag { get; init; }
    public required TypeUse Use { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PrimitiveShape), "primitive")]
[JsonDerivedType(typeof(EnumShape), "enum")]
[JsonDerivedType(typeof(ObjectShape), "object")]
[JsonDerivedType(typeof(ArrayShape), "array")]
[JsonDerivedType(typeof(MapShape), "map")]
[JsonDerivedType(typeof(BrandShape), "brand")]
[JsonDerivedType(typeof(UnionShape), "union")]
public abstract record DomainShape
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record PrimitiveShape : DomainShape
{
    public required string PrimitiveId { get; init; }
    [JsonIgnore]
    public override string Kind => "primitive";
}

public sealed record EnumShape : DomainShape
{
    public required string UnderlyingPrimitiveId { get; init; }
    public required bool Flags { get; init; }
    public required bool AllowUndefinedInteger { get; init; }
    public required IReadOnlyList<EnumMember> Members { get; init; }
    [JsonIgnore]
    public override string Kind => "enum";
}

public sealed record ObjectShape : DomainShape
{
    public required IReadOnlyList<DomainProperty> Properties { get; init; }
    public required Extension Extension { get; init; }
    [JsonIgnore]
    public override string Kind => "object";
}

public sealed record ArrayShape : DomainShape
{
    public required TypeUse Element { get; init; }
    [JsonIgnore]
    public override string Kind => "array";
}

public sealed record MapShape : DomainShape
{
    public required TypeUse Key { get; init; }
    public required TypeUse Value { get; init; }
    public required string ComparerId { get; init; }
    [JsonIgnore]
    public override string Kind => "map";
}

public sealed record BrandShape : DomainShape
{
    public required string BrandId { get; init; }
    public required TypeUse Base { get; init; }
    [JsonIgnore]
    public override string Kind => "brand";
}

public sealed record UnionShape : DomainShape
{
    public required IReadOnlyList<UnionVariant> Variants { get; init; }
    [JsonIgnore]
    public override string Kind => "union";
}

/// <summary>Public domain type. Direction and usage context are already reflected in the id.</summary>
public sealed record Model
{
    public required string Id { get; init; }
    public required string TsName { get; init; }
    public required string ClrIdentity { get; init; }
    public required DomainShape Shape { get; init; }
}
