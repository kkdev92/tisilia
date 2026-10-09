using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Tisilia.AspNetCore;

namespace Tisilia.InferenceFixture;

public sealed record InferredTodo(int Id, string Title);

public record InferredSummary(int Count);

/// <summary>Minimal API handlers that return IResult without response metadata, each with the typed twin that declares it.</summary>
public static class MinimalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // a lambda that returns one of two helpers
        app.MapGet("/inferred/todos/{id}", (int id) => id > 0 ? Results.Ok(new InferredTodo(id, "write")) : Results.NotFound())
            .WithTisiliaOperation("inferred.todo");
        app.MapGet("/declared/todos/{id}", Results<Ok<InferredTodo>, NotFound> (int id) => id > 0 ? TypedResults.Ok(new InferredTodo(id, "write")) : TypedResults.NotFound())
            .WithTisiliaOperation("declared.todo");

        // an async block body with several returns and calls between Map and WithTisiliaOperation
        app.MapPost("/inferred/todos", async (InferredTodo todo) =>
        {
            await Task.Yield();
            if (todo.Title.Length == 0)
            {
                return Results.BadRequest(new InferredSummary(0));
            }

            return Results.Created($"/inferred/todos/{todo.Id}", todo);
        }).WithName("inferredCreate").WithTisiliaOperation("inferred.create");
        app.MapPost("/declared/todos", async Task<Results<BadRequest<InferredSummary>, Created<InferredTodo>>> (InferredTodo todo) =>
        {
            await Task.Yield();
            if (todo.Title.Length == 0)
            {
                return TypedResults.BadRequest(new InferredSummary(0));
            }

            return TypedResults.Created($"/declared/todos/{todo.Id}", todo);
        }).WithName("declaredCreate").WithTisiliaOperation("declared.create");

        // a method group
        app.MapGet("/inferred/summary", Summary).WithTisiliaOperation("inferred.summary");
        app.MapGet("/declared/summary", () => TypedResults.Ok(new InferredSummary(2))).WithTisiliaOperation("declared.summary");

        // a local function, and the value type a generic helper takes
        IResult Local(int n) => n > 0 ? Results.Ok(n) : Results.BadRequest();
        app.MapGet("/inferred/local/{n}", Local).WithTisiliaOperation("inferred.local");
        app.MapGet("/declared/local/{n}", Results<Ok<int>, BadRequest> (int n) => n > 0 ? TypedResults.Ok(n) : TypedResults.BadRequest()).WithTisiliaOperation("declared.local");

        // the id on the lambda itself
        app.MapGet("/inferred/attributed", [TisiliaOperation("inferred.attributed")] () => Results.Accepted("/queue", new InferredSummary(1)));
        app.MapGet("/declared/attributed", [TisiliaOperation("declared.attributed")] () => TypedResults.Accepted("/queue", new InferredSummary(1)));

        // results that publish no response metadata: the declared twin says what they write
        app.MapGet("/inferred/problem/{fail}", (bool fail) => fail
                ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "down")
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["required"] }))
            .WithTisiliaOperation("inferred.problem");
        app.MapGet("/declared/problem/{fail}", (bool fail) => fail
                ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "down")
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["required"] }))
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesValidationProblem().WithTisiliaOperation("declared.problem");
        app.MapGet("/inferred/text", () => Results.Text("hello")).WithTisiliaOperation("inferred.text");
        app.MapGet("/declared/text", () => Results.Text("hello")).Produces<string>(200, "text/plain").WithTisiliaOperation("declared.text");
        app.MapGet("/inferred/json", () => Results.Json(new InferredTodo(1, "json"), statusCode: StatusCodes.Status201Created)).WithTisiliaOperation("inferred.json");
        app.MapGet("/declared/json", () => Results.Json(new InferredTodo(1, "json"), statusCode: StatusCodes.Status201Created)).Produces<InferredTodo>(201).WithTisiliaOperation("declared.json");
        app.MapGet("/inferred/empty", () => Results.Empty).WithTisiliaOperation("inferred.empty");
        app.MapGet("/declared/empty", () => TypedResults.Ok()).WithTisiliaOperation("declared.empty");

        // server-sent events
        app.MapGet("/inferred/events", () => Results.ServerSentEvents(Events())).WithTisiliaOperation("inferred.events");
        app.MapGet("/declared/events", () => TypedResults.ServerSentEvents(Events())).WithTisiliaOperation("declared.events");

        // status codes without a body, which typed results cannot declare: a switch expression whose arm throws
        app.MapGet("/inferred/status/{code}", (int code) => code switch
        {
            1 => Results.Unauthorized(),
            2 => Results.StatusCode(StatusCodes.Status202Accepted),
            3 => throw new InvalidOperationException("no response"),
            _ => Results.NoContent(),
        }).WithTisiliaOperation("inferred.status");
    }

    private static IResult Summary() => Results.Ok(new InferredSummary(2));

    private static async IAsyncEnumerable<InferredSummary> Events()
    {
        await Task.Yield();
        yield return new InferredSummary(1);
        yield return new InferredSummary(2);
    }
}

/// <summary>An id the source declares for one handler, which another handler receives at run time.</summary>
public static class MismatchedEndpoints
{
    public static void Map(IEndpointRouteBuilder app, bool asDeclared)
    {
        if (asDeclared)
        {
            app.MapGet("/mismatched", () => Results.Ok(1)).WithTisiliaOperation("mismatched");
        }
        else
        {
            app.MapGet("/mismatched/{other}", (int other) => Results.Ok(other)).WithMetadata(new TisiliaOperationAttribute("mismatched"));
        }
    }
}

/// <summary>Handlers whose responses the source does not fix, mapped one at a time: each makes the export fail with its reason.</summary>
public static class UnreadableEndpoints
{
    public static readonly IReadOnlyList<string> Names = ["local", "options", "object", "anonymous", "status", "conflict", "file", "helper", "builder"];

    public static void Map(IEndpointRouteBuilder app, string name)
    {
        switch (name)
        {
            case "local":
                app.MapGet("/unreadable", () =>
                {
                    IResult result = Results.Ok(1);
                    return result;
                }).WithTisiliaOperation("unreadable.local");
                break;
            case "options":
                app.MapGet("/unreadable", () => Results.Json(new InferredTodo(1, "x"), new System.Text.Json.JsonSerializerOptions())).WithTisiliaOperation("unreadable.options");
                break;
            case "object":
                app.MapGet("/unreadable", () => Results.Ok((object)new InferredTodo(1, "x"))).WithTisiliaOperation("unreadable.object");
                break;
            case "anonymous":
                app.MapGet("/unreadable", () => Results.Ok(new { id = 1 })).WithTisiliaOperation("unreadable.anonymous");
                break;
            case "status":
                app.MapGet("/unreadable/{code}", (int code) => Results.StatusCode(code)).WithTisiliaOperation("unreadable.status");
                break;
            case "conflict":
                app.MapGet("/unreadable/{a}", (bool a) => a ? Results.Ok(new InferredTodo(1, "x")) : Results.Ok(new InferredSummary(1))).WithTisiliaOperation("unreadable.conflict");
                break;
            case "file":
                app.MapGet("/unreadable", () => Results.Bytes(new byte[] { 1 }, "application/pdf")).WithTisiliaOperation("unreadable.file");
                break;
            case "helper":
                app.MapGet("/unreadable", () => Helper()).WithTisiliaOperation("unreadable.helper");
                break;
            case "builder":
                var builder = app.MapGet("/unreadable", () => Results.Ok(1));
                builder.WithTisiliaOperation("unreadable.builder");
                break;
        }
    }

    private static IResult Helper() => Results.Ok(1);
}
