using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Codecs;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Additional;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: MVC's ApiExplorer describes a parameter whose type MVC reads from one value (a TypeConverter or a TryParse: Int128,
/// Version, IPAddress, a type with its own TryParse) as a string (aspnetcore v10.0.0), so export reads the parameter's own type. The
/// additional codec types then bind on Kestrel from the text their codec binders write, in the route, query and headers, and from
/// their JSON codec in a body.
/// </summary>
public sealed class MvcParameterTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-mvc-types-" + Guid.NewGuid().ToString("N"));

    public MvcParameterTypeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Additional_codec_parameters_and_bodies_of_controller_actions_keep_their_types()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.AddTisiliaAdditionalConverters())
            .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MvcCodecParametersController))));
        builder.Services.AddTisilia(o => { o.ApiId = "mvc-codecs"; o.Codecs.AddAdditionalCodecs(_dir); });
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var get = export.Index!.Operations["mvc-codecs.get"].Parameters.ToDictionary(p => p.Name);
        Assert.Equal(AdditionalModule.ModelId("Int128"), get["big"].Use.TypeId);
        Assert.Equal(AdditionalModule.Grammar("version"), export.Index.Binders[get["version"].BinderId].GrammarId);
        Assert.Equal(AdditionalModule.ModelId("IPAddress"), get["X-Address"].Use.TypeId);
        Assert.Equal(AdditionalModule.ModelId("Int128"), Assert.IsType<Tisilia.Contract.JsonRequestBody>(export.Index.Operations["mvc-codecs.post"].RequestBody).Use.TypeId);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("mvc-codecs.get", new Dictionary<string, object>
            {
                ["big"] = "-170141183460469231731687303715884105728", ["unsigned"] = "340282366920938463463374607431768211455", ["integer"] = "-123456789012345678901234567890",
                ["half"] = 5.9604644775390625E-08, ["link"] = "https://example.test/a%20b?q=1", ["version"] = "1.2.3", ["X-Address"] = "fe80::1%3", ["network"] = "10.0.0.0/8",
                ["many"] = new[] { "1", "-2" },
            }),
            ("mvc-codecs.post", new { body = "170141183460469231731687303715884105727" }),
        ]);
        Assert.Equal("200 [\"-170141183460469231731687303715884105728\",\"340282366920938463463374607431768211455\",\"-123456789012345678901234567890\",\"6E-08\",\"https://example.test/a%20b?q=1\",\"1.2.3\",\"fe80::1%3\",\"10.0.0.0/8\",\"1,-2\"]", sent[0]);
        Assert.Equal("200 170141183460469231731687303715884105727", sent[1]); // MVC writes a string result as text/plain
    }

    [Fact]
    public async Task A_controller_parameter_of_a_type_with_its_own_TryParse_is_server_parsed_text()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MvcOwnParserController))));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-own-parser");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Severity == Tisilia.Generator.Diagnostics.DiagnosticSeverity.Warning && d.RelatedIds.Contains("own-parser") && d.Message.Contains("OwnParsed's own parser", StringComparison.Ordinal));
        var parameter = Assert.Single(export.Index!.Operations["own-parser"].Parameters);
        Assert.Equal(Tisilia.Generator.Validation.Builtins.AcceptServerParsed, export.Index.Binders[parameter.BinderId].ServerAcceptanceId);

        // Kestrel: MVC reads the value through the type's TryParse, which decides what the text means; the client sends the text as is
        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("own-parser", new { code = "a b&c" })]);
        Assert.Equal("200 parsed:a b&c", sent[0]);
    }

    private sealed class OneController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class MvcCodecParametersController : ControllerBase
{
    [HttpGet("/mvc-codecs/{big}")]
    [TisiliaOperation("mvc-codecs.get")]
    public ActionResult<string[]> Get([FromRoute] Int128 big, [FromQuery] UInt128 unsigned, [FromQuery] BigInteger integer, [FromQuery] Half half, [FromQuery] Uri link,
        [FromQuery] Version version, [FromHeader(Name = "X-Address")] IPAddress address, [FromQuery] IPNetwork network, [FromQuery] Int128[] many) =>
        new[]
        {
            big.ToString(CultureInfo.InvariantCulture), unsigned.ToString(CultureInfo.InvariantCulture), integer.ToString(CultureInfo.InvariantCulture), half.ToString(CultureInfo.InvariantCulture),
            link.OriginalString, version.ToString(), address.ToString(), network.ToString(), string.Join(",", many.Select(v => v.ToString(CultureInfo.InvariantCulture))),
        };

    [HttpPost("/mvc-codecs")]
    [TisiliaOperation("mvc-codecs.post")]
    public ActionResult<string> Post([FromBody] Int128 value) => value.ToString(CultureInfo.InvariantCulture);
}

[ApiController]
[NonController]
public sealed class MvcOwnParserController : ControllerBase
{
    [HttpGet("/own-parser")]
    [TisiliaOperation("own-parser")]
    public ActionResult<string> Get([FromQuery] OwnParsed code) => code.Text;
}

public sealed class OwnParsed
{
    public string Text { get; init; } = "";
    public static bool TryParse(string? text, IFormatProvider? provider, out OwnParsed result) { result = new OwnParsed { Text = "parsed:" + text }; return text is not null; }
}
