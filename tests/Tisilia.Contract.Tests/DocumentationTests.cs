using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Documentation in the contract: XML comment ids as the compiler writes them, comments as Markdown, attributes as
/// text and notes, the member list of a type, and the references the validator checks (SV02/SV03).
/// </summary>
public class DocumentationTests
{
    private static readonly Dictionary<string, XElement> CompilerIds = XDocument.Load(Path.ChangeExtension(typeof(DocumentationTests).Assembly.Location, ".xml"))
        .Root!.Element("members")!.Elements("member").ToDictionary(m => m.Attribute("name")!.Value, StringComparer.Ordinal);

    public static TheoryData<string> Members() =>
    [
        "type",
        "nested type", "generic type", "generic nested type", "property", "field", "enum field", "constructor",
        "method without parameters", "method with by-reference and array parameters", "method with a multi-dimensional array",
        "method with a constructed generic", "method with a nullable value", "generic method", "method of a generic type",
        "record", "indexer",
    ];

    private static MemberInfo Member(string name)
    {
        var widget = typeof(DocSamples.Widget);
        return name switch
        {
            "nested type" => typeof(DocSamples.Widget.Nested),
            "generic type" => typeof(DocSamples.Box<>),
            "generic nested type" => typeof(DocSamples.Box<>.Inner<,>),
            "property" => widget.GetProperty(nameof(DocSamples.Widget.Name))!,
            "field" => widget.GetField(nameof(DocSamples.Widget.Count))!,
            "enum field" => typeof(DocSamples.Color).GetField(nameof(DocSamples.Color.Red))!,
            "constructor" => widget.GetConstructor([typeof(string)])!,
            "method without parameters" => widget.GetMethod(nameof(DocSamples.Widget.Ping))!,
            "method with by-reference and array parameters" => widget.GetMethod(nameof(DocSamples.Widget.Fill))!,
            "method with a multi-dimensional array" => widget.GetMethod(nameof(DocSamples.Widget.Grid))!,
            "method with a constructed generic" => widget.GetMethod(nameof(DocSamples.Widget.Sum))!,
            "method with a nullable value" => widget.GetMethod(nameof(DocSamples.Widget.Maybe))!,
            "generic method" => widget.GetMethod(nameof(DocSamples.Widget.Pick))!,
            "method of a generic type" => typeof(DocSamples.Box<>).GetMethod(nameof(DocSamples.Box<int>.Put))!,
            "record" => typeof(DocSamples.Point),
            "indexer" => widget.GetProperty("Item")!,
            _ => typeof(DocSamples.Widget),
        };
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void Ids_are_the_ones_the_compiler_writes(string name)
    {
        // C# standard §D.4.2; the compiler's own documentation file of this assembly is the reference
        var id = DocumentationId.Of(Member(name));
        Assert.True(CompilerIds.ContainsKey(id), $"'{id}' is not in the compiler's file; it has: {string.Join(", ", CompilerIds.Keys.Where(k => k.Contains("DocSamples", StringComparison.Ordinal)).Order(StringComparer.Ordinal))}");
    }

    [Fact]
    public void Comments_read_as_markdown()
    {
        var md = XmlDocumentation.Markdown(XElement.Parse("""
            <summary>
              Returns the <c>id</c> of a <see cref="T:Acme.Widget"/>, or <see langword="null"/>; see
              <see href="https://example.com/x">the guide</see> and <paramref name="name"/>. <b>Bold</b> and <i>italic</i>.
              <para>Second paragraph.</para>
              <list type="bullet"><item><term>A</term><description>first</description></item><item><description>second</description></item></list>
              <list type="number"><item><description>one</description></item><item><description>two</description></item></list>
              <code>
                var x = 1;
                  if (x) { }
              </code>
            </summary>
            """));
        Assert.Equal(
            "Returns the `id` of a `Widget`, or `null`; see [the guide](https://example.com/x) and `name`. **Bold** and *italic*.\n\n"
            + "Second paragraph.\n\n- **A** — first\n- second\n\n1. one\n2. two\n\n```\nvar x = 1;\n  if (x) { }\n```",
            md);
        Assert.Null(XmlDocumentation.Markdown(XElement.Parse("<summary>   </summary>")));
    }

    [Fact]
    public void Types_list_their_documented_members_with_notes()
    {
        var docs = new ApiDocumentation();
        var type = typeof(DocSamples.Order);
        docs.Types([new DocumentedType("api.Order.request", type, [
            new DocumentedMember("id", typeof(Guid), type.GetProperty(nameof(DocSamples.Order.Id)), null),
            new DocumentedMember("quantity", typeof(int), type.GetProperty(nameof(DocSamples.Order.Quantity)), null),
            new DocumentedMember("code", typeof(string), type.GetProperty(nameof(DocSamples.Order.Code)), null),
            new DocumentedMember("tags", typeof(string[]), type.GetProperty(nameof(DocSamples.Order.Tags)), null),
            new DocumentedMember("kind", typeof(string), type.GetProperty(nameof(DocSamples.Order.Kind)), null),
            new DocumentedMember("note", typeof(string), type.GetProperty("Note"), null),
            new DocumentedMember("plain", typeof(string), type.GetProperty(nameof(DocSamples.Order.Plain)), null),
        ])]);
        var (targetId, summary, description) = Assert.Single(docs.Entries);
        Assert.Equal("api.Order.request", targetId);
        Assert.Equal("An order, as a client sends it.", summary);
        var (text, members) = DocumentationText.Split(description);
        Assert.Equal("Orders are kept for a year.", text);
        Assert.Equal("The order's id.", members["id"]);
        Assert.Equal("How many. 1 ≤ value < 100.", members["quantity"]);
        Assert.Equal("Length 2–8 characters. Pattern: `^[A-Z]+$`.", members["code"]);
        Assert.Equal("At least 1 item. At most 5 items.", members["tags"]);
        Assert.Equal("Allowed: `a`, `b`. Default: `a`.", members["kind"]);
        Assert.Equal("**Deprecated.** Use code. Free text.", members["note"]);
        Assert.False(members.ContainsKey("plain"), "a member with nothing to say is not listed");
    }

    [Fact]
    public void A_record_documents_its_positional_members_with_param_comments()
    {
        var docs = new ApiDocumentation();
        var type = typeof(DocSamples.Point);
        var constructor = type.GetConstructors().Single();
        docs.Types([new DocumentedType("api.Point.response", type, [
            new DocumentedMember("x", typeof(int), type.GetProperty(nameof(DocSamples.Point.X)), constructor.GetParameters()[0]),
            new DocumentedMember("y", typeof(int), type.GetProperty(nameof(DocSamples.Point.Y)), constructor.GetParameters()[1]),
        ])]);
        var (_, summary, description) = Assert.Single(docs.Entries);
        Assert.Equal("A point.", summary);
        var (_, members) = DocumentationText.Split(description);
        Assert.Equal("Across.", members["x"]);
        // [Description] on the parameter (without property:) still counts
        Assert.Equal("Down.", members["y"]);
    }

    [Fact]
    public void Values_in_notes_read_as_the_member_writes_them()
    {
        // a body member's values as System.Text.Json writes the member (an enum by its wire name, as OpenAPI writes [DefaultValue]); a
        // route, query or header value is bound from the C# name (Enum.TryParse), which the notes of parameters keep
        var shade = typeof(DocSamples.Order).GetProperty(nameof(DocSamples.Order.Shade));
        var named = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        List<string> Notes(JsonSerializerOptions? options) =>
            ApiDocumentation.Notes([shade], typeof(DocSamples.Color), withDefault: true, v => ApiDocumentation.WireLiteral(v, typeof(DocSamples.Color), options));
        Assert.Equal(["Not allowed: `red`.", "Default: `blue`."], Notes(named));
        Assert.Equal(["Not allowed: `0`.", "Default: `1`."], Notes(new JsonSerializerOptions()));
        Assert.Equal(["Not allowed: `Red`.", "Default: `Blue`."], Notes(null));
        Assert.Equal(["Not allowed: `Red`."], ApiDocumentation.Notes([shade], typeof(DocSamples.Color), withDefault: false));

        // [DefaultValue(1)] on an enum member is the member whose value is 1; a string loses its quotes, an empty one keeps them; a
        // value the options cannot write (System.Type) is shown as its C# literal
        Assert.Equal("blue", ApiDocumentation.WireLiteral(1, typeof(DocSamples.Color?), named));
        Assert.Equal("未定", ApiDocumentation.WireLiteral("未定", typeof(string), JsonSerializerOptions.Default));
        Assert.Equal("\"\"", ApiDocumentation.WireLiteral("", typeof(string), JsonSerializerOptions.Default));
        Assert.Equal("2.5", ApiDocumentation.WireLiteral(2.5, typeof(double), JsonSerializerOptions.Default));
        Assert.Equal("null", ApiDocumentation.WireLiteral(null, typeof(string), JsonSerializerOptions.Default));
        Assert.Equal("System.Object", ApiDocumentation.WireLiteral(typeof(object), typeof(Type), JsonSerializerOptions.Default));
    }

    [Fact]
    public void A_parameter_bound_from_an_obsolete_property_is_deprecated()
    {
        // MVC lists each property of a [FromQuery] model as a parameter of its own, described by the property's metadata; [Obsolete] on
        // the property makes the parameter deprecated (its description starts with the mark), before its notes
        var docs = new ApiDocumentation();
        var metadata = new EmptyModelMetadataProvider().GetMetadataForProperty(typeof(DocSamples.Filter), "Page");
        docs.Parameter("api.list.page", new ApiParameterDescription { Name = "page", ModelMetadata = metadata }, typeof(int));
        var (targetId, summary, description) = Assert.Single(docs.Entries);
        Assert.Equal(("api.list.page", "The page."), (targetId, summary));
        Assert.Equal("**Deprecated.** Use the cursor. 1 ≤ value ≤ 10.", description);
        Assert.True(DocumentationText.IsDeprecated(description));
        Assert.False(DocumentationText.IsDeprecated("Not **Deprecated.**"));
    }

    [Fact]
    public void Enum_members_and_inherited_comments()
    {
        var docs = new ApiDocumentation();
        docs.Types([
            new DocumentedType("api.Color.number", typeof(DocSamples.Color), typeof(DocSamples.Color).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => new DocumentedMember(f.Name, typeof(DocSamples.Color), f, null)).ToList()),
            new DocumentedType("api.Derived.response", typeof(DocSamples.Derived), []),
        ]);
        var entries = docs.Entries.ToDictionary(e => e.TargetId);
        var (_, members) = DocumentationText.Split(entries["api.Color.number"].Description);
        Assert.Equal("The colour of blood.", members["Red"]);
        Assert.Equal("Sky.", members["Blue"]);
        Assert.Equal("The base type's comment.", entries["api.Derived.response"].Summary);
    }

    [Fact]
    public void Member_lines_read_back()
    {
        var description = "Free text.\n\n" + DocumentationText.MembersHeading + "\n\n" + DocumentationText.MemberLine("a", "First.") + "\n" + DocumentationText.MemberLine("b-c", "Second — with a dash.");
        var (text, members) = DocumentationText.Split(description);
        Assert.Equal("Free text.", text);
        Assert.Equal(["a", "b-c"], members.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Second — with a dash.", members["b-c"]);
        Assert.Equal(("Only text.", 0), (DocumentationText.Split("Only text.").Text, DocumentationText.Split("Only text.").Members.Count));
        // the heading used for text of its own: the description stays whole
        var own = "Intro.\n\n" + DocumentationText.MembersHeading + "\n\nAre listed elsewhere.\n" + DocumentationText.MemberLine("a", "First.");
        Assert.Equal((own, 0), (DocumentationText.Split(own).Text, DocumentationText.Split(own).Members.Count));
    }

    [Fact]
    public void Entries_must_target_ids_of_the_contract_once()
    {
        var root = SampleContracts.UsersApiJson();
        root["documentation"] = new JsonArray(
            new JsonObject { ["targetId"] = "users.get", ["summary"] = "ok", ["description"] = "" },
            new JsonObject { ["targetId"] = "users.gett", ["summary"] = "typo", ["description"] = "" },
            new JsonObject { ["targetId"] = "users.get", ["summary"] = "twice", ["description"] = "" });
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        Assert.NotNull(loaded);
        SemanticValidator.Validate(loaded!, bag, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Rule == "SV03" && d.Path == "/documentation/1/targetId" && d.Message.Contains("users.gett", StringComparison.Ordinal));
        Assert.Contains(bag.Items, d => d.Rule == "SV02" && d.Path == "/documentation/2/targetId");
        Assert.Equal(2, bag.Items.Count(d => d.Severity == DiagnosticSeverity.Error));
    }
}

