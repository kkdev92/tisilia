using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.InferenceFixture;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// An endpoint that declares no response types — a minimal API handler returning <c>Results.Ok(value)</c>, an MVC action returning
/// <c>IActionResult</c> — is described by what Tisilia's source generator read from its return paths, compiled into the handler's
/// assembly (Tisilia.InferenceFixture is compiled with it). Each inferred operation has a twin in the fixture that declares the same
/// responses the typed way; the contract describes both alike. Oracle: Kestrel answers each case the contract describes.
/// </summary>
public sealed class ResponseInferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-response-inference-" + Guid.NewGuid().ToString("N"));

    public ResponseInferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "response-inference");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Minimal_api_handlers_returning_Results_are_described_as_their_typed_twins()
    {
        await using var app = await StartAsync(MinimalEndpoints.Map);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        var contract = JsonNode.Parse(export.Text!)!;
        foreach (var name in new[] { "todo", "create", "summary", "local", "attributed", "problem", "text", "json", "empty", "events" })
        {
            Assert.Equal(Responses(contract, "declared." + name), Responses(contract, "inferred." + name).Replace("inferred." + name, "declared." + name, StringComparison.Ordinal));
        }

        // what no typed result declares: status codes without a body, read from Unauthorized(), StatusCode(202) and NoContent(); the arm
        // that throws writes no response of its own
        var status = contract["operations"]!.AsArray().Single(o => (string?)o!["id"] == "inferred.status")!["responses"]!.AsArray();
        Assert.Equal([401, 202, 204], status.Select(r => (int)r!["status"]!));
        Assert.All(status, r => Assert.Equal("none", (string?)r!["body"]!["kind"]));
    }

    [Fact]
    public async Task Status_codes_without_a_body_decode_what_Kestrel_answers()
    {
        await using var app = await StartAsync(MinimalEndpoints.Map);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("inferred.status", new { code = 1 }), ("inferred.status", new { code = 2 }), ("inferred.status", new { code = 0 }), ("inferred.text", new { }), ("inferred.empty", new { })], execute: true);
        Assert.Equal([401, 202, 204, 200, 200], results.Select(r => { using var d = JsonDocument.Parse(r); Assert.Equal("response", d.RootElement.GetProperty("kind").GetString()); return d.RootElement.GetProperty("status").GetInt32(); }));
        using var text = JsonDocument.Parse(results[3]);
        Assert.Equal("hello", text.RootElement.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("local", "it returns result, which is not a call to a Results, TypedResults or ControllerBase helper (MinimalEndpoints.cs(")]
    [InlineData("options", "writes the value with JsonSerializerOptions chosen at run time")]
    [InlineData("object", "passes the value as an object: its type at run time decides what is written")]
    [InlineData("anonymous", "passes an anonymous type, which the contract cannot name")]
    [InlineData("status", "passes a status code that is not a constant")]
    [InlineData("conflict", "status 200 is written with InferredTodo on one path and InferredSummary on another")]
    [InlineData("file", "writes a file")]
    [InlineData("helper", "it returns Helper(), which is not a call to a Results, TypedResults or ControllerBase helper")]
    [InlineData("builder", "the endpoint builder it is called on does not come from a Map call in the same expression (MinimalEndpoints.cs(")]
    public async Task A_return_path_the_source_does_not_fix_is_reported_where_it_is(string name, string reason)
    {
        Assert.Contains(name, UnreadableEndpoints.Names);
        await using var app = await StartAsync(a => UnreadableEndpoints.Map(a, name));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var error = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV34");
        Assert.Contains("declares no response types (it returns IResult)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Tisilia could not read them from the source: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inferred_cases_decode_what_Kestrel_answers()
    {
        await using var app = await StartAsync(MinimalEndpoints.Map);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("inferred.todo", new { id = 7 }), ("inferred.todo", new { id = 0 }), ("inferred.create", new { body = new { id = 3, title = "plan" } }),
             ("inferred.create", new { body = new { id = 4, title = "" } }), ("inferred.summary", new { })], execute: true);
        using var found = JsonDocument.Parse(results[0]);
        Assert.Equal(("response", 200), (found.RootElement.GetProperty("kind").GetString(), found.RootElement.GetProperty("status").GetInt32()));
        Assert.Equal("write", found.RootElement.GetProperty("data").GetProperty("title").GetString());
        using var missing = JsonDocument.Parse(results[1]);
        Assert.Equal(("response", 404), (missing.RootElement.GetProperty("kind").GetString(), missing.RootElement.GetProperty("status").GetInt32()));
        using var created = JsonDocument.Parse(results[2]);
        Assert.Equal(("response", 201), (created.RootElement.GetProperty("kind").GetString(), created.RootElement.GetProperty("status").GetInt32()));
        Assert.Equal("plan", created.RootElement.GetProperty("data").GetProperty("title").GetString());
        using var refused = JsonDocument.Parse(results[3]);
        Assert.Equal(("response", 400), (refused.RootElement.GetProperty("kind").GetString(), refused.RootElement.GetProperty("status").GetInt32()));
        Assert.Equal(0, refused.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        using var summary = JsonDocument.Parse(results[4]);
        Assert.Equal(2, summary.RootElement.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Mvc_actions_returning_IActionResult_are_described_as_their_declared_twins()
    {
        await using var app = await StartMvcAsync([typeof(InferredMvcController), typeof(DeclaredMvcController)]);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        var contract = JsonNode.Parse(export.Text!)!;
        foreach (var name in new[] { "todo", "create", "problem", "text", "delete" })
        {
            Assert.Equal(Responses(contract, "mvc.declared." + name), Responses(contract, "mvc.inferred." + name).Replace("mvc.inferred." + name, "mvc.declared." + name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Mvc_inferred_cases_decode_what_Kestrel_answers()
    {
        await using var app = await StartMvcAsync([typeof(InferredMvcController)]);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("mvc.inferred.todo", new { id = 5 }), ("mvc.inferred.todo", new { id = 0 }), ("mvc.inferred.create", new { body = new { id = 3, title = "plan" } }),
             ("mvc.inferred.problem", new { fail = true }), ("mvc.inferred.problem", new { fail = false }), ("mvc.inferred.text", new { }),
             ("mvc.inferred.delete", new { id = 1 }), ("mvc.inferred.delete", new { id = 0 })], execute: true);
        var parsed = results.Select(r => JsonDocument.Parse(r).RootElement).ToList();
        Assert.All(parsed, r => Assert.Equal("response", r.GetProperty("kind").GetString()));
        Assert.Equal([200, 404, 201, 503, 400, 200, 204, 409], parsed.Select(r => r.GetProperty("status").GetInt32()));
        Assert.Equal("read", parsed[0].GetProperty("data").GetProperty("title").GetString());
        // NotFound() and StatusCode(409) under [ApiController] are written as ProblemDetails
        Assert.Equal(404, parsed[1].GetProperty("data").GetProperty("status").GetInt32());
        Assert.Equal("plan", parsed[2].GetProperty("data").GetProperty("title").GetString());
        Assert.Equal(503, parsed[3].GetProperty("data").GetProperty("status").GetInt32());
        Assert.Equal(400, parsed[4].GetProperty("data").GetProperty("status").GetInt32());
        Assert.Equal("hello", parsed[5].GetProperty("data").GetString());
        Assert.Equal(409, parsed[7].GetProperty("data").GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData(typeof(NullValueController), "Ok(null) is written as 204 No Content")]
    [InlineData(typeof(FileController), "writes a file")]
    [InlineData(typeof(ModelStateController), "writes the ModelStateDictionary as a SerializableError")]
    [InlineData(typeof(MinimalResultController), "the action returns IResult, which MVC writes with the minimal API JSON options")]
    [InlineData(typeof(OverridingController), "OverridingController overrides ControllerBase.Ok, which the action calls")]
    [InlineData(typeof(PolymorphicController), "status 200 is written by Ok(value), which MVC writes with the value's own type, so a value of a type derived from InferredShape has no discriminator")]
    public async Task An_action_whose_responses_the_source_does_not_fix_is_reported(Type controller, string reason)
    {
        await using var app = await StartMvcAsync([controller]);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV34" && d.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Null(export.Text);
    }

    [Fact]
    public async Task A_polymorphic_type_written_through_Ok_is_refused_where_the_source_shows_it()
    {
        // MVC writes `return value;` from ActionResult<Shape> with the declared type, and Ok(value) with the value's own type
        await using var app = await StartMvcAsync([typeof(DeclaredShapeController)]);
        using var client = new HttpClient();
        Assert.Equal("""{"$type":"circle","radius":1}""", await client.GetStringAsync(app.Urls.Single() + "/declared-shapes/value"));
        Assert.Equal("""{"radius":1}""", await client.GetStringAsync(app.Urls.Single() + "/declared-shapes/ok"));

        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var errors = export.Diagnostics.Items.Where(d => d.Rule == "SV34").ToList();
        Assert.Equal(["mvc.shape.declared", "mvc.shape.ok"], errors.SelectMany(e => e.RelatedIds).Order(StringComparer.Ordinal));
        Assert.All(errors, e => Assert.Contains("status 200 is written by Ok(value), which MVC writes with the value's own type, so a value of a type derived from InferredShape has no discriminator", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_polymorphic_value_returned_itself_decodes_with_its_discriminator()
    {
        await using var app = await StartMvcAsync([typeof(ShapeValueController)]);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("mvc.shape.only-value", new { })], execute: true);
        using var result = JsonDocument.Parse(results[0]);
        Assert.Equal(("circle", 2.0), (result.RootElement.GetProperty("data").GetProperty("$type").GetString(), result.RootElement.GetProperty("data").GetProperty("radius").GetDouble()));
    }

    [Fact]
    public async Task Problem_responses_are_not_inferred_under_an_application_ProblemDetailsFactory()
    {
        // ControllerBase.Problem() and ValidationProblem() take the status code from ProblemDetailsFactory, which the application may replace
        await using var app = await StartMvcAsync([typeof(InferredMvcController)], s => s.AddSingleton<ProblemDetailsFactory, OwnProblemDetailsFactory>());
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var error = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV34");
        Assert.Contains("operation 'mvc.inferred.problem'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Problem() and ValidationProblem() take their status code from the application's own ProblemDetailsFactory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_id_the_source_declares_for_another_handler_is_not_inferred()
    {
        // the source records 'mismatched' for a lambda without parameters; at run time a lambda that takes 'other' has the id
        await using var app = await StartAsync(a => MismatchedEndpoints.Map(a, asDeclared: false));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var error = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV34");
        Assert.Contains("the source declares 'mismatched' at MinimalEndpoints.cs(", error.Message, StringComparison.Ordinal);
        Assert.Contains("for another handler", error.Message, StringComparison.Ordinal);

        await using var declared = await StartAsync(a => MismatchedEndpoints.Map(a, asDeclared: true));
        Assert.False(declared.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh().Diagnostics.HasErrors);
    }

    [Fact]
    public async Task A_handler_compiled_without_the_generator_says_so()
    {
        // this test project does not run the generator: its handlers have no responses recorded
        await using var app = await StartAsync(a => a.MapGet("/plain", () => Microsoft.AspNetCore.Http.Results.Ok(1)).WithTisiliaOperation("plain"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var error = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV34");
        Assert.Contains("Tisilia's source generator did not run for Tisilia.Contract.Tests: the project that defines the handler reads the responses only when it references Kkdev92.Tisilia.AspNetCore itself", error.Message, StringComparison.Ordinal);
    }

    private sealed class OwnProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(Microsoft.AspNetCore.Http.HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = 418 };

        public override ValidationProblemDetails CreateValidationProblemDetails(Microsoft.AspNetCore.Http.HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = 418 };
    }

    private static async Task<WebApplication> StartMvcAsync(Type[] controllers, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Controllers(controllers)));
        configure?.Invoke(builder.Services);
        builder.Services.AddTisilia(o => o.ApiId = "response-inference");
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private sealed class Controllers(Type[] types) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            foreach (var type in types)
            {
                feature.Controllers.Add(type.GetTypeInfo());
            }
        }
    }

    private static string Responses(JsonNode contract, string operationId) =>
        contract["operations"]!.AsArray().Single(o => (string?)o!["id"] == operationId)!["responses"]!.ToJsonString();
}
