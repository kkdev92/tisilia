using System.Diagnostics;
using Tisilia.Generator;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Ownership;

namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary><c>generate --config file [--force]</c>: validate → generate → own. No network, no C# build, no module execution.</summary>
    public static Task<int> GenerateAsync(CommandLine cli, bool json) => Task.FromResult(Generate(cli.Require("config"), EvidencePaths(cli), TrustedIssuers(cli), cli.Flag("force"), json, out _));

    /// <summary>One generation run; <paramref name="inputs"/> receives the files the run depended on (config, contract, evidence) so that <c>watch</c> can observe them.</summary>
    internal static int Generate(string configPath, IReadOnlyList<string> evidencePaths, IReadOnlySet<string> trustedIssuers, bool force, bool json, out IReadOnlyList<string> inputs)
    {
        inputs = [Path.GetFullPath(configPath)];
        var bag = new DiagnosticBag { File = configPath };
        var config = Pipeline.LoadConfig(configPath, bag);
        if (config is null)
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        inputs = [Path.GetFullPath(configPath), Path.GetFullPath(config.ContractPath), .. evidencePaths.Select(Path.GetFullPath)];
        var plan = Pipeline.BuildPlan(config, bag, evidencePaths, trustedIssuers);
        if (plan is null)
        {
            Output.Report(bag, json, null);
            return PlanFailureExit(bag);
        }

        var previous = OwnedOutput.ReadManifest(config.OutputRoot, bag);
        if (bag.HasErrors)
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        if (!OwnedOutput.Write(config.OutputRoot, plan.Output, previous, force, bag))
        {
            Output.Report(bag, json, null);
            return ExitCodes.SafetyPolicyViolation;
        }

        Output.Report(bag, json, new { output = config.OutputRoot, files = plan.Output.Files.Count, operations = plan.Index.Operations.Count, semanticHash = plan.Contract.Document.SemanticHash, targetMode = config.Config.Target.ModuleMode.ToString().ToLowerInvariant(), coverage = plan.Output.Manifest.Coverage },
            $"generated {plan.Output.Files.Count} files in {config.OutputRoot} ({config.Config.Target.ModuleMode.ToString().ToLowerInvariant()}): {CoverageSummary(plan.Index.Operations.Count, plan.Output.Manifest.Coverage)}");
        return ExitCodes.Success;
    }

    /// <summary>The first failing stage decides the exit code: a document or config error is 2, a contract the generator cannot serve is 3.</summary>
    private static int PlanFailureExit(DiagnosticBag bag)
        => bag.Items.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code is TisiliaCodes.SchemaViolation or TisiliaCodes.FormatOrVersion or TisiliaCodes.ConfigInvalid) ? ExitCodes.ConfigOrSchema : ExitCodes.SemanticOrUnsupported;

    /// <summary>"3 operations, 2 qualified (1 unqualified: no-evidence)" — the human line of a coverage list.</summary>
    private static string CoverageSummary(int operations, IReadOnlyList<Documents.CoverageEntry> coverage)
    {
        var qualified = coverage.Count(c => c.Status == Documents.CoverageStatus.Qualified);
        var reasons = coverage.Where(c => c.Status != Documents.CoverageStatus.Qualified).SelectMany(c => c.ReasonCodes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return $"{operations} operation{(operations == 1 ? "" : "s")}, {qualified} qualified (codec scope; HTTP unobserved)" + (operations - qualified > 0 ? $" ({operations - qualified} unqualified: {string.Join(", ", reasons)})" : "");
    }

    /// <summary><c>check --config file</c>: diff planned output against owned files; never writes. Exit 4 on differences.</summary>
    public static Task<int> CheckAsync(CommandLine cli, bool json)
    {
        var configPath = cli.Require("config");
        var bag = new DiagnosticBag { File = configPath };
        var config = Pipeline.LoadConfig(configPath, bag);
        var plan = config is null ? null : Pipeline.BuildPlan(config, bag, EvidencePaths(cli), TrustedIssuers(cli));
        if (config is null || plan is null)
        {
            Output.Report(bag, json, null);
            return Task.FromResult(config is null ? ExitCodes.ConfigOrSchema : PlanFailureExit(bag));
        }

        var differences = OwnedOutput.Check(config.OutputRoot, plan.Output);
        Output.Report(bag, json, new { upToDate = differences.Count == 0, differences, coverage = plan.Output.Manifest.Coverage },
            differences.Count == 0
                ? $"up to date: {config.OutputRoot}; {CoverageSummary(plan.Index.Operations.Count, plan.Output.Manifest.Coverage)}"
                : $"{differences.Count} difference(s) in {config.OutputRoot}; run generate:" + Environment.NewLine + string.Join(Environment.NewLine, differences.Take(20).Select(d => "  " + d)) + (differences.Count > 20 ? Environment.NewLine + $"  … {differences.Count - 20} more" : ""),
            differences.Count == 0 ? null : "OUT OF DATE");
        return Task.FromResult(differences.Count == 0 ? ExitCodes.Success : ExitCodes.DiffMismatch);
    }

    public static int Diff(CommandLine cli, bool json)
    {
        var oldPath = cli.Require("old");
        var newPath = cli.Require("new");
        var bag = new DiagnosticBag();
        var result = Generator.Diff.ContractDiff.Compare(oldPath, newPath, bag);
        Output.Report(bag, json, result);
        if (bag.HasErrors)
        {
            return ExitCodes.ConfigOrSchema;
        }

        return result is { Breaking.Count: > 0 } ? ExitCodes.DiffMismatch : ExitCodes.Success;
    }

    /// <summary>
    /// <c>export --project dir --output file --allow-execute-project [--configuration c] [--environment e] [--no-build] [--timeout s]</c>:
    /// builds the application, then runs its export host (<c>dotnet run --no-build --no-launch-profile</c>). The run has a time limit —
    /// an application without AddTisilia would run as a server forever — and the contract is written to a staging file that replaces
    /// the output only on success, so a failed export never leaves a stale contract looking fresh. Everything the build and the
    /// application print goes to stderr: with <c>--format json</c> stdout carries the result only.
    /// </summary>
    public static async Task<int> ExportAsync(CommandLine cli, bool json, bool doctor = false)
    {
        var project = cli.Require("project");
        if (!cli.Flag("allow-execute-project"))
        {
            Console.Error.WriteLine("export/doctor runs application startup and therefore executes user code (migrations, hosted services, network); pass --allow-execute-project explicitly. This flag is not a sandbox; use isolated settings and credentials");
            return ExitCodes.SafetyPolicyViolation;
        }

        var output = Path.GetFullPath(cli.Option("output") ?? Path.Combine(Directory.Exists(project) ? project : Path.GetDirectoryName(Path.GetFullPath(project))!, doctor ? "tisilia.doctor.json" : "tisilia.contract.json"));
        var diagnosticsPath = output + ".diagnostics.json";
        var staging = output + "." + Guid.NewGuid().ToString("N") + ".exporting";
        var configuration = cli.Option("configuration") ?? "Debug";
        var timeoutSeconds = cli.WholeNumber("timeout", 120);
        if (timeoutSeconds <= 0)
        {
            throw new UsageException("--timeout must be at least 1 second");
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (!cli.Flag("no-build"))
        {
            Console.Error.WriteLine($"tisilia export: dotnet build {project}");
            var build = await RunForwardingAsync(["build", project, "--configuration", configuration, "-nologo"], null, null, forwardOutput: !doctor);
            if (build != 0)
            {
                if (doctor) { return WriteIncompleteDoctor(output, staging, json, "project build failed; application startup was not analyzed"); }
                Console.Error.WriteLine($"dotnet build failed with exit code {build}");
                return ExitCodes.ExternalProcessFailed;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        DeleteIfPresent(staging);
        var environment = new Dictionary<string, string>
        {
            ["TISILIA_EXPORT_OUTPUT"] = staging,
            ["TISILIA_EXPORT_DIAGNOSTICS"] = diagnosticsPath,
            ["ASPNETCORE_ENVIRONMENT"] = cli.Option("environment") ?? "Development",
            // no listening port is needed (Tisilia.AspNetCore replaces the server in export mode); an ephemeral loopback port otherwise
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
        };
        if (doctor)
        {
            environment["TISILIA_EXPORT_OUTPUT"] = "";
            environment["TISILIA_DOCTOR_OUTPUT"] = staging;
        }
        else { environment["TISILIA_DOCTOR_OUTPUT"] = ""; }
        // like conformance, without the launch profile: its applicationUrl would replace ASPNETCORE_URLS (a running development server
        // holds that port) and its variables would make the export depend on launchSettings.json rather than on this command line
        if (!doctor) { Console.Error.WriteLine($"tisilia export: dotnet run --project {project} (export host, {timeout.TotalSeconds:0} s at most) → {output}"); }
        var exit = await RunForwardingAsync(["run", "--project", project, "--configuration", configuration, "--no-build", "--no-launch-profile", "--"], environment, timeout, forwardOutput: !doctor);
        if (doctor && File.Exists(staging) && exit is 0 or 3 or 6)
        {
            var report = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(staging))!.AsObject();
            if (report["format"]?.GetValue<string>() != "tisilia.doctor-report" || report["version"]?.GetValue<string>() != "1.0")
            {
                DeleteIfPresent(staging);
                Console.Error.WriteLine("doctor host produced an incompatible report; use matching Tisilia packages");
                return ExitCodes.ExternalProcessFailed;
            }
            File.Move(staging, output, overwrite: true);
            if (json) { Console.WriteLine(report.ToJsonString(TisiliaJson.IndentedOptions)); }
            else
            {
                Console.WriteLine($"doctor: {report["overall"]}; selected {report["selectedCount"]}, analyzed {report["analyzedCount"]}, unanalyzed {report["unanalyzedCount"]}; HTTP unobserved");
                Console.WriteLine($"scope: {report["executionScope"]}; DateTime: {report["dateTimeScope"]}");
                foreach (var cause in report["causes"]!.AsArray())
                {
                    var ids = string.Join(", ", cause!["operationIds"]!.AsArray().Select(id => id!.GetValue<string>()));
                    Console.WriteLine($"cause {cause["reasonCode"]}: {cause["message"]}; operations: {ids}; Fix: {cause["fix"]}");
                }
                foreach (var operation in report["operations"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    Console.WriteLine($"{operation["operationId"]}: {operation["readiness"]} {operation["route"]}");
                    foreach (var diagnostic in operation["diagnostics"]!.AsArray()) { Console.WriteLine($"  {diagnostic!["code"]}: {diagnostic["message"]} Fix: {diagnostic["fix"]}"); }
                }
            }
            return exit.Value;
        }
        if (doctor)
        {
            return WriteIncompleteDoctor(output, staging, json, exit is null ? "application startup exceeded its deadline" : "application did not produce a doctor report; startup or metadata failed");
        }
        if (exit is null)
        {
            DeleteIfPresent(staging);
            Console.Error.WriteLine($"the application did not finish the export within {timeout.TotalSeconds:0} s and was stopped. Does it call AddTisilia (Tisilia.AspNetCore)? "
                + "Export mode writes the contract once the application has started and then stops it; slow startup work counts as well (raise the limit with --timeout <seconds>).");
            return ExitCodes.ExternalProcessFailed;
        }

        if (exit != 0 || !File.Exists(staging))
        {
            DeleteIfPresent(staging);
            Console.Error.WriteLine(exit == 0
                ? "the application exited without writing the contract: does it call AddTisilia (Tisilia.AspNetCore)?"
                : $"export host exited with {exit}; see {diagnosticsPath}");
            return exit == 3 ? ExitCodes.SemanticOrUnsupported : ExitCodes.ExternalProcessFailed;
        }

        File.Move(staging, output, overwrite: true);
        // the warnings of a successful export (SV44 artifact stability, the application's unsupported shapes…) are in the diagnostics
        // file the export host wrote; reported like every other command's, so that they are seen and not only filed
        var bag = new DiagnosticBag { File = output };
        bag.AddRange(ReadExportDiagnostics(diagnosticsPath));
        Output.Report(bag, json, new { output, diagnostics = diagnosticsPath },
            summary: $"wrote {output}" + (bag.Items.Count > 0 ? $" ({bag.Items.Count} diagnostic(s) above, also in {diagnosticsPath})" : ""));
        return ExitCodes.Success;
    }

    private static int WriteIncompleteDoctor(string output, string staging, bool json, string reason)
    {
        var report = new
        {
            format = "tisilia.doctor-report",
            version = "1.0",
            analysisComplete = false,
            overall = "analysis-incomplete",
            selectedCount = 0,
            analyzedCount = 0,
            unanalyzedCount = 0,
            selectionKnown = false,
            operations = Array.Empty<object>(),
            causes = new[] { new { reasonCode = "startup-unavailable", message = reason, fix = "inspect startup privately using isolated settings; application exception text is withheld", operationIds = Array.Empty<string>(), paths = Array.Empty<string>() } },
            executionScope = "application-startup-and-metadata; no handler probing; allow flag is not a sandbox",
            exitCode = ExitCodes.ExternalProcessFailed,
        };
        var text = System.Text.Json.JsonSerializer.Serialize(report, TisiliaJson.IndentedOptions) + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(staging, text);
        File.Move(staging, output, overwrite: true);
        if (json) { Console.Write(text); }
        else { Console.WriteLine("doctor: analysis-incomplete; selected set unknown; " + reason + "; HTTP unobserved; allow flag is not a sandbox"); }
        return ExitCodes.ExternalProcessFailed;
    }

    private static IEnumerable<Diagnostic> ReadExportDiagnostics(string path)
    {
        if (!File.Exists(path) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonArray items)
        {
            yield break;
        }

        foreach (var item in items.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            yield return new Diagnostic
            {
                Code = item["code"]?.GetValue<string>() ?? "",
                Severity = Enum.TryParse<DiagnosticSeverity>(item["severity"]?.GetValue<string>(), ignoreCase: true, out var severity) ? severity : DiagnosticSeverity.Warning,
                Rule = item["rule"]?.GetValue<string>(),
                Message = item["message"]?.GetValue<string>() ?? "",
                Path = item["path"]?.GetValue<string>() ?? "",
                RelatedIds = (item["relatedIds"] as System.Text.Json.Nodes.JsonArray)?.Select(id => id?.GetValue<string>() ?? "").ToList() ?? [],
                Fix = item["fix"]?.GetValue<string>(),
            };
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Runs <c>dotnet</c> with its output and errors forwarded line by line to stderr. Returns the exit code, or null when
    /// <paramref name="timeout"/> elapsed — the whole process tree is stopped then.
    /// </summary>
    private static async Task<int?> RunForwardingAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, TimeSpan? timeout, bool forwardOutput = true)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // reused MSBuild nodes would inherit the redirected handles and keep them open until their idle timeout (BuildParameters.EnableNodeReuse)
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[name] = value;
        }

        using var process = new Process { StartInfo = psi };
        var stderr = Console.Error;
        var gate = new object();
        void Forward(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null && forwardOutput)
            {
                lock (gate)
                {
                    stderr.WriteLine(e.Data);
                }
            }
        }

        process.OutputDataReceived += Forward;
        process.ErrorDataReceived += Forward;
        if (!process.Start())
        {
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancellation = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return null;
        }

        // the asynchronous readers deliver the last lines after the exit; this wait returns once they have
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary><c>--evidence a.json,b.json</c>: conformance evidence records to check for qualified coverage (SV45/SV46).</summary>
    internal static IReadOnlyList<string> EvidencePaths(CommandLine cli)
        => (cli.Option("evidence") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToList();

    /// <summary><c>--trusted-issuer id1,id2</c>: issuers whose evidence may qualify coverage; nothing is trusted implicitly.</summary>
    internal static IReadOnlySet<string> TrustedIssuers(CommandLine cli)
        => (cli.Option("trusted-issuer") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}
