using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tisilia.Documents;
using Tisilia.Generator;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Ownership;
using Tisilia.Tool;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>From a tisilia.config to the output plan: what generation accepts, reports and writes, and what init writes.</summary>
public sealed class PipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tisilia-pipeline-" + Guid.NewGuid().ToString("N"));

    public PipelineTests()
    {
        // an exported contract next to the modules it was exported against (the artifact paths are relative to the contract)
        var fixtures = Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures");
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        File.Copy(Path.Combine(fixtures, "minimal-api.contract.json"), ContractPath);
        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "modules"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_root, "api", "modules", Path.GetRelativePath(Path.Combine(fixtures, "modules"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ContractPath => Path.Combine(_root, "api", "tisilia.contract.json");

    private string ConfigPath => Path.Combine(_root, "tisilia.json");

    [Fact]
    public void EV09_Qualified_only_keeps_its_explicit_filter_and_reports_excluded_operations()
    {
        var configPath = WriteConfig(c => c["coveragePolicy"] = "qualified-only");
        var bag = new DiagnosticBag();
        var loaded = Tisilia.Generator.Validation.ContractLoader.Load(File.ReadAllText(ContractPath), bag)!;
        var index = Tisilia.Generator.Validation.SemanticValidator.Validate(loaded, bag)!;
        var closure = Tisilia.Generator.Closure.ContractClosure.Compute(index, ["users.get"]);
        // Synthetic evidence exercises trust/filter validation only; it is never published as an observed run.
        var (evidence, _) = ConformanceTests.SyntheticEvidence(index, closure);
        var evidencePath = Path.Combine(_root, "filter-test.evidence.json");
        File.WriteAllText(evidencePath, evidence.ToJsonString(TisiliaJson.Options));
        var config = Pipeline.LoadConfig(configPath, bag)!;
        var plan = Pipeline.BuildPlan(config, bag, [evidencePath], new HashSet<string> { "ci" });
        Assert.NotNull(plan);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
        Assert.Contains("usersGet", ClientOf(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("usersPut", ClientOf(plan), StringComparison.Ordinal);
        Assert.Contains(bag.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("users.put", StringComparison.Ordinal) && d.Message.Contains("not published", StringComparison.Ordinal));
    }

    private string WriteConfig(Action<JsonObject>? change = null)
    {
        var config = JsonSerializer.SerializeToNode(new ConfigDocument
        {
            Format = TisiliaJson.Formats.Config,
            Version = TisiliaJson.DraftVersion,
            ApiId = SampleContracts.ApiId,
            Contract = "api/tisilia.contract.json",
            Output = "web/generated",
            Target = new ConfigTarget { TypescriptMinimumMajor = 6, EcmaScript = "ES2022", ModuleMode = ModuleMode.NodeNext },
            Selection = "explicit",
            CoveragePolicy = CoveragePolicy.Development,
            Modules = [],
            PortableProjects = [],
            Limits = Limits.Default,
            Nuxt = new NuxtConfig { Enabled = false, Hydration = "browser-safe-only", SharedCache = false },
        }, TisiliaJson.Options)!.AsObject();
        change?.Invoke(config);
        File.WriteAllText(ConfigPath, config.ToJsonString(TisiliaJson.IndentedOptions));
        return ConfigPath;
    }

    private static (Pipeline.Plan? Plan, DiagnosticBag Bag) Plan(string configPath)
    {
        var bag = new DiagnosticBag { File = configPath };
        var config = Pipeline.LoadConfig(configPath, bag);
        Assert.True(config is not null, string.Join("\n", bag.Items));
        return (Pipeline.BuildPlan(config!, bag), bag);
    }

    private static string ClientOf(Pipeline.Plan? plan) => plan!.Output.Files.Single(f => f.Path == "client.ts").Content;

    [Fact]
    public void Union_variants_carry_their_discriminator_as_a_literal_type()
    {
        // a variant only ever holds its own tag: `kind: "circle"` instead of `kind: string` lets a union narrow by it
        // (`if (shape.kind === "circle")`)
        var (plan, _) = Plan(WriteConfig());
        var models = plan!.Output.Files.Single(f => f.Path == "models/index.ts").Content;
        Assert.Contains("readonly kind: \"circle\";", models, StringComparison.Ordinal);
        Assert.Contains("readonly kind: \"rect\";", models, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly kind: string;", models, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_becomes_jsdoc_on_models_members_arguments_and_client_methods()
    {
        // Documentation is display text outside the semantic hash — the generated client shows it in the editor
        var contract = JsonNode.Parse(File.ReadAllText(ContractPath))!.AsObject();
        contract["documentation"] = new JsonArray(
            Doc("users.get", "Get a user by id", "Returns the user or a problem details document."),
            Doc("users.get.id", "The user's id.", "Must be a */ GUID."),
            Doc("users.get.ok", "The user.", ""),
            Doc("users.get.not-found", "No such user.", ""),
            Doc("sample-api.Tisilia.Samples.MinimalApi.UserResponse.response", "A user as the API returns it.",
                "Kept for a year.\n\n**Members**\n\n- `nickname` — Shown in lists; null when not set.\n- `revision` — 1 ≤ value ≤ 9."),
            Doc("sample-api.Tisilia.Samples.MinimalApi.Visibility.number", "Who sees it.", "**Members**\n\n- `Private` — Only the owner."));
        File.WriteAllText(ContractPath, contract.ToJsonString(TisiliaJson.IndentedOptions));

        var (plan, bag) = Plan(WriteConfig());
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
        var models = plan!.Output.Files.Single(f => f.Path == "models/index.ts").Content;
        Assert.Contains("/**\n * A user as the API returns it.\n *\n * Kept for a year.\n *\n * sample-api.Tisilia.Samples.MinimalApi.UserResponse.response (Tisilia.Samples.MinimalApi.UserResponse)\n */\nexport interface UserResponse {", models, StringComparison.Ordinal);
        Assert.Contains("  /** Shown in lists; null when not set. */\n  readonly nickname", models, StringComparison.Ordinal);
        Assert.Contains("  /** 1 ≤ value ≤ 9. */\n  readonly revision", models, StringComparison.Ordinal);
        Assert.Contains("  /** Only the owner. */\n  Private:", models, StringComparison.Ordinal);
        Assert.DoesNotContain("**Members**", models, StringComparison.Ordinal);

        var operations = string.Concat(plan.Output.Files.Where(f => f.Path.StartsWith("operations/", StringComparison.Ordinal)).Select(f => f.Content));
        Assert.Contains("/**\n * Get a user by id\n *\n * Returns the user or a problem details document.\n *\n * GET /users/{id:guid} (users.get)\n */\nexport interface UsersGetArgs {", operations, StringComparison.Ordinal);
        // "*/" in documentation cannot end the comment early
        Assert.Contains("  /**\n   * The user's id.\n   *\n   * Must be a *\\/ GUID.\n   */\n  readonly id: Guid;", operations, StringComparison.Ordinal);

        var client = ClientOf(plan);
        Assert.Contains(" * - `users.get.ok` (200): The user.\n * - `users.get.not-found` (404): No such user.\n", client.Replace("\n  ", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        // an undocumented operation keeps a bare method signature
        Assert.Contains("\n  usersPut(args", client, StringComparison.Ordinal);

        static JsonObject Doc(string id, string summary, string description) => new() { ["targetId"] = id, ["summary"] = summary, ["description"] = description };
    }

    [Fact]
    public void Deprecated_targets_are_deprecated_in_the_client()
    {
        // [Obsolete] reaches the contract as documentation that starts with "**Deprecated.**" (OpenAPI's deprecated); the client marks
        // each such declaration @deprecated, which editors strike through where it is used
        var contract = JsonNode.Parse(File.ReadAllText(ContractPath))!.AsObject();
        contract["documentation"] = new JsonArray(
            Doc("users.put", "Replace a user", "**Deprecated.** Use PATCH."),
            Doc("users.get.id", "The user's id.", "**Deprecated.** Pass the handle instead."),
            Doc("sample-api.Tisilia.Samples.MinimalApi.UserResponse.response", "",
                "**Members**\n\n- `nickname` — **Deprecated.** Use the display name.\n- `revision` — Not deprecated."),
            Doc("sample-api.Tisilia.Samples.MinimalApi.Visibility.number", "Who sees it.", "**Deprecated.** Use roles."));
        File.WriteAllText(ContractPath, contract.ToJsonString(TisiliaJson.IndentedOptions));

        var (plan, bag) = Plan(WriteConfig());
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
        var client = ClientOf(plan!);
        Assert.Contains("   * PUT /users/{id:guid} (users.put)\n   *\n   * @deprecated\n   */\n  usersPut(args", client, StringComparison.Ordinal);
        Assert.DoesNotContain("@deprecated\n   */\n  usersGet(args", client, StringComparison.Ordinal);

        var operations = string.Concat(plan!.Output.Files.Where(f => f.Path.StartsWith("operations/", StringComparison.Ordinal)).Select(f => f.Content));
        Assert.Contains(" * PUT /users/{id:guid} (users.put)\n *\n * @deprecated\n */\nexport interface UsersPutArgs {", operations, StringComparison.Ordinal);
        Assert.Contains("   * **Deprecated.** Pass the handle instead.\n   *\n   * @deprecated\n   */\n  readonly id: Guid;", operations, StringComparison.Ordinal);

        var models = plan.Output.Files.Single(f => f.Path == "models/index.ts").Content;
        Assert.Contains("  /**\n   * **Deprecated.** Use the display name.\n   *\n   * @deprecated\n   */\n  readonly nickname", models, StringComparison.Ordinal);
        Assert.Contains("  /** Not deprecated. */\n  readonly revision", models, StringComparison.Ordinal);
        // a numeric enum is a type and a const of known members: both are deprecated
        Assert.Contains(" *\n * @deprecated\n */\nexport type Visibility = number;", models, StringComparison.Ordinal);
        Assert.Contains("/**\n * Known members of Visibility; the value domain is the full int32 range unless allowUndefinedInteger is false.\n *\n * @deprecated\n */\nexport const Visibility = {", models, StringComparison.Ordinal);
        Assert.Equal(6, Regex.Count(client + operations + models, "@deprecated"));

        static JsonObject Doc(string id, string summary, string description) => new() { ["targetId"] = id, ["summary"] = summary, ["description"] = description };
    }

    [Fact]
    public void A_config_generates_the_contract_it_names()
    {
        var (plan, bag) = Plan(WriteConfig());
        Assert.NotNull(plan);
        Assert.Empty(bag.Items);
        var client = ClientOf(plan);
        // a call's overrides replace the client's options, and its limits merge with the client's entry by entry
        Assert.Contains("limits: { ...options.limits, ...overrides?.limits } });", client, StringComparison.Ordinal);
        Assert.Contains("execute(operations.usersGetOperation, args, merged(overrides))", client, StringComparison.Ordinal);
        Assert.DoesNotContain("configuredLimits", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_limits_are_the_generated_clients_defaults()
    {
        var (plan, bag) = Plan(WriteConfig(c =>
        {
            c["limits"]!["maxBodyBytes"] = 1_048_576;
            c["limits"]!["timeoutMs"] = 5000;
        }));
        Assert.NotNull(plan);
        Assert.Empty(bag.Items);
        var client = ClientOf(plan);
        Assert.Contains("const configuredLimits = { maxBodyBytes: 1048576, maxDepth: 64, maxTokens: 1000000, maxNumberCharacters: 4096, timeoutMs: 5000, maxDiagnosticBytes: 262144 };", client, StringComparison.Ordinal);
        Assert.Contains("limits: { ...configuredLimits, ...options.limits, ...overrides?.limits } });", client, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_for_another_api_is_rejected()
    {
        var configPath = WriteConfig(c => c["apiId"] = "other-api");
        var (plan, bag) = Plan(configPath);
        Assert.Null(plan);
        var error = Assert.Single(bag.Items);
        Assert.Equal((TisiliaCodes.ConfigInvalid, DiagnosticSeverity.Error, "/apiId", configPath), (error.Code, error.Severity, error.Path, error.File));
        Assert.Contains("does not match the contract's apiId 'sample-api'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_generation_does_not_read_are_reported()
    {
        var (plan, bag) = Plan(WriteConfig(c =>
        {
            c["modules"] = new JsonArray("api/modules/demo.money");
            c["portableProjects"] = new JsonArray("api/tisilia.portable.json");
            c["nuxt"]!["enabled"] = true;
        }));
        Assert.NotNull(plan);
        Assert.Equal(["/modules", "/portableProjects", "/nuxt/enabled"], bag.Items.Select(d => d.Path));
        Assert.All(bag.Items, d => Assert.Equal((TisiliaCodes.ConfigInvalid, DiagnosticSeverity.Warning, ConfigPath), (d.Code, d.Severity, d.File)));
    }

    [Fact]
    public void Contract_diagnostics_name_the_contract_file_and_a_missing_module_its_place()
    {
        Directory.Delete(Path.Combine(_root, "api", "modules", "demo.portable"), recursive: true);
        var (plan, bag) = Plan(WriteConfig());
        Assert.Null(plan);
        var error = Assert.Single(bag.Items);
        Assert.Equal((TisiliaCodes.ModuleArtifact, "/modules", ContractPath), (error.Code, error.Path, error.File));
        Assert.Contains("is not installed at " + Path.Combine(_root, "api", "modules", "demo.portable", "demo.portable.portable.js"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_with_another_digest_is_reported_once()
    {
        File.AppendAllText(Path.Combine(_root, "api", "modules", "demo.money", "money.js"), "// changed\n");
        var (plan, bag) = Plan(WriteConfig());
        Assert.Null(plan);
        var error = Assert.Single(bag.Items);
        Assert.Contains("does not match the contract", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line endings", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ line endings (Git for Windows checks text out with CRLF)

    [Fact]
    public void A_crlf_checkout_of_the_contract_and_the_output_is_up_to_date_and_regenerates_without_force()
    {
        var configPath = WriteConfig();
        var output = Path.Combine(_root, "web", "generated");
        var (plan, bag) = Plan(configPath);
        Assert.True(OwnedOutput.Write(output, plan!.Output, previous: null, force: false, bag), string.Join("\n", bag.Items));
        LineEndingsTests.CheckOutWithCrLf(ContractPath);
        foreach (var file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
        {
            LineEndingsTests.CheckOutWithCrLf(file);
        }

        (plan, bag) = Plan(configPath);
        Assert.Empty(OwnedOutput.Check(output, plan!.Output));
        var previous = OwnedOutput.ReadManifest(output, bag);
        Assert.NotNull(previous);
        // a file the previous generation owned and this one does not write is still removed from a CRLF checkout
        var stale = Path.Combine(output, "stale.ts");
        File.WriteAllText(stale, "export {};\r\n");
        previous = previous! with { Files = [.. previous.Files, new Documents.GeneratedFile { Path = "stale.ts", Digest = Generator.Canonical.TisiliaHash.Sha256OfBytes("export {};\n"u8) }] };
        var client = Path.Combine(output, "client.ts");
        var checkedOut = File.ReadAllBytes(client);
        Assert.True(OwnedOutput.Write(output, plan.Output, previous, force: false, bag), string.Join("\n", bag.Items));
        Assert.Empty(bag.Items);
        Assert.False(File.Exists(stale));
        // the same text is left as git wrote it: rewritten with LF, git (core.autocrlf=true) would list it as modified
        Assert.Equal(checkedOut, File.ReadAllBytes(client));

        // an edit is still an edit, whatever its line endings
        File.AppendAllText(client, "// edited\r\n");
        Assert.Equal(["client.ts"], OwnedOutput.Check(output, plan.Output));
        Assert.False(OwnedOutput.Write(output, plan.Output, OwnedOutput.ReadManifest(output, bag), force: false, bag));
        Assert.Contains(bag.Items, d => d.Message.Contains("'client.ts' was edited after generation", StringComparison.Ordinal));
    }

    [Fact]
    public void A_crlf_checkout_of_a_module_artifact_is_reported_as_line_endings()
    {
        LineEndingsTests.CheckOutWithCrLf(Path.Combine(_root, "api", "modules", "demo.money", "money.js"));
        var (plan, bag) = Plan(WriteConfig());
        Assert.Null(plan);
        var error = Assert.Single(bag.Items);
        Assert.Contains("does not match the contract", error.Message, StringComparison.Ordinal);
        Assert.Contains("it differs only in line endings: git checked it out with CRLF", error.Message, StringComparison.Ordinal);
        Assert.Contains("in " + Path.Combine(_root, "api", "modules", "demo.money"), error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ init

    private int Init(params string[] arguments) => Commands.Init(new CommandLine(["init", "--config", ConfigPath, .. arguments]), json: true);

    [Fact]
    public void Init_writes_a_config_that_generates()
    {
        Assert.Equal(ExitCodes.Success, Init("--contract", ContractPath, "--output", Path.Combine(_root, "web", "src", "generated")));
        var config = JsonSerializer.Deserialize<ConfigDocument>(File.ReadAllText(ConfigPath), TisiliaJson.Options)!;
        Assert.Equal((SampleContracts.ApiId, "api/tisilia.contract.json", "web/src/generated", ModuleMode.Bundler), (config.ApiId, config.Contract, config.Output, config.Target.ModuleMode));
        Assert.Equal(Limits.Default, config.Limits);
        var (plan, bag) = Plan(ConfigPath);
        Assert.NotNull(plan);
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void Init_replaces_a_config_only_when_forced()
    {
        File.WriteAllText(ConfigPath, "{}");
        Assert.Equal(ExitCodes.SafetyPolicyViolation, Init("--contract", ContractPath, "--output", Path.Combine(_root, "web")));
        Assert.Equal("{}", File.ReadAllText(ConfigPath));
        Assert.Equal(ExitCodes.Success, Init("--contract", ContractPath, "--output", Path.Combine(_root, "web"), "--module-mode", "nodenext", "--force"));
        Assert.Equal(ModuleMode.NodeNext, JsonSerializer.Deserialize<ConfigDocument>(File.ReadAllText(ConfigPath), TisiliaJson.Options)!.Target.ModuleMode);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("another-api-id")]
    [InlineData("unknown-api-id")]
    [InlineData("module-mode")]
    public void Init_refuses_a_config_generate_would_reject(string problem)
    {
        string[] arguments = problem switch
        {
            // config paths may not leave the config's directory (safePath has no "..")
            "outside" => ["--contract", ContractPath, "--output", Path.Combine(_root, "..", "elsewhere")],
            "another-api-id" => ["--contract", ContractPath, "--output", Path.Combine(_root, "web"), "--api-id", "other-api"],
            // nothing exported yet and no --api-id: the apiId is unknown
            "unknown-api-id" => ["--contract", Path.Combine(_root, "api", "none.json"), "--output", Path.Combine(_root, "web")],
            _ => ["--contract", ContractPath, "--output", Path.Combine(_root, "web"), "--module-mode", "esm"],
        };
        Assert.Equal(ExitCodes.ConfigOrSchema, Init(arguments));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Init_takes_the_api_id_of_a_contract_not_exported_yet_from_the_command_line()
    {
        Assert.Equal(ExitCodes.Success, Init("--contract", Path.Combine(_root, "api", "later.json"), "--output", Path.Combine(_root, "web"), "--api-id", "later-api"));
        Assert.Equal("later-api", JsonSerializer.Deserialize<ConfigDocument>(File.ReadAllText(ConfigPath), TisiliaJson.Options)!.ApiId);
    }
}
