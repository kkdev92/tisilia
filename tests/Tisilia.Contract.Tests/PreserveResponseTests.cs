using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Under ReferenceHandler.Preserve the server writes reference metadata where System.Text.Json's converter can have it and the value
/// is not a value type (JsonConverter.TryHandleSerializedObjectReference, v10.0.0): <c>$id</c> first on objects and mutable dictionaries,
/// mutable collections inside <c>{"$id","$values"}</c>, and <c>{"$ref"}</c> for a value it wrote before; arrays, immutable collections,
/// structs and the JSON node types carry none. The contract marks each such server-write wire. Oracle: through Kestrel, the runtime
/// decodes what the server wrote, data that looks like metadata included, and a value written twice as one value.
/// </summary>
public sealed class PreserveResponseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-preserve-responses-" + Guid.NewGuid().ToString("N"));

    public PreserveResponseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> map, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
        configure?.Invoke(builder);
        builder.Services.AddTisilia(o => o.ApiId = "preserve-responses");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Structured_responses_decode_what_the_server_writes_under_Preserve()
    {
        await using var app = await StartAsync(a => a.MapGet("/order", () => PreservedOrder.Sample()).WithTisiliaOperation("order"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        using var client = new HttpClient();
        var wire = await client.GetStringAsync(app.Urls.Single() + "/order");
        // what the server writes: the shapes the contract marks, and data that looks like metadata where it carries none
        Assert.StartsWith("""{"$id":"1","customer":{"$id":"2","name":"日本"},"lines":{"$id":"3","$values":[{"$id":"4","sku":"a"}]},"numbers":[1,2]""", wire, StringComparison.Ordinal);
        Assert.Contains("""
            "readOnly":{"$id":"5","$values":[3]},"tags":{"$id":"6","$id":"1","x":"y"}
            """.Trim(), wire, StringComparison.Ordinal);
        Assert.Contains("""
            "fixed":{"$id":"1","a":"b"}
            """.Trim(), wire, StringComparison.Ordinal);
        Assert.Contains("""
            "point":{"x":1,"y":2},"dollar":{"$id":"struct"}
            """.Trim(), wire, StringComparison.Ordinal);

        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("order", new { })], execute: true);
        using var result = JsonDocument.Parse(results[0]);
        Assert.Equal("response", result.RootElement.GetProperty("kind").GetString());
        var data = result.RootElement.GetProperty("data");
        Assert.Equal("日本", data.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("a", data.GetProperty("lines")[0].GetProperty("sku").GetString());
        Assert.Equal("[1,2]", data.GetProperty("numbers").GetRawText());
        Assert.Equal("[3]", data.GetProperty("readOnly").GetRawText());
        Assert.Equal("""[["$id","1"],["x","y"]]""", data.GetProperty("tags").GetProperty("$entries").GetRawText());
        Assert.Equal("""[["$id","1"],["a","b"]]""", data.GetProperty("fixed").GetProperty("$entries").GetRawText());
        Assert.Equal(2, data.GetProperty("point").GetProperty("y").GetInt32());
        Assert.Equal("struct", data.GetProperty("dollar").GetProperty("$id").GetString());
        Assert.Equal("circle", data.GetProperty("shape").GetProperty("$type").GetString());
        Assert.Equal(2.5, data.GetProperty("shape").GetProperty("radius").GetDouble());
        // JSON values are the lossless AST: the node's own "$id" entry is data
        var node = data.GetProperty("node").GetProperty("$entries");
        Assert.Equal(["$id", "b"], node.EnumerateArray().Select(e => e[0].GetString()));
        Assert.Equal("node", node[0][1].GetProperty("value").GetString());
        Assert.Equal("$id", data.GetProperty("element").GetProperty("entries")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_value_the_server_writes_twice_is_decoded_as_one_value()
    {
        await using var app = await StartAsync(a => a.MapGet("/shared", () => { var line = new PreservedLine { Sku = "s" }; return new List<PreservedLine> { line, line }; }).WithTisiliaOperation("shared"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("shared", new { })], execute: true);
        using var result = JsonDocument.Parse(results[0]);
        Assert.Equal("response", result.RootElement.GetProperty("kind").GetString());
        Assert.Equal("s", result.RootElement.GetProperty("data")[0].GetProperty("sku").GetString());
        Assert.Equal("/data/0", result.RootElement.GetProperty("data")[1].GetProperty("$same").GetString());
    }

    [Fact]
    public async Task A_shared_value_of_optional_members_is_that_value_rather_than_an_empty_one()
    {
        // {"$ref":"2"} has none of the members: read as an object of optional members, it would be an empty value
        await using var app = await StartAsync(a => a.MapGet("/notes", () => { var note = new PreservedNote(); return new List<PreservedNote> { note, note }; }).WithTisiliaOperation("notes"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        using var client = new HttpClient();
        Assert.Equal("""{"$id":"1","$values":[{"$id":"2"},{"$ref":"2"}]}""", await client.GetStringAsync(app.Urls.Single() + "/notes"));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("notes", new { })], execute: true);
        using var result = JsonDocument.Parse(results[0]);
        Assert.Equal("response", result.RootElement.GetProperty("kind").GetString());
        Assert.Equal("/data/0", result.RootElement.GetProperty("data")[1].GetProperty("$same").GetString());
    }

    [Fact]
    public async Task Streams_and_events_carry_metadata_on_their_items_only()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/stream", () => Lines()).WithTisiliaOperation("stream");
            a.MapGet("/events", () => TypedResults.ServerSentEvents(Lines())).WithTisiliaOperation("events");
        });
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        using var client = new HttpClient();
        Assert.Equal("""[{"$id":"1","sku":"a"},{"$id":"2","sku":"b"}]""", await client.GetStringAsync(app.Urls.Single() + "/stream"));

        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("stream", new { }), ("events", new { })], execute: true);
        using var stream = JsonDocument.Parse(results[0]);
        Assert.Equal(["a", "b"], stream.RootElement.GetProperty("data").EnumerateArray().Select(l => l.GetProperty("sku").GetString()));
        using var events = JsonDocument.Parse(results[1]);
        Assert.Equal(["a", "b"], events.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("data").GetProperty("sku").GetString()));

        static async IAsyncEnumerable<PreservedLine> Lines()
        {
            await Task.Yield();
            yield return new PreservedLine { Sku = "a" };
            yield return new PreservedLine { Sku = "b" };
        }
    }

    [Fact]
    public async Task MVC_without_Preserve_keeps_its_own_unmarked_models()
    {
        // the minimal API preserves references and MVC does not: a type both serve is a model on each side
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/minimal-lines", () => new List<PreservedLine> { new() { Sku = "m" } }).WithTisiliaOperation("minimal-lines");
            a.MapControllers();
        }, b => b.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController())));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("minimal-lines", new { }), ("mvc-lines", new { })], execute: true);
        using var minimal = JsonDocument.Parse(results[0]);
        Assert.Equal("m", minimal.RootElement.GetProperty("data")[0].GetProperty("sku").GetString());
        using var mvc = JsonDocument.Parse(results[1]);
        Assert.Equal("c", mvc.RootElement.GetProperty("data")[0].GetProperty("sku").GetString());
    }

    private sealed class OnlyController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(PreservedLinesController).GetTypeInfo());
        }
    }
}

public sealed class PreservedOrder
{
    public PreservedCustomer Customer { get; set; } = new();
    public List<PreservedLine> Lines { get; set; } = [];
    public int[] Numbers { get; set; } = [];
    public IReadOnlyList<int> ReadOnly { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = [];
    public ImmutableSortedDictionary<string, string> Fixed { get; set; } = ImmutableSortedDictionary<string, string>.Empty;
    public PreservedPoint Point { get; set; }
    public DollarPoint Dollar { get; set; }
    public PreservedShape Shape { get; set; } = new PreservedCircle();
    public JsonObject Node { get; set; } = [];
    public JsonElement Element { get; set; }

    public static PreservedOrder Sample() => new()
    {
        Customer = new PreservedCustomer { Name = "日本" },
        Lines = [new PreservedLine { Sku = "a" }],
        Numbers = [1, 2],
        ReadOnly = new[] { 3 },
        // a key named "$id" is data: the dictionary's own $id comes first, the immutable one writes none
        Tags = new() { ["$id"] = "1", ["x"] = "y" },
        Fixed = ImmutableSortedDictionary.CreateRange(new Dictionary<string, string> { ["$id"] = "1", ["a"] = "b" }),
        Point = new PreservedPoint { X = 1, Y = 2 },
        Dollar = new DollarPoint { Id = "struct" },
        Shape = new PreservedCircle { Radius = 2.5 },
        Node = new JsonObject { ["$id"] = "node", ["b"] = new JsonArray(2) },
        Element = JsonDocument.Parse("""{"$id":"element"}""").RootElement.Clone(),
    };
}

public sealed class PreservedCustomer { public string Name { get; set; } = ""; }
public sealed class PreservedLine { public string Sku { get; set; } = ""; }
public sealed class PreservedNote { [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; } }
public struct PreservedPoint { public int X { get; set; } public int Y { get; set; } }
// a struct carries no metadata: its own "$id" property is data
public struct DollarPoint { [JsonPropertyName("$id")] public string Id { get; set; } }

[JsonPolymorphic]
[JsonDerivedType(typeof(PreservedCircle), "circle")]
public abstract class PreservedShape { }
public sealed class PreservedCircle : PreservedShape { public double Radius { get; set; } }

[ApiController]
[NonController]
public sealed class PreservedLinesController : ControllerBase
{
    [HttpGet("/mvc-lines")]
    [TisiliaOperation("mvc-lines")]
    public List<PreservedLine> Get() => [new() { Sku = "c" }];
}
