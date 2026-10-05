using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// The server of export mode: the contract is read from the built endpoints and the host stops right after, so nothing
/// listens — no port a running development server already holds, no HTTPS certificate a build machine lacks.
/// </summary>
internal sealed class ExportServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
