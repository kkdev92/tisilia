using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A minimal API writes a ProblemDetails value as application/problem+json when a Results or TypedResults helper writes it, whatever
/// media type the endpoint declares for it, and as application/json when the handler returns the value itself. The contract names the
/// media type the server writes, so the client selects the case: each path is exported, sent to Kestrel and read by the runtime.
/// </summary>
public sealed class MinimalProblemDetailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-minimal-problems-" + Guid.NewGuid().ToString("N"));

    public MinimalProblemDetailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    // TypedResults.NotFound(problem): the result type's own metadata
    [InlineData("typed", 404, "application/problem+json")]
    // Results.NotFound(problem) behind Produces<ProblemDetails>(404), which declares application/json
    [InlineData("declared", 404, "application/problem+json")]
    // the same behind ProducesProblem(404), which declares application/problem+json
    [InlineData("declared-problem", 404, "application/problem+json")]
    // a type derived from ProblemDetails
    [InlineData("validation", 400, "application/problem+json")]
    // TypedResults.Json(problem, statusCode: 409) behind Produces<ProblemDetails>(409)
    [InlineData("json", 409, "application/problem+json")]
    // the handler returns the ProblemDetails itself: RequestDelegateFactory writes it as any other value
    [InlineData("value", 200, "application/json")]
    public async Task The_contract_names_the_media_type_the_server_writes_a_ProblemDetails_with(string operation, int status, string mediaType)
    {
        await using var app = await StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        // what Kestrel answers
        using var http = new HttpClient();
        using var answer = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/problems/" + operation + "/0"));
        Assert.Equal(status, (int)answer.StatusCode);
        Assert.Equal(mediaType, answer.Content.Headers.ContentType?.MediaType);

        // the contract names it, and the runtime selects the case by it: another media type would be an unexpected-response
        var response = Assert.Single(export.Index!.Operations["problems." + operation].Responses, r => r.Status == status);
        Assert.Equal(mediaType, Assert.IsType<JsonResponseBody>(response.Body).MediaType);

        var result = Assert.Single(await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("problems." + operation, new { id = 0 })], execute: true));
        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("kind").GetString() == "response", result);
        Assert.Equal(status, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("missing", document.RootElement.GetProperty("data").GetProperty("title").GetString());
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "minimal-problems");
        var app = builder.Build();
        var problems = app.MapGroup("/problems");
        problems.MapGet("/typed/{id}", Results<Ok<Item>, NotFound<ProblemDetails>> (int id) => id > 0 ? TypedResults.Ok(new Item(id)) : TypedResults.NotFound(Missing(404)))
            .WithTisiliaOperation("problems.typed");
        problems.MapGet("/declared/{id}", IResult (int id) => id > 0 ? Results.Ok(new Item(id)) : Results.NotFound(Missing(404)))
            .Produces<Item>().Produces<ProblemDetails>(404).WithTisiliaOperation("problems.declared");
        problems.MapGet("/declared-problem/{id}", IResult (int id) => id > 0 ? Results.Ok(new Item(id)) : Results.NotFound(Missing(404)))
            .Produces<Item>().ProducesProblem(404).WithTisiliaOperation("problems.declared-problem");
        problems.MapGet("/validation/{id}", Results<Ok<Item>, BadRequest<HttpValidationProblemDetails>> (int id) => id > 0 ? TypedResults.Ok(new Item(id))
            : TypedResults.BadRequest(new HttpValidationProblemDetails(new Dictionary<string, string[]> { ["id"] = ["must be positive"] }) { Title = "missing", Status = 400 }))
            .WithTisiliaOperation("problems.validation");
        problems.MapGet("/json/{id}", IResult (int id) => id > 0 ? Results.Ok(new Item(id)) : TypedResults.Json(Missing(409), statusCode: 409))
            .Produces<Item>().Produces<ProblemDetails>(409).WithTisiliaOperation("problems.json");
        problems.MapGet("/value/{id}", ProblemDetails (int id) => Missing(200)).WithTisiliaOperation("problems.value");
        await app.StartAsync();
        return app;
    }

    private static ProblemDetails Missing(int status) => new() { Title = "missing", Status = status };

    public sealed record Item(int Id);
}
