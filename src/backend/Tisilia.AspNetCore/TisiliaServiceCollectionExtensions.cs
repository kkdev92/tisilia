using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tisilia.AspNetCore.Export;

namespace Tisilia.AspNetCore;

public static class TisiliaServiceCollectionExtensions
{
    /// <summary>
    /// Registers Tisilia export services. Does not change JSON options, authentication or CORS.
    /// The export hosted service only acts when the process was started by <c>tisilia export --allow-execute-project</c>
    /// (environment variable <c>TISILIA_EXPORT_OUTPUT</c>).
    /// </summary>
    public static IServiceCollection AddTisilia(this IServiceCollection services, Action<TisiliaOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<TisiliaOptions>().Configure(configure).Validate(o => !string.IsNullOrWhiteSpace(o.ApiId), "TisiliaOptions.ApiId is required");
        services.AddEndpointsApiExplorer();
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add(new TisiliaJsonResultFilter()));
        services.TryAddSingleton<TisiliaContractExporter>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, SemanticHashHeaderStartupFilter>());
        services.AddHostedService<TisiliaExportHostedService>();
        if (TisiliaExportHostedService.IsExportMode)
        {
            // export mode serves no request: nothing listens, so a running development server or a missing certificate cannot fail it
            services.AddSingleton<Microsoft.AspNetCore.Hosting.Server.IServer, ExportServer>();
        }

        services.AddHostedService<ServerTimeZoneCheck>();
        services.AddHostedService<Conformance.TisiliaRunnerHostedService>();
        if (Conformance.TisiliaRunnerHostedService.IsRunnerMode)
        {
            // stdout carries protocol records only; every console log line goes to stderr
            services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
        }

        return services;
    }
}

public static class TisiliaEndpointExtensions
{
    /// <summary>Registers a minimal API endpoint as an explicit Tisilia operation with a stable id.</summary>
    public static TBuilder WithTisiliaOperation<TBuilder>(this TBuilder builder, string operationId, params string[] tags) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        builder.WithMetadata(new TisiliaOperationAttribute(operationId) { Tags = tags });
        return builder;
    }

    /// <summary>
    /// Declares that the endpoint's server-sent events resume after the event whose id a reconnecting client sends in
    /// <c>Last-Event-ID</c> (<see cref="TisiliaEventResumeAttribute"/>; on a controller action, put the attribute on the action).
    /// </summary>
    public static TBuilder WithTisiliaEventResume<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new TisiliaEventResumeAttribute());
        return builder;
    }

    /// <summary>
    /// Maps <c>GET {ContractRoute}</c> returning the exported contract. Outside Development both
    /// <see cref="TisiliaOptions.AllowProduction"/> and <see cref="TisiliaOptions.AuthorizationPolicy"/> are required.
    /// </summary>
    public static IEndpointConventionBuilder MapTisiliaContract(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<TisiliaOptions>>().Value;
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        GuardPublication(options, environment, "MapTisiliaContract");
        var builder = endpoints.MapGet(options.ContractRoute, (TisiliaContractExporter exporter, HttpContext context) =>
        {
            var result = exporter.Export();
            if (result.Diagnostics.HasErrors)
            {
                return Results.Problem(title: "Tisilia contract export failed", detail: string.Join("\n", result.Diagnostics.Items.Select(d => d.ToString())), statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Text(result.Text!, "application/json; charset=utf-8");
        }).ExcludeFromDescription();
        if (options.AuthorizationPolicy is not null)
        {
            builder.RequireAuthorization(options.AuthorizationPolicy);
        }

        return builder;
    }

    /// <summary>The publication guard shared by the contract and Explorer routes.</summary>
    public static void GuardPublication(TisiliaOptions options, IHostEnvironment environment, string what)
    {
        if (!environment.IsDevelopment())
        {
            if (!options.AllowProduction || string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
            {
                throw new InvalidOperationException($"{what}: outside the Development environment both TisiliaOptions.AllowProduction=true and TisiliaOptions.AuthorizationPolicy are required. IP restrictions alone are not accepted.");
            }
        }
    }
}
