using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class RouteTransformerTests
{
    [Fact]
    public async Task Outbound_transformers_do_not_transform_explicit_incoming_parameter_values()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "transformers");
        builder.Services.Configure<RouteOptions>(o => o.ConstraintMap["lower"] = typeof(LowerTransformer));
        await using var app = builder.Build();
        app.MapGet("/articles/{id:lower}", (string id) => id).WithName("article").WithTisiliaOperation("article");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        Assert.Equal("/articles/mixed", app.Services.GetRequiredService<LinkGenerator>().GetPathByName("article", new { id = "MiXeD" }));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal("MiXeD", await client.GetStringAsync("/articles/MiXeD"));
        Assert.Equal("日本語", await client.GetStringAsync("/articles/" + Uri.EscapeDataString("日本語")));
    }

    public sealed class LowerTransformer : IOutboundParameterTransformer
    {
        public string? TransformOutbound(object? value) => value?.ToString()?.ToLowerInvariant();
    }
}
