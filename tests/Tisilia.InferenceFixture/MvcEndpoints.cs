using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Tisilia.InferenceFixture;

/// <summary>Actions that return IActionResult without response metadata. Each test registers the controllers it calls ([NonController] keeps
/// them out of every other host).</summary>
[ApiController]
[NonController]
[Route("inferred-mvc")]
public sealed class InferredMvcController : ControllerBase
{
    [HttpGet("todos/{id}")]
    [TisiliaOperation("mvc.inferred.todo")]
    public IActionResult Get(int id) => id > 0 ? Ok(new InferredTodo(id, "read")) : NotFound();

    [HttpPost("todos")]
    [TisiliaOperation("mvc.inferred.create")]
    public async Task<IActionResult> Create(InferredTodo todo)
    {
        await Task.Yield();
        if (todo.Title.Length == 0)
        {
            return BadRequest(new InferredSummary(0));
        }

        return CreatedAtAction(nameof(Get), new { id = todo.Id }, todo);
    }

    [HttpGet("problem/{fail}")]
    [TisiliaOperation("mvc.inferred.problem")]
    public ActionResult Fail(bool fail) => fail ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable) : ValidationProblem();

    [HttpGet("text")]
    [TisiliaOperation("mvc.inferred.text")]
    public IActionResult Text() => Content("hello");

    [HttpDelete("todos/{id}")]
    [TisiliaOperation("mvc.inferred.delete")]
    public IActionResult Delete(int id) => id > 0 ? NoContent() : StatusCode(StatusCodes.Status409Conflict);
}

/// <summary>The same actions, declaring their responses.</summary>
[ApiController]
[NonController]
[Route("declared-mvc")]
public sealed class DeclaredMvcController : ControllerBase
{
    [HttpGet("todos/{id}")]
    [TisiliaOperation("mvc.declared.todo")]
    [ProducesResponseType<InferredTodo>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(int id) => id > 0 ? Ok(new InferredTodo(id, "read")) : NotFound();

    [HttpPost("todos")]
    [TisiliaOperation("mvc.declared.create")]
    [ProducesResponseType<InferredSummary>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InferredTodo>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(InferredTodo todo)
    {
        await Task.Yield();
        if (todo.Title.Length == 0)
        {
            return BadRequest(new InferredSummary(0));
        }

        return CreatedAtAction(nameof(Get), new { id = todo.Id }, todo);
    }

    [HttpGet("problem/{fail}")]
    [TisiliaOperation("mvc.declared.problem")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult Fail(bool fail) => fail ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable) : ValidationProblem();

    [HttpGet("text")]
    [TisiliaOperation("mvc.declared.text")]
    [ProducesResponseType<string>(StatusCodes.Status200OK, "text/plain")]
    public IActionResult Text() => Content("hello");

    [HttpDelete("todos/{id}")]
    [TisiliaOperation("mvc.declared.delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Delete(int id) => id > 0 ? NoContent() : StatusCode(StatusCodes.Status409Conflict);
}

[JsonPolymorphic]
[JsonDerivedType(typeof(InferredCircle), "circle")]
public abstract record InferredShape;

public sealed record InferredCircle(double Radius) : InferredShape;

/// <summary>
/// Actions declaring a polymorphic type: MVC writes the value itself with the declared type, and a value passed to Ok() with its
/// own type, without the discriminator.
/// </summary>
[ApiController]
[NonController]
[Route("declared-shapes")]
public sealed class DeclaredShapeController : ControllerBase
{
    [HttpGet("value")]
    [TisiliaOperation("mvc.shape.value")]
    public ActionResult<InferredShape> GetValue() => new InferredCircle(1);

    [HttpGet("ok")]
    [TisiliaOperation("mvc.shape.ok")]
    public ActionResult<InferredShape> GetOk() => Ok(new InferredCircle(1));

    [HttpGet("declared")]
    [TisiliaOperation("mvc.shape.declared")]
    [ProducesResponseType<InferredShape>(StatusCodes.Status200OK)]
    public IActionResult GetDeclared() => Ok(new InferredCircle(1));
}

[ApiController]
[NonController]
[Route("shape-value")]
public sealed class ShapeValueController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.shape.only-value")]
    public async Task<ActionResult<InferredShape>> Get()
    {
        await Task.Yield();
        return new InferredCircle(2);
    }
}

/// <summary>Actions whose responses the source does not fix, registered one controller at a time.</summary>
[ApiController]
[NonController]
[Route("unreadable-mvc/null")]
public sealed class NullValueController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.null")]
    public IActionResult Get() => Ok(null);
}

[ApiController]
[NonController]
[Route("unreadable-mvc/file")]
public sealed class FileController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.file")]
    public IActionResult Get() => File(new byte[] { 1 }, "application/pdf");
}

[ApiController]
[NonController]
[Route("unreadable-mvc/shape")]
public sealed class PolymorphicController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.shape")]
    public IActionResult Get() => Ok(Shape());

    private static InferredShape Shape() => new InferredCircle(1);
}

[ApiController]
[NonController]
[Route("unreadable-mvc/model-state")]
public sealed class ModelStateController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.model-state")]
    public IActionResult Get() => BadRequest(ModelState);
}

[ApiController]
[NonController]
[Route("unreadable-mvc/result")]
public sealed class MinimalResultController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.result")]
    public IResult Get() => Results.Ok(new InferredSummary(1));
}

/// <summary>An action whose controller replaces the helper it calls.</summary>
public abstract class InferredBaseController : ControllerBase
{
    [HttpGet]
    [TisiliaOperation("mvc.unreadable.override")]
    public IActionResult Get() => Ok(new InferredSummary(1));
}

[ApiController]
[NonController]
[Route("unreadable-mvc/override")]
public sealed class OverridingController : InferredBaseController
{
    [NonAction]
    public override OkObjectResult Ok(object? value) => base.Ok(new InferredTodo(0, "replaced"));
}
