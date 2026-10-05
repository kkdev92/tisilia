using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Xunit;
using Xunit.Abstractions;

namespace Tisilia.Contract.Tests;

/// <summary>Independent ASP.NET Core oracle: real Kestrel binding, metadata and bytes, without using Tisilia's URL builder.</summary>
public sealed class AdoptionOracleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("optional-int", Presence.Optional, 200, "0")]
    [InlineData("optional-nullable", Presence.Optional, 200, "-1")]
    [InlineData("optional-bind-required", Presence.Required, 400, null)]
    [InlineData("optional-required-int", Presence.Required, 400, null)]
    [InlineData("optional-required-nullable", Presence.Required, 400, null)]
    [InlineData("optional-required-string", Presence.Required, 400, null)]
    public async Task MVC_optional_route_presence_matches_missing_value_binding(string route, Presence presence, int status, string? body)
    {
        await using var fixture = await AdoptionFixture.Start(register: true);
        using var response = await fixture.Client.GetAsync("/oracle-mvc/" + route);
        Assert.Equal(status, (int)response.StatusCode);
        if (body is not null) { Assert.Equal(body, await response.Content.ReadAsStringAsync()); }
        var exported = fixture.App.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        var contract = JsonSerializer.Deserialize<ContractDocument>(exported.Text!, TisiliaJson.Options)!;
        Assert.Equal(presence, Assert.Single(contract.Operations.Single(o => o.Id == route).Parameters).Presence);
        using var supplied = await fixture.Client.GetAsync("/oracle-mvc/" + route + "/7");
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        Assert.Equal("7", await supplied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NI08_Cookie_challenge_separates_Produces_metadata_from_registration()
    {
        async Task<(int Status, string? Location)> Observe(bool register, bool produces)
        {
            var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Services.AddAuthentication("Cookies").AddCookie("Cookies"); b.Services.AddAuthorization();
            if (register) { b.Services.AddTisilia(o => o.ApiId = "cookie-oracle"); }
            await using var app = b.Build(); app.UseAuthentication(); app.UseAuthorization();
            var endpoint = app.MapGet("/challenge", (Func<IResult>)(() => Results.Ok(1))).RequireAuthorization();
            if (produces) { endpoint.Produces<int>(200); }
            if (register) { endpoint.WithTisiliaOperation("challenge"); }
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/challenge");
            return ((int)response.StatusCode, response.Headers.Location?.PathAndQuery);
        }
        foreach (var produces in new[] { false, true })
        {
            var before = await Observe(false, produces); var after = await Observe(true, produces);
            output.WriteLine($"Produces={produces}: before={before}; after={after}");
            Assert.Equal(before, after); Assert.Contains(after.Status, new[] { 302, 401 });
        }
    }

    [Fact]
    public async Task RT26_TestServer_and_Kestrel_are_observed_separately()
    {
        await using var kestrel = await AdoptionFixture.Start();
        await using var testServer = await AdoptionFixture.Start(testServer: true);
        foreach (var path in new[] { "/base/v1/optional", "/base/v1/default/1", "/base/v1/complex/report.v1.pdf", "/base/v1/star/a%2Fb", "/base/v1/stars/a//b/", "/base/v1/stars/%252e" })
        {
            var network = await kestrel.Client.GetStringAsync(path);
            var memory = await testServer.Client.GetStringAsync(path);
            output.WriteLine($"{path}: Kestrel={network}; TestServer={memory}");
            Assert.Equal(network, memory);
        }
    }

    [Fact]
    public async Task NI02_Registration_preserves_configured_JSON_naming_null_enum_precision_converter_and_polymorphism()
    {
        await using var before = await AdoptionFixture.Start();
        await using var after = await AdoptionFixture.Start(register: true);
        var expected = await before.Client.GetStringAsync("/v1/json-settings");
        var actual = await after.Client.GetStringAsync("/v1/json-settings");
        Assert.Equal(expected, actual);
        Assert.Contains("\"nullable_name\":null", actual, StringComparison.Ordinal);
        Assert.Contains("9007199254740993", actual, StringComparison.Ordinal);
        Assert.Contains("1234567890.123456789", actual, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"Ready\"", actual, StringComparison.Ordinal);
        Assert.Contains("\"custom\":\"custom:7\"", actual, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"child\"", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task P0_Export_resolves_routes_and_binary_without_changing_handlers()
    {
        await using var fixture = await AdoptionFixture.Start(register: true);
        var result = fixture.App.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Items));
        Assert.NotNull(result.Text);
        var contract = JsonSerializer.Deserialize<ContractDocument>(result.Text, TisiliaJson.Options)!;
        Assert.Equal("0.1", contract.Version);
        Assert.Equal(0, fixture.Calls);
        var optional = contract.Operations.Single(o => o.Id == "optional");
        Assert.Equal(Presence.Optional, Assert.Single(optional.Parameters).Presence);
        Assert.Equal(Presence.Required, Assert.Single(contract.Operations.Single(o => o.Id == "required").Parameters).Presence);
        Assert.Equal(Presence.Optional, Assert.Single(contract.Operations.Single(o => o.Id == "default").Parameters).Presence);
        Assert.All(contract.Operations, o => Assert.NotNull(o.RoutePlan));
        Assert.IsType<BinaryResponseBody>(Assert.Single(contract.Operations.Single(o => o.Id == "file.marked").Responses).Body);
        Assert.IsType<JsonResponseBody>(Assert.Single(contract.Operations.Single(o => o.Id == "json.bytes").Responses).Body);
    }

    [Fact]
    public async Task G0_File_metadata_does_not_describe_instance_content_type_or_file_name()
    {
        await using var fixture = await AdoptionFixture.Start();
        var descriptions = fixture.App.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items.SelectMany(g => g.Items).ToArray();
        var endpoints = ((IEndpointRouteBuilder)fixture.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToArray();
        var snapshot = new
        {
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            aspnet = typeof(WebApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            endpoints = endpoints.Select(e => new
            {
                route = e.RoutePattern.RawText,
                parameters = e.RoutePattern.Parameters.Select(p => new { p.Name, p.IsOptional, p.IsCatchAll, p.EncodeSlashes, p.Default }),
                produces = e.Metadata.OfType<IProducesResponseTypeMetadata>().Select(m => new { m.StatusCode, type = m.Type?.FullName, m.ContentTypes }),
            }),
            descriptions = descriptions.Select(d => new
            {
                d.HttpMethod,
                d.RelativePath,
                parameters = d.ParameterDescriptions.Select(p => new { p.Name, p.IsRequired, type = p.Type.FullName, source = p.Source.Id, defaultValue = p.DefaultValue is DBNull or Missing ? "<absent>" : p.DefaultValue }),
                responses = d.SupportedResponseTypes.Select(r => new { r.StatusCode, type = r.Type?.FullName, media = r.ApiResponseFormats.Select(f => f.MediaType) }),
            }),
        };
        output.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(typeof(IEndpointMetadataProvider).IsAssignableFrom(typeof(FileContentHttpResult)));
        Assert.False(typeof(IEndpointMetadataProvider).IsAssignableFrom(typeof(FileStreamHttpResult)));
        var bare = descriptions.Single(d => d.RelativePath == "v1/file/bare");
        var bareResponse = Assert.Single(bare.SupportedResponseTypes);
        Assert.Equal(typeof(void), bareResponse.Type);
        Assert.Empty(bareResponse.ApiResponseFormats);
        var declared = descriptions.Single(d => d.RelativePath == "v1/file/marked");
        Assert.Contains(declared.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(FileContentResult));
        Assert.Equal(0, fixture.Calls);
        foreach (var path in new[] { "/v1/file/bare", "/v1/file/marked", "/v1/file/stream", "/oracle-mvc/file", "/oracle-mvc/stream" })
        {
            using var response = await fixture.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(AdoptionFixture.Bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("report.pdf", response.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("optional", null, null)]
    [InlineData("optional/abc", "abc", null)]
    [InlineData("default", "1", null)]
    [InlineData("default/1", "1", null)]
    [InlineData("default/2", "2", null)]
    [InlineData("complex/report", "report", null)]
    [InlineData("complex/report.pdf", "report", "pdf")]
    [InlineData("complex/report.v1.pdf", "report.v1", "pdf")]
    [InlineData("star/a%2Fb", "a%2Fb", null)]
    [InlineData("star/a", "a", null)]
    [InlineData("stars/a/b", "a/b", null)]
    [InlineData("stars/a//b/", "a//b/", null)]
    [InlineData("stars//a", "/a", null)]
    [InlineData("stars/a%20b/%E6%97%A5%E6%9C%AC%F0%9F%98%80/%252e/a%2Bb", "a b/日本😀/%2e/a+b", null)]
    public async Task G0_Kestrel_binding(string path, string? first, string? second)
    {
        await using var fixture = await AdoptionFixture.Start();
        using var response = await fixture.Client.GetAsync("/base/v1/" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RouteObservation>();
        Assert.NotNull(body);
        Assert.Equal(first, body.First);
        Assert.Equal(second, body.Second);
        Assert.Equal("/base", body.PathBase);
        output.WriteLine($"{path} => {JsonSerializer.Serialize(body)}");
    }

    [Fact]
    public async Task G0_LinkGenerator_and_binding_have_distinct_default_and_slash_semantics()
    {
        await using var fixture = await AdoptionFixture.Start();
        var links = fixture.App.Services.GetRequiredService<LinkGenerator>();
        Assert.Equal("/v1/star/a%2Fb", links.GetPathByName("star", new { path = "a/b" }));
        Assert.Equal("/v1/stars/a/b", links.GetPathByName("stars", new { path = "a/b" }));
        // The route default is the string "1": LinkGenerator retains an explicitly supplied int but elides the equal string.
        Assert.Equal("/v1/default/1", links.GetPathByName("default", new { page = 1 }));
        Assert.Equal("/v1/default", links.GetPathByName("default", new { page = "1" }));
        using var required = await fixture.Client.GetAsync("/v1/required");
        Assert.Equal(HttpStatusCode.BadRequest, required.StatusCode);
    }

    [Fact]
    public async Task G0_Registration_preserves_existing_bytes_binding_and_finite_async_json()
    {
        await using var before = await AdoptionFixture.Start();
        await using var after = await AdoptionFixture.Start(register: true);
        foreach (var path in new[] { "/v1/optional", "/v1/default", "/v1/complex/report.pdf", "/v1/stars/a/b", "/v1/file/marked", "/v1/bytes", "/v1/async" })
        {
            using var a = await before.Client.GetAsync(path);
            using var b = await after.Client.GetAsync(path);
            Assert.Equal(a.StatusCode, b.StatusCode);
            Assert.Equal(a.Content.Headers.ToString(), b.Content.Headers.ToString());
            Assert.Equal(await a.Content.ReadAsByteArrayAsync(), await b.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal("\"AP8Bwyg=\"", await after.Client.GetStringAsync("/v1/bytes"));
        Assert.Equal("[9007199254740993,9223372036854775807]", await after.Client.GetStringAsync("/v1/async"));
    }
}

internal sealed class AdoptionFixture(WebApplication app) : IAsyncDisposable
{
    public static readonly byte[] Bytes = [0x00, 0xff, 0x01, 0xc3, 0x28];
    public WebApplication App { get; } = app;
    public HttpClient Client { get; private set; } = null!;
    public int Calls { get; private set; }

    public static async Task<AdoptionFixture> Start(bool register = false, bool testServer = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (testServer) { builder.WebHost.UseTestServer(); }
        builder.Logging.ClearProviders();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddControllers().AddApplicationPart(typeof(AdoptionOracleController).Assembly);
        if (register)
        {
            builder.Services.AddTisilia(o => o.ApiId = "adoption-oracle");
        }
        var app = builder.Build();
        var fixture = new AdoptionFixture(app);
        app.UsePathBase("/base");
        app.Use(async (context, next) => { fixture.Calls++; await next(context); });
        var group = app.MapGroup("/v1");
        RouteObservation Observe(HttpContext c, string? a, string? b = null) => new(a, b, c.Request.PathBase.Value ?? "");
        void Register(RouteHandlerBuilder endpoint, string name)
        {
            endpoint.WithName(name);
            if (register) { endpoint.WithTisiliaOperation(name); }
        }
        Register(group.MapGet("/optional/{id?}", (HttpContext c, string? id) => Observe(c, id)), "optional");
        // RT03 intentionally observes the valid runtime route whose handler still requires the value.
#pragma warning disable ASP0007
        Register(group.MapGet("/required/{id?}", (HttpContext c, string id) => Observe(c, id)), "required");
#pragma warning restore ASP0007
        Register(group.MapGet("/default/{page=1}", (HttpContext c, int page) => Observe(c, page.ToString(System.Globalization.CultureInfo.InvariantCulture))), "default");
        Register(group.MapGet("/complex/{filename}.{ext?}", (HttpContext c, string filename, string? ext) => Observe(c, filename, ext)), "complex");
        Register(group.MapGet("/star/{*path}", (HttpContext c, string? path) => Observe(c, path)), "star");
        Register(group.MapGet("/stars/{**path}", (HttpContext c, string? path) => Observe(c, path)), "stars");
        group.MapGet("/file/bare", () => TypedResults.File(Bytes, "application/pdf", "report.pdf"));
        Register(group.MapGet("/file/marked", () => Results.File(Bytes, "application/pdf", "report.pdf"))
            .Produces<FileContentResult>(200, "application/pdf"), "file.marked");
        Register(group.MapGet("/file/stream", () => TypedResults.File(new MemoryStream(Bytes), "application/pdf", "report.pdf"))
            .Produces<FileStreamResult>(200, "application/pdf"), "file.stream");
        Register(group.MapGet("/bytes", () => TypedResults.Ok(Bytes)), "json.bytes");
        Register(group.MapGet("/async", Values), "json.async");
        // Uses its existing writer options; registration must not change any of their meanings.
        group.MapGet("/json-settings", () => Results.Json(new JsonInvariant(null, 9007199254740993L, 1234567890.123456789m, InvariantMode.Ready, new InvariantCustom(7), new InvariantChild("child-value")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        app.MapControllers();
        await app.StartAsync();
        fixture.Client = testServer ? app.GetTestClient() : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        return fixture;
    }

    private static async IAsyncEnumerable<long> Values()
    {
        await Task.Yield();
        yield return 9007199254740993L;
        yield return long.MaxValue;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

internal sealed record JsonInvariant(string? NullableName, long BigInteger, decimal Amount, InvariantMode Mode, InvariantCustom Custom, InvariantParent Polymorphic);
internal enum InvariantMode { Ready }
[System.Text.Json.Serialization.JsonConverter(typeof(InvariantConverter))]
internal sealed record InvariantCustom(int Value);
internal sealed class InvariantConverter : System.Text.Json.Serialization.JsonConverter<InvariantCustom>
{
    public override InvariantCustom Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, InvariantCustom value, JsonSerializerOptions options) => writer.WriteStringValue("custom:" + value.Value);
}
[System.Text.Json.Serialization.JsonDerivedType(typeof(InvariantChild), "child")]
internal abstract record InvariantParent;
internal sealed record InvariantChild(string Value) : InvariantParent;

public sealed record RouteObservation(string? First, string? Second, string PathBase);

[ApiController]
[Route("oracle-mvc")]
public sealed class AdoptionOracleController : ControllerBase
{
    [HttpGet("optional-int/{id?}")]
    [TisiliaOperation("optional-int")]
    public int OptionalInt(int id) => id;

    [HttpGet("optional-nullable/{id?}")]
    [TisiliaOperation("optional-nullable")]
    public int OptionalNullable(int? id) => id ?? -1;

    [HttpGet("optional-bind-required/{id?}")]
    [TisiliaOperation("optional-bind-required")]
    public int OptionalBindRequired([Microsoft.AspNetCore.Mvc.ModelBinding.BindRequired] int id) => id;

    [HttpGet("optional-required-int/{id?}")]
    [TisiliaOperation("optional-required-int")]
    public int OptionalRequiredInt([System.ComponentModel.DataAnnotations.Required] int id) => id;

    [HttpGet("optional-required-nullable/{id?}")]
    [TisiliaOperation("optional-required-nullable")]
    public int OptionalRequiredNullable([System.ComponentModel.DataAnnotations.Required] int? id) => id ?? -1;

    [HttpGet("optional-required-string/{id?}")]
    [TisiliaOperation("optional-required-string")]
    public string OptionalRequiredString(string id) => id;

    [HttpGet("file")]
    [ProducesResponseType(typeof(FileContentResult), 200, "application/pdf")]
    public IActionResult ContentFile() => File(AdoptionFixture.Bytes, "application/pdf", "report.pdf");

    [HttpGet("stream")]
    [ProducesResponseType(typeof(FileStreamResult), 200, "application/pdf")]
    public IActionResult StreamFile() => File(new MemoryStream(AdoptionFixture.Bytes), "application/pdf", "report.pdf");
}
