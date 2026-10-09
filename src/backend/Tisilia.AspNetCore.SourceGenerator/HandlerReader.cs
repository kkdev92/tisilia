using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Tisilia.AspNetCore.SourceGenerator;

/// <summary>
/// Reads every return path of a handler that returns <c>IResult</c> (a minimal API) or <c>IActionResult</c>/<c>ActionResult</c>
/// (an MVC action) and maps each to the response the helper it calls produces, as aspnetcore v10.0.0 implements them. One path it
/// cannot read makes the operation unreadable: the exporter reports that path rather than describe the others.
/// </summary>
/// <remarks>
/// A minimal API path is recorded as the TypedResults type the call creates, whose own endpoint metadata the exporter reads
/// (Results.Ok&lt;TValue&gt;(value) creates Ok&lt;TValue&gt;, or Ok when the value is null, Results.cs), or, for the results that publish no
/// response metadata, as the status code, body and media type they write. An MVC path is recorded as the status code and the
/// static type of the value, which is what [ProducesResponseType] declares; the parameters ControllerBase marks
/// [ActionResultObjectValue] are not trusted for this, because AcceptedAtAction(actionName, controllerName, routeValues) and
/// AcceptedAtRoute(routeValues) mark route values that are never written.
/// </remarks>
internal sealed class HandlerReader
{
    private const string ProblemDetails = "global::Microsoft.AspNetCore.Mvc.ProblemDetails";
    private const string HttpValidationProblemDetails = "global::Microsoft.AspNetCore.Http.HttpValidationProblemDetails";
    private const string ProblemJson = "application/problem+json";
    // ContentHttpResult and Utf8ContentHttpResult (ContentTypeConstants.DefaultContentType), MVC's ContentResultExecutor
    private const string TextDefault = "text/plain; charset=utf-8";
    // HttpResponseJsonExtensions.WriteAsJsonAsync (ContentTypeConstants.JsonContentTypeWithCharset)
    private const string JsonDefault = "application/json; charset=utf-8";

    private readonly Compilation compilation;
    private readonly List<ResponseModel> responses = [];
    private readonly List<HelperModel> helpers = [];
    private readonly List<WriteModel> writes = [];
    private string? failure;
    private SourceSpot? failureAt;
    private bool broken;

    private HandlerReader(Compilation compilation)
    {
        this.compilation = compilation;
    }

    /// <summary>
    /// The operation's model, or null when there is nothing to record: the handler returns a type that carries its own metadata
    /// (T, ActionResult&lt;T&gt;, Results&lt;…&gt;, a TypedResults type), or the code does not compile.
    /// </summary>
    public static OperationModel? Read(string id, SourceSpot at, IMethodSymbol handler, string kind, IBlockOperation? body, Compilation compilation)
    {
        var flavor = Awaited(handler.ReturnType) switch
        {
            var t when Is(t, "Microsoft.AspNetCore.Http.IResult") => "minimal",
            var t when Is(t, "Microsoft.AspNetCore.Mvc.IActionResult") || Is(t, "Microsoft.AspNetCore.Mvc.ActionResult") => "mvc",
            // its metadata comes from T, but a value it passes to Ok(value) is written with the value's own type
            var t when Is(t, "Microsoft.AspNetCore.Mvc.ActionResult`1") => "mvc-typed",
            _ => null,
        };
        if (flavor is null)
        {
            return null;
        }

        var reader = new HandlerReader(compilation);
        var handlerModel = new HandlerModel(
            kind,
            reader.TypeName(handler.ContainingType),
            kind == "method" ? handler.Name : null,
            new EquatableArray<ParameterModel>(handler.Parameters.Select(p => new ParameterModel(p.Name, p.RefKind == RefKind.None ? reader.TypeName(p.Type) : null))),
            reader.TypeName(handler.ReturnType));
        if (body is null)
        {
            reader.Fail(at, "its body is not in this project's source");
        }
        else
        {
            reader.ReadBody(body);
        }

        if (reader.broken)
        {
            return null;
        }

        if (flavor == "mvc-typed")
        {
            // only what MVC writes with the value's own type matters for an action whose metadata comes from its result type
            return reader.writes.Count == 0 ? null : new OperationModel(id, at, flavor, handlerModel, null, null,
                new EquatableArray<ResponseModel>([]), new EquatableArray<HelperModel>([]), new EquatableArray<WriteModel>(reader.writes.Distinct()));
        }

        return new OperationModel(
            id,
            at,
            flavor,
            handlerModel,
            reader.failure,
            reader.failureAt,
            new EquatableArray<ResponseModel>(reader.failure is null ? reader.responses.Distinct() : []),
            new EquatableArray<HelperModel>(reader.failure is null ? reader.helpers.Distinct() : []),
            new EquatableArray<WriteModel>(reader.writes.Distinct()));
    }

    /// <summary>A failure found before the handler was read: an operation whose builder does not lead to its handler.</summary>
    public static OperationModel Unreadable(string id, SourceSpot at, string failure) =>
        new(id, at, "unknown", new HandlerModel("unknown", null, null, new EquatableArray<ParameterModel>([]), null), failure, at,
            new EquatableArray<ResponseModel>([]), new EquatableArray<HelperModel>([]), new EquatableArray<WriteModel>([]));

    private void ReadBody(IBlockOperation body)
    {
        var any = false;
        foreach (var operation in body.Descendants())
        {
            if (operation is not IReturnOperation path || InNestedFunction(path, body))
            {
                continue;
            }

            if (path.Kind != OperationKind.Return)
            {
                Fail(path, "it is an iterator");
                return;
            }

            any = true;
            if (path.ReturnedValue is null)
            {
                Fail(path, "a return statement returns no value");
                continue;
            }

            ReadValue(path.ReturnedValue);
        }

        if (!any)
        {
            Fail(body, "it never returns a result");
        }
    }

    // a return statement of a lambda or local function written inside the handler returns from that function, not the handler
    private static bool InNestedFunction(IOperation operation, IOperation body)
    {
        for (var parent = operation.Parent; parent is not null && parent != body; parent = parent.Parent)
        {
            if (parent is ILocalFunctionOperation or IAnonymousFunctionOperation)
            {
                return true;
            }
        }

        return false;
    }

    private void ReadValue(IOperation returned)
    {
        // code that does not compile (an unknown name, an argument no overload takes) is the compiler's to report
        if (returned.DescendantsAndSelf().Any(o => o is IInvalidOperation))
        {
            broken = true;
            return;
        }

        var value = Unwrap(returned, out var userDefined);
        if (userDefined)
        {
            Fail(value, $"it returns {Text(value)} through a user-defined conversion");
            return;
        }

        switch (value)
        {
            case IInvalidOperation:
                broken = true;
                return;
            case IConditionalOperation { WhenFalse: { } whenFalse } conditional:
                ReadValue(conditional.WhenTrue);
                ReadValue(whenFalse);
                return;
            case ISwitchExpressionOperation choice:
                foreach (var arm in choice.Arms)
                {
                    ReadValue(arm.Value);
                }

                return;
            case IThrowOperation:
                // an exception, not a response
                return;
            case { ConstantValue: { HasValue: true, Value: null } }:
                // the framework throws for a null result (RequestDelegateFactory, MVC's action result executors)
                return;
            case IInvocationOperation call:
                ReadCall(call);
                return;
            case IPropertyReferenceOperation { Property: { Name: "Empty", IsStatic: true } property }
                when Is(property.ContainingType, "Microsoft.AspNetCore.Http.Results") || Is(property.ContainingType, "Microsoft.AspNetCore.Http.TypedResults"):
                // EmptyHttpResult writes nothing and leaves the status code at 200
                Add(new ResponseModel(200, "none", null, null, null));
                return;
            default:
                Fail(value, $"it returns {Text(value)}, which is not a call to a Results, TypedResults or ControllerBase helper");
                return;
        }
    }

    private void ReadCall(IInvocationOperation call)
    {
        var container = call.TargetMethod.ContainingType;
        if (Is(container, "Microsoft.AspNetCore.Http.Results"))
        {
            Minimal(call, typed: false);
        }
        else if (Is(container, "Microsoft.AspNetCore.Http.TypedResults"))
        {
            Minimal(call, typed: true);
        }
        else if (Is(container, "Microsoft.AspNetCore.Mvc.ControllerBase"))
        {
            Mvc(call);
        }
        else
        {
            Fail(call, $"it returns {Text(call)}, which is not a call to a Results, TypedResults or ControllerBase helper");
        }
    }

    private void Minimal(IInvocationOperation call, bool typed)
    {
        var method = call.TargetMethod;
        switch (method.Name)
        {
            case "Ok" or "Created" or "CreatedAtRoute" or "Accepted" or "AcceptedAtRoute"
                or "NotFound" or "BadRequest" or "Conflict" or "UnprocessableEntity" or "InternalServerError":
                ValueResult(call, typed);
                return;
            case "NoContent":
                AddResult(call, typed ? method.ReturnType : HttpResultsType("NoContent"));
                return;
            case "ValidationProblem" when typed:
                AddResult(call, method.ReturnType);
                return;
            case "ValidationProblem":
                // Results.ValidationProblem creates TypedResults.Problem(new HttpValidationProblemDetails(errors) { Status = statusCode }),
                // not the ValidationProblem result: the status code defaults to 400 (ProblemDetailsDefaults.Apply)
                if (Status(call, "statusCode", 400) is { } validation)
                {
                    Add(new ResponseModel(validation, "json", null, HttpValidationProblemDetails, ProblemJson));
                }

                return;
            case "Problem":
                if (Parameter(call, "problemDetails") is not null)
                {
                    Fail(call, $"{Text(call)} takes its status code from the ProblemDetails value");
                    return;
                }

                // ProblemHttpResult: the status code defaults to 500, the body is written as application/problem+json
                if (Status(call, "statusCode", 500) is { } problem)
                {
                    Add(new ResponseModel(problem, "json", null, ProblemDetails, ProblemJson));
                }

                return;
            case "Unauthorized":
                // UnauthorizedHttpResult sets 401 and writes nothing; it publishes no response metadata
                Add(new ResponseModel(401, "none", null, null, null));
                return;
            case "StatusCode":
                if (Status(call, "statusCode", null) is { } status)
                {
                    Add(new ResponseModel(status, "none", null, null, null));
                }

                return;
            case "Text" or "Content":
                TextResult(call);
                return;
            case "Json":
                JsonResult(call);
                return;
            case "ServerSentEvents":
                Events(call, typed);
                return;
            case "File" or "Bytes" or "Stream" or "PhysicalFile" or "VirtualFile":
                Fail(call, $"{Text(call)} writes a file, whose media type and range responses the source does not fix; declare it with .Produces<FileContentResult>(status, contentType)");
                return;
            case "Redirect" or "LocalRedirect" or "RedirectToRoute":
                Fail(call, $"{Text(call)} redirects, and fetch follows redirects itself");
                return;
            case "Challenge" or "Forbid" or "SignIn" or "SignOut":
                Fail(call, $"{Text(call)} leaves the response to an authentication handler");
                return;
            default:
                Fail(call, $"it returns {Text(call)}, which Tisilia does not read");
                return;
        }
    }

    /// <summary>
    /// Ok, Created, Accepted, NotFound, BadRequest, … with or without a value. TypedResults.X&lt;TValue&gt; creates X&lt;TValue&gt;;
    /// Results.X&lt;TValue&gt;(value) creates X when the value is null and X&lt;TValue&gt; otherwise, and Results.X(object? value) passes the
    /// value as an object (Results.cs, aspnetcore v10.0.0).
    /// </summary>
    private void ValueResult(IInvocationOperation call, bool typed)
    {
        var method = call.TargetMethod;
        var value = Parameter(call, "value") ?? Parameter(call, "error");
        if (value is null)
        {
            AddResult(call, typed ? method.ReturnType : HttpResultsType(method.Name));
            return;
        }

        // TValue, or object for the overloads that take the value as an object
        var valueType = method.IsGenericMethod ? method.TypeArguments[0] : value.Parameter!.Type;
        if (typed)
        {
            if (CheckValueType(call, valueType, allowObject: false))
            {
                AddResult(call, method.ReturnType);
            }

            return;
        }

        if (IsNull(value))
        {
            AddResult(call, HttpResultsType(method.Name));
            return;
        }

        if (CheckValueType(call, valueType, allowObject: false))
        {
            AddResult(call, HttpResultsType(method.Name + "`1", valueType));
        }
    }

    /// <summary>
    /// Text and Content write ContentHttpResult (or Utf8ContentHttpResult for UTF-8 bytes): the given content type, text/plain;
    /// charset=utf-8 otherwise, and the given status code, the response's 200 otherwise. They publish no response metadata.
    /// </summary>
    private void TextResult(IInvocationOperation call)
    {
        var contentType = Parameter(call, "contentType");
        if (contentType is not null && contentType.Parameter!.Type.SpecialType != SpecialType.System_String)
        {
            Fail(call, $"{Text(call)} passes a MediaTypeHeaderValue");
            return;
        }

        if (Parameter(call, "contentEncoding") is { } encoding && !IsNull(encoding))
        {
            Fail(encoding.Value, $"{Text(call)} passes an Encoding");
            return;
        }

        if (MediaType(call, contentType, TextDefault) is { } media && Status(call, "statusCode", 200) is { } status)
        {
            Add(new ResponseModel(status, "text", null, "string", media));
        }
    }

    /// <summary>
    /// Json writes JsonHttpResult&lt;TValue&gt;. Without JsonSerializerOptions it writes the value with the endpoint's JSON options, like
    /// Ok&lt;TValue&gt;, at the given status code and content type; a null value is not written. Options, a JsonTypeInfo or a
    /// JsonSerializerContext are chosen at run time, and a ProblemDetails value sets the status code itself.
    /// </summary>
    private void JsonResult(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if (Parameter(call, "options") is not { } options)
        {
            Fail(call, $"{Text(call)} writes the value with a JsonTypeInfo or JsonSerializerContext chosen at run time");
            return;
        }

        if (!IsNull(options))
        {
            Fail(options.Value, $"{Text(call)} writes the value with JsonSerializerOptions chosen at run time; declare them with WithTisiliaJsonOptions<T>(options) and return TypedResults.Json");
            return;
        }

        var data = Parameter(call, "data")!;
        var valueType = method.IsGenericMethod ? method.TypeArguments[0] : data.Parameter!.Type;
        if (IsProblemDetails(valueType))
        {
            Fail(call, $"{Text(call)} writes a ProblemDetails value, which sets the status code itself");
            return;
        }

        if (!CheckValueType(call, valueType, allowObject: false))
        {
            return;
        }

        if (MediaType(call, Parameter(call, "contentType"), JsonDefault) is { } media && Status(call, "statusCode", 200) is { } status)
        {
            Add(IsNull(data) ? new ResponseModel(status, "none", null, null, null) : new ResponseModel(status, "json", null, TypeName(valueType), media));
        }
    }

    /// <summary>ServerSentEventsResult&lt;T&gt;, whose metadata declares SseItem&lt;T&gt; as text/event-stream; string events for the non-generic overload.</summary>
    private void Events(IInvocationOperation call, bool typed)
    {
        var method = call.TargetMethod;
        if (typed)
        {
            AddResult(call, method.ReturnType);
            return;
        }

        var data = method.IsGenericMethod ? method.TypeArguments[0] : compilation.GetSpecialType(SpecialType.System_String);
        if (CheckValueType(call, data, allowObject: true))
        {
            AddResult(call, HttpResultsType("ServerSentEventsResult`1", data));
        }
    }

    private void Mvc(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        helpers.Add(new HelperModel(method.Name, new EquatableArray<string>(method.Parameters.Select(p => TypeName(p.Type) ?? "?"))));
        RecordWrite(call);
        switch (method.Name)
        {
            case "Ok":
                MvcValue(call, 200);
                return;
            case "Created" or "CreatedAtAction" or "CreatedAtRoute":
                MvcValue(call, 201);
                return;
            case "Accepted" or "AcceptedAtAction" or "AcceptedAtRoute":
                MvcValue(call, 202);
                return;
            case "NoContent":
                MvcValue(call, 204);
                return;
            case "BadRequest":
                MvcValue(call, 400);
                return;
            case "Unauthorized":
                MvcValue(call, 401);
                return;
            case "NotFound":
                MvcValue(call, 404);
                return;
            case "Conflict":
                MvcValue(call, 409);
                return;
            case "UnprocessableEntity":
                MvcValue(call, 422);
                return;
            case "StatusCode":
                if (Status(call, "statusCode", null) is { } status)
                {
                    MvcValue(call, status);
                }

                return;
            case "Problem":
                // ProblemDetailsFactory.CreateProblemDetails(statusCode ?? 500), written with ObjectResult.StatusCode = its Status
                if (Status(call, "statusCode", 500) is { } problem)
                {
                    Add(new ResponseModel(problem, "problem", null, null, null));
                }

                return;
            case "ValidationProblem":
                if (Parameter(call, "descriptor") is { } descriptor)
                {
                    // new BadRequestObjectResult(descriptor)
                    MvcBody(call, descriptor.Value, 400);
                }
                else if (Status(call, "statusCode", 400) is { } validation)
                {
                    // ProblemDetailsFactory.CreateValidationProblemDetails(ModelState, statusCode ?? 400)
                    Add(new ResponseModel(validation, "validation", null, null, null));
                }

                return;
            case "Content":
                MvcContent(call);
                return;
            case "File" or "PhysicalFile":
                Fail(call, $"{Text(call)} writes a file; declare it with [ProducesResponseType(typeof(FileContentResult), status, contentType)]");
                return;
            case "Challenge" or "Forbid" or "SignIn" or "SignOut":
                Fail(call, $"{Text(call)} leaves the response to an authentication handler");
                return;
            default:
                Fail(call, method.Name.StartsWith("Redirect", StringComparison.Ordinal) || method.Name.StartsWith("LocalRedirect", StringComparison.Ordinal)
                    ? $"{Text(call)} redirects, and fetch follows redirects itself"
                    : $"it returns {Text(call)}, which Tisilia does not read");
                return;
        }
    }

    /// <summary>A ControllerBase helper that receives a value: the status code of the result it creates, when the source fixes it.</summary>
    private void RecordWrite(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if ((Parameter(call, "value") ?? Parameter(call, "error") ?? Parameter(call, "descriptor")) is not { } value || IsNull(value))
        {
            return;
        }

        int? status = method.Name switch
        {
            "Ok" => 200,
            "Created" or "CreatedAtAction" or "CreatedAtRoute" => 201,
            "Accepted" or "AcceptedAtAction" or "AcceptedAtRoute" => 202,
            "BadRequest" or "ValidationProblem" => 400,
            "Unauthorized" => 401,
            "NotFound" => 404,
            "Conflict" => 409,
            "UnprocessableEntity" => 422,
            _ => Parameter(call, "statusCode") is { } code && TryConstant(code, out var constant) && constant is not null ? Convert.ToInt32(constant, CultureInfo.InvariantCulture) : null,
        };
        writes.Add(new WriteModel(status, method.Name));
    }

    /// <summary>
    /// A ControllerBase helper with the status code of the result it creates ([DefaultStatusCode]) and, when it takes one, the
    /// value: the parameter named value (or error), never one typed ModelStateDictionary (written as a SerializableError).
    /// </summary>
    private void MvcValue(IInvocationOperation call, int status)
    {
        if (Parameter(call, "modelState") is not null)
        {
            Fail(call, $"{Text(call)} writes the ModelStateDictionary as a SerializableError");
            return;
        }

        var value = Parameter(call, "value") ?? Parameter(call, "error");
        if (value is null)
        {
            Add(new ResponseModel(status, "none", null, null, null));
            return;
        }

        if (IsNull(value))
        {
            // an ObjectResult without a value writes no body; HttpNoContentOutputFormatter turns a 200 into 204, and above 399 the
            // client error mapping of [ApiController] does not apply to it, as it does to NotFound()
            if (status is > 200 and < 300)
            {
                Add(new ResponseModel(status, "none", null, null, null));
            }
            else
            {
                Fail(value.Value, status == 200 ? $"{Text(call)} is written as 204 No Content" : $"{Text(call)} writes the status code without a body, unlike {call.TargetMethod.Name}()");
            }

            return;
        }

        MvcBody(call, value.Value, status);
    }

    private void MvcBody(IInvocationOperation call, IOperation value, int status)
    {
        // the static type of the value, before its conversion to object
        var type = Unwrap(value, out _).Type;
        if (CheckValueType(call, type, allowObject: false))
        {
            Add(new ResponseModel(status, "value", null, TypeName(type), null));
        }
    }

    /// <summary>ContentResult: the given content type, text/plain; charset=utf-8 otherwise, at 200.</summary>
    private void MvcContent(IInvocationOperation call)
    {
        if (Parameter(call, "contentEncoding") is not null)
        {
            Fail(call, $"{Text(call)} passes an Encoding");
            return;
        }

        var contentType = Parameter(call, "contentType");
        if (contentType is not null && contentType.Parameter!.Type.SpecialType != SpecialType.System_String && !IsNull(contentType))
        {
            Fail(call, $"{Text(call)} passes a MediaTypeHeaderValue");
            return;
        }

        if (MediaType(call, contentType is null || IsNull(contentType) ? null : contentType, TextDefault) is { } media)
        {
            Add(new ResponseModel(200, "text", null, "string", media));
        }
    }

    private void AddResult(IOperation at, ITypeSymbol? result)
    {
        if (result is null || TypeName(result) is not { } name)
        {
            Fail(at, $"{Text(at)} creates a result type this compilation does not have");
            return;
        }

        Add(new ResponseModel(null, "result", name, null, null));
    }

    private void Add(ResponseModel response) => responses.Add(response);

    private void Fail(IOperation at, string message) => Fail(SourceSpot.Of(at.Syntax.GetLocation()), message);

    private void Fail(SourceSpot at, string message)
    {
        if (failure is null)
        {
            failure = message;
            failureAt = at;
        }
    }

    /// <summary>
    /// Whether the value's type can be described: not object or dynamic (the value's type at run time decides what is written),
    /// not anonymous or a tuple, not a result itself, and nameable by code generated outside it.
    /// </summary>
    private bool CheckValueType(IOperation at, ITypeSymbol? type, bool allowObject)
    {
        if (type is null || type.TypeKind == TypeKind.Error)
        {
            broken = true;
            return false;
        }

        if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic)
        {
            if (allowObject && type.SpecialType == SpecialType.System_Object)
            {
                return true;
            }

            Fail(at, $"{Text(at)} passes the value as an object: its type at run time decides what is written; pass a value of a declared type");
            return false;
        }

        if (type is INamedTypeSymbol { IsAnonymousType: true })
        {
            Fail(at, $"{Text(at)} passes an anonymous type, which the contract cannot name; return a named type");
            return false;
        }

        if (type is INamedTypeSymbol { IsTupleType: true })
        {
            Fail(at, $"{Text(at)} passes a tuple, whose elements System.Text.Json does not write");
            return false;
        }

        if (Implements(type, "Microsoft.AspNetCore.Http.IResult") || Implements(type, "Microsoft.AspNetCore.Mvc.IActionResult"))
        {
            Fail(at, $"{Text(at)} passes a result as the value");
            return false;
        }

        if (TypeName(type) is null)
        {
            Fail(at, $"{Text(at)} passes {type.ToDisplayString()}, which code generated outside it cannot name (a type parameter, or a private or file-local type)");
            return false;
        }

        return true;
    }

    /// <summary>The constant status code a parameter receives, or the helper's default when it is null or the overload has none.</summary>
    private int? Status(IInvocationOperation call, string parameter, int? fallback)
    {
        object? value = null;
        if (Parameter(call, parameter) is { } argument && !TryConstant(argument, out value))
        {
            Fail(argument.Value, $"{Text(call)} passes a status code that is not a constant");
            return null;
        }

        if (value is null)
        {
            if (fallback is null)
            {
                Fail(call, $"{Text(call)} has no status code");
            }

            return fallback;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The constant media type a parameter receives, or the helper's default when it is null or absent.</summary>
    private string? MediaType(IInvocationOperation call, IArgumentOperation? argument, string fallback)
    {
        if (argument is null)
        {
            return fallback;
        }

        if (!TryConstant(argument, out var value))
        {
            Fail(argument.Value, $"{Text(call)} passes a content type that is not a constant");
            return null;
        }

        return value as string ?? fallback;
    }

    private static IArgumentOperation? Parameter(IInvocationOperation call, string name) =>
        call.Arguments.FirstOrDefault(a => a.Parameter?.Name == name);

    private static bool IsNull(IArgumentOperation argument) => TryConstant(argument, out var value) && value is null;

    /// <summary>
    /// The value an argument passes when the compiler fixes it: a constant, the null of default for a reference or nullable type,
    /// or for an omitted argument the parameter's default (whose null for an int? is no constant to the compiler).
    /// </summary>
    private static bool TryConstant(IArgumentOperation argument, out object? value)
    {
        if (argument.ArgumentKind == ArgumentKind.DefaultValue)
        {
            value = argument.Parameter is { HasExplicitDefaultValue: true } parameter ? parameter.ExplicitDefaultValue : null;
            // "TValue? value = default" of a generic helper is default(TValue): no null when TValue is a value type
            return value is not null || argument.Parameter is not { Type: { IsValueType: true } valueType } || valueType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        }

        var operation = Unwrap(argument.Value, out _);
        if (operation.ConstantValue.HasValue)
        {
            value = operation.ConstantValue.Value;
            return true;
        }

        value = null;
        return operation is IDefaultValueOperation { Type: { } type }
            && (type.IsReferenceType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);
    }

    private static IOperation Unwrap(IOperation operation, out bool userDefined)
    {
        userDefined = false;
        while (operation is IConversionOperation conversion)
        {
            // ActionResult<TValue>'s own conversions, from the value and from an ActionResult, keep what they convert
            if (conversion.Conversion.IsUserDefined && !Is(conversion.OperatorMethod?.ContainingType, "Microsoft.AspNetCore.Mvc.ActionResult`1"))
            {
                userDefined = true;
                return operation;
            }

            operation = conversion.Operand;
        }

        return operation;
    }

    private INamedTypeSymbol? HttpResultsType(string metadataName, ITypeSymbol? argument = null)
    {
        var type = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults." + metadataName);
        return type is null ? null : argument is null ? type : type.Construct(argument);
    }

    /// <summary>The type as a C# expression generated code can put in typeof, or null when it cannot name it.</summary>
    public string? TypeName(ITypeSymbol? type) =>
        type is not null && Nameable(type) ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;

    private bool Nameable(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => Nameable(array.ElementType),
        INamedTypeSymbol named => !named.IsAnonymousType && named.TypeKind != TypeKind.Error && !named.IsFileLocal
            && compilation.IsSymbolAccessibleWithin(named.OriginalDefinition, compilation.Assembly)
            && (named.ContainingType is null || Nameable(named.ContainingType))
            && named.TypeArguments.All(Nameable),
        _ => false,
    };

    private static ITypeSymbol Awaited(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeArguments.Length: 1 } named
            && (Is(named, "System.Threading.Tasks.Task`1") || Is(named, "System.Threading.Tasks.ValueTask`1"))
            ? named.TypeArguments[0]
            : type;

    private static bool IsProblemDetails(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (Is(t, "Microsoft.AspNetCore.Mvc.ProblemDetails"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(ITypeSymbol type, string interfaceName) =>
        Is(type, interfaceName) || type.AllInterfaces.Any(i => Is(i, interfaceName));

    public static bool Is(ITypeSymbol? type, string metadataName) =>
        type is INamedTypeSymbol named && MetadataName(named.OriginalDefinition) == metadataName;

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingType is { } outer
            ? MetadataName(outer) + "+" + type.MetadataName
            : (type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + type.MetadataName;

    /// <summary>The source of an expression on one line, shortened: what a diagnostic quotes.</summary>
    private static string Text(IOperation operation)
    {
        var text = string.Join(" ", operation.Syntax.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text.Substring(0, 77) + "...";
    }
}
