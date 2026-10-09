using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// The responses Tisilia's source generator read from an operation's handler, which the exporter uses for an endpoint that declares
/// none. The generator compiles them into the handler's assembly as <c>Tisilia.Generated.TisiliaResponseInference</c>.
/// </summary>
/// <remarks>
/// Each inferred response is the response type ApiExplorer would list had the endpoint declared it the typed way. A minimal API
/// path names the TypedResults type it creates, and the metadata comes from that type's own PopulateMetadata, as for a handler
/// that returns it; the results that publish none (Unauthorized, StatusCode, Problem, Text, Json, Empty) carry the status code,
/// body and media type aspnetcore v10.0.0 writes. An MVC path is what [ProducesResponseType(typeof(T), status)] declares, with the
/// media types ApiExplorer gives it (ApiResponseTypeProvider.CalculateResponseFormatForType: the [Produces] content types, else each
/// output formatter's).
/// </remarks>
internal static class ResponseInference
{
    private const string ClassName = "Tisilia.Generated.TisiliaResponseInference";
    private const int Format = 1;
    private static readonly ConditionalWeakTable<Assembly, Registry> Registries = [];
    private static readonly MethodInfo PopulateMethod = typeof(ResponseInference).GetMethod(nameof(Populate), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The inferred responses, or why there are none.</summary>
    internal sealed record Outcome(List<ApiResponseType>? Responses, string? Failure);

    /// <summary>A ControllerBase helper that writes a value, which MVC writes with the value's own type; a null status code is one the source does not fix.</summary>
    internal sealed record HelperWrite(int? Status, string Helper);

    /// <summary>
    /// The ControllerBase helpers an MVC action passes values to, as its source shows them, whatever it declares; null when the
    /// source was not read (no generator ran for the handler's assembly, or it declares the operation for another handler).
    /// </summary>
    public static IReadOnlyList<HelperWrite>? HelperWrites(string operationId, System.Reflection.MethodInfo? handler)
    {
        if (handler is null || Registries.GetValue(handler.Module.Assembly, Registry.Load) is not { Failure: null } registry)
        {
            return null;
        }

        var matching = registry.Operations.Where(o => o.Id == operationId && o.Handler.Kind is "method" or "lambda" && o.Handler.Matches(handler)).ToList();
        return matching.Count == 0 ? null : matching.SelectMany(o => o.Writes).Distinct().ToList();
    }

    public static Outcome Read(string operationId, System.Reflection.MethodInfo? handler, ApiDescription description, IServiceProvider services)
    {
        if (handler is null)
        {
            return new(null, "the endpoint names no handler method");
        }

        var assembly = handler.Module.Assembly;
        var registry = Registries.GetValue(assembly, Registry.Load);
        if (registry.Failure is { } unavailable)
        {
            return new(null, unavailable);
        }

        var declared = registry.Operations.Where(o => o.Id == operationId).ToList();
        var known = declared.Where(o => o.Handler.Kind is "method" or "lambda").ToList();
        var matching = known.Where(o => o.Handler.Matches(handler)).ToList();
        if (matching.Count == 0)
        {
            return new(null, declared.Count == 0
                ? $"the source of {assembly.GetName().Name} has no WithTisiliaOperation(\"{operationId}\") on a Map call and no [TisiliaOperation(\"{operationId}\")] with a constant id"
                : known.Count == 0
                    ? $"{declared[0].Failure} ({declared[0].At})"
                    : $"the source declares '{operationId}' at {known[0].At} for another handler");
        }

        if (matching.Select(o => o.Content).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return new(null, $"the source declares '{operationId}' at {string.Join(" and ", matching.Select(o => o.At))} with handlers that return different responses");
        }

        var entry = matching[0];
        if (entry.Failure is { } failure)
        {
            return new(null, $"{failure} ({entry.FailureAt})");
        }

        var isMvc = description.ActionDescriptor is ControllerActionDescriptor;
        if (entry.Flavor != (isMvc ? "mvc" : "minimal"))
        {
            return new(null, isMvc ? "the action returns IResult, which MVC writes with the minimal API JSON options" : "the handler returns IActionResult, which a minimal API writes as a JSON object");
        }

        return isMvc ? Mvc(entry, (ControllerActionDescriptor)description.ActionDescriptor, services) : Minimal(entry, handler);
    }

    private static Outcome Minimal(Entry entry, System.Reflection.MethodInfo handler)
    {
        var responses = new List<ApiResponseType>();
        foreach (var inferred in entry.Responses)
        {
            if (inferred.Kind == "result")
            {
                // what the TypedResults type declares about itself, as RequestDelegateFactory reads it for a handler that returns it
                var builder = new RouteEndpointBuilder(null, RoutePatternFactory.Parse("/"), 0);
                if (!typeof(IEndpointMetadataProvider).IsAssignableFrom(inferred.Result))
                {
                    return new(null, $"{inferred.Result} publishes no endpoint metadata");
                }

                PopulateMethod.MakeGenericMethod(inferred.Result!).Invoke(null, [handler, builder]);
                var metadata = builder.Metadata.OfType<IProducesResponseTypeMetadata>().ToList();
                if (metadata.Count == 0)
                {
                    return new(null, $"{inferred.Result} publishes no response metadata");
                }

                foreach (var produced in metadata)
                {
                    var type = produced.Type ?? typeof(void);
                    var response = new ApiResponseType { StatusCode = produced.StatusCode, Type = type };
                    if (type != typeof(void))
                    {
                        // EndpointMetadataApiDescriptionProvider: the declared content types, else text/plain for a string and application/json
                        foreach (var media in produced.ContentTypes.DefaultIfEmpty(type == typeof(string) ? "text/plain" : "application/json"))
                        {
                            response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = media });
                        }
                    }

                    responses.Add(response);
                }
            }
            else if (inferred.Kind == "none")
            {
                responses.Add(new ApiResponseType { StatusCode = inferred.Status!.Value, Type = typeof(void) });
            }
            else if (inferred is { Body: { } body, Media: { } media })
            {
                var response = new ApiResponseType { StatusCode = inferred.Status!.Value, Type = body };
                response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = media });
                responses.Add(response);
            }
            else
            {
                return new(null, $"the source records a {inferred.Kind} response without its body or media type");
            }
        }

        return Distinct(responses);
    }

    private static Outcome Mvc(Entry entry, ControllerActionDescriptor action, IServiceProvider services)
    {
        // ControllerBase's helpers are virtual: the controller that runs the action must not replace one the action calls
        var controller = action.ControllerTypeInfo.AsType();
        foreach (var helper in entry.Helpers)
        {
            var method = helper.Parameters.Any(p => p is null) ? null : controller.GetMethod(helper.Name, BindingFlags.Public | BindingFlags.Instance, helper.Parameters!);
            if (method?.DeclaringType != typeof(ControllerBase))
            {
                return new(null, $"{controller.Name} overrides ControllerBase.{helper.Name}, which the action calls");
            }
        }

        if (entry.Responses.Any(r => r.Kind is "problem" or "validation")
            && services.GetService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>()?.GetType().FullName != "Microsoft.AspNetCore.Mvc.Infrastructure.DefaultProblemDetailsFactory")
        {
            return new(null, "Problem() and ValidationProblem() take their status code from the application's own ProblemDetailsFactory");
        }

        var formatters = services.GetService<IOptions<MvcOptions>>()?.Value.OutputFormatters.OfType<IApiResponseTypeMetadataProvider>().ToList() ?? [];
        // [Produces] on the action or its controller, in filter order, each narrowing what the previous allowed (ApiResponseTypeProvider)
        var declared = new MediaTypeCollection();
        foreach (var provider in action.FilterDescriptors.Select(f => f.Filter).OfType<IApiResponseMetadataProvider>().Where(p => p is not ProducesResponseTypeAttribute))
        {
            provider.SetContentTypes(declared);
        }

        var responses = new List<ApiResponseType>();
        foreach (var inferred in entry.Responses)
        {
            var type = inferred.Kind switch
            {
                "none" => typeof(void),
                "problem" => typeof(ProblemDetails),
                "validation" => typeof(ValidationProblemDetails),
                "text" => typeof(string),
                _ => inferred.Body,
            };
            if (type is null || (inferred.Kind == "text" && inferred.Media is null))
            {
                return new(null, $"the source records a {inferred.Kind} response without its body or media type");
            }

            var response = new ApiResponseType { StatusCode = inferred.Status!.Value, Type = type };
            if (inferred.Kind == "text")
            {
                // ContentResult writes its own content type, past the formatters
                response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = inferred.Media! });
            }
            else if (type != typeof(void))
            {
                foreach (var contentType in declared.Count == 0 ? [null] : declared.Cast<string?>())
                {
                    var supported = false;
                    foreach (var formatter in formatters)
                    {
                        if (formatter.GetSupportedContentTypes(contentType!, type) is not { } media)
                        {
                            continue;
                        }

                        supported = true;
                        foreach (var m in media)
                        {
                            response.ApiResponseFormats.Add(new ApiResponseFormat { Formatter = (IOutputFormatter)formatter, MediaType = m });
                        }
                    }

                    if (!supported && contentType is not null)
                    {
                        response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = contentType });
                    }
                }
            }

            responses.Add(response);
        }

        return Distinct(responses);
    }

    /// <summary>
    /// One response per status code, in the order of the return paths (the order a Results&lt;…&gt; union of them would list): two
    /// paths may write one status code only with one body and one media type.
    /// </summary>
    private static Outcome Distinct(List<ApiResponseType> responses)
    {
        var distinct = new List<ApiResponseType>();
        foreach (var response in responses)
        {
            if (distinct.Find(r => r.StatusCode == response.StatusCode) is not { } existing)
            {
                distinct.Add(response);
                continue;
            }

            if (existing.Type != response.Type || !existing.ApiResponseFormats.Select(f => f.MediaType).SequenceEqual(response.ApiResponseFormats.Select(f => f.MediaType), StringComparer.OrdinalIgnoreCase))
            {
                return new(null, $"status {response.StatusCode} is written with {Describe(existing)} on one path and {Describe(response)} on another");
            }
        }

        return new(distinct, null);

        static string Describe(ApiResponseType response) => response.Type is null || response.Type == typeof(void) ? "no body" : response.Type.Name;
    }

    private static void Populate<T>(System.Reflection.MethodInfo method, EndpointBuilder builder) where T : IEndpointMetadataProvider =>
        T.PopulateMetadata(method, builder);

    private sealed record Registry(IReadOnlyList<Entry> Operations, string? Failure)
    {
        public static Registry Load(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (assembly.GetType(ClassName, throwOnError: false) is not { } type)
            {
                return new([], $"Tisilia's source generator did not run for {name}: the project that defines the handler reads the responses only when it references Kkdev92.Tisilia.AspNetCore itself");
            }

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            if (type.GetField("Format", flags)?.GetRawConstantValue() is not Format)
            {
                return new([], $"{name} was compiled by another version of Tisilia's source generator; rebuild it with this one");
            }

            try
            {
                var types = (Type[])type.GetField("Types", flags)!.GetValue(null)!;
                var json = (string)type.GetField("Operations", flags)!.GetRawConstantValue()!;
                using var document = JsonDocument.Parse(json);
                return new(document.RootElement.EnumerateArray().Select(o => Entry.Parse(o, types)).ToList(), null);
            }
            catch (Exception e) when (e is TargetInvocationException or TypeLoadException or JsonException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException)
            {
                return new([], $"the responses {name} records cannot be read ({e.GetType().Name}: {e.Message})");
            }
        }
    }

    private sealed record Entry(string Id, string At, string Flavor, HandlerRef Handler, string? Failure, string? FailureAt, IReadOnlyList<Inferred> Responses, IReadOnlyList<Helper> Helpers, IReadOnlyList<HelperWrite> Writes, string Content)
    {
        public static Entry Parse(JsonElement o, Type[] types)
        {
            Type? TypeAt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? types[e.GetInt32()] : null;
            string? Text(JsonElement e, string name) => e.GetProperty(name).GetString();
            var handler = o.GetProperty("handler");
            return new Entry(
                Text(o, "id")!,
                Text(o, "at")!,
                Text(o, "flavor")!,
                new HandlerRef(
                    Text(handler, "kind")!,
                    TypeAt(handler.GetProperty("type")),
                    Text(handler, "name"),
                    handler.GetProperty("parameters").EnumerateArray().Select(p => (Text(p, "name")!, TypeAt(p.GetProperty("type")))).ToList(),
                    TypeAt(handler.GetProperty("returns"))),
                Text(o, "failure"),
                Text(o, "failureAt"),
                o.GetProperty("responses").EnumerateArray().Select(r => new Inferred(
                    Text(r, "kind")!,
                    r.TryGetProperty("status", out var status) ? status.GetInt32() : null,
                    TypeAt(r.GetProperty("result")),
                    TypeAt(r.GetProperty("body")),
                    Text(r, "media"))).ToList(),
                o.GetProperty("helpers").EnumerateArray().Select(h => new Helper(Text(h, "name")!, h.GetProperty("parameters").EnumerateArray().Select(TypeAt).ToArray())).ToList(),
                o.GetProperty("writes").EnumerateArray().Select(w => new HelperWrite(w.GetProperty("status").ValueKind == JsonValueKind.Number ? w.GetProperty("status").GetInt32() : null, Text(w, "helper")!)).ToList(),
                o.GetProperty("flavor").GetRawText() + o.GetProperty("failure").GetRawText() + o.GetProperty("responses").GetRawText());
        }
    }

    /// <summary>
    /// The handler the source declares the operation on. A method is found by its declaring type and name; a lambda or local
    /// function compiles to a method of the type it is written in or of a closure type nested in it. Both by their parameters'
    /// names and types and their return type.
    /// </summary>
    private sealed record HandlerRef(string Kind, Type? Type, string? Name, IReadOnlyList<(string Name, Type? Type)> Parameters, Type? Returns)
    {
        public bool Matches(System.Reflection.MethodInfo handler)
        {
            if (Kind == "method" ? handler.DeclaringType != Type || handler.Name != Name : Type is null || !NestedIn(handler.DeclaringType, Type))
            {
                return false;
            }

            var parameters = handler.GetParameters();
            return parameters.Length == Parameters.Count
                && parameters.Zip(Parameters).All(p => p.First.Name == p.Second.Name && (p.Second.Type is null || p.First.ParameterType == p.Second.Type))
                && (Returns is null || handler.ReturnType == Returns);
        }

        private static bool NestedIn(Type? type, Type outer)
        {
            for (var t = type; t is not null; t = t.DeclaringType)
            {
                if (t == outer)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed record Inferred(string Kind, int? Status, Type? Result, Type? Body, string? Media);

    private sealed record Helper(string Name, Type?[] Parameters);
}
