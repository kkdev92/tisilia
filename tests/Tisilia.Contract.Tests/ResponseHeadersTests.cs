using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class ResponseHeadersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-response-headers-" + Guid.NewGuid().ToString("N"));

    public ResponseHeadersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Group_and_endpoint_declarations_are_additive_and_status_specific()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        var operation = export.Index!.Operations["items.get"];
        Assert.Equal(["etag", "x-correlation-id"], Assert.Single(operation.Responses, r => r.Status == 200).ExposedHeaders);
        Assert.Equal(["retry-after", "x-correlation-id"], Assert.Single(operation.Responses, r => r.Status == 429).ExposedHeaders);
        Assert.Equal(["x-correlation-id"], Assert.Single(export.Index.Operations["items.delete"].Responses).ExposedHeaders);
        Assert.Empty(Assert.Single(export.Index.Operations["health"].Responses).ExposedHeaders);
        // Exporting an unchanged application does not change its hash or declaration order.
        Assert.Equal(export.Text, Export(app).Text);
    }

    [Fact]
    public async Task Every_body_kind_and_implicit_Mvc_no_content_receive_the_declaration()
    {
        await using var app = await StartAsync();
        var index = Export(app).Index!;
        foreach (var id in new[] { "items.get", "items.delete", "items.text", "items.events", "items.file", "mvc.xml", "mvc.nullable" })
        {
            Assert.All(index.Operations[id].Responses, r => Assert.Contains("x-correlation-id", r.ExposedHeaders));
        }
        Assert.Contains(index.Operations["mvc.nullable"].Responses, r => r.Status == 204);
        Assert.Equal(["content-disposition", "x-correlation-id"], Assert.Single(index.Operations["items.file"].Responses).ExposedHeaders);
        Assert.Equal(["content-disposition", "content-range", "x-correlation-id"], Assert.Single(index.Operations["items.partial"].Responses).ExposedHeaders);
        Assert.IsType<XmlResponseBody>(Assert.Single(index.Operations["mvc.xml"].Responses, r => r.Status == 200).Body);
    }

    [Fact]
    public async Task Mvc_attributes_and_MapControllers_conventions_both_apply()
    {
        await using var app = await StartAsync();
        var response = Assert.Single(Export(app).Index!.Operations["mvc.xml"].Responses, r => r.Status == 200);
        Assert.Equal(["etag", "x-controller", "x-correlation-id"], response.ExposedHeaders);
    }

    [Fact]
    public async Task A_client_reads_headers_on_json_bodyless_and_problem_responses()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("items.get", new { limited = false }), ("items.get", new { limited = true }), ("items.delete", new { })], execute: true);
        for (var i = 0; i < results.Count; i++)
        {
            using var document = JsonDocument.Parse(results[i]);
            var result = document.RootElement;
            Assert.Equal("response", result.GetProperty("kind").GetString());
            Assert.Equal(new[] { 200, 429, 204 }[i], result.GetProperty("status").GetInt32());
            var headers = result.GetProperty("headers").EnumerateArray().ToDictionary(h => h[0].GetString()!, h => h[1].GetString());
            Assert.Equal("test-correlation", headers["x-correlation-id"]);
            Assert.DoesNotContain("x-internal", headers.Keys);
            if (i == 0) { Assert.Equal("\"v1\"", headers["etag"]); Assert.DoesNotContain("retry-after", headers.Keys); }
            if (i == 1) { Assert.Equal("7", headers["retry-after"]); Assert.DoesNotContain("etag", headers.Keys); }
        }
    }

    [Theory]
    [InlineData("Set-Cookie", "SV36")]
    [InlineData("AUTHORIZATION", "SV36")]
    [InlineData("bad\r\nheader", "SV32")]
    [InlineData("*", "SV32")]
    public async Task Invalid_or_credential_headers_fail_export(string header, string rule)
    {
        await using var app = await StartAsync(endpoint => endpoint.WithTisiliaResponseHeaders(header));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors);
        Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == rule);
    }

    [Fact]
    public async Task A_status_without_a_response_is_reported_instead_of_silently_ignored()
    {
        await using var app = await StartAsync(endpoint => endpoint.WithTisiliaResponseHeaders(418, "ETag"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV28" && d.Message.Contains("418", StringComparison.Ordinal));
    }

    private static ExportResult Export(WebApplication app)
    {
        var result = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Items));
        return result;
    }

    private static async Task<WebApplication> StartAsync(Action<RouteHandlerBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyHeadersController()));
        builder.Services.AddTisilia(o => o.ApiId = "response-headers");
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Correlation-ID"] = "test-correlation";
            context.Response.Headers["X-Internal"] = "not-public";
            await next(context);
        });
        var group = app.MapGroup("/items").WithTisiliaResponseHeaders("X-Correlation-ID", "x-correlation-id");
        var get = group.MapGet("/", Results<Ok<HeaderValue>, ProblemHttpResult> (bool limited, HttpContext context) =>
        {
            context.Response.Headers.ETag = "\"v1\"";
            context.Response.Headers.RetryAfter = "7";
            return limited ? TypedResults.Problem(statusCode: 429, title: "limited") : TypedResults.Ok(new HeaderValue { Value = "ok" });
        }).ProducesProblem(429).WithTisiliaOperation("items.get")
            .WithTisiliaResponseHeaders(200, "ETag", "etag").WithTisiliaResponseHeaders(429, "Retry-After");
        configure?.Invoke(get);
        group.MapDelete("/", () => TypedResults.NoContent()).WithTisiliaOperation("items.delete");
        group.MapGet("/text", () => TypedResults.Text("ok")).Produces<string>(200, "text/plain").WithTisiliaOperation("items.text");
        group.MapGet("/events", () => TypedResults.ServerSentEvents(Events())).WithTisiliaOperation("items.events");
        group.MapGet("/file", () => TypedResults.File(new byte[] { 1 }, "application/octet-stream", "test.bin"))
            .Produces<FileContentResult>(200, "application/octet-stream").WithTisiliaOperation("items.file");
        group.MapGet("/partial", () => TypedResults.File(new byte[] { 1 }, "application/octet-stream", "test.bin"))
            .Produces<FileContentResult>(206, "application/octet-stream").WithTisiliaOperation("items.partial");
        app.MapGet("/health", () => TypedResults.Ok("ok")).WithTisiliaOperation("health");
        app.MapControllers().WithTisiliaResponseHeaders("X-Correlation-ID");
        await app.StartAsync();
        return app;
    }

    private static async IAsyncEnumerable<string> Events()
    {
        await Task.Yield();
        yield return "ok";
    }

    public sealed class HeaderValue { public string Value { get; set; } = ""; }

    private sealed class OnlyHeadersController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(ResponseHeadersController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
[Route("/mvc/headers")]
[TisiliaResponseHeaders("X-Controller")]
public sealed class ResponseHeadersController : ControllerBase
{
    [HttpGet("xml")]
    [Produces("application/xml")]
    [TisiliaOperation("mvc.xml")]
    [TisiliaResponseHeaders(200, "ETag")]
    public ActionResult<ResponseHeadersTests.HeaderValue> Xml() => new ResponseHeadersTests.HeaderValue { Value = "xml" };

    [HttpGet("nullable")]
    [TisiliaOperation("mvc.nullable")]
    public ActionResult<ResponseHeadersTests.HeaderValue?> Nullable() => (ResponseHeadersTests.HeaderValue?)null;
}
