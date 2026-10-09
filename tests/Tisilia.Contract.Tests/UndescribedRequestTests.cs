using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Requests the contract cannot describe are diagnosed instead of exported as something else. Oracle: through Kestrel, the XML-only
/// actions and a minimal API that declares only text/json answer JSON with 415, while a declared wildcard accepts application/json;
/// an XML body the input formatter reads binds from the bytes the client is given; the BindAsync handler reads a body the contract
/// would not mention, while an explicit [FromBody] on the same type binds JSON.
/// </summary>
public sealed class UndescribedRequestTests
{
    [Fact]
    public async Task A_request_body_no_client_can_send_is_diagnosed()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(XmlBodyController))));
        builder.Services.AddTisilia(o => o.ApiId = "xml-bodies");
        await using var app = builder.Build();
        app.MapControllers();
        // the request delegate reads application/json and +json types only, and routing turns application/json away
        app.MapPost("/minimal/text-json", ([FromBody] XmlBody value) => TypedResults.Ok(value.Value)).Accepts<XmlBody>("text/json").WithTisiliaOperation("minimal.text-json");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        // a record the XML formatter cannot read leaves ApiExplorer without any request format; [Consumes] still says XML only
        foreach (var (id, media) in new[] { ("xml.record", "application/xml"), ("minimal.text-json", "text/json") })
        {
            var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.RelatedIds.Contains(id));
            Assert.Equal("SV29", diagnostic.Rule);
            Assert.Contains(media, diagnostic.Message, StringComparison.Ordinal);
        }

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var path in new[] { "/xml/record", "/minimal/text-json" })
        {
            using var json = await client.PostAsync(path, new StringContent("{\"value\":1}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(415, (int)json.StatusCode);
        }
        using var textJson = await client.PostAsync("/minimal/text-json", new StringContent("{\"value\":1}", System.Text.Encoding.UTF8, "text/json"));
        Assert.Equal(415, (int)textJson.StatusCode);
        using var record = await client.PostAsync("/xml/record", new StringContent("<XmlRecord><Value>1</Value></XmlRecord>", System.Text.Encoding.UTF8, "application/xml"));
        Assert.Equal(415, (int)record.StatusCode);
    }

    [Fact]
    public async Task XML_a_DataContractSerializer_formatter_reads_and_writes_travels_as_the_bytes_given_with_a_warning()
    {
        // MVC's XmlSerializer formatters are described with XmlSerializer's own mapping (XmlBodyTests); DataContractSerializer's are not
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlDataContractSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(XmlOpaqueController))));
        builder.Services.AddTisilia(o => o.ApiId = "xml-opaque");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var body = Assert.IsType<BinaryRequestBody>(export.Index!.Operations["xml.in"].RequestBody);
        Assert.Equal("application/xml", body.MediaType);
        Assert.Equal("application/xml", Assert.IsType<BinaryResponseBody>(Assert.Single(export.Index.Operations["xml.out"].Responses).Body).MediaType);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Severity == DiagnosticSeverity.Warning && d.RelatedIds.Contains("xml.in") && d.Message.Contains("input formatter", StringComparison.Ordinal)
            && d.Message.Contains("XmlDataContractSerializerInputFormatter reads it", StringComparison.Ordinal));
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Severity == DiagnosticSeverity.Warning && d.RelatedIds.Contains("xml.out") && d.Message.Contains("application/xml", StringComparison.Ordinal));

        var work = Directory.CreateTempSubdirectory("tisilia-xml-");
        try
        {
            var sent = await InterpreterRequests.SendAsync(work.FullName, export.Text!, app.Urls.Single(), [
                ("xml.in", new { body = new Dictionary<string, string> { ["$utf8"] = "<XmlBody xmlns=\"http://schemas.datacontract.org/2004/07/Tisilia.Contract.Tests\"><Value>7</Value></XmlBody>" } }),
                ("xml.out", new { }),
            ]);
            Assert.Equal("200 7", sent[0]);
            Assert.StartsWith("200 <XmlBody ", sent[1], StringComparison.Ordinal);
            Assert.Contains("<Value>7</Value>", sent[1], StringComparison.Ordinal);
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_request_body_that_also_accepts_JSON_or_a_wildcard_is_sent_as_JSON()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(JsonBodyController))));
        builder.Services.AddTisilia(o => o.ApiId = "json-bodies");
        await using var app = builder.Build();
        app.MapControllers();
        // Accepts<T> takes a wildcard, which routing matches application/json against; [Consumes] refuses one at startup
        app.MapPost("/minimal/wildcard", ([FromBody] XmlBody value) => TypedResults.Ok(value.Value)).Accepts<XmlBody>("application/*").WithTisiliaOperation("minimal.wildcard");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var paths = new Dictionary<string, string> { ["xml.or-json"] = "/xml/or-json", ["minimal.wildcard"] = "/minimal/wildcard" };
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var (id, path) in paths)
        {
            var body = Assert.IsType<Tisilia.Contract.JsonRequestBody>(export.Index!.Operations[id].RequestBody);
            Assert.Equal("application/json", body.MediaType);
            using var json = await client.PostAsync(path, new StringContent("{\"value\":7}", System.Text.Encoding.UTF8, body.MediaType));
            Assert.Equal("7", await json.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task BindAsync_parameters_are_diagnosed_while_explicit_sources_services_and_HttpContext_are_not()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Clock>();
        builder.Services.AddTisilia(o => o.ApiId = "bind-async");
        await using var app = builder.Build();
        app.MapPost("/static", (StaticBound value) => TypedResults.Ok(value.Text)).WithTisiliaOperation("bind.static");
        app.MapPost("/interface", (InterfaceBound value) => TypedResults.Ok(value.Text)).WithTisiliaOperation("bind.interface");
        app.MapPost("/grouped", ([AsParameters] Grouped args) => TypedResults.Ok(args.Value.Text + args.Page)).WithTisiliaOperation("bind.as-parameters");
        app.MapGet("/plain", (Clock clock, HttpContext context, CancellationToken token) => TypedResults.Ok(clock.Now)).WithTisiliaOperation("bind.none");
        // an explicit source attribute wins over the type's BindAsync (RequestDelegateFactory.CreateArgument, aspnetcore v10.0.0)
        app.MapPost("/explicit", ([FromBody] StaticBound value) => TypedResults.Ok(value.Text)).WithTisiliaOperation("bind.explicit-body");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        foreach (var id in new[] { "bind.static", "bind.interface", "bind.as-parameters" })
        {
            var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.RelatedIds.Contains(id));
            Assert.Equal("SV30", diagnostic.Rule);
            Assert.Contains("BindAsync", diagnostic.Message, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.RelatedIds.Contains("bind.none"));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.RelatedIds.Contains("bind.explicit-body"));

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var bound = await client.PostAsync("/static", new StringContent("from the body"));
        Assert.Equal("\"from the body\"", await bound.Content.ReadAsStringAsync());
        using var json = await client.PostAsync("/explicit", new StringContent("{\"text\":\"from JSON\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal("\"from JSON\"", await json.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_form_parameter_bound_through_its_own_TryParse_is_server_parsed_text_and_the_same_type_in_a_model_stays_a_model()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-tryparse");
        await using var app = builder.Build();
        app.MapPost("/root", ([FromForm] Code code) => TypedResults.Ok(code.Value)).DisableAntiforgery().WithTisiliaOperation("tryparse.root");
        app.MapPost("/array", ([FromForm] Code[] codes) => TypedResults.Ok(codes.Length)).DisableAntiforgery().WithTisiliaOperation("tryparse.array");
        app.MapPost("/member", ([FromForm] CodeHolder value) => TypedResults.Ok(value.Code?.Value ?? "")).DisableAntiforgery().WithTisiliaOperation("tryparse.member");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        // the root value and array are text Code.TryParse reads: a string the contract does not check, with a warning
        foreach (var (id, name) in new[] { ("tryparse.root", "code"), ("tryparse.array", "codes") })
        {
            var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.RelatedIds.Contains(id));
            Assert.Equal(("SV30", DiagnosticSeverity.Warning), (diagnostic.Rule, diagnostic.Severity));
            Assert.Contains("Code's own parser", diagnostic.Message, StringComparison.Ordinal);
            var field = Assert.Single(Assert.IsType<FormRequestBody>(export.Index!.Operations[id].RequestBody).Fields);
            Assert.True(field.ServerParsed); Assert.Equal(name, field.Name);
            Assert.IsType<PrimitiveShape>(export.Index.Types[field.Use!.TypeId].Shape);
        }
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.RelatedIds.Contains("tryparse.member"));
        Assert.Equal("Code.Value", Assert.Single(Assert.IsType<FormRequestBody>(export.Index!.Operations["tryparse.member"].RequestBody).Fields).Name);
        var work = Directory.CreateTempSubdirectory("tisilia-tryparse-");
        try
        {
            var sent = await InterpreterRequests.SendAsync(work.FullName, export.Text!, app.Urls.Single(), [
                ("tryparse.root", new { body = new { code = "abc" } }), ("tryparse.array", new { body = new { codes = new[] { "a", "b" } } })]);
            Assert.Equal(["200 \"abc\"", "200 2"], sent);
        }
        finally { work.Delete(recursive: true); }

        // Kestrel: the root binds one value through TryParse; the member binds the type's properties
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var root = await client.PostAsync("/root", new FormUrlEncodedContent([KeyValuePair.Create("code", "abc")]));
        Assert.Equal("\"abc\"", await root.Content.ReadAsStringAsync());
        using var properties = await client.PostAsync("/root", new FormUrlEncodedContent([KeyValuePair.Create("Value", "abc")]));
        Assert.Equal(400, (int)properties.StatusCode);
        using var member = await client.PostAsync("/member", new FormUrlEncodedContent([KeyValuePair.Create("Code.Value", "abc")]));
        Assert.Equal("\"abc\"", await member.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Route_query_and_header_parameters_parsed_by_their_own_type_are_server_parsed_text()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "parsed-parameters");
        await using var app = builder.Build();
        app.MapGet("/codes/{code}", (Code code, [FromQuery] Code? filter, [FromQuery] Code[] tags, [FromHeader(Name = "X-Code")] Code header) =>
                TypedResults.Ok($"{code.Value}|{filter?.Value ?? "none"}|{string.Join(",", tags.Select(t => t.Value))}|{header.Value}"))
            .WithTisiliaOperation("parsed");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var parameters = export.Index!.Operations["parsed"].Parameters.ToDictionary(p => p.Name);
        Assert.Equal(["X-Code", "code", "filter", "tags"], parameters.Keys.Order(StringComparer.Ordinal));
        Assert.All(parameters.Values, p => Assert.Equal(Tisilia.Generator.Validation.Builtins.AcceptServerParsed, export.Index.Binders[p.BinderId].ServerAcceptanceId));
        Assert.Equal(4, export.Diagnostics.Items.Count(d => d.Rule == "SV30" && d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Code's own parser", StringComparison.Ordinal)));
        Assert.Equal((Presence.Required, Presence.Optional), (parameters["code"].Presence, parameters["filter"].Presence));

        var work = Directory.CreateTempSubdirectory("tisilia-parsed-");
        try
        {
            var sent = await InterpreterRequests.SendAsync(work.FullName, export.Text!, app.Urls.Single(), [
                ("parsed", new Dictionary<string, object> { ["code"] = "a b", ["filter"] = "f", ["tags"] = new[] { "x", "y" }, ["X-Code"] = "h" }),
                ("parsed", new Dictionary<string, object> { ["code"] = "c", ["tags"] = Array.Empty<string>(), ["X-Code"] = "h" }),
            ]);
            Assert.Equal(["200 \"a b|f|x,y|h\"", "200 \"c|none||h\""], sent);
        }
        finally { work.Delete(recursive: true); }
    }

    public sealed class Code
    {
        public string Value { get; set; } = "";
        public static bool TryParse(string? text, out Code result) { result = new Code { Value = text ?? "" }; return text is not null; }
    }

    public sealed class CodeHolder { public Code? Code { get; set; } }

    public sealed class Clock { public long Now => 1; }

    public readonly record struct Grouped(StaticBound Value, int Page);

    public sealed class StaticBound
    {
        public string Text { get; init; } = "";
        public static async ValueTask<StaticBound?> BindAsync(HttpContext context) => new() { Text = await new StreamReader(context.Request.Body).ReadToEndAsync() };
    }

    public sealed class InterfaceBound : IBindableFromHttpContext<InterfaceBound>
    {
        public string Text { get; init; } = "";
        static ValueTask<InterfaceBound?> IBindableFromHttpContext<InterfaceBound>.BindAsync(HttpContext context, ParameterInfo parameter)
            => ValueTask.FromResult<InterfaceBound?>(new() { Text = context.Request.Headers["X-Text"].ToString() });
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
public sealed class XmlBodyController : ControllerBase
{
    [HttpPost("/xml/record")]
    [TisiliaOperation("xml.record")]
    [Consumes("application/xml")]
    public ActionResult<long> Record([FromBody] XmlRecord value) => value.Value;

}

[ApiController]
[NonController]
public sealed class XmlOpaqueController : ControllerBase
{
    [HttpPost("/xml/in")]
    [TisiliaOperation("xml.in")]
    [Consumes("application/xml")]
    public ActionResult<int> In([FromBody] XmlBody value) => value.Value;

    [HttpGet("/xml/out")]
    [TisiliaOperation("xml.out")]
    [Produces("application/xml")]
    public ActionResult<XmlBody> Out() => new XmlBody { Value = 7 };
}

[ApiController]
[NonController]
public sealed class JsonBodyController : ControllerBase
{
    [HttpPost("/xml/or-json")]
    [TisiliaOperation("xml.or-json")]
    public ActionResult<int> OrJson([FromBody] XmlBody value) => value.Value;
}

public sealed class XmlBody { public int Value { get; set; } }

public sealed record XmlRecord(long Value);
