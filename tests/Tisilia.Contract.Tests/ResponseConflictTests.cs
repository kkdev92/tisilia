using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// ApiExplorer lists two body types declared for one status code side by side (ApiResponseTypeProvider.ResponseKey and
/// EndpointMetadataApiDescriptionProvider, aspnetcore v11.0.0). The client selects a case by status code and media type, so it could
/// not tell the two apart: the export refuses the operation, whichever way the types are declared. The framework's problem details
/// types are the exception: they are one body, which the client reads as the least derived of the declared types.
/// </summary>
public sealed class ResponseConflictTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-response-conflicts-" + Guid.NewGuid().ToString("N"));

    public ResponseConflictTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    // a route group's ProducesProblem(400) next to the endpoint's ProducesValidationProblem()
    [InlineData("group", "ProblemDetails")]
    // Results<Ok<First>, ValidationProblem, BadRequest<ProblemDetails>>
    [InlineData("union", "ProblemDetails")]
    // [ProducesResponseType<ProblemDetails>(400)] and [ProducesResponseType<ValidationProblemDetails>(400)] on one action
    [InlineData("attributes", "ProblemDetails")]
    // Produces<HttpValidationProblemDetails>(400) and Produces<ValidationProblemDetails>(400): both have the errors member
    [InlineData("validation", "HttpValidationProblemDetails")]
    public async Task The_problem_details_types_of_one_status_code_are_one_case(string shape, string type)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "response-conflicts");
        if (shape == "attributes")
        {
            builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only(typeof(ProblemsController))));
        }

        await using var app = builder.Build();
        switch (shape)
        {
            case "group":
                app.MapGroup("/problems").ProducesProblem(StatusCodes.Status400BadRequest)
                    .MapGet("/{id}", IResult (int id) => id > 0 ? Results.Ok(new First(id)) : id == 0 ? Results.ValidationProblem(Errors(), title: "invalid") : Results.Problem(title: "negative", statusCode: 400))
                    .Produces<First>().ProducesValidationProblem().WithTisiliaOperation("problems");
                break;
            case "union":
                app.MapGet("/problems/{id}", Results<Ok<First>, ValidationProblem, BadRequest<ProblemDetails>> (int id) => id > 0 ? TypedResults.Ok(new First(id))
                    : id == 0 ? TypedResults.ValidationProblem(Errors(), title: "invalid") : TypedResults.BadRequest(new ProblemDetails { Title = "negative", Status = 400 }))
                    .WithTisiliaOperation("problems");
                break;
            case "validation":
                app.MapGet("/problems/{id}", IResult (int id) => id > 0 ? Results.Ok(new First(id))
                    : id == 0 ? Results.ValidationProblem(Errors(), title: "invalid") : Results.BadRequest(new ValidationProblemDetails(Errors()) { Title = "negative", Status = 400 }))
                    .Produces<First>().Produces<HttpValidationProblemDetails>(400).Produces<ValidationProblemDetails>(400).WithTisiliaOperation("problems");
                break;
            default:
                app.MapControllers();
                break;
        }

        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var response = Assert.Single(export.Index!.Operations["problems"].Responses, r => r.Status == 400);
        var body = Assert.IsType<JsonResponseBody>(response.Body);
        Assert.Equal("application/problem+json", body.MediaType);
        Assert.Equal(type, export.Index.Types[body.Use.TypeId].ClrIdentity?.Split('.')[^1]);

        // the server answers with either type, and the runtime reads both as the one case
        using var http = new HttpClient();
        foreach (var id in new[] { 0, -1 })
        {
            using var answer = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/problems/" + id));
            Assert.Equal((400, "application/problem+json"), ((int)answer.StatusCode, answer.Content.Headers.ContentType?.MediaType));
        }

        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("problems", new { id = 0 }), ("problems", new { id = -1 })], execute: true);
        Assert.Equal(2, results.Count);
        using var invalid = JsonDocument.Parse(results[0]);
        using var negative = JsonDocument.Parse(results[1]);
        Assert.True(invalid.RootElement.GetProperty("kind").GetString() == "response" && negative.RootElement.GetProperty("kind").GetString() == "response", results[0] + "\n" + results[1]);
        Assert.Equal("invalid", invalid.RootElement.GetProperty("data").GetProperty("title").GetString());
        Assert.Equal("negative", negative.RootElement.GetProperty("data").GetProperty("title").GetString());
        // what the validation type adds is a member of its own where the case has it, and extension data where the case is ProblemDetails
        var data = invalid.RootElement.GetProperty("data");
        if (type == "ProblemDetails")
        {
            Assert.Contains("errors", data.GetProperty("extensions").GetProperty("$entries").EnumerateArray().Select(entry => entry[0].GetString()));
        }
        else
        {
            Assert.True(data.TryGetProperty("errors", out _), results[0]);
        }
    }

    [Fact]
    public async Task A_type_the_application_derives_from_problem_details_is_still_refused_next_to_another()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "response-conflicts");
        await using var app = builder.Build();
        app.MapGet("/problems/{id}", IResult (int id) => id > 0 ? Results.Ok(new First(id)) : Results.BadRequest(new QuotaProblem { Title = "quota", Status = 400, Remaining = 0 }))
            .Produces<First>().ProducesProblem(StatusCodes.Status400BadRequest).Produces<QuotaProblem>(400).WithTisiliaOperation("problems");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.Severity == Tisilia.Generator.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal("SV28", diagnostic.Rule);
        Assert.Contains("status 400", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("QuotaProblem", diagnostic.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string[]> Errors() => new() { ["id"] = ["must be positive"] };

    [Theory]
    // Results<Ok<First>, Ok<Second>>
    [InlineData("union")]
    // Produces<First>() and Produces<Second>()
    [InlineData("produces")]
    // [ProducesResponseType<First>(200)] and [ProducesResponseType<Second>(200)] on one action
    [InlineData("attributes")]
    public async Task Two_body_types_declared_for_one_status_code_are_refused(string shape)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "response-conflicts");
        if (shape == "attributes")
        {
            builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only(typeof(ConflictController))));
        }

        await using var app = builder.Build();
        switch (shape)
        {
            case "union":
                app.MapGet("/conflict/{id}", Results<Ok<First>, Ok<Second>> (int id) => id > 0 ? TypedResults.Ok(new First(id)) : TypedResults.Ok(new Second("none")))
                    .WithTisiliaOperation("conflict");
                break;
            case "produces":
                app.MapGet("/conflict/{id}", IResult (int id) => id > 0 ? Results.Ok(new First(id)) : Results.Ok(new Second("none")))
                    .Produces<First>().Produces<Second>().WithTisiliaOperation("conflict");
                break;
            default:
                app.MapControllers();
                break;
        }

        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors, "the export succeeded:\n" + export.Text);
        Assert.Null(export.Text);
        var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.Severity == Tisilia.Generator.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal("SV28", diagnostic.Rule);
        Assert.Contains("status 200", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("First", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Second", diagnostic.Message, StringComparison.Ordinal);
    }

    public sealed record First(int Id);

    public sealed record Second(string Name);

    public sealed class QuotaProblem : ProblemDetails
    {
        public int Remaining { get; set; }
    }

    private sealed class Only(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
[Route("problems")]
public sealed class ProblemsController : ControllerBase
{
    [HttpGet("{id}")]
    [TisiliaOperation("problems")]
    [ProducesResponseType<ResponseConflictTests.First>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public IActionResult Get(int id) => id > 0 ? Ok(new ResponseConflictTests.First(id))
        : id == 0 ? ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["id"] = ["must be positive"] }) { Title = "invalid", Status = 400 })
        : Problem(title: "negative", statusCode: 400);
}

[ApiController]
[NonController]
[Route("conflict")]
public sealed class ConflictController : ControllerBase
{
    [HttpGet("{id}")]
    [TisiliaOperation("conflict")]
    [ProducesResponseType<ResponseConflictTests.First>(StatusCodes.Status200OK)]
    [ProducesResponseType<ResponseConflictTests.Second>(StatusCodes.Status200OK)]
    public IActionResult Get(int id) => id > 0 ? Ok(new ResponseConflictTests.First(id)) : Ok(new ResponseConflictTests.Second("none"));
}
