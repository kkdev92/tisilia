using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public enum ScalarRepresentation
{
    [JsonStringEnumMemberName("native")] Native,
    [JsonStringEnumMemberName("string")] String,
}

public enum UnknownMembers
{
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("ignore")] Ignore,
}

/// <summary>Portable Codec DSL program node. Discriminated by <c>op</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ScalarOp), "scalar")]
[JsonDerivedType(typeof(RefOp), "ref")]
[JsonDerivedType(typeof(NullableOp), "nullable")]
[JsonDerivedType(typeof(ArrayOp), "array")]
[JsonDerivedType(typeof(ObjectOp), "object")]
[JsonDerivedType(typeof(TokenUnionOp), "token-union")]
[JsonDerivedType(typeof(TaggedUnionOp), "tagged-union")]
public abstract record Program
{
    [JsonIgnore]
    public abstract string Op { get; }
}

public sealed record ScalarOp : Program
{
    public required string ScalarId { get; init; }
    public required ScalarRepresentation Representation { get; init; }
    [JsonIgnore]
    public override string Op => "scalar";
}

public sealed record RefOp : Program
{
    public required string DefinitionId { get; init; }
    [JsonIgnore]
    public override string Op => "ref";
}

public sealed record NullableOp : Program
{
    public required Program Value { get; init; }
    [JsonIgnore]
    public override string Op => "nullable";
}

public sealed record ArrayOp : Program
{
    public required Program Element { get; init; }
    [JsonIgnore]
    public override string Op => "array";
}

public sealed record ObjectMember
{
    public required string Name { get; init; }
    public required Presence Presence { get; init; }
    public required Program Node { get; init; }
}

public sealed record ObjectOp : Program
{
    public required IReadOnlyList<ObjectMember> Members { get; init; }
    public required UnknownMembers UnknownMembers { get; init; }
    [JsonIgnore]
    public override string Op => "object";
}

public sealed record TokenBranchOp
{
    public required JsonToken Token { get; init; }
    public required Program Node { get; init; }
}

public sealed record TokenUnionOp : Program
{
    public required IReadOnlyList<TokenBranchOp> Branches { get; init; }
    [JsonIgnore]
    public override string Op => "token-union";
}

public sealed record TaggedBranchOp
{
    public required string Tag { get; init; }
    public required Program Node { get; init; }
}

public sealed record TaggedUnionOp : Program
{
    public required string Discriminator { get; init; }
    public required IReadOnlyList<TaggedBranchOp> Branches { get; init; }
    [JsonIgnore]
    public override string Op => "tagged-union";
}

public sealed record PortableDirection
{
    public required string DomainTypeId { get; init; }
    public required Program Program { get; init; }
    public required string ProjectionId { get; init; }
    public required string EquivalenceId { get; init; }
}
