using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.AspNetCore.Export;

public sealed partial class TisiliaContractExporter
{
    private static bool IsFileMarker(Type? type) => type == typeof(FileContentResult) || type == typeof(FileStreamResult)
        || type == typeof(Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult) || type == typeof(Microsoft.AspNetCore.Http.HttpResults.FileStreamHttpResult);

    /// <summary>ProblemDetails and the two types ASP.NET Core derives from it, each of which adds only the errors member.</summary>
    private static bool IsFrameworkProblem(Type? type) => type == typeof(ProblemDetails) || type == typeof(Microsoft.AspNetCore.Http.HttpValidationProblemDetails) || type == typeof(ValidationProblemDetails);

    /// <summary>
    /// The responses ApiExplorer describes, one per status code and type: it lists every type declared for a status code side by side
    /// (ApiResponseTypeProvider.ResponseKey and EndpointMetadataApiDescriptionProvider, aspnetcore v11.0.0). The file markers of a
    /// status code are one binary body, whose cases SV28 compares with the others by media type. The framework's problem details
    /// types of one status code are one case, read as the least derived of them. Any other two types of one status code would be
    /// one case twice — the client selects a case by status code and media type — so they are refused here.
    /// </summary>
    private static List<ApiResponseType> ResponseMetadata(ApiDescription description, DiagnosticBag bag, string path, string operationId)
    {
        var responses = description.SupportedResponseTypes.Where(r => !r.IsDefaultResponse).ToList();
        var explicitResponses = new List<ApiResponseType>();
        foreach (var metadata in description.ActionDescriptor.EndpointMetadata)
        {
            if (metadata is IProducesResponseTypeMetadata produces)
            {
                explicitResponses.Add(Create(produces.StatusCode, produces.Type, produces.ContentTypes));
            }
            else if (metadata is ProducesResponseTypeAttribute attribute)
            {
                var media = new MediaTypeCollection();
                ((IApiResponseMetadataProvider)attribute).SetContentTypes(media);
                explicitResponses.Add(Create(attribute.StatusCode, attribute.Type, media));
            }
        }
        var fileStatuses = explicitResponses.Where(r => IsFileMarker(r.Type)).Select(r => r.StatusCode).ToHashSet();
        foreach (var status in fileStatuses)
        {
            // ApiExplorer gives a declaration without media types application/json (CreateDefaultApiResponseFormat), which a file is
            // not, and lets a declaration of a higher scope replace the others of its status code. A status code with a file marker is
            // read from the declarations themselves, each with the media types it names, so SV28 can refuse the ones that conflict.
            responses.RemoveAll(r => r.StatusCode == status);
            responses.AddRange(explicitResponses.Where(r => r.StatusCode == status));
        }
        responses = responses.DistinctBy(r => (r.StatusCode, Type: IsFileMarker(r.Type) ? "binary" : r.Type?.FullName,
            Media: string.Join("|", r.ApiResponseFormats.Select(f => Generator.Validation.HttpRules.ParseMediaType(f.MediaType)?.Essence ?? f.MediaType.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))).ToList();

        foreach (var status in responses.Where(r => !fileStatuses.Contains(r.StatusCode)).GroupBy(r => r.StatusCode).Where(g => g.Select(r => r.Type).Distinct().Count() > 1).ToList())
        {
            var declared = status.Select(r => r.Type).Distinct().ToList();
            if (declared.All(IsFrameworkProblem))
            {
                // A route group's ProducesProblem(400) next to an endpoint's ProducesValidationProblem(), or
                // Results<ValidationProblem, BadRequest<ProblemDetails>>: the server writes either body with the same media type, and
                // what the more derived type adds (errors) is a member the [JsonExtensionData] of ProblemDetails reads. The case is the
                // declared type the others derive from. A type an application derives from them stays refused: its converter or a
                // member it hides can change the JSON the base type describes.
                var common = declared.First(candidate => declared.All(candidate!.IsAssignableFrom));
                var merged = Create(status.Key, common, status.SelectMany(r => r.ApiResponseFormats).Select(f => f.MediaType));
                merged.Description = status.Select(r => r.Description).FirstOrDefault(text => !string.IsNullOrEmpty(text));
                responses[responses.IndexOf(status.First())] = merged;
                responses.RemoveAll(r => r.StatusCode == status.Key && !ReferenceEquals(r, merged));
                continue;
            }

            var types = declared.Select(type => type is null || type == typeof(void) ? "no body" : FriendlyName(type));
            bag.Error(TisiliaCodes.AmbiguousResponseCase, "SV28", path + "/responses",
                $"operation '{operationId}': status {status.Key} is declared with more than one response type ({string.Join(", ", types)}); the client selects a response case by status code and media type, so it could not tell them apart", [operationId],
                "declare one type for the status code: answer the other body with another status code, or declare a type that describes both");
            responses.RemoveAll(r => r.StatusCode == status.Key && !ReferenceEquals(r, status.First()));
        }

        return responses;

        static ApiResponseType Create(int status, Type? type, IEnumerable<string> media)
        {
            var response = new ApiResponseType { StatusCode = status, Type = type ?? typeof(void) };
            foreach (var value in media.Distinct(StringComparer.OrdinalIgnoreCase)) { response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = value }); }
            return response;
        }
    }
}
