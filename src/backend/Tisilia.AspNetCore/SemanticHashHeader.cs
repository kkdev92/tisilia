using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tisilia.AspNetCore.Export;

namespace Tisilia.AspNetCore;

/// <summary>
/// Every response of a Tisilia operation names the semantic
/// hash of the contract it was produced under, so a client generated from another contract reports contract-mismatch instead of decoding
/// a changed wire. The hash comes from the cached export; an export with errors emits no header.
/// </summary>
internal sealed class SemanticHashHeaderStartupFilter(IOptions<TisiliaOptions> options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (options.Value.EmitSemanticHashHeader)
        {
            var header = options.Value.SemanticHashHeader;
            app.Use(async (context, nextMiddleware) =>
            {
                // the endpoint is known once routing ran, which is always before the response starts
                context.Response.OnStarting(() =>
                {
                    if (context.GetEndpoint()?.Metadata.GetMetadata<TisiliaOperationAttribute>() is not null
                        && context.RequestServices.GetRequiredService<TisiliaContractExporter>().Export() is { Diagnostics.HasErrors: false, Root: { } root }
                        && root["semanticHash"]?.GetValue<string>() is { } hash)
                    {
                        context.Response.Headers[header] = hash;
                    }

                    return Task.CompletedTask;
                });
                await nextMiddleware(context);
            });
        }

        next(app);
    };
}
