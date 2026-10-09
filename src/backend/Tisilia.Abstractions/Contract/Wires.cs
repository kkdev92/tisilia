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
[JsonDerivedType(typeof(XmlTextWire), "xml-text")]
[JsonDerivedType(typeof(XmlElementWire), "xml-element")]
[JsonDerivedType(typeof(XmlItemsWire), "xml-items")]
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
    /// <summary>
    /// Reference metadata (ReferenceHandler.Preserve): on a server-write wire the server writes the array as
    /// <c>{"$id": id, "$values": [...]}</c>, or <c>{"$ref": id}</c> for a collection it wrote before; on a server-read wire it reads them,
    /// so a request writes a collection it reaches again that way. Absent otherwise.
    /// </summary>
    public bool? ReferenceMetadata { get; init; }
    [JsonIgnore]
    public override string Kind => "array";
}

public sealed record ObjectWire : WireShape
{
    public required IReadOnlyList<WireProperty> Properties { get; init; }
    public required AdditionalPolicy Additional { get; init; }
    public required string DuplicatePolicyId { get; init; }
    public required string NameMatchingId { get; init; }
    /// <summary>
    /// Reference metadata (ReferenceHandler.Preserve): on a server-write wire the server writes <c>"$id": id</c> before the properties (a
    /// map's entries), or <c>{"$ref": id}</c> in place of a value it wrote before; on a server-read wire it reads them and makes the value
    /// before its members, so a request writes a value it reaches again that way, also inside the value. Absent otherwise.
    /// </summary>
    public bool? ReferenceMetadata { get; init; }
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
    /// <summary><c>{"$ref": id}</c> may stand in place of a value written before (ReferenceHandler.Preserve); each variant's wire says whether it carries <c>$id</c>.</summary>
    public bool? ReferenceMetadata { get; init; }
    [JsonIgnore]
    public override string Kind => "tagged-union";
}

public sealed record LosslessJsonWire : WireShape
{
    public required string GrammarId { get; init; }
    [JsonIgnore]
    public override string Kind => "lossless-json";
}

/// <summary>The XML name of an enum constant (<c>[XmlEnum]</c>, or the constant's own name).</summary>
public sealed record XmlEnumName
{
    /// <summary>Decimal integer text of the constant's value.</summary>
    public required string Value { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// XML text: the character content of an element or the value of an attribute, in the form XmlSerializer writes and reads for the
/// CLR type (<see cref="GrammarId"/>). An enum grammar lists the constants' XML names in XmlSerializer's order.
/// </summary>
public sealed record XmlTextWire : WireShape
{
    public required string GrammarId { get; init; }
    public IReadOnlyList<XmlEnumName>? Names { get; init; }
    [JsonIgnore]
    public override string Kind => "xml-text";
}

/// <summary>
/// A member of an <see cref="XmlElementWire"/>: an attribute, a child element, or the element's character content, carrying the domain
/// property <see cref="Property"/>.
/// </summary>
public sealed record XmlMember
{
    public required string Property { get; init; }
    /// <summary>The local name of the attribute or element; absent for the character content.</summary>
    public string? Name { get; init; }
    /// <summary>The namespace URI of the attribute or element; absent for none.</summary>
    public string? Namespace { get; init; }
    public required WireRef Wire { get; init; }
    public required Presence Presence { get; init; }
    /// <summary>An element written as <c>xsi:nil="true"</c> when the value is null (each element, for a repeated member).</summary>
    public bool? Nillable { get; init; }
    /// <summary>A collection written as one element per value directly in the parent (<c>[XmlElement]</c> on a collection); <see cref="Wire"/> is the values'.</summary>
    public bool? Repeated { get; init; }
    /// <summary>
    /// On a server-write wire, the text of the member's <c>[DefaultValue]</c>: the server leaves the member out when its value equals it,
    /// so an absent member has this value.
    /// </summary>
    public string? Default { get; init; }
}

/// <summary>The XML element content of a class as XmlSerializer writes it: attributes, then child elements in order, or character content.</summary>
public sealed record XmlElementWire : WireShape
{
    public required IReadOnlyList<XmlMember> Attributes { get; init; }
    public required IReadOnlyList<XmlMember> Elements { get; init; }
    public XmlMember? Text { get; init; }
    [JsonIgnore]
    public override string Kind => "xml-element";
}

/// <summary>An item element of an <see cref="XmlItemsWire"/>.</summary>
public sealed record XmlItem
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public required WireRef Wire { get; init; }
    /// <summary>A null value is written as an item with <c>xsi:nil="true"</c>.</summary>
    public bool? Nillable { get; init; }
}

/// <summary>The XML element content of a collection as XmlSerializer writes it: one item element per value.</summary>
public sealed record XmlItemsWire : WireShape
{
    public required XmlItem Item { get; init; }
    [JsonIgnore]
    public override string Kind => "xml-items";
}

public sealed record Wire
{
    public required string Id { get; init; }
    public required WireDirection Direction { get; init; }
    public required WireShape Shape { get; init; }
}
