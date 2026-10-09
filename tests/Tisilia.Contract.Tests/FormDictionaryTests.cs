using System.Globalization;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: the minimal API form mapper reads a dictionary from name[key] and a root dictionary from [key]; the key is the text up to
/// the first ']', parsed with the invariant culture, and keys are gathered ignoring case (DictionaryConverter, FormDataReader.GetKeys,
/// aspnetcore v10.0.0). The runtime's encoder, from the exported contract, writes those keys on Kestrel and refuses ']' and keys
/// that differ only in case before sending.
/// </summary>
public sealed class FormDictionaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-form-maps-" + Guid.NewGuid().ToString("N"));

    public FormDictionaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Dictionaries_bind_from_name_and_key_at_the_root_and_in_models()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-maps");
        await using var app = builder.Build();
        app.MapPost("/root", ([FromForm] Dictionary<string, string> values) => TypedResults.Ok(Echo(values)))
            .DisableAntiforgery().WithTisiliaOperation("root");
        app.MapPost("/model", ([FromForm] MapModel model) => TypedResults.Ok(new[] { Echo(model.Labels), Echo(model.Counts), Echo(model.Ids), Echo(model.Modes) }))
            .DisableAntiforgery().WithTisiliaOperation("model");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var root = Assert.Single(Assert.IsType<FormRequestBody>(export.Index!.Operations["root"].RequestBody).Fields);
        Assert.Equal(("map", ""), (root.Kind, root.WireName));
        var counts = Assert.IsType<FormRequestBody>(export.Index.Operations["model"].RequestBody).Fields.Single(f => f.Name == "Counts");
        Assert.Equal("map", counts.Kind);
        Assert.Equal("tisilia.int64@0.1", Assert.IsType<PrimitiveShape>(export.Index.Types[counts.KeyUse!.TypeId].Shape).PrimitiveId);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("root", Body(new { values = Map(("a.b", "1"), ("x[y", "2"), ("", "empty"), ("日本", "3")) })),
            ("model", Body(new
            {
                Labels = Map(("first", "one"), ("second", "two")),
                Counts = Map(("-9007199254740993", "9007199254740993"), ("0", "-1")),
                Ids = Map(("0f8fad5b-d9cb-469f-a165-70867728950e", "true")),
                Modes = Map(("1", "Final")),
            })),
            ("root", Body(new { values = Map(("bad]key", "1")) })),
            ("root", Body(new { values = Map(("k", "1"), ("K", "2")) })),
        ]);
        Assert.Equal("200 \"=empty|a.b=1|x[y=2|日本=3\"", sent[0]);
        Assert.Equal("200 [\"first=one|second=two\",\"-9007199254740993=9007199254740993|0=-1\",\"0f8fad5b-d9cb-469f-a165-70867728950e=True\",\"1=Final\"]", sent[1]);
        Assert.Equal("grammar: form dictionary keys cannot contain ']'", sent[2]);
        Assert.Equal("grammar: form dictionary keys must differ ignoring case", sent[3]);

        // Kestrel: what the refusals avoid — the key ends at its first ']' and loses its value, and keys that differ only in case become one entry
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var bracket = await client.PostAsync("/root", new FormUrlEncodedContent([KeyValuePair.Create("[bad]key]", "1")]));
        Assert.Equal("\"bad=\"", await bracket.Content.ReadAsStringAsync());
        using var cased = await client.PostAsync("/root", new FormUrlEncodedContent([KeyValuePair.Create("[k]", "1"), KeyValuePair.Create("[K]", "2")]));
        Assert.Matches("^\"[kK]=[^|]*\"$", await cased.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("enum key", "form dictionary keys must be strings, integers or Guids")]
    [InlineData("model values", "form dictionary values must be builtin scalars")]
    [InlineData("unclosed bracket", "cannot have a field name with a '[' that no ']' follows")]
    public async Task Dictionaries_the_contract_cannot_describe_exactly_are_diagnosed(string shape, string message)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-map-limits");
        await using var app = builder.Build();
        var endpoint = shape switch
        {
            "enum key" => app.MapPost("/maps", ([FromForm] EnumKeyed model) => TypedResults.NoContent()),
            "model values" => app.MapPost("/maps", ([FromForm] ModelValued model) => TypedResults.NoContent()),
            _ => app.MapPost("/maps", ([FromForm] UnclosedNamed model) => TypedResults.NoContent()),
        };
        endpoint.DisableAntiforgery().WithTisiliaOperation("maps");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Message.Contains(message, StringComparison.Ordinal));
    }

    private static string Echo<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? values) =>
        values is null ? "none" : string.Join("|", values.Select(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture) + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

    private static object Body(object body) => new { body };

    /// <summary>A map field's value for <see cref="InterpreterRequests"/>: editor texts the field's codecs read.</summary>
    private static object Map(params (string Key, string Value)[] entries) => new Dictionary<string, object> { ["$map"] = entries.Select(e => new[] { e.Key, e.Value }).ToArray() };

    public sealed class MapModel
    {
        public Dictionary<string, string>? Labels { get; set; }
        public IDictionary<long, long>? Counts { get; set; }
        public IReadOnlyDictionary<Guid, bool>? Ids { get; set; }
        public Dictionary<int, MapMode>? Modes { get; set; }
    }

    public enum MapMode { Draft, Final }

    public sealed class EnumKeyed { public Dictionary<MapMode, string>? Values { get; set; } }

    public sealed class ModelValued { public Dictionary<string, MapModel>? Values { get; set; } }

    public sealed class UnclosedNamed
    {
        public Dictionary<string, string>? Values { get; set; }
        [DataMember(Name = "q[")] public string? Opening { get; set; }
    }
}
