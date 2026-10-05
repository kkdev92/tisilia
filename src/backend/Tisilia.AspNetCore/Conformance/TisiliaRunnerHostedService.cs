using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Conformance;

namespace Tisilia.AspNetCore.Conformance;

/// <summary>
/// Runner mode: when <c>TISILIA_RUNNER</c> is set, the application starts normally, exports its
/// contract once, reports its environment to <c>TISILIA_RUNNER_ENV</c> and then answers runner-message records
/// read from stdin on stdout, one per line, until stdin closes. Logs go to stderr only. Started by
/// <c>tisilia conformance --allow-execute-adapters</c>; user code runs, nothing is sandboxed.
/// </summary>
public sealed class TisiliaRunnerHostedService(IHostApplicationLifetime lifetime, TisiliaContractExporter exporter, IOptions<TisiliaOptions> options, IHostEnvironment environment, ILogger<TisiliaRunnerHostedService> logger) : IHostedService
{
    public static bool IsRunnerMode => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RunnerProtocol.ModeVariable));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsRunnerMode)
        {
            return Task.CompletedTask;
        }

        lifetime.ApplicationStarted.Register(() => _ = Task.Run(ServeAsync));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ServeAsync()
    {
        try
        {
            var result = exporter.Export();
            if (result.Diagnostics.HasErrors || result.Index is null || result.Adapters is null)
            {
                foreach (var d in result.Diagnostics.Items)
                {
                    logger.LogError("tisilia runner: {Diagnostic}", d.ToString());
                }

                Environment.ExitCode = 3;
                return;
            }

            var core = new DotnetRunnerCore(result.Index, result.Adapters, options.Value.Codecs.Paired, options.Value.Behaviors.All);
            var sessionId = Environment.GetEnvironmentVariable(RunnerProtocol.SessionVariable) ?? "";
            var maxRecordBytes = long.TryParse(Environment.GetEnvironmentVariable(RunnerProtocol.MaxRecordBytesVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : RunnerProtocol.DefaultMaxRecordBytes;
            if (Environment.GetEnvironmentVariable(RunnerProtocol.EnvironmentFileVariable) is { Length: > 0 } envPath)
            {
                WriteEnvironment(envPath, result.Index.Document.SemanticHash);
            }

            logger.LogInformation("tisilia runner: serving session {Session} with {Adapters} adapters", sessionId, result.Adapters.CodecIds.Count());
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            while (await stdin.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var response = HandleLine(line, sessionId, maxRecordBytes, core);
                await stdout.WriteLineAsync(RunnerProtocol.Serialize(response)).ConfigureAwait(false);
            }

            Environment.ExitCode = 0;
        }
        catch (Exception e)
        {
            logger.LogError(e, "tisilia runner failed");
            Environment.ExitCode = 6;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    public static RunnerMessage HandleLine(string line, string sessionId, long maxRecordBytes, DotnetRunnerCore core)
    {
        var message = RunnerProtocol.Parse(line, maxRecordBytes, out var error);
        if (message is null)
        {
            var code = error is not null && error.Contains("per-record limit", StringComparison.Ordinal) ? RunnerFailureCode.Limit : RunnerFailureCode.InvalidInput;
            return RunnerProtocol.Failure(sessionId, RequestIdOf(line, maxRecordBytes), code, "protocol.malformed", "");
        }

        if (message is not RunnerRequest request)
        {
            return RunnerProtocol.Failure(sessionId, message.RequestId, RunnerFailureCode.InvalidInput, "protocol.not-a-request", "");
        }

        if (!string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
        {
            return RunnerProtocol.Failure(sessionId, request.RequestId, RunnerFailureCode.InvalidInput, "protocol.session-mismatch", "");
        }

        try
        {
            return RunnerProtocol.Success(sessionId, request.RequestId, core.Handle(request));
        }
        catch (RunnerFailureException f)
        {
            return RunnerProtocol.Failure(sessionId, request.RequestId, f.Code, f.SafeMessageId, f.Path);
        }
        catch (Exception e)
        {
            return RunnerProtocol.Failure(sessionId, request.RequestId, RunnerFailureCode.Internal, "internal." + e.GetType().Name, "");
        }
    }

    /// <summary>Best-effort request id of a record that failed strict parsing, so that the failure still correlates (SV54).</summary>
    private static string RequestIdOf(string line, long maxRecordBytes)
    {
        if (Encoding.UTF8.GetByteCount(line) > maxRecordBytes)
        {
            return "unknown";
        }

        try
        {
            var id = JsonNode.Parse(line) is JsonObject obj && obj["requestId"] is JsonNode node && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
            return id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z][A-Za-z0-9_.:@/\-]{0,159}$") ? id : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private void WriteEnvironment(string path, string semanticHash)
    {
        var runnerAssembly = typeof(TisiliaRunnerHostedService).Assembly;
        var entry = Assembly.GetEntryAssembly();
        var artifacts = new JsonArray();
        if (entry?.Location is { Length: > 0 } location && File.Exists(location))
        {
            var relative = Path.GetRelativePath(environment.ContentRootPath, location).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                relative = Path.GetFileName(location);
            }

            artifacts.Add(new JsonObject { ["target"] = "dotnet", ["path"] = relative, ["digest"] = TisiliaHash.Sha256OfFile(location) });
        }

        var report = new JsonObject
        {
            ["ready"] = true,
            ["runner"] = "dotnet",
            ["semanticHash"] = semanticHash,
            ["runnerDigest"] = TisiliaHash.Sha256OfFile(runnerAssembly.Location),
            ["matrix"] = new JsonObject
            {
                ["dotnet"] = RuntimeVersion(),
                ["aspnetcore"] = VersionOf(typeof(WebApplication).Assembly),
                ["stj"] = VersionOf(typeof(JsonSerializer).Assembly),
                ["os"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            },
            ["context"] = new JsonArray(
                new JsonObject { ["name"] = "dotnet.culture", ["value"] = CultureInfo.CurrentCulture.Name.Length == 0 ? "invariant" : CultureInfo.CurrentCulture.Name },
                new JsonObject { ["name"] = "dotnet.timeZone", ["value"] = TimeZoneInfo.Local.Id }),
            ["applicationArtifacts"] = artifacts,
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, report.ToJsonString() + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    private static string RuntimeVersion()
    {
        var description = RuntimeInformation.FrameworkDescription; // ".NET 10.0.12"
        var digits = new string(description.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(c => char.IsAsciiDigit(c) || c is '.' or '-' or '+').ToArray());
        return IsSemver(digits) ? digits : Environment.Version.ToString(3);
    }

    private static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            var core = plus >= 0 ? informational[..plus] : informational;
            if (IsSemver(core))
            {
                return core;
            }
        }

        return (assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    }

    private static bool IsSemver(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$");
}
