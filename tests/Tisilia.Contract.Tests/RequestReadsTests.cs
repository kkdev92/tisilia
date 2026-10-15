using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// What the request pipeline reads outside an endpoint's parameters — a middleware or filter that reads an idempotency key — is declared
/// on the endpoint or its group (<c>WithTisiliaRequestReads</c>) and becomes typed parameters; headers a caller passes through without a
/// type are allowed the same way (<c>WithTisiliaRequestHeaders</c>).
/// </summary>
public sealed class RequestReadsTests : IDisposable
{
    private const string Key = "550e8400-e29b-41d4-a716-446655440000";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-request-reads-" + Guid.NewGuid().ToString("N"));

    public RequestReadsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Pipeline_reads_are_typed_parameters_and_passed_headers_are_allowed()
    {
        await using var app = await StartAsync(group => group
            .WithTisiliaRequestReads(reads => reads.Header<Guid>("Idempotency-Key").Header<string>("If-Match", optional: true))
            .WithTisiliaRequestHeaders("Accept-Language", "X-Request-Id"));
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;

        foreach (var operation in new[] { "orders.create", "orders.list" })
        {
            var op = index.Operations[operation];
            var key = Assert.Single(op.Parameters, p => p.Name == "Idempotency-Key");
            Assert.Equal((ParameterLocation.Header, Presence.Required, "std.guid"), (key.Location, key.Presence, key.Use.TypeId));
            Assert.Equal(Presence.Optional, Assert.Single(op.Parameters, p => p.Name == "If-Match").Presence);
            Assert.Equal(Builtins.AcceptServerParsed, index.Binders[key.BinderId].ServerAcceptanceId);
            Assert.Equal(["accept-language", "x-request-id"], op.Security.RequestHeaderAllowlist);
        }

        // an endpoint outside the group declares nothing
        Assert.Empty(index.Operations["health"].Parameters);
        Assert.Empty(index.Operations["health"].Security.RequestHeaderAllowlist);
    }

    [Fact]
    public async Task A_declared_header_travels_with_its_type_and_a_required_one_is_asked_for()
    {
        await using var app = await StartAsync(group => group.WithTisiliaRequestReads(reads => reads.Header<Guid>("Idempotency-Key")));
        var export = Export(app);
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("orders.create", new Dictionary<string, object> { ["Idempotency-Key"] = Key }), ("orders.create", new Dictionary<string, object>())], execute: true);

        using var sent = JsonDocument.Parse(results[0]);
        Assert.Equal(Key, sent.RootElement.GetProperty("data").GetString());
        using var missing = JsonDocument.Parse(results[1]);
        Assert.Equal(("transport-failure", "request-encoding"), (missing.RootElement.GetProperty("kind").GetString(), missing.RootElement.GetProperty("reason").GetString()));
        Assert.Contains("Idempotency-Key", missing.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_that_is_not_a_scalar_or_repeats_a_parameter_is_reported()
    {
        await using var app = await StartAsync(group => group.WithTisiliaRequestReads(reads => reads.Header<Uri[][]>("X-Links").Query<int>("page")));
        var export = Export(app);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Message.Contains("'X-Links' of the request pipeline (WithTisiliaRequestReads)", StringComparison.Ordinal));
        // orders.list binds page itself
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Message.Contains("'page' of the request pipeline (WithTisiliaRequestReads) is also another parameter", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new RouteGroupBuilderStub().WithTisiliaRequestReads(reads => reads.NotFromRequest()));
        Assert.Throws<ArgumentException>(() => new RouteGroupBuilderStub().WithTisiliaRequestReads(reads => reads.Query<string>(RequestReads.ModelName)));
    }

    [Theory]
    [InlineData("Authorization", "a credential")]
    [InlineData("Cookie", "a credential")]
    [InlineData("Connection", "hop-by-hop")]
    [InlineData("Idempotency-Key", "also a header parameter")]
    public async Task A_header_the_client_cannot_pass_through_is_refused(string header, string message)
    {
        await using var app = await StartAsync(group => group.WithTisiliaRequestReads(reads => reads.Header<Guid>("Idempotency-Key")));
        var root = JsonNode.Parse(Export(app).Text!)!.AsObject();
        var operation = root["operations"]!.AsArray().Select(o => o!.AsObject()).Single(o => o["id"]!.GetValue<string>() == "orders.create");
        operation["security"]!["requestHeaderAllowlist"] = new JsonArray(header.ToLowerInvariant());

        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        Assert.NotNull(loaded);
        SemanticValidator.Validate(loaded!, bag, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Rule == "SV32" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains(message, StringComparison.Ordinal));
    }

    internal static async Task<WebApplication> StartAsync(Action<RouteGroupBuilder> declare)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "request-reads");
        var app = builder.Build();
        var orders = app.MapGroup("/orders");
        declare(orders);
        // the handler echoes what the pipeline would read
        orders.MapPost("/", (HttpContext context) => TypedResults.Ok(context.Request.Headers["Idempotency-Key"].ToString())).WithTisiliaOperation("orders.create");
        orders.MapGet("/", (int? page) => TypedResults.Ok(page ?? 0)).WithTisiliaOperation("orders.list");
        app.MapGet("/health", () => TypedResults.Ok("ok")).WithTisiliaOperation("health");
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    private sealed class RouteGroupBuilderStub : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention) { }
    }
}
