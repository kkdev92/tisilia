using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.SourceGenerator;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Tisilia's source generator over sources written here, compiled against the ASP.NET Core this process runs: what it records for
/// each return path, what it refuses and why, and that what it generates compiles in any project. ResponseInferenceTests exports
/// what it records.
/// </summary>
public sealed class ResponseInferenceGeneratorTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Http.HttpResults;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Tisilia;
        using Tisilia.AspNetCore;

        """;

    [Fact]
    public void A_project_without_operations_gets_no_class()
    {
        var run = Run("public static class Nothing { public static void Map(IEndpointRouteBuilder app) => app.MapGet(\"/\", () => Results.Ok(1)); }");
        Assert.Null(run.Generated);
    }

    [Fact]
    public void A_chain_is_read_back_through_its_conventions_to_the_handler()
    {
        var run = Run("""
            public sealed record Todo(int Id);
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) =>
                    app.MapGet("/todos/{id}", (int id) => id > 0 ? Results.Ok(new Todo(id)) : Results.NotFound())
                        .WithName("todo").RequireAuthorization().WithTisiliaOperation("todo");
            }
            """);
        var operation = Assert.Single(run.Operations);
        Assert.Equal(("todo", "minimal", (string?)null), (operation.Id, operation.Flavor, operation.Failure));
        Assert.Equal(["global::Microsoft.AspNetCore.Http.HttpResults.Ok<global::Todo>", "global::Microsoft.AspNetCore.Http.HttpResults.NotFound"], operation.Results);
        Assert.Equal(("lambda", "global::Endpoints", "id", "int", "global::Microsoft.AspNetCore.Http.IResult"),
            (operation.HandlerKind, operation.HandlerType, operation.Parameters.Single().Name, operation.Parameters.Single().Type, operation.Returns));
    }

    [Fact]
    public void Handlers_whose_result_type_carries_metadata_are_not_recorded()
    {
        var run = Run("""
            public sealed record Todo(int Id);
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/a", () => TypedResults.Ok(new Todo(1))).WithTisiliaOperation("typed");
                    app.MapGet("/b", () => new Todo(1)).WithTisiliaOperation("value");
                    app.MapGet("/c", Results<Ok<Todo>, NotFound> () => TypedResults.NotFound()).WithTisiliaOperation("union");
                }
            }
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("action")]
                public ActionResult<Todo> Get() => new Todo(1);
            }
            """);
        Assert.Null(run.Generated);
    }

    [Fact]
    public void Return_statements_of_functions_written_inside_the_handler_are_theirs()
    {
        var run = Run("""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) =>
                    app.MapGet("/", () =>
                    {
                        Func<IResult> other = () => Results.Conflict();
                        IResult Local() { return Results.Unauthorized(); }
                        return Results.NoContent();
                    }).WithTisiliaOperation("nested");
            }
            """);
        Assert.Equal(["global::Microsoft.AspNetCore.Http.HttpResults.NoContent"], Assert.Single(run.Operations).Results);
    }

    [Theory]
    [InlineData("Results.Ok<string>(null)", "global::Microsoft.AspNetCore.Http.HttpResults.Ok")]
    [InlineData("Results.Ok()", "global::Microsoft.AspNetCore.Http.HttpResults.Ok")]
    [InlineData("Results.Ok(null)", "global::Microsoft.AspNetCore.Http.HttpResults.Ok")]
    [InlineData("Results.Ok(\"text\")", "global::Microsoft.AspNetCore.Http.HttpResults.Ok<string>")]
    [InlineData("TypedResults.Ok<string>(null)", "global::Microsoft.AspNetCore.Http.HttpResults.Ok<string>")]
    [InlineData("Results.CreatedAtRoute<int>(\"route\")", "global::Microsoft.AspNetCore.Http.HttpResults.CreatedAtRoute<int>")]
    [InlineData("Results.CreatedAtRoute<int?>(\"route\")", "global::Microsoft.AspNetCore.Http.HttpResults.CreatedAtRoute")]
    [InlineData("Results.Accepted()", "global::Microsoft.AspNetCore.Http.HttpResults.Accepted")]
    [InlineData("Results.NotFound(42)", "global::Microsoft.AspNetCore.Http.HttpResults.NotFound<int>")]
    [InlineData("Results.ServerSentEvents(Events())", "global::Microsoft.AspNetCore.Http.HttpResults.ServerSentEventsResult<string>")]
    [InlineData("TypedResults.ValidationProblem(new Dictionary<string, string[]>())", "global::Microsoft.AspNetCore.Http.HttpResults.ValidationProblem")]
    public void A_null_value_makes_the_bodyless_result_and_another_the_typed_one(string call, string result)
    {
        // Results.X<TValue>(value) creates X when the value is null, X<TValue> otherwise; default(int) is no null
        var run = Run($$"""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/", IResult () => {{call}}).WithTisiliaOperation("op");
                static async IAsyncEnumerable<string> Events() { await Task.Yield(); yield return "e"; }
            }
            """);
        Assert.Equal([result], Assert.Single(run.Operations).Results);
    }

    [Theory]
    [InlineData("Results.Unauthorized()", """{"kind":"none","status":401,"result":null,"body":null,"media":null}""")]
    [InlineData("Results.StatusCode(StatusCodes.Status418ImATeapot)", """{"kind":"none","status":418,"result":null,"body":null,"media":null}""")]
    [InlineData("Results.Problem()", """{"kind":"json","status":500,"result":null,"body":0,"media":"application/problem+json"}""")]
    [InlineData("Results.Problem(statusCode: 409)", """{"kind":"json","status":409,"result":null,"body":0,"media":"application/problem+json"}""")]
    [InlineData("Results.ValidationProblem(new Dictionary<string, string[]>(), statusCode: 422)", """{"kind":"json","status":422,"result":null,"body":0,"media":"application/problem+json"}""")]
    [InlineData("Results.Text(\"a\")", """{"kind":"text","status":200,"result":null,"body":0,"media":"text/plain; charset=utf-8"}""")]
    [InlineData("Results.Text(\"a\"u8, \"text/csv\", 201)", """{"kind":"text","status":201,"result":null,"body":0,"media":"text/csv"}""")]
    [InlineData("Results.Content(\"a\", \"text/html\", null, 202)", """{"kind":"text","status":202,"result":null,"body":0,"media":"text/html"}""")]
    [InlineData("Results.Json(1, contentType: \"application/json\", statusCode: 203)", """{"kind":"json","status":203,"result":null,"body":0,"media":"application/json"}""")]
    [InlineData("Results.Json<string>(null)", """{"kind":"none","status":200,"result":null,"body":null,"media":null}""")]
    [InlineData("Results.Empty", """{"kind":"none","status":200,"result":null,"body":null,"media":null}""")]
    public void Results_that_publish_no_metadata_are_recorded_as_what_they_write(string call, string response)
    {
        var run = Run($$"""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/", () => {{call}}).WithTisiliaOperation("op");
            }
            """);
        Assert.Equal([response], Assert.Single(run.Operations).Responses);
    }

    [Theory]
    [InlineData("Results.Ok((object)1)", "passes the value as an object")]
    [InlineData("Results.Ok(new { a = 1 })", "passes an anonymous type, which the contract cannot name")]
    [InlineData("Results.Ok((1, 2))", "passes a tuple, whose elements System.Text.Json does not write")]
    [InlineData("Results.Ok(Hidden.Make())", "passes Endpoints.Hidden, which code generated outside it cannot name")]
    [InlineData("Results.Ok(Results.Ok())", "passes a result as the value")]
    [InlineData("Results.StatusCode(Code())", "passes a status code that is not a constant")]
    [InlineData("Results.Text(\"a\", Media())", "passes a content type that is not a constant")]
    [InlineData("Results.Text(\"a\", null, System.Text.Encoding.Latin1)", "passes an Encoding")]
    [InlineData("Results.Json(1, new System.Text.Json.JsonSerializerOptions())", "writes the value with JsonSerializerOptions chosen at run time")]
    [InlineData("Results.Json(1, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<int>)null!)", "writes the value with a JsonTypeInfo or JsonSerializerContext chosen at run time")]
    [InlineData("Results.Json(new ProblemDetails())", "writes a ProblemDetails value, which sets the status code itself")]
    [InlineData("Results.Problem(new ProblemDetails())", "takes its status code from the ProblemDetails value")]
    [InlineData("Results.Redirect(\"/\")", "redirects, and fetch follows redirects itself")]
    [InlineData("Results.Forbid()", "leaves the response to an authentication handler")]
    [InlineData("Results.File(new byte[0])", "writes a file")]
    [InlineData("Code() > 0 ? Results.Ok() : Other()", "it returns Other(), which is not a call to a Results, TypedResults or ControllerBase helper")]
    public void A_path_the_source_does_not_fix_is_reported_with_its_place(string call, string reason)
    {
        var run = Run($$"""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/", () => {{call}}).WithTisiliaOperation("op");
                static int Code() => 200;
                static string Media() => "text/plain";
                static IResult Other() => Results.Ok();
                private sealed class Hidden { public static Hidden Make() => new(); }
            }
            """);
        var operation = Assert.Single(run.Operations);
        Assert.Contains(reason, operation.Failure, StringComparison.Ordinal);
        Assert.StartsWith("Program.cs(13,", operation.FailureAt, StringComparison.Ordinal);
        Assert.Empty(operation.Responses);
    }

    [Fact]
    public void A_handler_written_in_a_generic_method_cannot_name_its_type_parameter()
    {
        var run = Run("""
            public static class Endpoints
            {
                public static void Map<T>(IEndpointRouteBuilder app) where T : new() =>
                    app.MapGet("/", () => Results.Ok(new T())).WithTisiliaOperation("generic");
            }
            """);
        Assert.Contains("passes T, which code generated outside it cannot name", Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_local_type_cannot_be_named_by_the_generated_class()
    {
        var run = Run("""
            file sealed record Local(int A);
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/", () => Results.Ok(new Local(1))).WithTisiliaOperation("file");
            }
            """);
        Assert.Contains("which code generated outside it cannot name", Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
        Assert.Empty(run.Errors);
    }

    [Theory]
    [InlineData("var b = app.MapGet(\"/\", () => Results.Ok()); b.WithTisiliaOperation(\"op\");", "the endpoint builder it is called on does not come from a Map call in the same expression")]
    [InlineData("app.MapGet(\"/\", (HttpContext c) => c.Response.WriteAsync(\"x\")).WithTisiliaOperation(\"op\");", "the endpoint is mapped to a RequestDelegate, which returns no result")]
    [InlineData("app.MapGroup(\"/g\").WithTisiliaOperation(\"op\");", "comes from MapGroup, which is not a Map call")]
    [InlineData("Delegate d = () => Results.Ok(); app.MapGet(\"/\", d).WithTisiliaOperation(\"op\");", "its handler is neither a lambda nor a method group")]
    public void A_chain_that_does_not_lead_to_a_handler_is_reported(string statements, string reason)
    {
        var run = Run($$"""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) { {{statements}} }
            }
            """);
        Assert.Contains(reason, Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_that_is_not_a_constant_and_code_that_does_not_compile_record_nothing()
    {
        var run = Run("""
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app, string id)
                {
                    app.MapGet("/a", () => Results.Ok(1)).WithTisiliaOperation(id);
                    app.MapGet("/b", () => Results.Ok(Missing.Value)).WithTisiliaOperation("broken");
                }
            }
            """, allowErrors: true);
        Assert.Null(run.Generated);
    }

    [Fact]
    public void Attributes_mark_actions_methods_and_lambdas()
    {
        var run = Run("""
            public sealed record Todo(int Id);
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/lambda", [TisiliaOperation("lambda")] () => Results.Ok(new Todo(1)));
                    app.MapGet("/method", Handler);
                }

                [TisiliaOperation("method")]
                static IResult Handler(int id) => Results.Ok(new Todo(id));
            }
            [ApiController]
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("action")]
                public async Task<IActionResult> Get(int id) { await Task.Yield(); return id > 0 ? Ok(new Todo(id)) : NotFound(); }
            }
            """);
        Assert.Equal(["action", "lambda", "method"], run.Operations.Select(o => o.Id));
        var action = run.Operations[0];
        Assert.Equal(("mvc", "method", "global::TodosController", "Get"), (action.Flavor, action.HandlerKind, action.HandlerType, action.HandlerName));
        Assert.Equal(["""{"kind":"value","status":200,"result":null,"body":0,"media":null}""", """{"kind":"none","status":404,"result":null,"body":null,"media":null}"""], action.Responses);
        Assert.Equal(["Ok(object)", "NotFound()"], action.Helpers);
        Assert.Equal(("lambda", "global::Endpoints"), (run.Operations[1].HandlerKind, run.Operations[1].HandlerType));
        Assert.Equal(("method", "Handler"), (run.Operations[2].HandlerKind, run.Operations[2].HandlerName));
    }

    [Theory]
    [InlineData("Ok()", """{"kind":"none","status":200,"result":null,"body":null,"media":null}""")]
    [InlineData("Created(\"/x\", 1)", """{"kind":"value","status":201,"result":null,"body":0,"media":null}""")]
    [InlineData("Created(\"/x\", null)", """{"kind":"none","status":201,"result":null,"body":null,"media":null}""")]
    [InlineData("CreatedAtAction(\"Get\", new { id = 1 }, 2)", """{"kind":"value","status":201,"result":null,"body":0,"media":null}""")]
    [InlineData("AcceptedAtAction(\"Get\", \"Todos\", new { id = 1 })", """{"kind":"none","status":202,"result":null,"body":null,"media":null}""")]
    [InlineData("AcceptedAtRoute(new { id = 1 })", """{"kind":"none","status":202,"result":null,"body":null,"media":null}""")]
    [InlineData("AcceptedAtRoute(new { id = 1 }, \"v\")", """{"kind":"value","status":202,"result":null,"body":0,"media":null}""")]
    [InlineData("StatusCode(503, 1)", """{"kind":"value","status":503,"result":null,"body":0,"media":null}""")]
    [InlineData("Unauthorized(\"why\")", """{"kind":"value","status":401,"result":null,"body":0,"media":null}""")]
    [InlineData("Problem()", """{"kind":"problem","status":500,"result":null,"body":null,"media":null}""")]
    [InlineData("ValidationProblem(new ValidationProblemDetails())", """{"kind":"value","status":400,"result":null,"body":0,"media":null}""")]
    [InlineData("ValidationProblem(statusCode: 422)", """{"kind":"validation","status":422,"result":null,"body":null,"media":null}""")]
    [InlineData("Content(\"a\", \"text/csv\")", """{"kind":"text","status":200,"result":null,"body":0,"media":"text/csv"}""")]
    public void Controller_helpers_are_read_from_a_table_not_from_their_parameter_attributes(string call, string response)
    {
        // AcceptedAtAction(actionName, controllerName, routeValues) and AcceptedAtRoute(routeValues) mark route values with
        // [ActionResultObjectValue], and write no body
        var run = Run($$"""
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("op")]
                public IActionResult Get() => {{call}};
            }
            """);
        Assert.Equal([response], Assert.Single(run.Operations).Responses);
    }

    [Theory]
    [InlineData("Ok(null)", "is written as 204 No Content")]
    [InlineData("NotFound(null)", "writes the status code without a body, unlike NotFound()")]
    [InlineData("BadRequest(ModelState)", "writes the ModelStateDictionary as a SerializableError")]
    [InlineData("Content(\"a\", \"text/plain\", System.Text.Encoding.UTF8)", "passes an Encoding")]
    [InlineData("Redirect(\"/\")", "redirects")]
    [InlineData("PhysicalFile(\"/f\", \"text/plain\")", "writes a file")]
    [InlineData("Challenge()", "leaves the response to an authentication handler")]
    public void A_controller_path_the_source_does_not_fix_is_reported(string call, string reason)
    {
        var run = Run($$"""
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("op")]
                public IActionResult Get() => {{call}};
            }
            """);
        Assert.Contains(reason, Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_defined_conversion_is_refused()
    {
        // C# has no user-defined conversion to an interface, but one to ActionResult
        var run = Run("""
            public sealed class Wrapper { public static implicit operator ActionResult(Wrapper w) => new OkResult(); }
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("op")]
                public ActionResult Get() => new Wrapper();
            }
            """);
        Assert.Contains("it returns new Wrapper() through a user-defined conversion", Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_passed_to_helpers_are_recorded_for_every_action_and_every_path()
    {
        // MVC writes them with the value's own type whatever the action declares: recorded for ActionResult<T> too, and where
        // another path (the value itself, a status code that is not a constant) cannot be read
        var run = Run("""
            public sealed record Todo(int Id);
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("typed")]
                public ActionResult<Todo> Get(int code) => code switch
                {
                    0 => new Todo(0),
                    1 => Ok(new Todo(1)),
                    2 => StatusCode(code, new Todo(2)),
                    3 => CreatedAtAction("Get", new { id = 3 }, new Todo(3)),
                    _ => NotFound(),
                };
            }
            """);
        var operation = Assert.Single(run.Operations);
        Assert.Equal("mvc-typed", operation.Flavor);
        Assert.Equal(["200 Ok", "null StatusCode", "201 CreatedAtAction"], operation.Writes);
    }

    [Fact]
    public void A_helper_the_controller_overrides_is_its_own_method()
    {
        var run = Run("""
            public sealed class TodosController : ControllerBase
            {
                [TisiliaOperation("op")]
                public IActionResult Get() => Ok(1);

                [NonAction]
                public override OkObjectResult Ok(object? value) => base.Ok(2);
            }
            """);
        Assert.Contains("it returns Ok(1), which is not a call to a Results, TypedResults or ControllerBase helper", Assert.Single(run.Operations).Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_of_the_generator_is_the_operation_s_failure_and_not_the_build_s()
    {
        // a generator that throws fails a build that treats warnings as errors (CS8785); cancellation still goes through
        var spot = new SourceSpot("Program.cs", 3, 9);
        var failed = OperationReader.Guarded("op", spot, () => throw new InvalidOperationException("unexpected shape"));
        Assert.Equal(("op", "Tisilia's source generator could not read it (InvalidOperationException: unexpected shape)", spot), (failed!.Id, failed.Failure, failed.FailureAt));
        Assert.Throws<OperationCanceledException>(() => OperationReader.Guarded("op", spot, () => throw new OperationCanceledException()));
    }

    [Fact]
    public void The_generated_class_compiles_cleanly_where_warnings_are_errors()
    {
        // an obsolete type named in typeof would warn (CS0618): the class disables warnings, and nothing refers to it
        var run = Run("""
            [Obsolete("old")] public sealed record Old(int A);
            public static class Endpoints
            {
            #pragma warning disable CS0618
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/", () => Results.Ok(new Old(1))).WithTisiliaOperation("old");
            #pragma warning restore CS0618
            }
            """, warningsAsErrors: true);
        Assert.NotNull(run.Generated);
        Assert.Empty(run.Errors);
        Assert.Contains("typeof(global::Microsoft.AspNetCore.Http.HttpResults.Ok<global::Old>)", run.Generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Places_are_relative_to_the_project_and_the_output_is_deterministic()
    {
        const string source = """
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/b", () => Results.Ok(Code())).WithTisiliaOperation("b");
                    app.MapGet("/a", () => Results.Ok()).WithTisiliaOperation("a");
                }
                static object Code() => 1;
            }
            """;
        var inside = Run(source, path: "C:/src/app/Endpoints/Todos.cs", projectDirectory: "C:\\src\\app\\");
        Assert.Equal(["a", "b"], inside.Operations.Select(o => o.Id));
        Assert.StartsWith("Endpoints/Todos.cs(", inside.Operations[1].FailureAt, StringComparison.Ordinal);
        Assert.Equal(inside.Generated, Run(source, path: "C:/src/app/Endpoints/Todos.cs", projectDirectory: "C:\\src\\app\\").Generated);
        var outside = Run(source, path: "C:/elsewhere/Todos.cs", projectDirectory: "C:\\src\\app\\");
        Assert.StartsWith("Todos.cs(", outside.Operations[1].FailureAt, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", outside.Generated!, StringComparison.Ordinal);
    }

    private sealed record Recorded(
        string Id, string Flavor, string? Failure, string? FailureAt, string HandlerKind, string? HandlerType, string? HandlerName,
        IReadOnlyList<(string Name, string? Type)> Parameters, string? Returns, IReadOnlyList<string?> Results, IReadOnlyList<string> Responses, IReadOnlyList<string> Helpers,
        IReadOnlyList<string> Writes);

    private sealed record GeneratorRun(string? Generated, IReadOnlyList<Recorded> Operations, IReadOnlyList<Diagnostic> Errors);

    private static GeneratorRun Run(string source, string path = "Program.cs", string? projectDirectory = null, bool allowErrors = false, bool warningsAsErrors = false)
    {
        var tree = CSharpSyntaxTree.ParseText(Usings + source, new CSharpParseOptions(LanguageVersion.Latest), path: path);
        var compilation = CSharpCompilation.Create("App", [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));
        if (!allowErrors)
        {
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ResponseInferenceGenerator().AsSourceGenerator()], optionsProvider: new ProjectOptions(projectDirectory));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics);
        var generated = output.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("TisiliaResponseInference.g.cs", StringComparison.Ordinal));
        var errors = generated is null ? [] : output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == generated).ToList();
        return new GeneratorRun(generated?.ToString(), generated is null ? [] : Parse(generated.ToString()), errors);
    }

    // the class's Types and Operations, read back as the exporter reads them
    private static List<Recorded> Parse(string generated)
    {
        var types = generated.Split('\n').Where(l => l.TrimStart().StartsWith("typeof(", StringComparison.Ordinal))
            .Select(l => l.Trim()[7..^2]).ToList();
        var start = generated.IndexOf("Operations = @\"", StringComparison.Ordinal) + 15;
        var json = generated[start..generated.LastIndexOf("\";", StringComparison.Ordinal)].Replace("\"\"", "\"", StringComparison.Ordinal);
        string? TypeAt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? types[e.GetInt32()] : null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(o =>
        {
            var handler = o.GetProperty("handler");
            var responses = o.GetProperty("responses").EnumerateArray().ToList();
            return new Recorded(
                o.GetProperty("id").GetString()!, o.GetProperty("flavor").GetString()!, o.GetProperty("failure").GetString(), o.GetProperty("failureAt").GetString(),
                handler.GetProperty("kind").GetString()!, TypeAt(handler.GetProperty("type")), handler.GetProperty("name").GetString(),
                handler.GetProperty("parameters").EnumerateArray().Select(p => (p.GetProperty("name").GetString()!, TypeAt(p.GetProperty("type")))).ToList(),
                TypeAt(handler.GetProperty("returns")),
                responses.Where(r => r.GetProperty("kind").GetString() == "result").Select(r => TypeAt(r.GetProperty("result"))).ToList(),
                // body indexes are local to each response's own type list: rewritten as 0 for the body a response names
                responses.Select(r => r.GetRawText().Replace("\"body\":" + r.GetProperty("body").GetRawText(), r.GetProperty("body").ValueKind == JsonValueKind.Number ? "\"body\":0" : "\"body\":null", StringComparison.Ordinal)).ToList(),
                o.GetProperty("helpers").EnumerateArray().Select(h => h.GetProperty("name").GetString() + "(" + string.Join(", ", h.GetProperty("parameters").EnumerateArray().Select(p => TypeAt(p))) + ")").ToList(),
                o.GetProperty("writes").EnumerateArray().Select(w => w.GetProperty("status").GetRawText() + " " + w.GetProperty("helper").GetString()).ToList());
        }).ToList();
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        // the framework this process runs (Microsoft.NETCore.App and Microsoft.AspNetCore.App) and Tisilia's own assemblies
        var shared = Path.DirectorySeparatorChar + "shared" + Path.DirectorySeparatorChar;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Contains(shared + "Microsoft.NETCore.App" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || p.Contains(shared + "Microsoft.AspNetCore.App" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Append(typeof(TisiliaOperationAttribute).Assembly.Location)
            .Append(typeof(TisiliaEndpointExtensions).Assembly.Location);
        return [.. paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    private sealed class ProjectOptions(string? projectDirectory) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(projectDirectory);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(null);

        private sealed class Options(string? projectDirectory) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = projectDirectory ?? "";
                return projectDirectory is not null && string.Equals(key, "build_property.ProjectDir", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
