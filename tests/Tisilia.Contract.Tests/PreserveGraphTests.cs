using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Under ReferenceHandler.Preserve the server writes a value it wrote before as <c>{"$ref": id}</c>, an ancestor included, and reads a
/// request the same way where its converter registers the value before reading the members (ObjectDefaultConverter, JsonCollectionConverter,
/// JsonDictionaryConverter, v10.0.0); a type built through its constructor refuses metadata read while it waits for its arguments
/// (ObjectWithParameterizedConstructorConverter). Oracle: through Kestrel, the runtime decodes shared and cyclic responses as one value,
/// and the server receives a request's shared and cyclic values as the same instances.
/// </summary>
public sealed class PreserveGraphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-preserve-graphs-" + Guid.NewGuid().ToString("N"));

    public PreserveGraphTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<(WebApplication App, ConcurrentQueue<string> Bodies)> StartAsync(bool preserve = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        if (preserve) { builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve); }
        builder.Services.AddTisilia(o => o.ApiId = "preserve-graphs");
        var app = builder.Build();
        var bodies = new ConcurrentQueue<string>();
        // the request bodies as they arrive
        app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true)) { bodies.Enqueue(await reader.ReadToEndAsync()); }
            context.Request.Body.Position = 0;
            await next();
        });
        app.MapGet("/graph/cycle", () =>
        {
            var root = new GraphNode { Name = "root" };
            root.Next = root;
            root.Children.Add(new GraphNode { Name = "child", Next = root });
            return root;
        }).WithTisiliaOperation("graph.cycle");
        app.MapGet("/graph/shared", () =>
        {
            var node = new GraphNode { Name = "n" };
            var list = new List<GraphNode> { node };
            var map = new Dictionary<string, GraphNode> { ["k"] = node };
            return new GraphPair { First = node, Second = node, Left = list, Right = list, MapA = map, MapB = map };
        }).WithTisiliaOperation("graph.shared");
        app.MapGet("/graph/shapes", () => { var circle = new GraphCircle { Radius = 1 }; return new List<GraphShape> { circle, circle }; }).WithTisiliaOperation("graph.shapes");
        app.MapGet("/graph/shape-and-circle", () => { var circle = new GraphCircle { Radius = 1 }; return new ShapeAndCircle { Shape = circle, Circle = circle }; }).WithTisiliaOperation("graph.shape-and-circle");
        app.MapPost("/graph/pair", (GraphPair pair) => new PairReport(
            ReferenceEquals(pair.First, pair.Second), ReferenceEquals(pair.Left, pair.Right), pair.MapA is not null && ReferenceEquals(pair.MapA, pair.MapB),
            pair.ArrayA is not null && ReferenceEquals(pair.ArrayA, pair.ArrayB), pair.RecordA is not null && ReferenceEquals(pair.RecordA, pair.RecordB),
            pair.First?.Name == pair.Second?.Name && pair.RecordA == pair.RecordB)).WithTisiliaOperation("graph.pair");
        app.MapPost("/graph/node", (GraphNode node) => new NodeReport(ReferenceEquals(node, node.Next), node.Children.Count > 0 && node.Children.All(c => ReferenceEquals(c.Next, node))))
            .WithTisiliaOperation("graph.node");
        app.MapPost("/graph/holder", (RecordHolder holder) => ReferenceEquals(holder, holder.Record?.Holder)).WithTisiliaOperation("graph.holder");
        await app.StartAsync();
        return (app, bodies);
    }

    private static ExportResult Export(WebApplication app)
    {
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        return export;
    }

    private static Dictionary<string, string> Same(string path) => new() { ["$same"] = path };

    [Fact]
    public async Task A_response_of_shared_and_cyclic_values_decodes_as_one_value_each()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var export = Export(app);
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("graph.cycle", new { }), ("graph.shared", new { }), ("graph.shapes", new { })], execute: true);

        using var cycle = JsonDocument.Parse(results[0]);
        var root = cycle.RootElement.GetProperty("data");
        Assert.Equal("root", root.GetProperty("name").GetString());
        Assert.Equal("/data", root.GetProperty("next").GetProperty("$same").GetString());
        Assert.Equal("/data", root.GetProperty("children")[0].GetProperty("next").GetProperty("$same").GetString());

        using var shared = JsonDocument.Parse(results[1]);
        var pair = shared.RootElement.GetProperty("data");
        Assert.Equal("n", pair.GetProperty("first").GetProperty("name").GetString());
        Assert.Equal("/data/first", pair.GetProperty("second").GetProperty("$same").GetString());
        Assert.Equal("/data/first", pair.GetProperty("left")[0].GetProperty("$same").GetString());
        Assert.Equal("/data/left", pair.GetProperty("right").GetProperty("$same").GetString());
        Assert.Equal("/data/first", pair.GetProperty("mapA").GetProperty("$entries")[0][1].GetProperty("$same").GetString());
        Assert.Equal("/data/mapA", pair.GetProperty("mapB").GetProperty("$same").GetString());

        using var shapes = JsonDocument.Parse(results[2]);
        Assert.Equal("circle", shapes.RootElement.GetProperty("data")[0].GetProperty("$type").GetString());
        Assert.Equal("/data/0", shapes.RootElement.GetProperty("data")[1].GetProperty("$same").GetString());
    }

    [Fact]
    public async Task A_reference_to_a_value_first_written_at_a_position_of_another_type_is_refused()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var export = Export(app);
        using var client = new HttpClient();
        // the polymorphic position carries the discriminator; the derived position reads the same instance without one
        Assert.Equal("""{"$id":"1","shape":{"$id":"2","$type":"circle","radius":1},"circle":{"$ref":"2"}}""", await client.GetStringAsync(app.Urls.Single() + "/graph/shape-and-circle"));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("graph.shape-and-circle", new { })], execute: true);
        using var result = JsonDocument.Parse(results[0]);
        Assert.Equal(("codec-failure", "unsupported", "/circle"), (result.RootElement.GetProperty("kind").GetString(), result.RootElement.GetProperty("code").GetString(), result.RootElement.GetProperty("path").GetString()));
        Assert.Contains("another type", result.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_writes_a_shared_value_once_where_the_server_reads_references_and_twice_elsewhere()
    {
        var (app, bodies) = await StartAsync();
        await using var _app = app;
        var export = Export(app);
        var body = new Dictionary<string, object?>
        {
            ["first"] = new { name = "n", children = Array.Empty<object>() },
            ["second"] = Same("/body/first"),
            ["left"] = new object[] { new { name = "l", children = Array.Empty<object>() } },
            ["right"] = Same("/body/left"),
            ["mapA"] = new Dictionary<string, object> { ["$entries"] = new object[] { new object[] { "k", new { name = "m", children = Array.Empty<object>() } } } },
            ["mapB"] = Same("/body/mapA"),
            ["arrayA"] = new object[] { new { name = "a", children = Array.Empty<object>() } },
            ["arrayB"] = Same("/body/arrayA"),
            ["recordA"] = new { name = "r", back = (object?)null },
            ["recordB"] = Same("/body/recordA"),
        };
        bodies.Clear();
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("graph.pair", new { body })]);
        Assert.StartsWith("200 ", results[0], StringComparison.Ordinal);
        var report = JsonSerializer.Deserialize<PairReport>(results[0][4..], JsonSerializerOptions.Web)!;
        // a class, a list and a dictionary are one instance on the server; an array and a record (built through its constructor) are
        // written twice, as the same value
        Assert.Equal(new PairReport(SameNode: true, SameList: true, SameMap: true, SameArray: false, SameRecord: false, EqualValues: true), report);
        Assert.True(bodies.TryDequeue(out var wire));
        using var sent = JsonDocument.Parse(wire!);
        Assert.Equal("""{"$ref":"2"}""", sent.RootElement.GetProperty("second").GetRawText());
        Assert.Equal("2", sent.RootElement.GetProperty("first").GetProperty("$id").GetString());
        Assert.Equal(JsonValueKind.Array, sent.RootElement.GetProperty("arrayB").ValueKind);
        Assert.False(sent.RootElement.TryGetProperty("$id", out _)); // an id no reference names is left out
    }

    [Fact]
    public async Task A_request_value_that_contains_itself_reaches_the_server_as_a_cycle()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var export = Export(app);
        var node = new Dictionary<string, object?>
        {
            ["name"] = "root",
            ["next"] = Same("/body"),
            ["children"] = new object[] { new Dictionary<string, object?> { ["name"] = "c", ["children"] = Array.Empty<object>(), ["next"] = Same("/body") } },
        };
        var holder = new Dictionary<string, object?> { ["record"] = new Dictionary<string, object?> { ["name"] = "r", ["holder"] = Same("/body") } };
        var throughRecord = new Dictionary<string, object?> { ["record"] = new Dictionary<string, object?> { ["name"] = "r", ["holder"] = new Dictionary<string, object?> { ["record"] = Same("/body/record") } } };
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("graph.node", new { body = node }), ("graph.holder", new { body = holder }), ("graph.holder", new { body = throughRecord })]);
        Assert.StartsWith("200 ", results[0], StringComparison.Ordinal);
        Assert.Equal(new NodeReport(SelfCycle: true, ChildrenBack: true), JsonSerializer.Deserialize<NodeReport>(results[0][4..], JsonSerializerOptions.Web));
        // a cycle through a record back to a class: the class is registered before its members
        Assert.Equal("200 true", results[1]);
        // a cycle back to the record itself: the server registers it only after its members
        Assert.StartsWith("unsupported: ", results[2], StringComparison.Ordinal);
        Assert.Contains("contains itself", results[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_Preserve_a_shared_value_is_written_twice_and_a_cycle_is_refused()
    {
        var (app, bodies) = await StartAsync(preserve: false);
        await using var _app = app;
        var export = Export(app);
        var shared = new Dictionary<string, object?> { ["first"] = new { name = "n", children = Array.Empty<object>() }, ["second"] = Same("/body/first") };
        var cyclic = new Dictionary<string, object?> { ["name"] = "root", ["next"] = Same("/body"), ["children"] = Array.Empty<object>() };
        bodies.Clear();
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("graph.pair", new { body = shared }), ("graph.node", new { body = cyclic })]);
        Assert.StartsWith("200 ", results[0], StringComparison.Ordinal);
        Assert.False(JsonSerializer.Deserialize<PairReport>(results[0][4..], JsonSerializerOptions.Web)!.SameNode);
        Assert.True(bodies.TryDequeue(out var wire));
        Assert.DoesNotContain("$", wire!, StringComparison.Ordinal);
        Assert.StartsWith("unsupported: ", results[1], StringComparison.Ordinal);
        Assert.Contains("contains itself", results[1], StringComparison.Ordinal);
    }

    public sealed class GraphNode
    {
        public string Name { get; set; } = "";
        public GraphNode? Next { get; set; }
        public List<GraphNode> Children { get; set; } = [];
    }

    public sealed record GraphRecord(string Name, GraphNode? Back);

    public sealed class GraphPair
    {
        public GraphNode? First { get; set; }
        public GraphNode? Second { get; set; }
        public List<GraphNode>? Left { get; set; }
        public List<GraphNode>? Right { get; set; }
        public Dictionary<string, GraphNode>? MapA { get; set; }
        public Dictionary<string, GraphNode>? MapB { get; set; }
        public GraphNode[]? ArrayA { get; set; }
        public GraphNode[]? ArrayB { get; set; }
        public GraphRecord? RecordA { get; set; }
        public GraphRecord? RecordB { get; set; }
    }

    public sealed record PairReport(bool SameNode, bool SameList, bool SameMap, bool SameArray, bool SameRecord, bool EqualValues);

    public sealed record NodeReport(bool SelfCycle, bool ChildrenBack);

    public sealed class RecordHolder
    {
        public HeldRecord? Record { get; set; }
    }

    public sealed record HeldRecord(string Name, RecordHolder? Holder);

    [JsonPolymorphic, JsonDerivedType(typeof(GraphCircle), "circle")]
    public abstract class GraphShape;

    public sealed class GraphCircle : GraphShape
    {
        public double Radius { get; set; }
    }

    public sealed class ShapeAndCircle
    {
        public GraphShape? Shape { get; set; }
        public GraphCircle? Circle { get; set; }
    }
}
