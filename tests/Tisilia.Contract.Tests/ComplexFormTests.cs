using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Documents;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
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

public sealed class ComplexFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collection_models_and_constructor_objects_bind_exact_indexed_paths(bool multipart)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "complex-forms");
        await using var app = builder.Build();
        app.MapPost("/nested", ([FromForm] Order value) => value).DisableAntiforgery().WithTisiliaOperation("nested");
        app.MapPost("/root", ([FromForm] List<Line> values) => values).DisableAntiforgery().WithTisiliaOperation("root");
        app.MapPost("/array", ([FromForm] Line[] values) => values).DisableAntiforgery().WithTisiliaOperation("array");
        app.MapPost("/scalars", ([FromForm] List<long> values) => values).DisableAntiforgery().WithTisiliaOperation("scalars");
        app.MapPost("/files", ([FromForm] FileOrder value) => value.Items!.Select(i => new { i.Title, name = i.File!.FileName, size = i.File.Length }).ToArray())
            .DisableAntiforgery().WithTisiliaOperation("files");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        Assert.Equal("multipart/form-data", Assert.IsType<FormRequestBody>(exported.Index!.Operations["files"].RequestBody).MediaType);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var endpoint in new[] { "nested", "root", "array" })
        {
            var prefix = endpoint == "nested" ? "Lines" : "";
            var pairs = new List<KeyValuePair<string, string>> {
                new(prefix + "[0].Id", "9007199254740993"), new(prefix + "[0].Details.Label", "日本😀"),
                new(prefix + "[0].Details.Tags[0]", "one"), new(prefix + "[0].Details.Tags[1]", "two"),
                new(prefix + "[1].Id", "-9007199254740993"), new(prefix + "[1].Details.Label", "second"),
                new(prefix + "[1].Details.Tags[0]", "three") };
            using var content = Content(pairs, multipart);
            using var response = await client.PostAsync("/" + endpoint, content);
            Assert.Equal(200, (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var lines = endpoint == "nested" ? json.RootElement.GetProperty("lines") : json.RootElement;
            Assert.Equal(2, lines.GetArrayLength());
            Assert.Equal(9007199254740993L, lines[0].GetProperty("id").GetInt64());
            Assert.Equal(-9007199254740993L, lines[1].GetProperty("id").GetInt64());
            Assert.Equal("日本😀", lines[0].GetProperty("details").GetProperty("label").GetString());
            Assert.Equal(2, lines[0].GetProperty("details").GetProperty("tags").GetArrayLength());
        }
        using var scalarContent = Content([new("[0]", "9007199254740993"), new("[1]", "-9007199254740993")], multipart);
        using var scalarResponse = await client.PostAsync("/scalars", scalarContent);
        Assert.Equal("[9007199254740993,-9007199254740993]", await scalarResponse.Content.ReadAsStringAsync());
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent("日本"), "Items[0].Title"); upload.Add(new ByteArrayContent([0, 255, 42]), "Items[0].File", "sample.bin");
        using var fileResponse = await client.PostAsync("/files", upload);
        Assert.Equal(200, (int)fileResponse.StatusCode);
        using var fileJson = JsonDocument.Parse(await fileResponse.Content.ReadAsStringAsync());
        Assert.Equal("sample.bin", fileJson.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(3, fileJson.RootElement[0].GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Named_files_bind_on_a_model_but_framework_does_not_terminate_collections_of_file_lists()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "file-lists");
        await using var app = builder.Build();
        app.MapPost("/single", ([FromForm] NamedFiles model) => model.Files!.Count).DisableAntiforgery().WithTisiliaOperation("single");
        app.MapPost("/collection", ([FromForm] FileLists model) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("collection");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var single = new MultipartFormDataContent();
        single.Add(new ByteArrayContent([1]), "Files", "one.bin"); single.Add(new ByteArrayContent([2]), "Files", "two.bin");
        using var singleResponse = await client.PostAsync("/single", single);
        Assert.Equal("2", await singleResponse.Content.ReadAsStringAsync());
        using var collection = new MultipartFormDataContent();
        collection.Add(new ByteArrayContent([1]), "Items[0].Files", "one.bin");
        using var collectionResponse = await client.PostAsync("/collection", collection);
        Assert.Equal(400, (int)collectionResponse.StatusCode);
        // FileConverter reports found=true even when GetFiles returns an empty list, so the collection never terminates.
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Contains(exported.Diagnostics.Items, d => d.Rule == "SV30" && d.RelatedIds.Contains("collection"));
        Assert.DoesNotContain(exported.Diagnostics.Items, d => d.RelatedIds.Contains("single"));
    }

    public sealed class NamedFiles { public IReadOnlyList<IFormFile>? Files { get; set; } }
    public sealed class FileLists { public List<NamedFiles>? Items { get; set; } }

    [Theory]
    [InlineData("empty")]
    [InlineData("unindexed")]
    [InlineData("scalar-children")]
    [InlineData("nested-root")]
    [InlineData("overlap")]
    [InlineData("two-roots")]
    [InlineData("disjoint-indexes")]
    public void Malformed_object_form_contracts_are_rejected(string mutation)
    {
        var root = SampleContracts.UsersApiJson();
        var op = root["operations"]![0]!;
        op["method"] = "POST";
        var field = JsonNode.Parse("""
            {"name":"Rows","kind":"object","repeated":true,"indexed":true,"presence":"required","fields":[
              {"name":"File","kind":"file","repeated":false,"presence":"required"}]}
            """)!;
        if (mutation == "empty") { field["fields"] = new JsonArray(); }
        if (mutation == "unindexed") { field["indexed"] = false; }
        if (mutation == "scalar-children") { field["kind"] = "file"; }
        if (mutation == "nested-root") { field["fields"]![0]!["wireName"] = ""; }
        var fields = new JsonArray(field);
        if (mutation == "overlap") { var duplicate = field["fields"]![0]!.DeepClone(); duplicate["name"] = "Rows[0].File"; fields.Add(duplicate); }
        if (mutation == "two-roots") { field["wireName"] = ""; var duplicate = field.DeepClone(); duplicate["name"] = "Other"; duplicate["fields"]![0]!["name"] = "OtherFile"; fields.Add(duplicate); }
        if (mutation == "disjoint-indexes")
        {
            var first = field["fields"]![0]!.DeepClone(); first["name"] = "Files[0]";
            var second = first.DeepClone(); second["name"] = "Files[1]";
            fields = new JsonArray(first, second);
        }
        op["requestBody"] = new JsonObject { ["kind"] = "form", ["mediaType"] = "multipart/form-data", ["presence"] = "required", ["fields"] = fields };
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        if (loaded is not null) { SemanticValidator.Validate(loaded, bag, verifyHashes: false); }
        if (mutation == "disjoint-indexes") { Assert.False(bag.HasErrors, string.Join("\n", bag.Items)); }
        else { Assert.Contains(bag.Items, d => d.Rule == "SV30" || d.Code == TisiliaCodes.SchemaViolation); }
    }

    private static HttpContent Content(IEnumerable<KeyValuePair<string, string>> pairs, bool multipart)
    {
        if (!multipart) { return new FormUrlEncodedContent(pairs); }
        var content = new MultipartFormDataContent();
        foreach (var pair in pairs) { content.Add(new StringContent(pair.Value), pair.Key); }
        return content;
    }

    public sealed record Order(List<Line> Lines);
    public sealed record Line(long Id, Detail Details);
    public sealed record Detail(string Label, string[] Tags);
    public sealed class FileOrder { public List<FileLine>? Items { get; set; } }
    public sealed class FileLine { public string? Title { get; set; } public IFormFile? File { get; set; } }
}
