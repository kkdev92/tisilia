using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.TypeScript;
using GeneratedFile = Tisilia.Generator.TypeScript.GeneratedFile;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Portable;

/// <summary>Ownership record of a portable generation (tool-internal; the module declaration itself is the codec manifest).</summary>
public sealed record PortableOwnership(string Format, string Version, string ProjectId, IReadOnlyList<Documents.GeneratedFile> Files)
{
    public const string FileName = "tisilia.portable-ownership.json";
    public const string FormatId = "tisilia.portable-ownership";
}

public sealed record PortablePlan(LoadedPortableProject Project, IReadOnlyList<GeneratedFile> CsharpFiles, IReadOnlyList<GeneratedFile> TypescriptFiles, JsonObject CodecManifest);

/// <summary>
/// <c>codec generate --project</c>: load and validate the project, generate the C# converters,
/// the TypeScript module and the codec manifest, then write them without overwriting files the previous generation
/// did not own. No projection is executed; the C# source is only proven by compiling it.
/// </summary>
public static class PortableCodegen
{
    public static PortablePlan? Plan(string projectPath, string? contentRoot, DiagnosticBag bag)
    {
        var project = PortableProjectLoader.Load(projectPath, bag);
        if (project is null || bag.HasErrors)
        {
            return null;
        }

        var root = Path.GetFullPath(contentRoot ?? project.Directory);
        var tsFile = Path.Combine(project.TypescriptOutput, project.Project.ProjectId + ".portable.js");
        var tsArtifact = Path.GetRelativePath(root, tsFile).Replace('\\', '/');
        if (tsArtifact.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(tsArtifact))
        {
            bag.Error(TisiliaCodes.OutputPath, "SV38", "/output/typescript", $"the TypeScript output '{project.Project.Output.Typescript}' must lie under the content root {root} so that the module can be served and digest-checked");
            return null;
        }

        var csharp = PortableCSharpGenerator.Generate(project, tsArtifact, bag);
        var typescript = PortableTsGenerator.Generate(project, bag);
        if (bag.HasErrors)
        {
            return null;
        }

        var manifest = CodecManifest(project, typescript, tsArtifact, bag);
        return manifest is null ? null : new PortablePlan(project, csharp, typescript, manifest);
    }

    /// <summary>The module declaration (<c>tisilia.codec-manifest</c>): module + generated wires/codecs are exported by the application at runtime; here the module and its bindings/models are declared for reuse.</summary>
    private static JsonObject? CodecManifest(LoadedPortableProject project, IReadOnlyList<GeneratedFile> typescript, string tsArtifact, DiagnosticBag bag)
    {
        var js = typescript.First(f => f.Path.EndsWith(".js", StringComparison.Ordinal));
        var digest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(js.Content));
        var exports = new List<ModuleExport>();
        foreach (var def in project.Definitions.OrderBy(d => d.Definition.Id, StringComparer.Ordinal))
        {
            var camel = PortableModel.Camel(def.Definition.Id.Split('.').Last());
            var converter = PortableModel.Pascal(def.Definition.Id.Split('.').Last()) + "PortableConverter";
            exports.Add(new ModuleExport { Name = converter, Role = ExportRole.Codec, Targets = [ArtifactTarget.Dotnet] });
            exports.Add(new ModuleExport { Name = camel + "DomainRule", Role = ExportRole.DomainRule, Targets = [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node] });
            exports.Add(new ModuleExport { Name = camel + "Validate", Role = ExportRole.Validator, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] });
            if (def.Definition.Request is not null)
            {
                exports.Add(new ModuleExport { Name = camel + "RequestEncode", Role = ExportRole.Codec, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] });
                exports.Add(new ModuleExport { Name = camel + "RequestInput", Role = ExportRole.RequestInput, Targets = [ArtifactTarget.Browser] });
            }

            if (def.Definition.Response is not null)
            {
                exports.Add(new ModuleExport { Name = camel + "ResponseDecode", Role = ExportRole.Codec, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] });
            }
        }

        var manifest = new CodecManifest
        {
            Format = TisiliaJson.Formats.CodecManifest,
            Version = TisiliaJson.DraftVersion,
            Module = new Module
            {
                Id = project.Project.ProjectId,
                Version = project.Definitions.Select(d => d.Definition.DefinitionVersion).OrderBy(v => v, StringComparer.Ordinal).First(),
                Abi = TisiliaJson.DraftVersion,
                Artifacts =
                [
                    new Artifact { Target = ArtifactTarget.Browser, Path = tsArtifact, Digest = digest },
                    new Artifact { Target = ArtifactTarget.Node, Path = tsArtifact, Digest = digest },
                ],
                Exports = exports,
                DependencyIds = project.Imports.Select(i => i.Project.ProjectId).ToList(),
                License = "MIT",
                NoticeFiles = [],
            },
            Bindings = project.Project.Bindings,
            Codecs = [],
            Types = project.Project.Models,
            Wires = [],
            Equivalences = project.Project.Equivalences.Concat(project.SynthesizedEquivalences).ToList(),
            Projections = project.Project.Projections,
            Comparers = [],
            Binders = [],
            ResultAdapters = [],
        };
        var node = JsonSerializer.SerializeToNode(manifest, TisiliaJson.Options)!.AsObject();
        return TisiliaSchemas.Instance.ValidateStructure(DocumentKind.CodecManifest, node, bag) ? node : null;
    }

    /// <summary>Writes the plan. Files owned by a previous generation are replaced; anything else on disk is never overwritten.</summary>
    public static bool Write(PortablePlan plan, bool force, DiagnosticBag bag)
    {
        var csRoot = plan.Project.CsharpOutput;
        var tsRoot = plan.Project.TypescriptOutput;
        var manifestPath = Path.Combine(csRoot, PortableOwnership.FileName);
        var previous = ReadOwnership(manifestPath, bag);
        var owned = previous?.Files.ToDictionary(f => f.Path, f => f.Digest, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var outputs = new List<(string Full, string Relative, string Content)>();
        foreach (var f in plan.CsharpFiles)
        {
            outputs.Add((Path.Combine(csRoot, f.Path), "csharp/" + f.Path, f.Content));
        }

        foreach (var f in plan.TypescriptFiles)
        {
            outputs.Add((Path.Combine(tsRoot, f.Path), "typescript/" + f.Path, f.Content));
        }

        outputs.Add((Path.Combine(csRoot, plan.Project.Project.ProjectId + ".codec-manifest.json"), "csharp/" + plan.Project.Project.ProjectId + ".codec-manifest.json", plan.CodecManifest.ToJsonString(TisiliaJson.IndentedOptions) + "\n"));
        foreach (var (full, relative, content) in outputs)
        {
            if (File.Exists(full))
            {
                // a CRLF checkout of what generation wrote is not an edit (LineEndings)
                var current = File.ReadAllBytes(full);
                if (!owned.TryGetValue(relative, out var ownedDigest))
                {
                    bag.Error(TisiliaCodes.OwnershipConflict, "SV38", "/output", $"'{full}' exists but is not owned by a previous portable generation; refusing to overwrite");
                }
                else if (!LineEndings.StillWritten(current, ownedDigest) && !LineEndings.SameText(current, System.Text.Encoding.UTF8.GetBytes(content)) && !force)
                {
                    bag.Error(TisiliaCodes.OwnershipConflict, "SV38", "/output", $"'{full}' was edited after generation (digest {TisiliaHash.Sha256OfBytes(current)} ≠ {ownedDigest}); pass --force to replace it");
                }
            }
        }

        if (bag.HasErrors)
        {
            return false;
        }

        Directory.CreateDirectory(csRoot);
        Directory.CreateDirectory(tsRoot);
        // the module script is a contract artifact hashed byte for byte (SV44): git must not convert its line endings
        LineEndings.PinDirectory(tsRoot, "tisilia codec generate", plan.TypescriptFiles.Select(f => f.Path));
        var files = new List<Documents.GeneratedFile>();
        foreach (var (full, relative, content) in outputs)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            files.Add(new Documents.GeneratedFile { Path = relative, Digest = TisiliaHash.Sha256OfBytes(bytes) });
            // a file that already holds its text is left alone (a CRLF checkout rewritten with LF would show as modified in git);
            // the module script only when byte for byte: the contract records its bytes
            if (File.Exists(full) && (relative.StartsWith("typescript/", StringComparison.Ordinal) ? File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes) : LineEndings.SameText(File.ReadAllBytes(full), bytes)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, full, overwrite: true);
        }

        var ownership = new PortableOwnership(PortableOwnership.FormatId, TisiliaJson.DraftVersion, plan.Project.Project.ProjectId, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
        var ownershipText = JsonSerializer.Serialize(ownership, TisiliaJson.IndentedOptions) + "\n";
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath).Replace("\r\n", "\n", StringComparison.Ordinal) != ownershipText)
        {
            File.WriteAllText(manifestPath, ownershipText);
        }

        return true;
    }

    private static PortableOwnership? ReadOwnership(string path, DiagnosticBag bag)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var ownership = JsonSerializer.Deserialize<PortableOwnership>(File.ReadAllText(path), TisiliaJson.Options);
            if (ownership?.Format != PortableOwnership.FormatId)
            {
                bag.Error(TisiliaCodes.OwnershipConflict, "SV38", "/output", $"'{path}' is not a tisilia.portable-ownership record");
                return null;
            }

            return ownership;
        }
        catch (JsonException e)
        {
            bag.Error(TisiliaCodes.OwnershipConflict, "SV38", "/output", $"'{path}' cannot be read: {e.Message}");
            return null;
        }
    }
}
