using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
/// Model ids name a CLR type and a direction. MVC serializes with its own JsonOptions: when they describe types otherwise than the
/// minimal API's, MVC's models get their own scope, so that a type both serve is described for each. Oracle: through Kestrel, the
/// runtime's encoder and decoder talk to both with the same type under different naming policies.
/// </summary>
public sealed class ProfileScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-profile-scope-" + Guid.NewGuid().ToString("N"));

    public ProfileScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<WebApplication> StartAsync(Action<IMvcBuilder> mvc, Action<WebApplicationBuilder>? configure = null, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        mvc(builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController())));
        configure?.Invoke(builder);
        builder.Services.AddTisilia(o => o.ApiId = "scopes");
        var app = builder.Build();
        app.MapPost("/minimal", (ScopedItem item) => new ScopedItem { DisplayName = item.DisplayName + "!", Color = item.Color }).WithTisiliaOperation("minimal");
        map?.Invoke(app);
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task A_type_MVC_and_the_minimal_API_name_differently_is_described_for_each()
    {
        await using var app = await StartAsync(m => m.AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        string[] Names(string op, bool request)
        {
            var use = request ? ((JsonRequestBody)export.Index!.Operations[op].RequestBody!).Use : ((JsonResponseBody)export.Index!.Operations[op].Responses.Single(r => r.Status == 200).Body!).Use;
            return ((ObjectShape)export.Index.Types[use.TypeId].Shape).Properties.Select(p => p.Name).ToArray();
        }
        Assert.Equal(["displayName", "color"], Names("minimal", request: true));
        Assert.Equal(["display_name", "color"], Names("mvc", request: true));
        Assert.Equal(["display_name", "color"], Names("mvc", request: false));

        // before, MVC's request carried the minimal API's names: MVC bound nothing and answered "200 []"
        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("minimal", new { body = new Dictionary<string, string> { ["$json"] = """{"displayName":"a","color":1}""" } }),
            ("mvc", new { body = new Dictionary<string, string> { ["$json"] = """{"display_name":"b","color":1}""" } }),
            ("mvc", new { body = new Dictionary<string, string> { ["$json"] = """{"displayName":"b","color":1}""" } }),
        ]);
        Assert.Equal("""200 {"displayName":"a!","color":1}""", sent[0]);
        Assert.Equal("""200 {"display_name":"b?","color":1}""", sent[1]);
        Assert.StartsWith("unexpected-property:", sent[2], StringComparison.Ordinal);

        // and the decoder reads each answer with its own names
        var executed = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("minimal", new { body = new Dictionary<string, string> { ["$json"] = """{"displayName":"a","color":1}""" } }),
            ("mvc", new { body = new Dictionary<string, string> { ["$json"] = """{"display_name":"b","color":1}""" } }),
        ], execute: true);
        using var minimal = JsonDocument.Parse(executed[0]);
        Assert.Equal("a!", minimal.RootElement.GetProperty("data").GetProperty("displayName").GetString());
        using var mvc = JsonDocument.Parse(executed[1]);
        Assert.Equal("b?", mvc.RootElement.GetProperty("data").GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Polymorphic_variants_are_described_for_each_profile()
    {
        await using var app = await StartAsync(m => m.AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower),
            map: a => a.MapGet("/minimal-shape", () => (ScopedShape)new ScopedCircle { LineWidth = 2, RadiusLength = 1.5 }).WithTisiliaOperation("minimal-shape"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var executed = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("minimal-shape", new { }), ("mvc-shape", new { })], execute: true);
        using var minimal = JsonDocument.Parse(executed[0]);
        Assert.Equal(1.5, minimal.RootElement.GetProperty("data").GetProperty("radiusLength").GetDouble());
        using var mvc = JsonDocument.Parse(executed[1]);
        Assert.Equal(1.5, mvc.RootElement.GetProperty("data").GetProperty("radius_length").GetDouble());
    }

    [Fact]
    public async Task MVC_with_the_minimal_API_settings_shares_its_models()
    {
        // MVC's own defaults differ only in MaxDepth (32), which changes no model
        await using var app = await StartAsync(_ => { });
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal(((JsonRequestBody)export.Index!.Operations["minimal"].RequestBody!).Use.TypeId, ((JsonRequestBody)export.Index.Operations["mvc"].RequestBody!).Use.TypeId);
        Assert.Equal("scopes.Tisilia.Contract.Tests.ScopedItem.request", ((JsonRequestBody)export.Index.Operations["mvc"].RequestBody!).Use.TypeId);
    }

    [Fact]
    public async Task One_converter_type_with_other_settings_on_each_side_is_diagnosed()
    {
        // both sides convert enums with JsonStringEnumConverter, which names them otherwise on each side: one model id cannot describe both
        await using var app = await StartAsync(m => m.AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper))),
            b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var conflict = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV16");
        Assert.Contains("scopes.profile.minimal", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("scopes.profile.mvc", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("ScopedColor", conflict.Message, StringComparison.Ordinal);
        Assert.Null(export.Text);
    }

    private sealed class OnlyController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(ScopedController).GetTypeInfo());
        }
    }
}

public sealed class ScopedItem
{
    public string DisplayName { get; set; } = "";
    public ScopedColor Color { get; set; }
}

public enum ScopedColor { DeepRed = 1, LightBlue = 2 }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScopedCircle), "circle")]
public abstract class ScopedShape
{
    public int LineWidth { get; set; }
}

public sealed class ScopedCircle : ScopedShape
{
    public double RadiusLength { get; set; }
}

[ApiController]
[NonController]
public sealed class ScopedController : ControllerBase
{
    [HttpPost("/mvc")]
    [TisiliaOperation("mvc")]
    public ScopedItem Echo([FromBody] ScopedItem item) => new() { DisplayName = item.DisplayName + "?", Color = item.Color };

    [HttpGet("/mvc-shape")]
    [TisiliaOperation("mvc-shape")]
    public ScopedShape Shape() => new ScopedCircle { LineWidth = 2, RadiusLength = 1.5 };
}
