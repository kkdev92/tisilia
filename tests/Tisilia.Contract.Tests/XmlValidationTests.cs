using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// SV55 and the XML rules of SV10/SV11/SV29: an exported contract of MVC XmlSerializer bodies (<see cref="XmlOrdersController"/>) is
/// valid, and each change that would make its XML ambiguous, unwritable or undecodable is refused with its rule.
/// </summary>
public sealed class XmlValidationTests
{
    private static readonly Lazy<Task<string>> Exported = new(async () =>
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController(typeof(XmlOrdersController))));
        builder.Services.AddTisilia(o => o.ApiId = "xml-rules");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        return export.Text!;
    });

    private static async Task<JsonObject> ContractAsync() => JsonNode.Parse(await Exported.Value)!.AsObject();

    private static DiagnosticBag Validate(JsonObject root)
    {
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        Assert.NotNull(loaded);
        SemanticValidator.Validate(loaded, bag, verifyHashes: false);
        return bag;
    }

    private static IEnumerable<JsonObject> Wires(JsonObject root) => root["wires"]!.AsArray().Select(w => w!.AsObject());

    private static JsonObject Shape(JsonObject wire) => wire["shape"]!.AsObject();

    /// <summary>The XML content wire of the order in one direction (the class with the "id" attribute).</summary>
    private static JsonObject OrderWire(JsonObject root, string direction) => Shape(Wires(root).Single(w => w["direction"]!.GetValue<string>() == direction && Shape(w)["kind"]!.GetValue<string>() == "xml-element"
        && Shape(w)["attributes"]!.AsArray().Any(a => a!["name"]?.GetValue<string>() == "id")));

    private static JsonObject Member(JsonObject element, string property) => element["attributes"]!.AsArray().Concat(element["elements"]!.AsArray())
        .Select(m => m!.AsObject()).Single(m => m["property"]!.GetValue<string>() == property);

    private static JsonObject Codec(JsonObject root, string id) => root["codecs"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["id"]!.GetValue<string>() == id);

    private static JsonObject Model(JsonObject root, string id) => root["types"]!.AsArray().Select(t => t!.AsObject()).Single(t => t["id"]!.GetValue<string>() == id);

    private static JsonObject Echo(JsonObject root) => root["operations"]!.AsArray().Select(o => o!.AsObject()).Single(o => o["id"]!.GetValue<string>() == "xml.orders.echo");

    [Fact]
    public async Task The_exported_XML_contract_is_valid()
    {
        var bag = Validate(await ContractAsync());
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
    }

    [Theory]
    [InlineData("json-wire-in-xml", "SV55", "references JSON wire")]
    [InlineData("xml-wire-in-json-codec", "SV55", "is not bound to the XmlSerializer but converts through XML wire")]
    [InlineData("grammar", "SV55", "is not an XML text grammar")]
    [InlineData("enum-without-names", "SV55", "lists the XML name of each constant")]
    [InlineData("enum-other-values", "SV55", "names other values")]
    [InlineData("element-name", "SV55", "is not an XML local name")]
    [InlineData("duplicate-element", "SV10", "appears more than once")]
    [InlineData("xsi-attribute", "SV55", "is reserved by XML")]
    [InlineData("structured-attribute", "SV55", "carries text")]
    [InlineData("repeated-attribute", "SV55", "only an element member repeats")]
    [InlineData("default-on-request", "SV55", "a default value is what the server leaves out")]
    [InlineData("mixed-content", "SV55", "mixed content")]
    [InlineData("unknown-property", "SV11", "has no domain property")]
    [InlineData("nillable-not-nullable", "SV55", "may be written with xsi:nil but its value is not nullable")]
    [InlineData("nullable-without-nil", "SV55", "has no XML form of null")]
    [InlineData("absent-without-value", "SV11", "with no value for an absent member")]
    [InlineData("implementation", "SV55", "with the builtin xml-element encoder")]
    [InlineData("json-codec-body", "SV29", "uses an XML codec with the request capability")]
    [InlineData("nullable-request", "SV29", "an XML request body is never null")]
    [InlineData("browser-safe", "SV29", "XML responses are server-only")]
    [InlineData("media-type", "SV29", "must be application/xml, text/xml or a concrete application/…+xml type")]
    [InlineData("root-name", "SV55", "root element '1order' is not an XML local name")]
    public async Task A_change_that_breaks_the_XML_is_refused(string change, string rule, string expected)
    {
        var root = await ContractAsync();
        var request = OrderWire(root, "server-read");
        var response = OrderWire(root, "server-write");
        var echo = Echo(root);
        switch (change)
        {
            case "json-wire-in-xml":
                Member(request, "Customer")["wire"]!["wireId"] = "std.string.read";
                break;
            case "xml-wire-in-json-codec":
                Codec(root, "std.string.codec")["capabilities"]!["request"]!["wire"]!["wireId"] = Member(request, "Customer")["wire"]!["wireId"]!.GetValue<string>();
                break;
            case "grammar":
                Shape(Wires(root).First(w => Shape(w)["grammarId"]?.GetValue<string>() == "tisilia.grammar.xml-string@0.1"))["grammarId"] = "tisilia.grammar.string@0.1";
                break;
            case "enum-without-names":
                Shape(Wires(root).First(w => Shape(w)["names"] is not null)).Remove("names");
                break;
            case "enum-other-values":
                Shape(Wires(root).First(w => Shape(w)["names"] is not null))["names"]![0]!["value"] = "99";
                break;
            case "element-name":
                Member(request, "Customer")["name"] = "a b";
                break;
            case "duplicate-element":
                Member(request, "Customer")["name"] = "Total";
                break;
            case "xsi-attribute":
                Member(request, "Channel")["namespace"] = "http://www.w3.org/2001/XMLSchema-instance";
                break;
            case "structured-attribute":
                Member(request, "Channel")["wire"]!["wireId"] = Member(request, "Extra")["wire"]!["wireId"]!.GetValue<string>();
                break;
            case "repeated-attribute":
                Member(request, "Channel")["repeated"] = true;
                break;
            case "default-on-request":
                Member(request, "Retries")["default"] = "5";
                break;
            case "mixed-content":
            {
                var price = Shape(Wires(root).First(w => Shape(w)["text"] is not null));
                price["elements"]!.AsArray().Add(Member(request, "Customer").DeepClone());
                break;
            }

            case "unknown-property":
                Member(request, "Customer")["property"] = "Client";
                break;
            case "nillable-not-nullable":
                Member(request, "Total")["nillable"] = true;
                break;
            case "nullable-without-nil":
            {
                var model = Model(root, Wires(root).Single(w => ReferenceEquals(Shape(w), request))["id"]!.GetValue<string>()[..^".read".Length]);
                model["shape"]!["properties"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Customer")!["use"]!["semanticNullable"] = true;
                break;
            }

            case "absent-without-value":
                Member(response, "Total")["presence"] = "optional";
                break;
            case "implementation":
            {
                var codecId = Wires(root).Single(w => ReferenceEquals(Shape(w), request))["id"]!.GetValue<string>()[..^".read".Length] + ".codec";
                Codec(root, codecId)["capabilities"]!["request"]!["implementation"]!["id"] = "tisilia.codec.xml-text.encode@0.1";
                break;
            }

            case "json-codec-body":
                echo["requestBody"]!["use"] = new JsonObject { ["typeId"] = "std.string", ["codecId"] = "std.string.codec", ["semanticNullable"] = false };
                break;
            case "nullable-request":
                echo["requestBody"]!["use"]!["semanticNullable"] = true;
                break;
            case "browser-safe":
                echo["responses"]![0]!["hydration"] = "browser-safe";
                break;
            case "media-type":
                echo["requestBody"]!["mediaType"] = "application/json";
                break;
            case "root-name":
                echo["requestBody"]!["root"]!["name"] = "1order";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        var bag = Validate(root);
        Assert.Contains(bag.Items, d => d.Severity == DiagnosticSeverity.Error && d.Rule == rule && d.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_conformance_suite_lists_XML_round_trips_as_not_observed()
    {
        // the runner observes System.Text.Json only: an XML codec gets domain validation, and its round trips are listed, not left out
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(await Exported.Value, bag);
        var index = SemanticValidator.Validate(loaded!, bag)!;
        var closure = Tisilia.Generator.Closure.ContractClosure.Compute(index, ["xml.orders.echo"]);
        Assert.Empty(closure.JsonCodecIds);
        var suite = Tisilia.Generator.Conformance.SuiteBuilder.Build(index, closure, new Tisilia.Generator.Conformance.SuiteOptions { Seed = 1, CasesPerCodec = 2 });
        var xmlCodecs = index.Codecs.Values.Where(c => c.BindingId == Builtins.BindingXmlSerializer && closure.RegistryRefs.Any(r => r.Id == c.Id)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        // the closure reaches the codecs of both bodies, and through them every XML codec they use
        var echo = index.Operations["xml.orders.echo"];
        Assert.Contains(Assert.IsType<Tisilia.Contract.XmlRequestBody>(echo.RequestBody).Use.CodecId, xmlCodecs);
        Assert.Contains(Assert.IsType<Tisilia.Contract.XmlResponseBody>(Assert.Single(echo.Responses).Body).Use.CodecId, xmlCodecs);
        Assert.All(suite.Cases, c => Assert.IsType<Tisilia.Generator.Conformance.DomainValidationCase>(c));
        Assert.Contains(suite.Cases, c => c is Tisilia.Generator.Conformance.DomainValidationCase { ExpectValid: true } v && xmlCodecs.Contains(v.CodecId));
        Assert.All(xmlCodecs, id => Assert.Contains(suite.NotApplicable, n => n.CodecId == id && n.Reason == "xml-not-observed"));
    }

    [Fact]
    public async Task Another_XML_name_is_a_breaking_change_of_the_body()
    {
        var oldRoot = await ContractAsync();
        Tisilia.Generator.Diff.DiffResult Compare(Action<JsonObject> change)
        {
            var newRoot = oldRoot.DeepClone().AsObject();
            change(newRoot);
            var paths = new[] { oldRoot, newRoot }.Select(root =>
            {
                var copy = root.DeepClone().AsObject();
                copy["semanticHash"] = Tisilia.Generator.Canonical.TisiliaHash.SemanticHash(copy);
                var path = Path.Combine(Path.GetTempPath(), "tisilia-xml-diff-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, copy.ToJsonString());
                return path;
            }).ToArray();
            try
            {
                var bag = new DiagnosticBag();
                var result = Tisilia.Generator.Diff.ContractDiff.Compare(paths[0], paths[1], bag);
                Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
                return result!;
            }
            finally
            {
                foreach (var path in paths)
                {
                    File.Delete(path);
                }
            }
        }

        Assert.Empty(Compare(_ => { }).Breaking);
        Assert.Contains(Compare(root => Member(OrderWire(root, "server-read"), "Customer")["name"] = "Client").Breaking, d => d.Kind == "body-changed" && d.Id == "xml.orders.echo");
        Assert.Contains(Compare(root => Member(OrderWire(root, "server-write"), "Customer")["namespace"] = "urn:other").Breaking, d => d.Kind == "response-changed" && d.Id == "xml.orders.echo");
        Assert.Contains(Compare(root => Echo(root)["requestBody"]!["root"]!["name"] = "Order").Breaking, d => d.Kind == "body-changed");
    }

    private sealed class OnlyController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}
