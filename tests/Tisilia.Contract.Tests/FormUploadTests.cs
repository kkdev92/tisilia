using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
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

public sealed class FormUploadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forms_export_and_bind_exact_values_without_disabling_antiforgery(bool multipart)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "forms");
        builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        await using var app = builder.Build(); app.UseAntiforgery();
        var calls = 0;
        app.MapGet("/token", (HttpContext c, IAntiforgery af) => af.GetAndStoreTokens(c).RequestToken!);
        app.MapPost("/form", ([FromForm] string value, [FromForm] long id, [FromForm] decimal amount, [FromForm] int[] tags) =>
        {
            calls++;
            return new FormEcho(value, id, amount, tags);
        }).Accepts<IFormCollection>(multipart ? "multipart/form-data" : "application/x-www-form-urlencoded").WithTisiliaOperation("form");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items)); Assert.Equal(0, calls);
        using var contract = JsonDocument.Parse(exported.Text!);
        var operation = contract.RootElement.GetProperty("operations")[0];
        Assert.Equal("form", operation.GetProperty("requestBody").GetProperty("kind").GetString());
        Assert.Contains("antiforgery", operation.GetProperty("security").GetProperty("csrfPolicyId").GetString()!, StringComparison.Ordinal);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var token = await client.GetStringAsync("/token");
        var pairs = new KeyValuePair<string, string>[] { new("value", "日本😀\r\n+&="), new("id", "9007199254740993"), new("amount", "1234567890.123456789"), new("tags", "1"), new("tags", "2") };
        HttpContent Content()
        {
            if (!multipart) { return new FormUrlEncodedContent(pairs); }
            var body = new MultipartFormDataContent(); foreach (var p in pairs) { body.Add(new StringContent(p.Value), p.Key); }
            return body;
        }
        using var denied = await client.PostAsync("/form", Content()); Assert.Equal(400, (int)denied.StatusCode); Assert.Equal(0, calls);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var accepted = await client.PostAsync("/form", Content());
        Assert.Equal(200, (int)accepted.StatusCode);
        using var json = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.Equal("日本😀\r\n+&=", json.RootElement.GetProperty("value").GetString());
        Assert.Equal(9007199254740993L, json.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(1234567890.123456789m, json.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(2, json.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_parts_export_and_bind_single_or_multiple_exact_bytes(bool multiple)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "files");
        await using var app = builder.Build();
        static async Task<string> Read(IFormFile file) { using var buffer = new MemoryStream(); await file.CopyToAsync(buffer); return file.FileName + ":" + Convert.ToHexString(buffer.ToArray()); }
        if (multiple) { app.MapPost("/file", async (IFormFileCollection files) => { var result = new List<string>(); foreach (var f in files) { result.Add(await Read(f)); } return result.ToArray(); }).DisableAntiforgery().WithTisiliaOperation("file"); }
        else { app.MapPost("/file", (IFormFile file) => Read(file)).DisableAntiforgery().WithTisiliaOperation("file"); }
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        using var contract = JsonDocument.Parse(exported.Text!);
        Assert.Equal("tisilia.csrf.none@0.1", contract.RootElement.GetProperty("operations")[0].GetProperty("security").GetProperty("csrfPolicyId").GetString());
        using var body = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0, 255, 195, 40]); bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(bytes, multiple ? "files" : "file", "sample.bin");
        if (multiple) { body.Add(new ByteArrayContent([]), "files", "empty.bin"); }
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await client.PostAsync("/file", body); response.EnsureSuccessStatusCode();
        Assert.Contains("sample.bin:00FFC328", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public sealed record FormEcho(string Value, long Id, decimal Amount, int[] Tags);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_model_uses_form_names_indexed_collections_and_exact_enums(bool multipart)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "nested-forms");
        await using var app = builder.Build();
        app.MapPost("/nested", ([FromForm] NestedForm model) => new { model.Title, model.Details!.Id, model.Details.Tags, mode = (long)model.Mode })
            .DisableAntiforgery().WithTisiliaOperation("nested");
        app.MapPost("/enum", ([FromForm] FormMode mode) => (long)mode).DisableAntiforgery().WithTisiliaOperation("enum");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        var form = Assert.IsType<Tisilia.Contract.FormRequestBody>(exported.Index!.Operations["nested"].RequestBody);
        Assert.Equal(new[] { "Details.Id", "Details.Tags", "Mode", "title_text" }, form.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.True(form.Fields.Single(f => f.Name == "Details.Tags").Indexed);
        var pairs = new KeyValuePair<string, string>[] { new("title_text", "日本😀"), new("Details.Id", "9007199254740993"), new("Details.Tags[0]", "one"), new("Details.Tags[1]", "two"), new("Mode", "Large") };
        using var content = multipart ? (HttpContent)new MultipartFormDataContent() : new FormUrlEncodedContent(pairs);
        if (content is MultipartFormDataContent multi) { foreach (var p in pairs) { multi.Add(new StringContent(p.Value), p.Key); } }
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/nested", content); Assert.Equal(200, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("日本😀", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(9007199254740993L, json.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(new[] { "one", "two" }, json.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(9007199254740993L, json.RootElement.GetProperty("mode").GetInt64());
        using var enumResponse = await client.PostAsync("/enum", new FormUrlEncodedContent([new KeyValuePair<string, string>("mode", "Large")]));
        Assert.Equal("9007199254740993", await enumResponse.Content.ReadAsStringAsync());
    }

    public enum FormMode : long { Small = 1, Large = 9007199254740993L }
    public sealed class NestedForm
    {
        [System.Runtime.Serialization.DataMember(Name = "title_text", IsRequired = true)]
        public string? Title { get; set; }
        public FormDetails? Details { get; set; }
        public FormMode Mode { get; set; }
        [System.Runtime.Serialization.IgnoreDataMember]
        public object? Ignored { get; set; }
    }
    public sealed class FormDetails
    {
        public long Id { get; set; }
        public string[]? Tags { get; set; }
    }

    [Fact]
    public async Task Constructor_bound_form_record_exports_required_fields_and_binds_names()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "record-forms");
        await using var app = builder.Build();
        app.MapPost("/record", ([FromForm] FormRecord model) => model).DisableAntiforgery().WithTisiliaOperation("record");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        var form = Assert.IsType<Tisilia.Contract.FormRequestBody>(exported.Index!.Operations["record"].RequestBody);
        Assert.All(form.Fields, f => Assert.Equal(Tisilia.Contract.Presence.Required, f.Presence));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var result = await client.PostAsync("/record", new FormUrlEncodedContent([new("record_title", "日本"), new("Id", "9007199254740993")]));
        Assert.Equal(200, (int)result.StatusCode);
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("日本", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(9007199254740993L, json.RootElement.GetProperty("id").GetInt64());
    }
    public sealed record FormRecord([property: System.Runtime.Serialization.DataMember(Name = "record_title")] string Title, long Id);

    [Theory]
    [InlineData("recursive")]
    public async Task Unrepresentable_form_models_fail_closed(string shape)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTisilia(o => o.ApiId = "unsupported-forms");
        await using var app = builder.Build();
        var endpoint = shape switch
        {
            "recursive" => app.MapPost("/form", ([FromForm] RecursiveForm value) => TypedResults.NoContent()),
            "complex-list" => app.MapPost("/form", ([FromForm] ComplexListForm value) => TypedResults.NoContent()),
            _ => app.MapPost("/form", ([FromForm] List<int> value) => TypedResults.NoContent()),
        };
        endpoint.DisableAntiforgery().WithTisiliaOperation("unsupported");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(exported.Diagnostics.HasErrors); Assert.Null(exported.Root);
        Assert.Contains(exported.Diagnostics.Items, d => d.Rule == "SV30");
    }
    public sealed class RecursiveForm { public RecursiveForm? Next { get; set; } }
    public sealed class ComplexListForm { public List<FormDetails>? Details { get; set; } }
}
