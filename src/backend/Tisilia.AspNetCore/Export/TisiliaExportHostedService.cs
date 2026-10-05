using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// Export mode: when <c>TISILIA_EXPORT_OUTPUT</c> is set, the application starts normally
/// (routing, DI and options are real), the contract is exported once endpoints are built, and the host stops.
/// Anything the application does on startup (migrations, hosted services) happens; the CLI therefore requires
/// <c>--allow-execute-project</c>. Tisilia does not claim to sandbox this.
/// </summary>
public sealed class TisiliaExportHostedService(IHostApplicationLifetime lifetime, TisiliaContractExporter exporter, ILogger<TisiliaExportHostedService> logger) : IHostedService
{
    public const string OutputVariable = "TISILIA_EXPORT_OUTPUT";
    public const string DiagnosticsVariable = "TISILIA_EXPORT_DIAGNOSTICS";
    public const string DoctorVariable = "TISILIA_DOCTOR_OUTPUT";

    /// <summary>The process was started by <c>tisilia export</c>.</summary>
    public static bool IsExportMode => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable)) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DoctorVariable));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var doctor = Environment.GetEnvironmentVariable(DoctorVariable);
        if (string.IsNullOrWhiteSpace(doctor)) { doctor = null; }
        var output = doctor ?? Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output))
        {
            return Task.CompletedTask;
        }

        lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                if (doctor is not null)
                {
                    var report = exporter.Diagnose();
                    File.WriteAllText(doctor, System.Text.Json.JsonSerializer.Serialize(report, TisiliaJson.IndentedOptions) + "\n");
                    Environment.ExitCode = report.ExitCode;
                    return;
                }
                var result = exporter.Export();
                var diagnosticsPath = Environment.GetEnvironmentVariable(DiagnosticsVariable) ?? output + ".diagnostics.json";
                File.WriteAllText(diagnosticsPath, System.Text.Json.JsonSerializer.Serialize(result.Diagnostics.Items.Select(d => new
                {
                    code = d.Code,
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    rule = d.Rule,
                    message = d.Message,
                    path = d.Path,
                    relatedIds = d.RelatedIds,
                    fix = d.Fix,
                }), TisiliaJson.IndentedOptions) + "\n");
                if (result.Diagnostics.HasErrors || result.Text is null)
                {
                    foreach (var d in result.Diagnostics.Items)
                    {
                        logger.LogError("tisilia export: {Diagnostic}", d.ToString());
                    }

                    Environment.ExitCode = 3;
                }
                else
                {
                    File.WriteAllText(output, result.Text);
                    logger.LogInformation("tisilia export: wrote {Path} ({Operations} operations)", output, result.OperationCount);
                    Environment.ExitCode = 0;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "tisilia export failed");
                Environment.ExitCode = 6;
            }
            finally
            {
                lifetime.StopApplication();
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
