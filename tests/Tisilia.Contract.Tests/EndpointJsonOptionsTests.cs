using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class EndpointJsonOptionsTests
{
    [Fact]
    public async Task Declared_response_options_are_frozen_enforced_and_separate_from_request_options()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "json-options");
        await using var app = builder.Build();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var calls = 0;
        app.MapPost("/custom", (Payload value) => { calls++; return TypedResults.Json(value, options); }).WithTisiliaJsonOptions<Payload>(options).ProducesProblem(400).WithTisiliaOperation("custom");
        app.MapGet("/default", () => new Payload(1)).WithTisiliaOperation("default");
        app.MapGet("/mismatch", () => TypedResults.Json(new Payload(1), new JsonSerializerOptions(JsonSerializerDefaults.Web))).WithTisiliaJsonOptions<Payload>(options).WithTisiliaOperation("mismatch");
        app.MapGet("/null", () => TypedResults.Json<Payload>(null, options)).WithTisiliaJsonOptions<Payload>(options).WithTisiliaOperation("null");
        app.MapGet("/status", () => TypedResults.Json(new Payload(1), options, statusCode: 201)).WithTisiliaJsonOptions<Payload>(options).WithTisiliaOperation("status");
        app.MapGet("/media", () => TypedResults.Json(new Payload(1), options, contentType: "application/problem+json")).WithTisiliaJsonOptions<Payload>(options).WithTisiliaOperation("media");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items)); Assert.Equal(0, calls);
        Assert.True(options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => options.PropertyNamingPolicy = null);
        using var contract = JsonDocument.Parse(export.Text!);
        var op = contract.RootElement.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "custom");
        Assert.NotEqual(op.GetProperty("requestBody").GetProperty("profileId").GetString(), op.GetProperty("responses")[0].GetProperty("body").GetProperty("profileId").GetString());
        Assert.Equal(op.GetProperty("requestBody").GetProperty("profileId").GetString(), op.GetProperty("responses").EnumerateArray().Single(r => r.GetProperty("status").GetInt32() == 400).GetProperty("body").GetProperty("profileId").GetString());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/custom", new StringContent("{\"largeNumber\":9007199254740993}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal("{\"large_number\":9007199254740993}", await response.Content.ReadAsStringAsync());
        Assert.Equal("{\"largeNumber\":1}", await client.GetStringAsync("/default"));
        foreach (var path in new[] { "/mismatch", "/null", "/status", "/media" }) { Assert.Equal(500, (int)(await client.GetAsync(path)).StatusCode); }
    }

    public sealed record Payload(long LargeNumber);

    [Fact]
    public async Task Mvc_JsonResult_uses_declared_settings_and_keeps_validation_errors_on_the_Mvc_profile()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new DeclaredJsonFeatureProvider()));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-json");
        await using var app = builder.Build();
        app.MapControllers().WithTisiliaJsonOptions<Payload>("declared-json", DeclaredJsonController.Options);
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        var entry = report.Operations.Single(o => o.OperationId == "declared-json");
        Assert.Equal("supported", entry.Readiness);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var good = await client.PostAsync("/declared-json?mode=ok", new StringContent("{\"largeNumber\":9007199254740993}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)good.StatusCode); Assert.Equal("{\"large_number\":9007199254740993}", await good.Content.ReadAsStringAsync());
        foreach (var mode in new[] { "options", "type", "null", "status", "media" })
        {
            using var bad = await client.PostAsync("/declared-json?mode=" + mode, new StringContent("{\"largeNumber\":1}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(500, (int)bad.StatusCode);
        }
        using var invalid = await client.PostAsync("/declared-json?mode=ok", new StringContent("{\"largeNumber\":\"bad\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)invalid.StatusCode);
        Assert.Contains("errors", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class DeclaredJsonFeatureProvider : Microsoft.AspNetCore.Mvc.ApplicationParts.IApplicationFeatureProvider<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPart> parts, Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(DeclaredJsonController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class DeclaredJsonController : ControllerBase
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    [HttpPost("/declared-json")]
    [TisiliaOperation("declared-json")]
    public JsonResult Post([FromBody] EndpointJsonOptionsTests.Payload value, [FromQuery] string mode = "ok") => new(
        mode == "null" ? null : mode == "type" ? (object)"wrong" : value,
        mode == "options" ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : Options)
    {
        StatusCode = mode == "status" ? 201 : 200,
        ContentType = mode == "media" ? "application/problem+json" : "application/json",
    };
}
