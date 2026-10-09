using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Codecs;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Additional;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: form values of the additional codec types, written by the runtime's form encoder from the contract (the interpreter's
/// descriptors, which Explorer uses) as the request codec's canonical text, bind on Kestrel to the same values. A root value or
/// array binds through the type's TryParse, members and other collections through the form mapper's IParsable&lt;T&gt; or Uri
/// converter (aspnetcore v10.0.0).
/// </summary>
public sealed class FormCodecValueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-form-codecs-" + Guid.NewGuid().ToString("N"));

    public FormCodecValueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Additional_codec_values_bind_at_the_root_in_models_and_in_collections()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.AddTisiliaAdditionalConverters());
        builder.Services.AddTisilia(o => { o.ApiId = "form-codecs"; o.Codecs.AddAdditionalCodecs(_dir); });
        await using var app = builder.Build();
        app.MapPost("/root", ([FromForm] Int128 big, [FromForm] Version version, [FromForm] Uri link, [FromForm] IPAddress address, [FromForm] BigInteger[] values, [FromForm] Half? half) =>
                TypedResults.Ok(new[] { big.ToString(CultureInfo.InvariantCulture), version.ToString(), link.OriginalString, address.ToString(), string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture))), half?.ToString(CultureInfo.InvariantCulture) ?? "none" }))
            .DisableAntiforgery().WithTisiliaOperation("root");
        app.MapPost("/list", ([FromForm] List<UInt128> values) => TypedResults.Ok(values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()))
            .DisableAntiforgery().WithTisiliaOperation("list");
        app.MapPost("/model", ([FromForm] CodecModel model) => TypedResults.Ok(new[]
            {
                model.Signed.ToString(CultureInfo.InvariantCulture), model.Unsigned?.ToString(CultureInfo.InvariantCulture) ?? "none", model.Big.ToString(CultureInfo.InvariantCulture),
                model.Half.ToString(CultureInfo.InvariantCulture), model.Link?.OriginalString ?? "none", model.Address?.ToString() ?? "none", model.Network.ToString(),
                string.Join(",", model.Many?.Select(v => v.ToString(CultureInfo.InvariantCulture)) ?? []), string.Join(",", model.Items?.Select(i => i.Address?.ToString()) ?? []),
            }))
            .DisableAntiforgery().WithTisiliaOperation("model");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var root = Assert.IsType<FormRequestBody>(export.Index!.Operations["root"].RequestBody).Fields.ToDictionary(f => f.Name);
        Assert.Equal(AdditionalModule.Grammar("int128-string"), root["big"].GrammarId);
        Assert.Equal(AdditionalModule.Grammar("version"), root["version"].GrammarId);
        Assert.Equal(AdditionalModule.ModelId("Int128"), root["big"].Use!.TypeId);
        // a root array binds every repeated value through TryParse; a root list goes through the form mapper with indexes
        Assert.True(root["values"].Repeated); Assert.False(root["values"].Indexed);
        Assert.Equal(Presence.Optional, root["half"].Presence);
        var list = Assert.Single(Assert.IsType<FormRequestBody>(export.Index.Operations["list"].RequestBody).Fields);
        Assert.True(list.Indexed); Assert.Equal("", list.WireName);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("root", Body(new { big = "-170141183460469231731687303715884105728", version = "1.2.3.4", link = "https://example.test/a%20b?q=1&r=%26#frag", address = "fe80::1%3", values = new[] { "-123456789012345678901234567890", "0" }, half = 5.9604644775390625E-08 })),
            ("root", Body(new { big = "1", version = "0.0", link = "../relative%20path?x", address = "::ffff:1.2.3.4", values = new[] { "7" } })),
            ("list", Body(new { values = new[] { "340282366920938463463374607431768211455", "0" } })),
            ("model", Body(new { Signed = "170141183460469231731687303715884105727", Unsigned = "1", Big = "-1", Half = -65504, // Half.MinValue: ToString() writes its shortest text, -65500
                Link = "mailto:a@example.test", Address = "10.0.0.1", Network = "2001:db8::/32", Many = new[] { "1", "-2" }, Items = new[] { new { Address = "::1" }, new { Address = "192.168.0.1" } } })),
            ("root", Body(new { big = "1", version = "1.0", link = "", address = "::1", values = new[] { "1" } })),
        ]);
        Assert.Equal("200 [\"-170141183460469231731687303715884105728\",\"1.2.3.4\",\"https://example.test/a%20b?q=1&r=%26#frag\",\"fe80::1%3\",\"-123456789012345678901234567890,0\",\"6E-08\"]", sent[0]);
        Assert.Equal("200 [\"1\",\"0.0\",\"../relative%20path?x\",\"::ffff:1.2.3.4\",\"7\",\"none\"]", sent[1]);
        Assert.Equal("200 [\"340282366920938463463374607431768211455\",\"0\"]", sent[2]);
        Assert.Equal("200 [\"170141183460469231731687303715884105727\",\"1\",\"-1\",\"-65500\",\"mailto:a@example.test\",\"10.0.0.1\",\"2001:db8::/32\",\"1,-2\",\"::1,192.168.0.1\"]", sent[3]);
        // an empty value is refused before sending, as an empty parameter is
        Assert.Equal("grammar: this form value cannot be empty", sent[4]);
    }

    [Theory]
    [InlineData("module value without a grammar")]
    [InlineData("builtin value with a grammar")]
    [InlineData("file with a grammar")]
    [InlineData("unknown grammar")]
    [InlineData("parsed number")]
    [InlineData("parsed file")]
    public async Task The_contract_names_the_grammar_of_module_codec_form_values_and_only_of_them(string mutation)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => { o.ApiId = "form-codec-grammar"; o.Codecs.AddAdditionalCodecs(_dir); });
        await using var app = builder.Build();
        app.MapPost("/values", ([FromForm] Int128 big, [FromForm] long small, IFormFile file) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("values");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var root = JsonNode.Parse(export.Text!)!;
        var fields = root["operations"]!.AsArray().Single(o => (string?)o!["id"] == "values")!["requestBody"]!["fields"]!.AsArray();
        JsonNode Field(string name) => fields.Single(f => (string?)f!["name"] == name)!;
        var grammar = (string)Field("big")["grammarId"]!;
        switch (mutation)
        {
            case "module value without a grammar": Field("big").AsObject().Remove("grammarId"); break;
            case "builtin value with a grammar": Field("small")["grammarId"] = grammar; break;
            case "file with a grammar": Field("file")["grammarId"] = grammar; break;
            case "parsed number": Field("small")["serverParsed"] = true; break;
            case "parsed file": Field("file")["serverParsed"] = true; break;
            default: Field("big")["grammarId"] = AdditionalModule.Grammar("no-such-grammar"); break;
        }
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        if (loaded is not null) { SemanticValidator.Validate(loaded, bag, verifyHashes: false); }
        Assert.Contains(bag.Items, d => d.Rule is "SV29" or "SV03");
    }

    [Fact]
    public async Task A_member_of_a_type_without_IParsable_is_diagnosed_and_MVC_codec_values_are_server_parsed_text()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new CodecControllerFeature()));
        builder.Services.AddTisilia(o => { o.ApiId = "form-codec-limits"; o.Codecs.AddAdditionalCodecs(_dir); });
        await using var app = builder.Build();
        app.MapPost("/version-member", ([FromForm] VersionModel model) => TypedResults.Ok(model is null ? "no model" : model.Version?.ToString() ?? "null"))
            .DisableAntiforgery().WithTisiliaOperation("version-member");
        app.MapPost("/version-list", ([FromForm] List<Version> values) => TypedResults.Ok(values.Count))
            .DisableAntiforgery().WithTisiliaOperation("version-list");
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        foreach (var operation in new[] { "version-member", "version-list" })
        {
            Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.RelatedIds.Contains(operation) && d.Message.Contains("does not implement IParsable<T>", StringComparison.Ordinal));
        }
        // MVC reads the value with its TypeConverter and the request culture: text the contract does not check, with a warning
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Severity == DiagnosticSeverity.Warning && d.RelatedIds.Contains("mvc-codec") && d.Message.Contains("Int128's own parser", StringComparison.Ordinal));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error && d.RelatedIds.Contains("mvc-codec"));

        // Kestrel: the form mapper cannot read Version as one value, and the handler receives no model at all
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/version-member", new FormUrlEncodedContent([KeyValuePair.Create("Version", "1.2")]));
        Assert.Equal("\"no model\"", await response.Content.ReadAsStringAsync());
    }

    private static object Body(object body) => new { body };

    public sealed class CodecModel
    {
        public Int128 Signed { get; set; }
        public UInt128? Unsigned { get; set; }
        public BigInteger Big { get; set; }
        public Half Half { get; set; }
        public Uri? Link { get; set; }
        public IPAddress? Address { get; set; }
        public IPNetwork Network { get; set; }
        public List<Int128>? Many { get; set; }
        public List<CodecItem>? Items { get; set; }
    }

    public sealed class CodecItem { public IPAddress? Address { get; set; } }

    public sealed class VersionModel { public Version? Version { get; set; } }

    private sealed class CodecControllerFeature : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(CodecFormController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class CodecFormController : ControllerBase
{
    [HttpPost("/mvc-codec")]
    [TisiliaOperation("mvc-codec")]
    public ActionResult<string> Post([FromForm] Int128 value) => value.ToString(CultureInfo.InvariantCulture);
}
