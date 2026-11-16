using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: System.Text.Json writes the entries of a <c>[JsonExtensionData]</c> member as members of the object that holds it, a
/// JsonObject's as a dictionary's (JsonConverter&lt;T&gt;.TryWriteDataExtensionProperty and JsonObjectConverter.WriteExtensionDataValue,
/// dotnet/runtime v11.0.0). The contract captures them, and the runtime reads them from Kestrel.
/// </summary>
public sealed class ExtensionDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-extension-data-" + Guid.NewGuid().ToString("N"));

    public ExtensionDataTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("object")]
    [InlineData("dictionary")]
    public async Task Extension_data_of_a_response_is_written_as_members_and_read_by_the_client(string shape)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "extension-data");
        await using var app = builder.Build();
        app.MapGet("/object", () => new WithObject { Id = 1, Extra = new JsonObject { ["x"] = 1, ["nested"] = new JsonObject { ["y"] = "z" } } })
            .WithTisiliaOperation("object");
        app.MapGet("/dictionary", () => new WithDictionary
        {
            Id = 1,
            Extra = new() { ["x"] = JsonSerializer.SerializeToElement(1), ["nested"] = JsonSerializer.SerializeToElement(new { y = "z" }) },
        }).WithTisiliaOperation("dictionary");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        using var http = new HttpClient();
        Assert.Equal("{\"id\":1,\"x\":1,\"nested\":{\"y\":\"z\"}}", await http.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/" + shape)));

        var result = Assert.Single(await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [(shape, new { })], execute: true));
        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("kind").GetString() == "response", result);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("id").GetInt32());
        var entries = data.GetProperty("extensions").GetProperty("$entries").EnumerateArray().Select(entry => entry[0].GetString()!).ToArray();
        Assert.Equal(new[] { "x", "nested" }, entries);
    }

    public sealed class WithObject
    {
        public int Id { get; set; }

        [JsonExtensionData]
        public JsonObject? Extra { get; set; }
    }

    public sealed class WithDictionary
    {
        public int Id { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
