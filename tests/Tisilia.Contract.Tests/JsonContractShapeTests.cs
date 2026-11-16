using System.Numerics;
using System.Text;
using System.Text.Json;
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
/// Oracle: what System.Text.Json 11 writes for the contract shapes it added, through Kestrel, next to what the exported contract says
/// of them. A closed hierarchy whose derived types the serializer infers, a naming policy or an ignore condition declared on a type, a
/// read-only set, read-only extension data and an enum member named by the empty string are described and read by the runtime; a C#
/// union and the IEEE 754 decimal and brain floating-point types are refused, as every type without a codec is.
/// </summary>
public sealed class JsonContractShapeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-json-shapes-" + Guid.NewGuid().ToString("N"));

    public JsonContractShapeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    // [JsonPolymorphic(InferClosedTypePolymorphism = true)] on a closed record: the discriminators are the derived types' names
    [InlineData("closed", """{"one":{"$type":"Circle","radius":1.5},"many":[{"$type":"Square","length":2},{"$type":"Circle","radius":3}]}""")]
    // [JsonNamingPolicy] on the type, and on a member in its place
    [InlineData("naming", """{"release_version":"11","event-name":"Launch"}""")]
    // [JsonIgnore(Condition = WhenWritingNull)] on the type: a null member is left out
    [InlineData("ignore-null", """{"id":7}""")]
    [InlineData("ignore-value", """{"notes":"n","id":7}""")]
    // the type's condition takes the place of the options' WhenWritingDefault: a member that cannot be null is always written
    [InlineData("ignore-default", """{"id":0}""")]
    // without a condition of its own, the options' WhenWritingDefault leaves both members out
    [InlineData("options-default", "{}")]
    [InlineData("set", """{"values":[1,2]}""")]
    [InlineData("readonly-extension", """{"id":1,"x":1}""")]
    // [JsonStringEnumMemberName("")] on a member of an enum that is not [Flags]: the name is the empty string
    [InlineData("blank-enum", """{"mark":"","other":"Set"}""")]
    public async Task Shapes_the_serializer_describes_are_exported_and_read_by_the_client(string shape, string written)
    {
        await using var app = await StartAsync(shape);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        using var http = new HttpClient();
        Assert.Equal(written, await http.GetStringAsync(new Uri(new Uri(app.Urls.Single()), "/shape")));

        var result = Assert.Single(await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("shape", new { })], execute: true));
        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("kind").GetString() == "response", result);
        var data = document.RootElement.GetProperty("data");
        if (shape == "readonly-extension")
        {
            Assert.Equal("x", data.GetProperty("extensions").GetProperty("$entries")[0][0].GetString());
            return;
        }

        // the client holds what the server wrote, member for member; an enum's members by their values
        Assert.Equal(shape == "blank-enum" ? """{"mark":0,"other":1}""" : written, data.GetRawText());
    }

    [Theory]
    [InlineData("union", "Reading")]
    [InlineData("decimal64", "Decimal64")]
    [InlineData("bfloat16", "BFloat16")]
    public async Task Shapes_without_a_codec_are_refused(string shape, string type)
    {
        await using var app = await StartAsync(shape);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV03" && d.Message.Contains(type, StringComparison.Ordinal));
    }

    /// <summary>
    /// A type classifier names the derived type the server reads, whatever the discriminator says, so a request body of such a type is
    /// refused. It has no part in writing: the same type in a response is exported and read.
    /// </summary>
    [Fact]
    public async Task A_type_classifier_decides_what_the_server_reads_so_the_request_is_refused()
    {
        await using (var reading = await StartAsync("classified-request"))
        {
            using var http = new HttpClient();
            using var content = new StringContent("""{"$type":"cat","name":"Tama","lives":9}""", Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(new Uri(reading.Urls.Single()), "/shape"), content);
            Assert.Equal("Dog", await response.Content.ReadAsStringAsync());

            var refused = reading.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
            Assert.Null(refused.Text);
            Assert.Contains(refused.Diagnostics.Items, d => d.Rule == "SV03" && d.Message.Contains("type classifier", StringComparison.Ordinal));
        }

        await using var writing = await StartAsync("classified-response");
        var export = writing.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join(Environment.NewLine, export.Diagnostics.Items));
        var result = Assert.Single(await InterpreterRequests.SendAsync(_dir, export.Text!, writing.Urls.Single(), [("shape", new { })], execute: true));
        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("kind").GetString() == "response", result);
    }

    private static async Task<WebApplication> StartAsync(string shape)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "json-shapes");
        if (shape is "ignore-default" or "options-default")
        {
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault);
        }

        var app = builder.Build();
        var endpoint = shape switch
        {
            "closed" => app.MapGet("/shape", () => new ShapeHolder(new Circle(1.5), [new Square(2), new Circle(3)])),
            "naming" => app.MapGet("/shape", () => new Named("11", "Launch")),
            "ignore-null" => app.MapGet("/shape", () => new Sparse(null, 7)),
            "ignore-value" => app.MapGet("/shape", () => new Sparse("n", 7)),
            "ignore-default" => app.MapGet("/shape", () => new Sparse(null, 0)),
            "options-default" => app.MapGet("/shape", () => new Plain(null, 0)),
            "set" => app.MapGet("/shape", () => new Sets(new HashSet<int> { 1, 2 })),
            "readonly-extension" => app.MapGet("/shape", () => new ReadOnlyExtra { Id = 1, Extra = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(1) } }),
            "blank-enum" => app.MapGet("/shape", () => new Marked(Mark.None, Mark.Set)),
            "classified-request" => app.MapPost("/shape", (Pet pet) => pet.GetType().Name),
            "classified-response" => app.MapGet("/shape", () => (Pet)new Cat("Tama", 9)),
            "union" => app.MapGet("/shape", () => new UnionHolder(new Reading("hello"), new Reading(42))),
            "decimal64" => app.MapGet("/shape", () => new Money((Decimal64)12.5m)),
            _ => app.MapGet("/shape", () => new Small((BFloat16)1.5f)),
        };
        endpoint.WithTisiliaOperation("shape");
        await app.StartAsync();
        return app;
    }

    public union Reading(int, string);

    public sealed record UnionHolder(Reading First, Reading Second);

    [JsonPolymorphic(InferClosedTypePolymorphism = true)]
    public closed record Shape;

    public sealed record Circle(double Radius) : Shape;

    public sealed record Square(double Length) : Shape;

    public sealed record ShapeHolder(Shape One, Shape[] Many);

    [JsonNamingPolicy(JsonKnownNamingPolicy.SnakeCaseLower)]
    public sealed record Named(string ReleaseVersion, [property: JsonNamingPolicy(JsonKnownNamingPolicy.KebabCaseLower)] string EventName);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public sealed record Sparse(string? Notes, int Id);

    public sealed record Plain(string? Notes, int Id);

    public sealed record Sets(IReadOnlySet<int> Values);

    [JsonPolymorphic(TypeClassifier = typeof(AlwaysDog))]
    [JsonDerivedType(typeof(Cat), "cat")]
    [JsonDerivedType(typeof(Dog), "dog")]
    public abstract record Pet(string Name);

    public sealed record Cat(string Name, int Lives) : Pet(Name);

    public sealed record Dog(string Name, bool Barks) : Pet(Name);

    public sealed class AlwaysDog : JsonTypeClassifierFactory
    {
        public override bool CanClassify(JsonTypeClassifierContext context) => true;

        public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options) => (ref Utf8JsonReader reader) => typeof(Dog);
    }

    [JsonConverter(typeof(JsonStringEnumConverter<Mark>))]
    public enum Mark
    {
        [JsonStringEnumMemberName("")] None,
        Set,
    }

    public sealed record Marked(Mark Mark, Mark Other);

    public sealed record Money(Decimal64 Amount);

    public sealed record Small(BFloat16 Value);

    public sealed class ReadOnlyExtra
    {
        public int Id { get; set; }

        [JsonExtensionData]
        public IReadOnlyDictionary<string, JsonElement>? Extra { get; set; }
    }
}
