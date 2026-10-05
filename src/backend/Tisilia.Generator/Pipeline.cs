using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Ownership;
using Tisilia.Generator.TypeScript;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator;

/// <summary>Loads <c>tisilia.config</c> and drives validate → generate → own. No network, no module execution.</summary>
public static class Pipeline
{
    /// <summary>This generator's version as the generation manifest records it: the package version, without build metadata.</summary>
    public static string GeneratorVersion { get; } = ProductVersion(typeof(Pipeline).Assembly);

    /// <summary>The informational version of a Tisilia assembly (the package version), without the <c>+</c> build metadata.</summary>
    public static string ProductVersion(System.Reflection.Assembly assembly)
    {
        var informational = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion ?? "0.0.0";
        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }

    public sealed record LoadedConfig(ConfigDocument Config, string ConfigDirectory, string ContractPath, string OutputRoot);

    public static LoadedConfig? LoadConfig(string configPath, DiagnosticBag bag)
    {
        var full = Path.GetFullPath(configPath);
        if (!File.Exists(full))
        {
            bag.Add(new Diagnostic { Code = TisiliaCodes.ConfigInvalid, Severity = DiagnosticSeverity.Error, Message = "config file not found", File = full });
            return null;
        }

        var node = TisiliaSchemas.ParseStrict(File.ReadAllText(full), bag);
        if (node is null || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Config, node, bag))
        {
            return null;
        }

        var config = JsonSerializer.Deserialize<ConfigDocument>(node, TisiliaJson.Options)!;
        ValidateLimits(config.Limits, bag);

        if (bag.HasErrors)
        {
            return null;
        }

        var dir = Path.GetDirectoryName(full)!;
        return new LoadedConfig(config, dir, Path.GetFullPath(Path.Combine(dir, config.Contract)), Path.GetFullPath(Path.Combine(dir, config.Output)));
    }

    private static void ValidateLimits(Limits limits, DiagnosticBag bag)
    {
        if (limits.MaxBodyBytes < 1 || limits.MaxDepth is < 1 or > 1024 || limits.MaxTokens < 1 || limits.MaxNumberCharacters is < 1 or > 4096 || limits.TimeoutMs < 1 || limits.MaxDiagnosticBytes < 0)
        {
            bag.Error(TisiliaCodes.InvalidLimits, "SV37", "/limits", "limits must be positive safe integers (maxDepth at most 1024, maxNumberCharacters at most 4096, maxDiagnosticBytes may be 0)");
        }
    }

    public sealed record Plan(LoadedContract Contract, ContractIndex Index, OutputPlan Output);

    /// <summary>
    /// Validates the contract and builds the full output plan without touching the output directory. Evidence files
    /// are checked against the contract; only valid evidence qualifies coverage (SV45/SV46), and with
    /// <c>coveragePolicy=qualified-only</c> only qualified operations are published.
    /// </summary>
    public static Plan? BuildPlan(LoadedConfig config, DiagnosticBag bag, IReadOnlyList<string>? evidencePaths = null, IReadOnlySet<string>? trustedIssuers = null)
    {
        // diagnostics about the contract carry the contract's file: their JSON Pointers point into it, not into the config
        var contractBag = new DiagnosticBag { File = config.ContractPath };
        try
        {
            return BuildPlan(config, bag, contractBag, evidencePaths, trustedIssuers);
        }
        finally
        {
            bag.AddRange(contractBag.Items);
        }
    }

    private static Plan? BuildPlan(LoadedConfig config, DiagnosticBag bag, DiagnosticBag contractBag, IReadOnlyList<string>? evidencePaths, IReadOnlySet<string>? trustedIssuers)
    {
        var loaded = ContractLoader.LoadFile(config.ContractPath, contractBag);
        if (loaded is null)
        {
            return null;
        }

        // the config names the API it generates for; a contract of another API is a misconfigured path, not a client to write
        if (loaded.Document.ApiId != config.Config.ApiId)
        {
            bag.Add(ConfigDiagnostic(DiagnosticSeverity.Error, "/apiId", $"config apiId '{config.Config.ApiId}' does not match the contract's apiId '{loaded.Document.ApiId}' ({config.ContractPath})", $"point 'contract' at the export of '{config.Config.ApiId}', or set \"apiId\": \"{loaded.Document.ApiId}\""));
            return null;
        }

        ReportUnusedSettings(config.Config, bag);
        var index = SemanticValidator.Validate(loaded, contractBag);
        if (index is null)
        {
            return null;
        }

        var evidences = (evidencePaths ?? []).Select(p => Conformance.EvidenceValidator.Check(p, index, trustedIssuers ?? new HashSet<string>(StringComparer.Ordinal), bag, config.ConfigDirectory, config.OutputRoot)).ToList();
        var coverage = Conformance.EvidenceValidator.Coverage(index, evidences);
        if (config.Config.CoveragePolicy == CoveragePolicy.QualifiedOnly)
        {
            var qualified = coverage.Where(c => c.Status == CoverageStatus.Qualified).Select(c => c.OperationId).ToHashSet(StringComparer.Ordinal);
            if (qualified.Count == 0)
            {
                var reasons = string.Join(", ", coverage.SelectMany(c => c.ReasonCodes).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                bag.Error(TisiliaCodes.CoverageNotQualified, "SV46", "/coveragePolicy", $"coveragePolicy 'qualified-only' publishes only operations with valid codec conformance evidence and none qualifies ({reasons}); use 'development' for codec-not-applicable operations, or run codec conformance and pass --evidence/--trusted-issuer; HTTP remains unobserved");
                return null;
            }

            if (qualified.Count < index.Operations.Count)
            {
                foreach (var entry in coverage.Where(c => c.Status != CoverageStatus.Qualified))
                {
                    bag.Warning(TisiliaCodes.CoverageNotQualified, "SV46", "/operations", $"operation '{entry.OperationId}' is not published under qualified-only ({string.Join(", ", entry.ReasonCodes)})", [entry.OperationId]);
                }

                var reduced = index.Document with { Operations = index.Document.Operations.Where(o => qualified.Contains(o.Id)).ToList() };
                index = new ContractIndex(reduced);
                coverage = coverage.Where(c => qualified.Contains(c.OperationId)).ToList();
            }
        }

        var contractDir = Path.GetDirectoryName(config.ContractPath)!;
        var options = new TsGenerationOptions
        {
            ModuleMode = config.Config.Target.ModuleMode,
            GeneratorVersion = GeneratorVersion,
            CoveragePolicy = config.Config.CoveragePolicy,
            Limits = config.Config.Limits,
            ModuleImportResolver = (module, artifact) =>
            {
                var full = Path.GetFullPath(Path.Combine(contractDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(full))
                {
                    contractBag.Error(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"module '{module.Id}': artifact '{artifact.Path}' is not installed at {full}; generated clients only bind pre-installed modules", [module.Id],
                        module.Id == Additional.AdditionalModule.ModuleId ? "install it: " + Additional.AdditionalModule.InstallHint(contractDir) : "copy the module's files there: the contract's artifact paths are relative to the contract");
                    return null;
                }

                var bytes = File.ReadAllBytes(full);
                var actual = TisiliaHash.Sha256OfBytes(bytes);
                if (actual != artifact.Digest)
                {
                    var lineEndings = LineEndings.ArtifactHint(bytes, artifact.Digest, full);
                    contractBag.Error(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"module '{module.Id}': artifact '{artifact.Path}' digest {actual} does not match the contract ({artifact.Digest}); " + (lineEndings ?? "the installed module is not the one the contract was exported against"), [module.Id],
                        module.Id != Additional.AdditionalModule.ModuleId ? null
                        : lineEndings is null ? "install the module of this Tisilia version: " + Additional.AdditionalModule.InstallHint(contractDir) + " --force"
                        : "or install it again, which also writes that .gitattributes: " + Additional.AdditionalModule.InstallHint(contractDir));
                    return null;
                }

                var relative = Path.GetRelativePath(config.OutputRoot, full).Replace('\\', '/');
                return relative.StartsWith('.') ? relative : "./" + relative;
            },
        };
        var files = TsGenerator.Generate(index, options, contractBag, out var nameMappings);
        if (files is null)
        {
            return null;
        }

        var closure = ContractClosure.Compute(index, index.Operations.Keys);
        var moduleArtifacts = closure.DeclaredModuleArtifacts();
        var manifest = new GenerationManifest
        {
            Format = TisiliaJson.Formats.GenerationManifest,
            Version = TisiliaJson.DraftVersion,
            ApiId = index.Document.ApiId,
            SemanticHash = index.Document.SemanticHash,
            // with CRLF read as LF: a CRLF checkout of the committed contract leaves the committed output up to date
            ContractArtifactDigest = LineEndings.Sha256OfTextFile(config.ContractPath),
            GeneratorVersion = GeneratorVersion,
            Abi = TisiliaJson.DraftVersion,
            TargetMode = config.Config.Target.ModuleMode,
            Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new Documents.GeneratedFile { Path = f.Path, Digest = f.Digest }).ToList(),
            NameMappings = nameMappings.OrderBy(n => n.Kind).ThenBy(n => n.Id, StringComparer.Ordinal).ToList(),
            ModuleArtifacts = moduleArtifacts,
            Coverage = coverage,
        };
        var manifestNode = JsonSerializer.SerializeToNode(manifest, TisiliaJson.Options)!.AsObject();
        if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.GenerationManifest, manifestNode, bag))
        {
            return null;
        }

        return new Plan(loaded, index, new OutputPlan(files, manifest));
    }

    /// <summary>Settings that generation does not act on are reported, so that setting them is never mistaken for an effect.</summary>
    private static void ReportUnusedSettings(ConfigDocument config, DiagnosticBag bag)
    {
        if (config.Modules.Count > 0)
        {
            bag.Add(ConfigDiagnostic(DiagnosticSeverity.Warning, "/modules", "'modules' is not read by generate: module artifacts are imported from the paths the contract declares (relative to the contract)", "leave \"modules\": [] and install the module where the contract's artifact path points"));
        }

        if (config.PortableProjects.Count > 0)
        {
            bag.Add(ConfigDiagnostic(DiagnosticSeverity.Warning, "/portableProjects", "'portableProjects' is not read by generate: portable codecs are generated with `tisilia codec generate --project <file>`", "leave \"portableProjects\": [] and run codec generate for each project"));
        }

        if (config.Nuxt.Enabled)
        {
            bag.Add(ConfigDiagnostic(DiagnosticSeverity.Warning, "/nuxt/enabled", "generate writes no Nuxt files; the Nuxt integration is the @kkdev92/tisilia-nuxt module registered in nuxt.config", "add '@kkdev92/tisilia-nuxt' to the modules of nuxt.config"));
        }
    }

    /// <summary>A configuration diagnostic: config settings have no SV rule of their own.</summary>
    private static Diagnostic ConfigDiagnostic(DiagnosticSeverity severity, string path, string message, string fix)
        => new() { Code = TisiliaCodes.ConfigInvalid, Severity = severity, Path = path, Message = message, Fix = fix };
}
