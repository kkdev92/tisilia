using System.Net.ServerSentEvents;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Oracle: ServerSentEventsResult.cs (aspnetcore v10.0.0), exercised through Kestrel.</summary>
public sealed class SseTests
{
    [Fact]
    public async Task Interfaces_that_can_hold_raw_byte_events_are_diagnosed()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "events");
        await using var app = builder.Build();
        app.MapGet("/events", () => TypedResults.ServerSentEvents(Values<IEnumerable<byte>>([1], [2]))).WithTisiliaOperation("events");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Message.Contains("SSE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Byte_array_and_object_events_reach_the_client_as_their_text_with_a_warning()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "opaque-events");
        await using var app = builder.Build();
        app.MapGet("/bytes", () => TypedResults.ServerSentEvents(Values<byte[]>("a\r\nb"u8.ToArray(), "日本"u8.ToArray()))).WithTisiliaOperation("bytes");
        app.MapGet("/objects", () => TypedResults.ServerSentEvents(Values<object>("text", new { value = 9007199254740993L }))).WithTisiliaOperation("objects");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        foreach (var id in new[] { "bytes", "objects" })
        {
            var body = Assert.IsType<Tisilia.Contract.SseResponseBody>(Assert.Single(export.Index!.Operations[id].Responses).Body);
            Assert.Equal("text", body.DataFormat);
            Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Severity == Tisilia.Generator.Diagnostics.DiagnosticSeverity.Warning && d.RelatedIds.Contains(id));
        }

        // the runtime decodes each event's data as text: CRLF becomes LF; object data is JSON (a string is quoted)
        var work = Directory.CreateTempSubdirectory("tisilia-events-");
        try
        {
            var results = await InterpreterRequests.SendAsync(work.FullName, export.Text!, app.Urls.Single(), [("bytes", new { }), ("objects", new { })], execute: true);
            using var bytes = JsonDocument.Parse(results[0]);
            Assert.Equal(["a\nb", "日本"], bytes.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("data").GetString()));
            using var objects = JsonDocument.Parse(results[1]);
            Assert.Equal(["\"text\"", "{\"value\":9007199254740993}"], objects.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("data").GetString()));
        }
        finally { work.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_SSE_exports_the_data_encoding_and_matches_Kestrel(bool json)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "events");
        await using var app = builder.Build();
        var calls = 0;
        if (json) { app.MapGet("/events", () => { calls++; return TypedResults.ServerSentEvents(Values<long?>(9007199254740993L, null)); }).WithTisiliaOperation("events"); }
        else { app.MapGet("/events", () => { calls++; return TypedResults.ServerSentEvents(Values("日本😀\ntwo", "")); }).WithTisiliaOperation("events"); }
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal(0, calls);
        using var document = JsonDocument.Parse(export.Text!);
        var response = document.RootElement.GetProperty("operations")[0].GetProperty("responses")[0];
        Assert.Equal("sse", response.GetProperty("body").GetProperty("kind").GetString());
        Assert.Equal(json ? "json" : "text", response.GetProperty("body").GetProperty("dataFormat").GetString());
        Assert.Equal("server-only", response.GetProperty("hydration").GetString());
        using var client = new HttpClient();
        var text = await client.GetStringAsync(app.Urls.Single() + "/events");
        Assert.Contains(json ? "9007199254740993" : "日本😀", text, StringComparison.Ordinal);
        Assert.Contains("id: first", text, StringComparison.Ordinal);
        Assert.Contains("retry: 123", text, StringComparison.Ordinal);
        Assert.Contains("data: \n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_actions_returning_SSE_export_their_events_with_the_minimal_API_JSON_options()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        // MVC writes PascalCase here; ServerSentEventsResult<T> still serializes with the minimal API JsonOptions (camelCase)
        builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null)
            .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new SseControllerFeatureProvider()));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-events");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var body = Assert.IsType<Tisilia.Contract.SseResponseBody>(Assert.Single(export.Index!.Operations["mvc-events"].Responses).Body);
        Assert.Equal("json", body.DataFormat);
        Assert.Equal("mvc-events.profile.minimal", body.ProfileId);
        Assert.Equal("tisilia.naming.camel-case@0.1", export.Index.Profiles[body.ProfileId!].Options.PropertyNamingPolicyId);

        using var client = new HttpClient();
        var text = await client.GetStringAsync(app.Urls.Single() + "/mvc-events");
        Assert.Contains("data: {\"value\":9007199254740993}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Value\"", text, StringComparison.Ordinal);
    }

    internal static async IAsyncEnumerable<SseItem<T>> Values<T>(T first, T second)
    {
        yield return new SseItem<T>(first, "update") { EventId = "first", ReconnectionInterval = TimeSpan.FromMilliseconds(123) };
        await Task.Yield();
        yield return new SseItem<T>(second);
    }

    private sealed class SseControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(SseEventsController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class SseEventsController : ControllerBase
{
    [HttpGet("/mvc-events")]
    [TisiliaOperation("mvc-events")]
    public ServerSentEventsResult<SseTick> Get() => TypedResults.ServerSentEvents(SseTests.Values(new SseTick(9007199254740993L), new SseTick(2)));
}

public sealed record SseTick(long Value);
