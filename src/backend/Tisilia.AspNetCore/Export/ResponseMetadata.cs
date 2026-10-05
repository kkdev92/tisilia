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

    private static List<ApiResponseType> ResponseMetadata(ApiDescription description, DiagnosticBag bag, string path)
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
            // ApiExplorer's status dictionary can hide competing metadata. Retain all declarations so SV28 can reject conflicts.
            responses.RemoveAll(r => r.StatusCode == status);
            responses.AddRange(explicitResponses.Where(r => r.StatusCode == status));
        }
        return responses.DistinctBy(r => (r.StatusCode, Type: IsFileMarker(r.Type) ? "binary" : r.Type?.FullName,
            Media: string.Join("|", r.ApiResponseFormats.Select(f => Generator.Validation.HttpRules.ParseMediaType(f.MediaType)?.Essence ?? f.MediaType.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))).ToList();

        static ApiResponseType Create(int status, Type? type, IEnumerable<string> media)
        {
            var response = new ApiResponseType { StatusCode = status, Type = type ?? typeof(void) };
            foreach (var value in media.Distinct(StringComparer.OrdinalIgnoreCase)) { response.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = value }); }
            return response;
        }
    }
}
