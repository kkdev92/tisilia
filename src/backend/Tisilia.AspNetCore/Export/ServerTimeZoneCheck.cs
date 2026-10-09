using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tisilia.Generator.Building;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// Outside Development, warns once when the process's own time zone has other UTC offsets than the declared
/// <see cref="Bindings.DateTimeBindingCollection.ServerTimeZone"/>: clients check DateTime dictionary keys against the declared zone, so keys
/// this server reads as one could reach it as different keys. The comparison runs after startup and never delays it.
/// </summary>
internal sealed class ServerTimeZoneCheck(IOptions<TisiliaOptions> options, IHostEnvironment environment, ILogger<ServerTimeZoneCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.DateTimes.ServerTimeZone is not { } declared || environment.IsDevelopment()
            || TisiliaExportHostedService.IsExportMode || Conformance.TisiliaRunnerHostedService.IsRunnerMode)
        {
            return Task.CompletedTask;
        }

        var local = TimeZoneInfo.Local;
        _ = Task.Run(() =>
        {
            try
            {
                if (!ServerTimeZoneTable.Of(declared).HasSameOffsets(ServerTimeZoneTable.Of(local)))
                {
                    logger.LogWarning(
                        "Tisilia: this process runs in time zone '{Local}', whose UTC offsets differ from the declared server time zone '{Declared}' (TisiliaOptions.DateTimes.ServerTimeZone). Clients check DateTime dictionary keys against the declared zone, so keys this server reads as one key can arrive as different keys; declare the zone this server runs in.",
                        local.Id, declared.Id);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                logger.LogWarning(e, "Tisilia: the declared server time zone '{Declared}' could not be compared with this process's time zone '{Local}'", declared.Id, local.Id);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
