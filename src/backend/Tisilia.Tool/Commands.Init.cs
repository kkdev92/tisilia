using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary>
    /// <c>init --contract file --output dir [--config tisilia.json] [--api-id id] [--module-mode bundler|nodenext] [--force]</c>:
    /// writes a <c>tisilia.config</c> with the default settings. Paths are stored relative to the config, which must
    /// contain both the contract and the output (safePath has no <c>..</c>). Reads the contract's apiId; executes nothing.
    /// </summary>
    public static int Init(CommandLine cli, bool json)
    {
        var configPath = Path.GetFullPath(cli.Option("config") ?? "tisilia.json");
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var bag = new DiagnosticBag { File = configPath };
        var contractOption = cli.Require("contract");
        var outputOption = cli.Require("output");
        if (Directory.Exists(configPath) || cli.Option("config") is { } named && (named.EndsWith('/') || named.EndsWith('\\')))
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV38", "", "--config names a directory; name the config file in it, e.g. " + Path.Combine(configPath, "tisilia.json"));
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        if (File.Exists(configPath) && !cli.Flag("force"))
        {
            bag.Error(TisiliaCodes.OutputPath, "SV38", "", "the config file exists; nothing was written (re-run with --force to replace it)");
            Output.Report(bag, json, null);
            return ExitCodes.SafetyPolicyViolation;
        }

        var contract = RelativeToConfig("contract", contractOption);
        var output = RelativeToConfig("output", outputOption);
        var apiId = ConfiguredApiId(Path.GetFullPath(contractOption), cli.Option("api-id"));
        var moduleMode = cli.Option("module-mode") switch
        {
            null or "bundler" => ModuleMode.Bundler,
            "nodenext" => ModuleMode.NodeNext,
            var other => Invalid<ModuleMode?>("/target/moduleMode", $"--module-mode '{other}' is not bundler or nodenext"),
        };
        if (bag.HasErrors || contract is null || output is null || apiId is null || moduleMode is null)
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        var config = new ConfigDocument
        {
            Format = TisiliaJson.Formats.Config,
            Version = TisiliaJson.DraftVersion,
            ApiId = apiId,
            Contract = contract,
            Output = output,
            Target = new ConfigTarget { TypescriptMinimumMajor = 6, EcmaScript = "ES2022", ModuleMode = moduleMode.Value },
            Selection = "explicit",
            CoveragePolicy = CoveragePolicy.Development,
            Modules = [],
            PortableProjects = [],
            Limits = Limits.Default,
            Nuxt = new NuxtConfig { Enabled = false, Hydration = "browser-safe-only", SharedCache = false },
        };
        var node = JsonSerializer.SerializeToNode(config, TisiliaJson.IndentedOptions)!.AsObject();
        if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Config, node, bag))
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        try
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(configPath, node.ToJsonString(TisiliaJson.IndentedOptions) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV38", "", "cannot write the config: " + e.Message);
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }
        var relativeConfig = Path.GetRelativePath(Environment.CurrentDirectory, configPath);
        Output.Report(bag, json, new { config = configPath, apiId, contract, output, moduleMode = moduleMode.Value.ToString().ToLowerInvariant() },
            $"wrote {configPath}: apiId '{apiId}', contract '{contract}', output '{output}' ({moduleMode.Value.ToString().ToLowerInvariant()})" + Environment.NewLine +
            $"next: dotnet tisilia generate --config {relativeConfig}");
        return ExitCodes.Success;

        // the config stores paths relative to its own directory and never leaves it (common.schema.json safePath)
        string? RelativeToConfig(string name, string value)
        {
            var relative = Path.GetRelativePath(configDirectory, Path.GetFullPath(value)).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            {
                return Invalid<string>("/" + name, $"--{name} '{value}' is outside {configDirectory}; config paths may not leave the config's directory",
                    "put the config in a directory that contains both the contract and the output (--config <dir>/tisilia.json), e.g. the repository root");
            }

            return relative;
        }

        // the apiId of the contract when it is already exported; a different --api-id is the same mistake generate rejects
        string? ConfiguredApiId(string contractPath, string? option)
        {
            string? exported = null;
            if (File.Exists(contractPath))
            {
                try
                {
                    exported = JsonNode.Parse(File.ReadAllText(contractPath))?["apiId"]?.GetValue<string>();
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
                {
                    return Invalid<string>("/contract", $"'{contractPath}' is not a contract with a string apiId ({e.Message})");
                }
            }

            if (exported is not null && option is not null && option != exported)
            {
                return Invalid<string>("/apiId", $"--api-id '{option}' differs from the contract's apiId '{exported}'", $"omit --api-id or pass --api-id {exported}");
            }

            return exported ?? option ?? Invalid<string>("/apiId", $"'{contractPath}' does not exist yet, so its apiId is unknown",
                "run `tisilia export` first, or pass --api-id with the ApiId the application registers (AddTisilia(o => o.ApiId = ...))");
        }

        T? Invalid<T>(string path, string message, string? fix = null)
        {
            bag.Add(new Diagnostic { Code = TisiliaCodes.ConfigInvalid, Severity = DiagnosticSeverity.Error, Path = path, Message = message, Fix = fix });
            return default;
        }
    }
}
