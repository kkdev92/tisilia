using System.ComponentModel;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Xunit;
using ExportXmlBody = Tisilia.AspNetCore.Export.XmlBody;

namespace Tisilia.Contract.Tests;

/// <summary>
/// XmlSerializer's own mapping (read from System.Private.Xml's model) becomes XML models and wires as the formatters write and read
/// them; a form the client does not write or read is not described, with the reason. Oracle: the mapping XmlSerializer itself makes
/// for each type (its names, namespaces, order, nillable, default and Specified members), checked against what it writes in XmlBodyTests.
/// </summary>
public sealed class XmlMappingTests
{
    private static (ExportXmlBody? Body, string? Reason, ContractBuilder Builder) Map(Type type, WireDirection direction)
    {
        var builder = new ContractBuilder("t");
        var body = new XmlTypeMapper(builder, new TisiliaOptions { ApiId = "t" }).Map(type, direction, out var reason);
        return (body, reason, builder);
    }

    [Theory]
    [InlineData(typeof(XmlZoo), "derived types ([XmlInclude])")]
    [InlineData(typeof(XmlChoice), "a choice of elements")]
    [InlineData(typeof(XmlAnyHolder), "Rest: [XmlAnyElement]")]
    [InlineData(typeof(XmlMixedHolder), "character content of a collection")]
    [InlineData(typeof(XmlListAttributeHolder), "an attribute that lists values")]
    [InlineData(typeof(XmlTimeHolder), "a time of day with an offset")]
    [InlineData(typeof(XmlTokenHolder), "white space XmlSerializer collapses")]
    [InlineData(typeof(XmlSelfWritingHolder), "serializes itself (IXmlSerializable)")]
    [InlineData(typeof(XmlNodeHolder), "an XML node")]
    [InlineData(typeof(XmlObjectHolder), "an object member")]
    [InlineData(typeof(XmlNullableDefaultHolder), "[DefaultValue] on a member that can be null")]
    [InlineData(typeof(XmlClashHolder), "more than one member is named 'id'")]
    [InlineData(typeof(Dictionary<string, int>), "could not be read")]
    public void A_form_the_client_does_not_write_has_a_reason_and_adds_nothing(Type type, string reason)
    {
        foreach (var direction in new[] { WireDirection.ServerRead, WireDirection.ServerWrite })
        {
            var (body, why, builder) = Map(type, direction);
            Assert.Null(body);
            Assert.Contains(reason, why, StringComparison.Ordinal);
            // checked before anything is added: the contract has no partial XML model
            var contract = builder.BuildJson();
            Assert.DoesNotContain(contract["types"]!.AsArray(), t => t!["id"]!.GetValue<string>().StartsWith("t.xml.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Names_namespaces_order_and_writer_conditions_are_XmlSerializers()
    {
        var (body, reason, builder) = Map(typeof(XmlOrder), WireDirection.ServerWrite);
        Assert.Null(reason);
        Assert.Equal(("order", "urn:shop"), (body!.Root.Name, body.Root.Namespace));
        var wire = (XmlElementWire)builder.GetWire(builder.GetCodec(body.Use.CodecId)!.Capabilities.Response!.Wire.WireId)!.Shape;
        // attributes first, then elements in declaration order, as the writer writes them (ReflectionXmlSerializationWriter.WriteStructMethod)
        Assert.Equal(["id", "Channel"], wire.Attributes.Select(a => a.Name));
        Assert.Equal(["Customer", "Note", "Priority", "Total", "Ratio", "PlacedAt", "Updated", "Due", "Slot", "Window", "Ref", "Blob", "Grade", "Color", "Access", "Lines", "tag", "Codes", "Extra", "Retries", "Discount", "Points", "Price"], wire.Elements.Select(e => e.Name));
        XmlMember Member(string property) => wire.Attributes.Concat(wire.Elements).Single(m => m.Property == property);
        // attributes are unqualified, elements in the root's namespace unless they name another
        Assert.Null(Member("id").Namespace);
        Assert.Equal("urn:shop", Member("Customer").Namespace);
        Assert.Equal("urn:ext", Member("Extra").Namespace);
        Assert.True(Member("Note").Nillable);
        Assert.True(Member("Priority").Nillable); // Nullable<T> elements are nillable
        Assert.Null(Member("Customer").Nillable);
        Assert.True(Member("tag").Repeated);
        Assert.Equal("5", Member("Retries").Default);
        Assert.Equal(Presence.Optional, Member("Discount").Presence); // XSpecified
        Assert.Equal(Presence.Optional, Member("Points").Presence); // ShouldSerializePoints()
        Assert.Equal(Presence.Required, Member("Total").Presence);
        var lines = (XmlItemsWire)builder.GetWire(builder.GetCodec(builder.GetType(body.Use.TypeId) is { Shape: ObjectShape shape } ? shape.Properties.Single(p => p.Name == "Lines").Use.CodecId : "")!.Capabilities.Response!.Wire.WireId)!.Shape;
        Assert.Equal(("line", "urn:shop", true), (lines.Item.Name, lines.Item.Namespace, lines.Item.Nillable));
        var color = (XmlTextWire)builder.GetWire(Member("Color").Wire.WireId)!.Shape;
        Assert.Equal(["Red", "Green", "bleu"], color.Names!.Select(n => n.Name));
    }

    [Fact]
    public void The_server_reads_every_member_as_optional_and_null_only_as_xsi_nil()
    {
        var (body, _, builder) = Map(typeof(XmlOrder), WireDirection.ServerRead);
        var model = (ObjectShape)builder.GetType(body!.Use.TypeId)!.Shape;
        Assert.All(model.Properties, p => Assert.Equal(Presence.Optional, p.Presence));
        Assert.Equal(["Note", "Priority"], model.Properties.Where(p => p.Use.SemanticNullable).Select(p => p.Name));
    }
}

public sealed class XmlAnyHolder
{
    [XmlAnyElement] public XmlElement[]? Rest { get; set; }
}

public sealed class XmlMixedHolder
{
    [XmlText] public string[]? Text { get; set; }
    public string? Child { get; set; }
}

public sealed class XmlListAttributeHolder
{
    [XmlAttribute] public int[]? Values { get; set; }
}

public sealed class XmlTimeHolder
{
    [XmlElement(DataType = "time")] public DateTime At { get; set; }
}

public sealed class XmlTokenHolder
{
    [XmlElement(DataType = "token")] public string? Token { get; set; }
}

public sealed class XmlSelfWritingHolder
{
    public XmlSelfWriting? Value { get; set; }
}

public sealed class XmlSelfWriting : IXmlSerializable
{
    public XmlSchema? GetSchema() => null;

    public void ReadXml(XmlReader reader) => reader.Skip();

    public void WriteXml(XmlWriter writer)
    {
    }
}

public sealed class XmlNodeHolder
{
    public XmlElement? Node { get; set; }
}

public sealed class XmlObjectHolder
{
    public object? Value { get; set; }
}

public sealed class XmlNullableDefaultHolder
{
    [DefaultValue("x")] public string? Name { get; set; }
}

public sealed class XmlClashHolder
{
    [XmlAttribute("id")] public int A { get; set; }
    [XmlElement("id")] public int B { get; set; }
}
