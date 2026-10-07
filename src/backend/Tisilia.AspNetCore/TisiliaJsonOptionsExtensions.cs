using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tisilia.Generator.Validation;

namespace Tisilia.AspNetCore;

internal sealed record TisiliaJsonOptionsMetadata(Type ValueType, JsonSerializerOptions Options, int StatusCode, string ContentType);

public static class TisiliaJsonOptionsExtensions
{
    /// <summary>
    /// Declares and enforces the options of a non-null <see cref="JsonHttpResult{TValue}"/> response.
    /// Pass this same options instance to TypedResults.Json. The options are frozen; request JSON options stay unchanged.
    /// A result with another type, options instance, status, media type or a null value is rejected before it is written.
    /// </summary>
    public static RouteHandlerBuilder WithTisiliaJsonOptions<T>(this RouteHandlerBuilder builder, JsonSerializerOptions options, int statusCode = 200, string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        var media = HttpRules.ParseMediaType(contentType);
        if (media is null || !HttpRules.JsonMediaEssences.Contains(media.Essence) || media.Charset is not (null or "utf-8") || statusCode is < 200 or > 599 || HttpRules.IsBodylessStatus(statusCode))
        {
            throw new ArgumentException("a JSON response requires an explicit body-bearing status and UTF-8 JSON media type");
        }
        options.MakeReadOnly(populateMissingResolver: true);
        builder.WithMetadata(new TisiliaJsonOptionsMetadata(typeof(T), options, statusCode, contentType));
        builder.Produces<T>(statusCode, contentType);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is not JsonHttpResult<T> json || !ReferenceEquals(json.JsonSerializerOptions, options)
                || json.Value is null || (json.StatusCode ?? 200) != statusCode || (json.ContentType ?? "application/json") != contentType)
            {
                throw new InvalidOperationException("the JSON result does not match WithTisiliaJsonOptions");
            }
            return result;
        });
        return builder;
    }

    /// <summary>Declares one controller operation's JsonResult settings. Pass the same frozen options to new JsonResult(value, options).</summary>
    public static ControllerActionEndpointConventionBuilder WithTisiliaJsonOptions<T>(this ControllerActionEndpointConventionBuilder builder,
        string operationId, JsonSerializerOptions options, int statusCode = 200, string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(options); ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var media = HttpRules.ParseMediaType(contentType);
        if (media is null || !HttpRules.JsonMediaEssences.Contains(media.Essence) || media.Charset is not (null or "utf-8") || statusCode is < 200 or > 599 || HttpRules.IsBodylessStatus(statusCode))
        {
            throw new ArgumentException("a JSON response requires an explicit body-bearing status and UTF-8 JSON media type");
        }
        options.MakeReadOnly(populateMissingResolver: true);
        builder.Add(endpoint =>
        {
            if (endpoint.Metadata.OfType<TisiliaOperationAttribute>().LastOrDefault()?.OperationId == operationId)
            {
                endpoint.Metadata.Add(new TisiliaJsonOptionsMetadata(typeof(T), options, statusCode, contentType));
            }
        });
        return builder;
    }
}

internal sealed class TisiliaJsonResultFilter : IAsyncAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => int.MaxValue;
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var declaration = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<TisiliaJsonOptionsMetadata>();
        // Automatic validation/authentication results retain their own pipeline and JSON profile.
        if (declaration is not null && context.Result is JsonResult json)
        {
            var actualMedia = HttpRules.ParseMediaType(json.ContentType ?? context.HttpContext.Response.ContentType ?? "application/json; charset=utf-8");
            var expectedMedia = HttpRules.ParseMediaType(declaration.ContentType)!;
            if (!ReferenceEquals(json.SerializerSettings, declaration.Options) || json.Value?.GetType() != declaration.ValueType
                || (json.StatusCode ?? context.HttpContext.Response.StatusCode) != declaration.StatusCode
                || actualMedia?.Essence != expectedMedia.Essence || actualMedia.Charset is not (null or "utf-8"))
            {
                throw new InvalidOperationException("the JSON result does not match WithTisiliaJsonOptions");
            }
        }
        return next();
    }
}
