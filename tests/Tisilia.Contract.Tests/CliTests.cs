using System.Diagnostics;
using Tisilia.Tool;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>The CLI as a process: exit codes for the commands a script runs, including its mistakes.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        // the tool's assembly is copied next to the tests (project reference)
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _dir };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Tisilia.Tool.dll"));
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void Help_succeeds_and_an_unknown_command_fails()
    {
        Assert.Equal(ExitCodes.Success, Run().ExitCode);
        Assert.Equal(ExitCodes.Success, Run("help").ExitCode);
        var mistyped = Run("genrate", "--config", "tisilia.json");
        Assert.Equal(ExitCodes.ConfigOrSchema, mistyped.ExitCode);
        Assert.Contains("unknown command 'genrate'", mistyped.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closure_of_an_unknown_operation_fails()
    {
        var contract = Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures", "minimal-api.contract.json");
        Assert.Equal(ExitCodes.Success, Run("closure", "--contract", contract, "--operation", "users.get").ExitCode);
        var unknown = Run("closure", "--contract", contract, "--operation", "users.get,nope.nope");
        Assert.Equal(ExitCodes.SemanticOrUnsupported, unknown.ExitCode);
        Assert.Contains("unknown operation id(s): nope.nope", unknown.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_refuses_a_directory_as_the_config()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "web"));
        var result = Run("init", "--config", "web", "--contract", "api/tisilia.contract.json", "--output", "web/api", "--api-id", "probe");
        Assert.Equal(ExitCodes.ConfigOrSchema, result.ExitCode);
        Assert.Contains("--config names a directory", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.ConfigOrSchema, Run("init", "--config", "other/", "--contract", "api/tisilia.contract.json", "--output", "web/api", "--api-id", "probe").ExitCode);
        Assert.False(File.Exists(Path.Combine(_dir, "other")));
    }

    /// <summary>The fixture contract next to the modules it was exported against, and a config generating into web/generated.</summary>
    private void InitFixtureProject()
    {
        var fixtures = Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures");
        Directory.CreateDirectory(Path.Combine(_dir, "api"));
        File.Copy(Path.Combine(fixtures, "minimal-api.contract.json"), Path.Combine(_dir, "api", "tisilia.contract.json"));
        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "modules"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_dir, "api", "modules", Path.GetRelativePath(Path.Combine(fixtures, "modules"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Assert.Equal(ExitCodes.Success, Run("init", "--contract", "api/tisilia.contract.json", "--output", "web/generated").ExitCode);
    }

    [Fact]
    public async Task Watch_reports_a_run_that_cannot_write_instead_of_crashing()
    {
        InitFixtureProject();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _dir };
        foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "Tisilia.Tool.dll"), "watch", "--config", "tisilia.json", "--runs", "2", "--debounce-ms", "100" })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        string? line;
        while ((line = await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(1))) is not null && !line.Contains("watching", StringComparison.Ordinal))
        {
        }

        Assert.NotNull(line);
        // the second run cannot create its output (a file where its folder belongs), the way a file held by an editor fails a run
        Directory.Delete(Path.Combine(_dir, "web"), recursive: true);
        File.WriteAllText(Path.Combine(_dir, "web"), "not a folder");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "api", "tisilia.contract.json"), DateTime.UtcNow);
        var rest = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        await stdout;
        Assert.Contains("tisilia watch: run 2 failed: ", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", rest, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.ConfigOrSchema, process.ExitCode);
    }

    [Fact]
    public void A_file_the_command_cannot_write_is_a_diagnostic_not_a_crash()
    {
        InitFixtureProject();
        // a file where the output's parent folder belongs: creating the output fails on every platform
        File.WriteAllText(Path.Combine(_dir, "web"), "not a folder");
        var result = Run("generate", "--config", "tisilia.json", "--format", "json");
        Assert.Equal(ExitCodes.ConfigOrSchema, result.ExitCode);
        var report = System.Text.Json.Nodes.JsonNode.Parse(result.Stdout)!;
        Assert.False(report["ok"]!.GetValue<bool>());
        Assert.Equal(Generator.Diagnostics.TisiliaCodes.OutputPath, report["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_names_a_manifest_it_cannot_read_with_or_without_force()
    {
        InitFixtureProject();
        Assert.Equal(ExitCodes.Success, Run("generate", "--config", "tisilia.json").ExitCode);
        // what an earlier Tisilia leaves in the output directory: a manifest of another format version
        var manifestPath = Path.Combine(_dir, "web", "generated", "tisilia.generation-manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["version"] = "0.3";
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        // without the manifest, generated files cannot be told from the user's: --force does not get past it
        foreach (var force in new[] { false, true })
        {
            var result = Run(["generate", "--config", "tisilia.json", "--format", "json", .. force ? new[] { "--force" } : []]);
            Assert.Equal(ExitCodes.ConfigOrSchema, result.ExitCode);
            var diagnostics = System.Text.Json.Nodes.JsonNode.Parse(result.Stdout)!["diagnostics"]!.AsArray();
            Assert.All(diagnostics, d => Assert.EndsWith(Path.Combine("web", "generated", "tisilia.generation-manifest.json"), d!["file"]!.GetValue<string>(), StringComparison.Ordinal));
            Assert.Contains(diagnostics, d => d!["code"]!.GetValue<string>() == Generator.Diagnostics.TisiliaCodes.GenerationManifest);
        }
    }

    [Fact]
    public void Check_without_the_evidence_of_a_qualified_output_says_to_pass_it_rather_than_generate()
    {
        InitFixtureProject();
        // evidence that qualifies users.get, as conformance writes it, and an output generated with it
        var bag = new Generator.Diagnostics.DiagnosticBag();
        var loaded = Generator.Validation.ContractLoader.Load(File.ReadAllText(Path.Combine(_dir, "api", "tisilia.contract.json")), bag)!;
        var index = Generator.Validation.SemanticValidator.Validate(loaded, bag)!;
        var (node, evidence) = ConformanceTests.SyntheticEvidence(index, Generator.Closure.ContractClosure.Compute(index, ["users.get"]));
        File.WriteAllText(Path.Combine(_dir, "api", "tisilia.evidence.json"), node.ToJsonString(TisiliaJson.Options));
        string[] withEvidence = ["--evidence", "api/tisilia.evidence.json", "--trusted-issuer", "ci"];
        Assert.Equal(ExitCodes.Success, Run(["generate", "--config", "tisilia.json", .. withEvidence]).ExitCode);
        Assert.Equal(ExitCodes.Success, Run(["check", "--config", "tisilia.json", .. withEvidence]).ExitCode);

        // without it, check plans users.get unqualified and the manifest differs; generating, the usual advice, would drop the
        // qualification, so check names the evidence instead
        var text = Run("check", "--config", "tisilia.json");
        Assert.Equal(ExitCodes.DiffMismatch, text.ExitCode);
        Assert.Contains("warning TIS1604 [SV46]: the manifest records 1 operation as qualified by conformance evidence, but check got no --evidence, so it plans it unqualified", text.Stdout, StringComparison.Ordinal);
        Assert.Contains("; check with that evidence before you generate:", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("run generate", text.Stdout, StringComparison.Ordinal);
        var report = System.Text.Json.Nodes.JsonNode.Parse(Run("check", "--config", "tisilia.json", "--format", "json").Stdout)!;
        var warning = Assert.Single(report["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == Generator.Diagnostics.TisiliaCodes.CoverageNotQualified)!;
        Assert.EndsWith(Path.Combine("web", "generated", "tisilia.generation-manifest.json"), warning["file"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(evidence.Id, Assert.Single(warning["relatedIds"]!.AsArray())!.GetValue<string>());

        // an output generated without evidence has no qualification to lose: a difference there is still "run generate"
        Assert.Equal(ExitCodes.Success, Run("generate", "--config", "tisilia.json").ExitCode);
        File.AppendAllText(Path.Combine(_dir, "web", "generated", "index.ts"), "// edited\n");
        var plain = Run("check", "--config", "tisilia.json");
        Assert.Equal(ExitCodes.DiffMismatch, plain.ExitCode);
        Assert.Contains("run generate:", plain.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("TIS1604", plain.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("watch", "--config", "tisilia.json", "--runs", "two")]
    [InlineData("watch", "--config", "tisilia.json", "--debounce-ms", "-5")]
    [InlineData("conformance", "--config", "tisilia.json", "--project", ".", "--allow-execute-adapters", "--cases", "1e3")]
    [InlineData("export", "--project", ".", "--allow-execute-project", "--timeout", "0")]
    [InlineData("validate", "--contract", "c.json", "--format", "jsn")]
    public void A_mistyped_option_value_is_a_usage_error_not_a_default(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(ExitCodes.ConfigOrSchema, result.ExitCode);
        Assert.Contains("usage: ", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
    }
}
