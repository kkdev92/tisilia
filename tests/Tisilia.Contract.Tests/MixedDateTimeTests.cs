using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.AspNetCore.Conformance;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class MixedDateTimeTests
{
    // .NET oracle: JsonHelpers.Date.TryParseAsISO in dotnet/runtime v10.0.0, recorded in tests/datetime-sources.txt.
    [Fact]
    public async Task Mixed_JSON_Kinds_and_dictionary_keys_export_without_a_declaration_and_match_Kestrel()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "mixed-datetime");
        await using var app = builder.Build();
        var calls = 0;
        app.MapPost("/time", (Payload value) => { calls++; return TypedResults.Ok(value); }).WithTisiliaOperation("time");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items)); Assert.Equal(0, calls);
        Assert.Contains("tisilia.datetime@0.1", export.Text, StringComparison.Ordinal);
        var suite = SuiteBuilder.Build(export.Index!, ContractClosure.Compute(export.Index!, ["time"]), new SuiteOptions { CasesPerCodec = 40, TimeZoneId = TimeZoneInfo.Local.Id });
        Assert.NotEmpty(suite.Cases.OfType<RequestRoundTripCase>());
        Assert.Contains(suite.Cases.OfType<RequestRoundTripCase>(), t => export.Index!.Types[export.Index.Codecs[t.CodecId].TypeId].Shape is MapShape);
        Assert.Contains(suite.Cases.OfType<RequestRoundTripCase>(), t => export.Index!.Types[export.Index.Codecs[t.CodecId].TypeId].Shape is MapShape && DomainAst.ToJsonText(t.Domain) != DomainAst.ToJsonText(t.Expected));
        var runner = new DotnetRunnerCore(export.Index!, export.Adapters!, []);
        foreach (var test in suite.Cases.OfType<RequestRoundTripCase>())
        {
            // Canonical domain strings of builtin DateTime values are already valid STJ input.
            var actual = runner.Handle(RunnerProtocol.Request("dates", test.Id, RunnerAction.DotnetRead, test.CodecId, test.ProfileId, [], [test.Domain]));
            Assert.Equal(DomainAst.ToJsonText(test.Expected), DomainAst.ToJsonText(actual));
        }
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var text in new[] { "2026-09-30T06:04:05.1234567Z", "2026-09-30T06:04:05.1234567", "2026-09-30T15:04:05.1234567+09:00", "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999" })
        {
            var input = "{\"at\":\"" + text + "\",\"optional\":null,\"items\":[\"" + text + "\"],\"keys\":{\"" + text + "\":\"exact\"}}";
            using var response = await client.PostAsync("/time", new StringContent(input, Encoding.UTF8, "application/json"));
            Assert.Equal(200, (int)response.StatusCode);
            var value = JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(text));
            var expected = JsonSerializer.Serialize(value);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expected, body.RootElement.GetProperty("at").GetRawText());
            Assert.Equal(expected, body.RootElement.GetProperty("items")[0].GetRawText());
            Assert.Equal(JsonSerializer.Deserialize<string>(expected), body.RootElement.GetProperty("keys").EnumerateObject().Single().Name);
        }
    }

    public sealed record Payload(DateTime At, DateTime? Optional, DateTime[] Items, Dictionary<DateTime, string> Keys);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateTime_form_fields_preserve_precision_and_observe_each_framework_binder(bool multipart)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "date-forms");
        await using var app = builder.Build();
        app.MapPost("/scalar", ([Microsoft.AspNetCore.Mvc.FromForm] DateTime at) => new { value = at, kind = at.Kind.ToString(), ticks = at.Ticks }).DisableAntiforgery().WithTisiliaOperation("scalar");
        app.MapPost("/model", ([Microsoft.AspNetCore.Mvc.FromForm] DateForm model) => new { value = model.At, kind = model.At.Kind.ToString(), ticks = model.At.Ticks }).DisableAntiforgery().WithTisiliaOperation("model");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var text in new[] { "2026-09-30T06:04:05.1234567Z", "2026-09-30T06:04:05.1234567", "2026-09-30T15:04:05.1234567+09:00" })
        {
            foreach (var path in new[] { "scalar", "model" })
            {
                var name = path == "scalar" ? "at" : "At";
                using var content = multipart ? (HttpContent)new MultipartFormDataContent() : new FormUrlEncodedContent([new KeyValuePair<string, string>(name, text)]);
                if (content is MultipartFormDataContent multi) { multi.Add(new StringContent(text), name); }
                using var response = await client.PostAsync("/" + path, content);
                Assert.Equal(200, (int)response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var styles = path == "scalar" ? System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces : System.Globalization.DateTimeStyles.None;
                var expected = DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, styles);
                Assert.Equal(expected.Kind.ToString(), body.RootElement.GetProperty("kind").GetString());
                Assert.Equal(expected.Ticks, body.RootElement.GetProperty("ticks").GetInt64());
            }
        }
    }

    public sealed class DateForm { public DateTime At { get; set; } }

    [Fact]
    public async Task DateTime_dictionary_keys_must_honor_application_converters()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "date-key-converter");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new CustomDateConverter()));
        await using var app = builder.Build();
        app.MapGet("/keys", () => new Dictionary<DateTime, string>()).WithTisiliaOperation("keys");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors);
        Assert.Contains(export.Diagnostics.Items, d => d.Message.Contains("custom converter", StringComparison.Ordinal));
    }

    private sealed class CustomDateConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
