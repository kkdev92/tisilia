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
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// An API whose operations share their failures — ProblemDetails for 400 and 404 everywhere — declares them once: ASP.NET Core's
/// <c>ProducesProblem</c> on a route group (.NET 9 and later) for minimal APIs, a global <c>ProducesResponseTypeAttribute</c> filter for
/// controllers. Every operation under the declaration then has those cases, and the client reads them as typed responses.
/// </summary>
public sealed class CommonProblemResponsesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-common-problems-" + Guid.NewGuid().ToString("N"));

    public CommonProblemResponsesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Problems_declared_once_are_cases_of_every_operation_under_the_declaration()
    {
        await using var app = await StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        // a ProblemHttpResult whose statuses the group declares is described
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Rule == "SV34");
        var index = export.Index!;

        foreach (var operation in new[] { "items.get", "items.create", "orders.get" })
        {
            var problems = index.Operations[operation].Responses.Where(r => r.Status is 400 or 404).ToList();
            Assert.Equal([400, 404], problems.Select(r => r.Status).Order());
            Assert.All(problems, r => Assert.Equal("application/problem+json", Assert.IsType<JsonResponseBody>(r.Body).MediaType));
            Assert.All(problems, r => Assert.EndsWith("ProblemDetails", index.Types[((JsonResponseBody)r.Body).Use.TypeId].ClrIdentity, StringComparison.Ordinal));
        }

        // an operation outside the group declares only its own responses
        Assert.DoesNotContain(index.Operations["health"].Responses, r => r.Status is 400 or 404);
    }

    [Fact]
    public async Task The_client_reads_a_shared_problem_as_a_typed_response()
    {
        await using var app = await StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("items.get", new { id = 0 }), ("orders.get", new { id = 0 })], execute: true);

        foreach (var result in results)
        {
            using var document = JsonDocument.Parse(result);
            Assert.Equal("response", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("missing", document.RootElement.GetProperty("data").GetProperty("title").GetString());
        }
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddControllers(o =>
        {
            // every controller action can answer these
            o.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), 400, "application/problem+json"));
            o.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), 404, "application/problem+json"));
        }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyOrders()));
        builder.Services.AddTisilia(o => o.ApiId = "common-problems");
        var app = builder.Build();
        var items = app.MapGroup("/items").ProducesProblem(400).ProducesProblem(404);
        items.MapGet("/{id}", Results<Ok<Item>, ProblemHttpResult> (int id) => id > 0 ? TypedResults.Ok(new Item(id)) : TypedResults.Problem(statusCode: 404, title: "missing")).WithTisiliaOperation("items.get");
        items.MapPost("/", (Item item) => TypedResults.Created($"/items/{item.Id}", item)).WithTisiliaOperation("items.create");
        app.MapGet("/health", () => TypedResults.Ok("ok")).WithTisiliaOperation("health");
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    public sealed record Item(int Id);

    private sealed class OnlyOrders : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(CommonProblemOrdersController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
[Route("/orders")]
public sealed class CommonProblemOrdersController : ControllerBase
{
    [HttpGet("{id}")]
    [TisiliaOperation("orders.get")]
    public ActionResult<CommonProblemResponsesTests.Item> Get(int id) => id > 0 ? new CommonProblemResponsesTests.Item(id) : Problem(statusCode: 404, title: "missing");
}
