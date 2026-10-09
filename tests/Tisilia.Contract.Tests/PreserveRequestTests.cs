using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: under ReferenceHandler.Preserve, System.Text.Json reads plain JSON trees — objects, arrays, dictionaries, constructor
/// parameters and a polymorphic discriminator written first — without $id/$ref metadata, and refuses property names that start
/// with '$' (observed on 10.0.12). The client writes plain trees, so a structured request body is exported; structured responses,
/// which the server writes with $id/$values/$ref, stay refused.
/// </summary>
public sealed class PreserveRequestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-preserve-" + Guid.NewGuid().ToString("N"));

    public PreserveRequestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Structured_request_bodies_are_read_as_plain_trees_under_Preserve()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
        builder.Services.AddTisilia(o => o.ApiId = "preserve-requests");
        await using var app = builder.Build();
        app.MapPost("/orders", ([FromBody] PreservedOrder order) => TypedResults.Ok(
                $"{order.Customer.Name}|{string.Join(",", order.Lines.Select(l => l.Sku + "x" + l.Count))}|{string.Join(",", order.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value))}|{order.Shape switch { PreservedCircle c => "circle " + c.Radius, PreservedSquare s => "square " + s.Side, _ => "?" }}"))
            .WithTisiliaOperation("orders");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Contains("\"referenceHandling\": \"Preserve\"", export.Text!, StringComparison.Ordinal);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("orders", new { body = new Dictionary<string, string> { ["$json"] = """
                {"customer":{"name":"日本"},"lines":[{"sku":"a","count":2},{"sku":"b","count":1}],"tags":{"k":"v","x.y":"z"},"shape":{"$type":"circle","radius":2.5}}
                """ } }),
        ]);
        Assert.Equal("200 \"日本|ax2,bx1|k=v,x.y=z|circle 2.5\"", sent[0]);
    }

    [Fact]
    public async Task A_request_property_named_with_a_leading_dollar_is_diagnosed_under_Preserve()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
        builder.Services.AddTisilia(o => o.ApiId = "preserve-dollar");
        await using var app = builder.Build();
        app.MapPost("/schema", ([FromBody] DollarNamed value) => TypedResults.NoContent()).WithTisiliaOperation("schema");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Contains(export.Diagnostics.Items, d => d.Code == TisiliaCodes.ReferencePreserve && d.Message.Contains("'DollarNamed.$schema'", StringComparison.Ordinal));

        // Kestrel: System.Text.Json refuses the property, so no request with it can succeed
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/schema", new StringContent("{\"$schema\":\"s\",\"name\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task Conformance_runs_request_round_trips_under_Preserve_but_writes_structured_values_only_under_another_profile()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
        builder.Services.AddTisilia(o => o.ApiId = "preserve-suite");
        await using var app = builder.Build();
        app.MapPost("/orders", ([FromBody] PreservedOrder order) => TypedResults.Ok(order.Customer.Name)).WithTisiliaOperation("orders");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var preserve = Assert.Single(index.Profiles.Values, p => p.Options.ReferenceHandling == Tisilia.Contract.ReferenceHandling.Preserve).Id;
        var closure = Tisilia.Generator.Closure.ContractClosure.Compute(index, ["orders"]);
        var suite = Tisilia.Generator.Conformance.SuiteBuilder.Build(index, closure, new Tisilia.Generator.Conformance.SuiteOptions { Seed = 7, CasesPerCodec = 3, KeyCasesPerCodec = 2 });
        var order = ((Tisilia.Contract.JsonRequestBody)index.Operations["orders"].RequestBody).Use.CodecId;
        Assert.Contains(suite.Cases, c => c is Tisilia.Generator.Conformance.RequestRoundTripCase r && r.CodecId == order && r.ProfileId == preserve);
        // the server writes with $id/$values/$ref under Preserve: no structured response or key round trip runs there
        Assert.DoesNotContain(suite.Cases, c => c.ProfileId == preserve && c is Tisilia.Generator.Conformance.KeyRoundTripCase);
        Assert.DoesNotContain(suite.Cases, c => c is Tisilia.Generator.Conformance.ResponseRoundTripCase r && r.ProfileId == preserve && index.Types[index.Codecs[r.CodecId].TypeId].Shape is not Tisilia.Contract.PrimitiveShape);
    }

    public sealed record PreservedOrder(PreservedCustomer Customer, List<PreservedLine> Lines, Dictionary<string, string> Tags, PreservedShape Shape);

    public sealed record PreservedCustomer(string Name);

    public sealed class PreservedLine
    {
        public required string Sku { get; init; }
        public required int Count { get; init; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(PreservedCircle), "circle")]
    [JsonDerivedType(typeof(PreservedSquare), "square")]
    public abstract class PreservedShape;

    public sealed class PreservedCircle : PreservedShape { public required double Radius { get; init; } }

    public sealed class PreservedSquare : PreservedShape { public required double Side { get; init; } }

    public sealed class DollarNamed
    {
        [JsonPropertyName("$schema")] public string? Schema { get; set; }
        public string? Name { get; set; }
    }
}
