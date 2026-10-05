using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary>
    /// <c>conformance --config file --project dir [--client dir] --allow-execute-adapters [--output evidence.json]</c>:
    /// builds and starts the application in runner mode and the generated client in the Node runner, executes the
    /// standard suite and writes a <c>tisilia.conformance-evidence</c> record.
    /// Exit 5 when the verdict is failed, 6 when a runner could not be started, 7 without the explicit permission.
    /// </summary>
    public static async Task<int> ConformanceAsync(CommandLine cli, bool json)
    {
        var configPath = cli.Require("config");
        var project = Path.GetFullPath(cli.Require("project"));
        var seed = cli.WholeNumber("seed", 1UL);
        var cases = Math.Max(2, cli.WholeNumber("cases", 12));
        if (!cli.Flag("allow-execute-adapters"))
        {
            Console.Error.WriteLine("conformance starts the application in runner mode and executes the generated client's codec modules; pass --allow-execute-adapters explicitly");
            return ExitCodes.SafetyPolicyViolation;
        }

        var bag = new DiagnosticBag { File = configPath };
        var config = Pipeline.LoadConfig(configPath, bag);
        if (config is null)
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        var loaded = ContractLoader.LoadFile(config.ContractPath, bag);
        var index = loaded is null ? null : SemanticValidator.Validate(loaded, bag);
        if (index is null)
        {
            Output.Report(bag, json, null);
            return loaded is null ? ExitCodes.ConfigOrSchema : ExitCodes.SemanticOrUnsupported;
        }

        var operationIds = (cli.Option("operation") ?? string.Join(",", index.Operations.Keys)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = operationIds.Where(id => !index.Operations.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV03", "/operations", "unknown operation id(s): " + string.Join(", ", unknown));
            Output.Report(bag, json, null);
            return ExitCodes.SemanticOrUnsupported;
        }

        var closure = ContractClosure.Compute(index, operationIds);
        var contractDir = Path.GetDirectoryName(config.ContractPath)!;
        var moduleArtifacts = closure.DeclaredModuleArtifacts();
        foreach (var artifact in moduleArtifacts)
        {
            var full = Path.GetFullPath(Path.Combine(contractDir, artifact.Artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(full))
            {
                bag.Error(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"module '{artifact.ModuleId}': artifact '{artifact.Artifact.Path}' is not installed at {full}", [artifact.ModuleId]);
                continue;
            }

            var bytes = File.ReadAllBytes(full);
            var actual = TisiliaHash.Sha256OfBytes(bytes);
            if (actual != artifact.Artifact.Digest)
            {
                bag.Error(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"module '{artifact.ModuleId}': artifact '{artifact.Artifact.Path}' digest {actual} does not match the contract ({artifact.Artifact.Digest}); " + (LineEndings.ArtifactHint(bytes, artifact.Artifact.Digest, full) ?? "re-run export after rebuilding"), [artifact.ModuleId]);
            }
        }

        var clientDir = Path.GetFullPath(cli.Option("client") ?? config.OutputRoot);
        var clientIndex = Path.Combine(clientDir, "index.js");
        var clientRegistry = Path.Combine(clientDir, "registry.js");
        if (!File.Exists(clientIndex) || !File.Exists(clientRegistry))
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV39", "/output", $"compiled generated client not found under {clientDir} (index.js/registry.js); run generate and tsc first");
        }

        if (bag.HasErrors)
        {
            Output.Report(bag, json, null);
            return ExitCodes.SemanticOrUnsupported;
        }

        var log = Console.Error;
        var timeout = TimeSpan.FromMilliseconds(config.Config.Limits.TimeoutMs);
        var suiteOptions = new SuiteOptions { Seed = seed, CasesPerCodec = cases, KeyCasesPerCodec = Math.Max(1, cases / 2) };

        // 1. build the application (dotnet run would mix MSBuild output into the protocol stream)
        string targetPath;
        if (!cli.Flag("no-build"))
        {
            var build = await RunProcessAsync("dotnet", ["build", project, "-nologo", "-v", "q"], project, log).ConfigureAwait(false);
            if (build != 0)
            {
                log.WriteLine($"dotnet build failed with exit code {build}");
                return ExitCodes.ExternalProcessFailed;
            }
        }

        var (targetExit, targetOutput) = await CaptureProcessAsync("dotnet", ["msbuild", project, "-getProperty:TargetPath", "-nologo"], project).ConfigureAwait(false);
        targetPath = targetOutput.Trim();
        if (targetExit != 0 || !File.Exists(targetPath))
        {
            log.WriteLine($"could not resolve the application's TargetPath (exit {targetExit}): {targetOutput}");
            return ExitCodes.ExternalProcessFailed;
        }

        // 2. start both runners with a fresh session
        var sessionId = RunnerProtocol.NewSessionId();
        var tempDir = Directory.CreateTempSubdirectory("tisilia-conformance-");
        var dotnetEnvPath = Path.Combine(tempDir.FullName, "dotnet.env.json");
        var nodeEnvPath = Path.Combine(tempDir.FullName, "node.env.json");
        var maxRecordBytes = config.Config.Limits.MaxBodyBytes;
        var dotnetLaunch = new RunnerLaunch("dotnet", "dotnet", [targetPath], project, new Dictionary<string, string>
        {
            [RunnerProtocol.ModeVariable] = "1",
            [RunnerProtocol.EnvironmentFileVariable] = dotnetEnvPath,
            ["ASPNETCORE_ENVIRONMENT"] = cli.Option("environment") ?? "Development",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        });
        // one line: node -e discards code that starts with a newline on Windows; bare specifiers resolve from the client directory (cwd)
        var bootstrap = string.Join(" ",
            "import { pathToFileURL } from \"node:url\";",
            "const runner = await import(\"@kkdev92/tisilia-runtime/conformance/runner\");",
            "const client = await import(pathToFileURL(process.env.TISILIA_RUNNER_CLIENT).href);",
            "const registry = await import(pathToFileURL(process.env.TISILIA_RUNNER_REGISTRY).href);",
            "await runner.runRunner({ registry: client.createRegistry(), moduleExport: registry.moduleExport, contractPath: process.env.TISILIA_RUNNER_CONTRACT, environmentPath: process.env.TISILIA_RUNNER_ENV });");
        var nodeLaunch = new RunnerLaunch("node", "node", ["--input-type=module", "-e", bootstrap], clientDir, new Dictionary<string, string>
        {
            ["TISILIA_RUNNER_CLIENT"] = clientIndex,
            ["TISILIA_RUNNER_REGISTRY"] = clientRegistry,
            [RunnerProtocol.ContractVariable] = config.ContractPath,
            [RunnerProtocol.EnvironmentFileVariable] = nodeEnvPath,
        });

        log.WriteLine($"tisilia conformance: session {sessionId}, {closure.OperationIds.Count()} operation(s), seed {seed}");
        RunnerClient? dotnet = null;
        RunnerClient? node = null;
        try
        {
            try
            {
                dotnet = RunnerClient.Start(dotnetLaunch, sessionId, maxRecordBytes, log);
                node = RunnerClient.Start(nodeLaunch, sessionId, maxRecordBytes, log);
            }
            catch (Exception e) when (e is RunnerProtocolException or System.ComponentModel.Win32Exception)
            {
                log.WriteLine("failed to start a runner: " + e.Message);
                return ExitCodes.ExternalProcessFailed;
            }

            JsonObject dotnetEnv;
            JsonObject nodeEnv;
            try
            {
                dotnetEnv = await dotnet.WaitForEnvironmentAsync(dotnetEnvPath, TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(false);
                nodeEnv = await node.WaitForEnvironmentAsync(nodeEnvPath, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            }
            catch (RunnerProtocolException e)
            {
                log.WriteLine(e.Message);
                return ExitCodes.ExternalProcessFailed;
            }

            if (dotnetEnv["semanticHash"]?.GetValue<string>() is { } exportedHash && exportedHash != index.Document.SemanticHash)
            {
                bag.Error(TisiliaCodes.HashMismatch, "SV43", "/semanticHash", $"the application exports semanticHash {exportedHash} but the contract file has {index.Document.SemanticHash}; re-run export so that the suite tests the contract the client was generated from");
                Output.Report(bag, json, null);
                return ExitCodes.SemanticOrUnsupported;
            }

            var matrix = BuildMatrix(dotnetEnv, nodeEnv, bag);
            if (matrix is null)
            {
                Output.Report(bag, json, null);
                return ExitCodes.ConfigOrSchema;
            }

            var context = ReadNameValues(dotnetEnv["context"]).Concat(ReadNameValues(nodeEnv["context"])).ToList();
            // datetime-local-wire is certified under the .NET runner's fixed zone: the suite computes those
            // expectations for the zone the runner reported, and the evidence context records it as dotnet.timeZone
            var dotnetZone = context.FirstOrDefault(e => e.Name == "dotnet.timeZone")?.Value;
            if (dotnetZone is null)
            {
                bag.Error(TisiliaCodes.ConfigInvalid, "SV45", "", "the .NET runner did not report its time zone (dotnet.timeZone); datetime-local-wire cannot be certified without the fixed context");
                Output.Report(bag, json, null);
                return ExitCodes.ConfigOrSchema;
            }

            try
            {
                SuiteBuilder.ResolveTimeZone(dotnetZone);
            }
            catch (ArgumentException e)
            {
                bag.Error(TisiliaCodes.ConfigInvalid, "SV45", "", e.Message);
                Output.Report(bag, json, null);
                return ExitCodes.ConfigOrSchema;
            }

            suiteOptions = suiteOptions with { TimeZoneId = dotnetZone };
            log.WriteLine($"tisilia conformance: datetime-local-wire expectations computed for the runner's zone '{dotnetZone}'");
            var applicationArtifacts = ReadArtifacts(dotnetEnv["applicationArtifacts"]).Concat(ClientArtifacts(clientDir, config.ConfigDirectory)).ToList();
            var runnerDigest = TisiliaHash.Sha256OfBytes(Jcs.Serialize(new JsonObject
            {
                ["dotnet"] = dotnetEnv["runnerDigest"]?.GetValue<string>() ?? "",
                ["node"] = nodeEnv["runnerDigest"]?.GetValue<string>() ?? "",
            }));

            // 3. the suite: candidates from the closure, filtered by the registered domain rules of module codecs
            var candidate = SuiteBuilder.Build(index, closure, suiteOptions);
            var nodeRunner = node;
            var suite = await SuiteBuilder.FilterAsync(index, candidate, async (codecId, profileId, domain) =>
            {
                var message = await nodeRunner.SendAsync(RunnerAction.ValidateDomain, codecId, profileId, [], [domain], timeout, CancellationToken.None).ConfigureAwait(false);
                return message is RunnerSuccess { Outputs: [JsonBooleanValue { Value: true }] };
            }).ConfigureAwait(false);
            log.WriteLine($"tisilia conformance: {suite.Cases.Count} cases ({candidate.Cases.Count - suite.Cases.Count} candidates outside the module domain rules), {suite.RequiredTests.Count} required categories, {suite.NotApplicable.Count} round trip(s) not claimed (G1)");
            foreach (var entry in suite.NotApplicable)
            {
                log.WriteLine($"  not applicable: {entry.CodecId} / {entry.EquivalenceId} ({entry.Reason})");
            }
            if (cli.Option("suite-output") is { } suiteOutput)
            {
                File.WriteAllText(suiteOutput, JsonSerializer.Serialize(suite, TisiliaJson.IndentedOptions) + "\n");
            }

            // 4. execute
            var executor = new ConformanceExecutor(dotnet, node, index, timeout, log);
            var results = await executor.ExecuteAllAsync(suite, CancellationToken.None).ConfigureAwait(false);

            // 5. evidence
            var built = EvidenceBuilder.Build(index, closure, suite, results, new EvidenceInputs
            {
                IssuerId = cli.Option("issuer") ?? "local",
                Matrix = matrix,
                Context = context,
                ModuleArtifacts = moduleArtifacts,
                ApplicationArtifacts = applicationArtifacts,
                Limits = config.Config.Limits,
                RunnerDigest = runnerDigest,
                IssuedAt = DateTimeOffset.UtcNow,
            }, bag);
            if (built is null)
            {
                Output.Report(bag, json, null);
                return ExitCodes.ConformanceFailed;
            }

            var output = Path.GetFullPath(cli.Option("output") ?? Path.Combine(config.ConfigDirectory, "tisilia.evidence.json"));
            File.WriteAllText(output, built.Node.ToJsonString(TisiliaJson.IndentedOptions) + "\n");
            if (cli.Option("report") is { } reportPath)
            {
                var report = new
                {
                    evidenceId = built.Evidence.Id,
                    verdict = built.Evidence.Verdict.ToString().ToLowerInvariant(),
                    counts = built.Evidence.Counts,
                    suite = new { suite.Id, suite.Version, Digest = built.Evidence.Suite.Digest, suite.Seed, cases = suite.Cases.Count, notApplicable = suite.NotApplicable },
                    results = results.Select(r => new { r.Id, r.Category, status = r.Status.ToString().ToLowerInvariant(), r.Message }),
                };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, TisiliaJson.IndentedOptions) + "\n");
            }

            var counts = built.Evidence.Counts;
            var summary = new
            {
                evidence = output,
                evidenceId = built.Evidence.Id,
                verdict = built.Evidence.Verdict.ToString().ToLowerInvariant(),
                passed = counts.Passed,
                failed = counts.Failed,
                skipped = counts.Skipped,
                requiredTests = suite.RequiredTests.Count,
                closureDigest = built.Evidence.ClosureDigest,
                failures = results.Where(r => r.Status != CaseStatus.Passed).Take(25).Select(r => r.Id + ": " + r.Message),
            };
            Output.Report(bag, json, summary);
            return built.Evidence.Verdict == Verdict.Passed ? ExitCodes.Success : ExitCodes.ConformanceFailed;
        }
        finally
        {
            if (node is not null)
            {
                await node.DisposeAsync().ConfigureAwait(false);
            }

            if (dotnet is not null)
            {
                await dotnet.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                tempDir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static RuntimeMatrix? BuildMatrix(JsonObject dotnetEnv, JsonObject nodeEnv, DiagnosticBag bag)
    {
        var d = dotnetEnv["matrix"] as JsonObject;
        var n = nodeEnv["matrix"] as JsonObject;
        string? Get(JsonObject? o, string name) => o?[name]?.GetValue<string>();
        var typescript = Get(n, "typescript");
        if (typescript is null)
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV39", "/target", "the Node runner could not resolve the TypeScript version (typescript must be installed next to the generated client); the evidence matrix requires it");
            return null;
        }

        var dotnet = Get(d, "dotnet");
        var aspnetcore = Get(d, "aspnetcore");
        var stj = Get(d, "stj");
        var os = Get(d, "os");
        var arch = Get(d, "architecture");
        if (dotnet is null || aspnetcore is null || stj is null || os is null || arch is null)
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV45", "", "the .NET runner did not report its full runtime matrix");
            return null;
        }

        return new RuntimeMatrix
        {
            Dotnet = dotnet,
            Aspnetcore = aspnetcore,
            Stj = stj,
            Typescript = typescript,
            Node = Get(n, "node"),
            Os = os,
            Architecture = arch,
        };
    }

    private static IEnumerable<NameValue> ReadNameValues(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            yield break;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var name = item["name"]?.GetValue<string>();
            var value = item["value"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name) && value is not null)
            {
                yield return new NameValue { Name = name, Value = value };
            }
        }
    }

    private static IEnumerable<Artifact> ReadArtifacts(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            yield break;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var target = item["target"]?.GetValue<string>();
            var path = item["path"]?.GetValue<string>();
            var digest = item["digest"]?.GetValue<string>();
            if (target is not null && path is not null && digest is not null && Enum.TryParse<ArtifactTarget>(target, ignoreCase: true, out var t))
            {
                yield return new Artifact { Target = t, Path = path, Digest = digest };
            }
        }
    }

    /// <summary>The compiled client files the Node runner executes (application artifacts are recorded, never hashed into the contract).</summary>
    private static IEnumerable<Artifact> ClientArtifacts(string clientDir, string configDir)
    {
        foreach (var file in Directory.EnumerateFiles(clientDir, "*.js", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(configDir, file).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                relative = Path.GetRelativePath(clientDir, file).Replace('\\', '/');
            }

            yield return new Artifact { Target = ArtifactTarget.Node, Path = relative, Digest = TisiliaHash.Sha256OfFile(file) };
        }
    }

    private static async Task<int> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TextWriter log)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        // reused MSBuild nodes would inherit the redirected pipes and keep ReadToEnd waiting until their idle timeout
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (text.Trim().Length > 0)
        {
            log.WriteLine(text.TrimEnd());
        }

        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string Output)> CaptureProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }
}
