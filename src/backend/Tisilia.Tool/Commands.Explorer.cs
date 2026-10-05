using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary>
    /// <c>explorer build --registry file --allow-execute-build [--output dir]</c>: bundles the Explorer SPA
    /// with the browser artifacts of a trusted registry of codec modules. The registry (<c>tisilia.explorer-registry</c>, an
    /// implementation-defined record) names each module's codec manifest with the digest it trusts; every artifact is checked
    /// against the manifest's digest before it is copied, nothing is fetched. The Explorer package's own build script runs,
    /// which executes package code, so the flag is required. The output carries <c>tisilia.explorer-bundle.json</c> with the
    /// digests of every bundled file so that evidence can bind to the bundle.
    /// </summary>
    public static async Task<int> ExplorerAsync(CommandLine cli, bool json)
    {
        if (cli.Positional.Count < 2 || cli.Positional[1] != "build")
        {
            throw new UsageException("explorer build --registry <file> --allow-execute-build [--output <dir>]");
        }

        var registryPath = Path.GetFullPath(cli.Require("registry"));
        var bag = new DiagnosticBag { File = registryPath };
        if (!File.Exists(registryPath))
        {
            bag.Error(TisiliaCodes.SchemaViolation, "SV01", "", $"registry '{registryPath}' not found");
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        var node = TisiliaSchemas.ParseStrict(File.ReadAllText(registryPath), bag);
        if (node is not JsonObject root || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.ExplorerRegistry, root, bag))
        {
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        var registryDir = Path.GetDirectoryName(registryPath)!;
        var explorerDir = Path.GetFullPath(Path.Combine(registryDir, root["explorer"]!.GetValue<string>()));
        var packageJsonPath = Path.Combine(explorerDir, "package.json");
        string explorerName = "", explorerVersion = "";
        if (!File.Exists(packageJsonPath))
        {
            bag.Error(TisiliaCodes.OutputPath, "SV38", "/explorer", $"explorer package '{explorerDir}' has no package.json");
        }
        else
        {
            var package = JsonNode.Parse(File.ReadAllText(packageJsonPath))?.AsObject();
            explorerName = package?["name"]?.GetValue<string>() ?? "";
            explorerVersion = package?["version"]?.GetValue<string>() ?? "";
            if (package?["scripts"]?["build"] is null)
            {
                bag.Error(TisiliaCodes.OutputPath, "SV38", "/explorer", $"explorer package '{explorerName}' declares no `build` script");
            }
        }

        var modules = new List<(string Id, string Version, List<(string Path, string File, string Digest)> Artifacts)>();
        var index = 0;
        foreach (var entry in root["modules"]!.AsArray())
        {
            var pointer = "/modules/" + index++;
            var manifestPath = Path.GetFullPath(Path.Combine(registryDir, entry!["manifest"]!.GetValue<string>()));
            var moduleRoot = Path.GetFullPath(Path.Combine(registryDir, entry["root"]!.GetValue<string>()));
            var trustedDigest = entry["digest"]!.GetValue<string>();
            if (!File.Exists(manifestPath))
            {
                bag.Error(TisiliaCodes.ModuleArtifact, "SV44", pointer + "/manifest", $"codec manifest '{manifestPath}' not found");
                continue;
            }

            // the codec manifest is a JSON document Tisilia writes: a CRLF checkout of it is the trusted one (LineEndings)
            var manifestBytes = File.ReadAllBytes(manifestPath);
            if (!LineEndings.StillWritten(manifestBytes, trustedDigest))
            {
                bag.Error(TisiliaCodes.ModuleArtifact, "SV44", pointer + "/digest", $"codec manifest '{manifestPath}' has digest {TisiliaHash.Sha256OfBytes(manifestBytes)} but the registry trusts {trustedDigest}; " + (LineEndings.ArtifactHint(manifestBytes, trustedDigest, manifestPath) ?? "the installed module is not the trusted one"));
                continue;
            }

            var manifestBag = new DiagnosticBag { File = manifestPath };
            var manifestNode = TisiliaSchemas.ParseStrict(File.ReadAllText(manifestPath), manifestBag);
            if (manifestNode is not JsonObject manifestRoot || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.CodecManifest, manifestRoot, manifestBag))
            {
                bag.AddRange(manifestBag.Items);
                continue;
            }

            var manifest = JsonSerializer.Deserialize<CodecManifest>(manifestRoot, TisiliaJson.Options)!;
            var artifacts = new List<(string Path, string File, string Digest)>();
            foreach (var artifact in manifest.Module.Artifacts.Where(a => a.Target == ArtifactTarget.Browser))
            {
                var file = Path.GetFullPath(Path.Combine(moduleRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(moduleRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    bag.Error(TisiliaCodes.OutputPath, "SV38", pointer, $"module '{manifest.Module.Id}' artifact '{artifact.Path}' escapes its root '{moduleRoot}'");
                    continue;
                }

                if (!File.Exists(file))
                {
                    bag.Error(TisiliaCodes.ModuleArtifact, "SV44", pointer, $"module '{manifest.Module.Id}' artifact '{artifact.Path}' is not installed under '{moduleRoot}'; bundles only carry pre-installed modules");
                    continue;
                }

                var fileBytes = File.ReadAllBytes(file);
                var fileDigest = TisiliaHash.Sha256OfBytes(fileBytes);
                if (fileDigest != artifact.Digest)
                {
                    bag.Error(TisiliaCodes.ModuleArtifact, "SV44", pointer, $"module '{manifest.Module.Id}' artifact '{artifact.Path}' has digest {fileDigest} but its manifest declares {artifact.Digest}" + (LineEndings.ArtifactHint(fileBytes, artifact.Digest, file) is { } hint ? "; " + hint : ""));
                    continue;
                }

                artifacts.Add((artifact.Path, file, fileDigest));
            }

            modules.Add((manifest.Module.Id, manifest.Module.Version, artifacts));
        }

        string? contractSource = null;
        if (root["contract"] is { } contractNode)
        {
            contractSource = Path.GetFullPath(Path.Combine(registryDir, contractNode.GetValue<string>()));
            if (!File.Exists(contractSource))
            {
                bag.Error(TisiliaCodes.SchemaViolation, "SV01", "/contract", $"contract '{contractSource}' not found");
            }
        }

        if (bag.HasErrors)
        {
            Output.Report(bag, json, null);
            return ExitCodes.SemanticOrUnsupported;
        }

        if (!cli.Flag("allow-execute-build"))
        {
            Console.Error.WriteLine("explorer build runs the Explorer package's build script (npm run build → vite), which executes package code; pass --allow-execute-build explicitly");
            return ExitCodes.SafetyPolicyViolation;
        }

        Console.Error.WriteLine($"tisilia explorer build: npm run build in {explorerDir} ({explorerName}@{explorerVersion})");
        var exit = OperatingSystem.IsWindows()
            ? await RunProcessAsync("cmd", ["/c", "npm", "run", "build"], explorerDir, Console.Error).ConfigureAwait(false)
            : await RunProcessAsync("npm", ["run", "build"], explorerDir, Console.Error).ConfigureAwait(false);
        if (exit != 0)
        {
            Console.Error.WriteLine($"explorer package build exited with {exit}");
            return ExitCodes.ExternalProcessFailed;
        }

        var dist = Path.Combine(explorerDir, "dist");
        if (!File.Exists(Path.Combine(dist, "index.html")))
        {
            Console.Error.WriteLine($"the explorer build produced no {Path.Combine(dist, "index.html")}");
            return ExitCodes.ExternalProcessFailed;
        }

        var outDir = Path.GetFullPath(cli.Option("output") ?? dist);
        if (!string.Equals(outDir, Path.GetFullPath(dist), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                Console.Error.WriteLine($"output '{outDir}' is not empty; refusing to overwrite files the bundle does not own");
                return ExitCodes.SafetyPolicyViolation;
            }

            CopyDirectory(dist, outDir);
        }

        var bundled = new List<object>();
        foreach (var (id, version, artifacts) in modules)
        {
            var moduleArtifacts = new List<object>();
            foreach (var (path, file, digest) in artifacts)
            {
                var target = Path.Combine(outDir, "modules", id, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                moduleArtifacts.Add(new { target = "browser", path, digest });
            }

            bundled.Add(new { id, version, artifacts = moduleArtifacts });
        }

        string? contractDigest = null;
        if (contractSource is not null)
        {
            File.Copy(contractSource, Path.Combine(outDir, "contract"), overwrite: true);
            contractDigest = TisiliaHash.Sha256OfFile(contractSource);
        }

        var bundlePath = Path.Combine(outDir, "tisilia.explorer-bundle.json");
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, bundlePath, StringComparison.OrdinalIgnoreCase))
            .Select(f => new { path = Path.GetRelativePath(outDir, f).Replace('\\', '/'), digest = TisiliaHash.Sha256OfFile(f) })
            .OrderBy(f => f.path, StringComparer.Ordinal)
            .ToList();
        var bundle = new
        {
            format = TisiliaJson.Formats.ExplorerBundle,
            version = TisiliaJson.DraftVersion,
            explorer = new { name = explorerName, version = explorerVersion },
            registry = new { path = Path.GetRelativePath(outDir, registryPath).Replace('\\', '/'), digest = TisiliaHash.Sha256OfFile(registryPath) },
            contract = contractDigest is null ? null : new { path = "contract", digest = contractDigest },
            modules = bundled,
            files,
        };
        File.WriteAllText(bundlePath, JsonSerializer.Serialize(bundle, TisiliaJson.IndentedOptions) + "\n");
        Output.Report(bag, json, new { output = outDir, explorer = explorerName + "@" + explorerVersion, modules = modules.Select(m => m.Id + "@" + m.Version).ToList(), files = files.Count, bundle = bundlePath });
        return ExitCodes.Success;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
