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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_byte_event_types_and_interfaces_that_can_contain_them_are_diagnosed(bool sequence)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "events");
        await using var app = builder.Build();
        if (sequence) { app.MapGet("/events", () => TypedResults.ServerSentEvents(Values<IEnumerable<byte>>([1], [2]))).WithTisiliaOperation("events"); }
        else { app.MapGet("/events", () => TypedResults.ServerSentEvents(Values<byte[]>([1], [2]))).WithTisiliaOperation("events"); }
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Message.Contains("SSE", StringComparison.Ordinal));
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
    public async Task Controller_actions_returning_SSE_are_diagnosed_as_minimal_API_only()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new SseControllerFeatureProvider()));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-events");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        // the action declares SseItem<long> through its result type, so the diagnostic must name the real cause: MVC
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Message.Contains("minimal API", StringComparison.Ordinal));
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
    public ServerSentEventsResult<long> Get() => TypedResults.ServerSentEvents(SseTests.Values(1L, 2L));
}
