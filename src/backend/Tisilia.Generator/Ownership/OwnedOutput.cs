using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using GeneratedFile = Tisilia.Generator.TypeScript.GeneratedFile;

namespace Tisilia.Generator.Ownership;

public sealed record OutputPlan(IReadOnlyList<GeneratedFile> Files, GenerationManifest Manifest)
{
    public const string ManifestFileName = "tisilia.generation-manifest.json";
}

/// <summary>
/// Ownership: generation writes only files recorded in the manifest, refuses to touch edited or foreign
/// files, rejects paths outside the output root (including symlinks/reparse points) and replaces files atomically per file
/// after every file was generated and checked.
/// </summary>
public static partial class OwnedOutput
{
    public static string ManifestText(GenerationManifest manifest) => JsonSerializer.Serialize(manifest, TisiliaJson.IndentedOptions) + "\n";

    /// <summary>
    /// The previous generation's manifest, or null when there is none. One that cannot be read (broken, or written by a version
    /// of Tisilia with another format) is an error: without it, generated files cannot be told from foreign ones.
    /// </summary>
    public static GenerationManifest? ReadManifest(string outputRoot, DiagnosticBag bag)
    {
        var path = Path.Combine(outputRoot, OutputPlan.ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // diagnostics about the manifest carry the manifest's file: their JSON Pointers point into it, not into the config
        var manifestBag = new DiagnosticBag { File = path };
        try
        {
            var node = Validation.TisiliaSchemas.ParseStrict(File.ReadAllText(path), manifestBag);
            if (node is not null && Validation.TisiliaSchemas.Instance.ValidateStructure(Validation.DocumentKind.GenerationManifest, node, manifestBag))
            {
                return JsonSerializer.Deserialize<GenerationManifest>(node, TisiliaJson.Options);
            }

            manifestBag.Error(TisiliaCodes.GenerationManifest, "SV48", "",
                $"cannot read the previous generation's manifest{WrittenBy(node)}, so the files it generated cannot be told from files of your own",
                fix: "delete the generated files and the manifest (or generate into an empty directory), then generate again; --force does not apply: it overrides edits only to files a readable manifest owns");
            return null;
        }
        finally
        {
            bag.AddRange(manifestBag.Items);
        }
    }

    /// <summary>" (version 0.3, written by Tisilia 0.2.0-alpha; this Tisilia reads 0.1)" for a manifest of another format version, else "".</summary>
    private static string WrittenBy(JsonNode? manifest)
    {
        var version = VersionText((manifest as JsonObject)?["version"]);
        if (version is null || version == TisiliaJson.DraftVersion)
        {
            return "";
        }

        var generator = VersionText((manifest as JsonObject)?["generatorVersion"]);
        return $" (version {version}{(generator is null ? "" : ", written by Tisilia " + generator)}; this Tisilia reads {TisiliaJson.DraftVersion})";
    }

    // the manifest failed validation, so only what looks like a version goes into the message
    private static string? VersionText(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && VersionLike().IsMatch(text) ? text : null;

    [GeneratedRegex("^[0-9][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex VersionLike();

    /// <summary>
    /// Compares the plan with the current output directory. Returns the list of paths that differ (no writes). Line endings do
    /// not count: a CRLF checkout of the committed output is up to date (<see cref="LineEndings"/>).
    /// </summary>
    public static IReadOnlyList<string> Check(string outputRoot, OutputPlan plan)
    {
        var differences = new List<string>();
        foreach (var file in plan.Files)
        {
            var full = Path.Combine(outputRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || !LineEndings.SameText(File.ReadAllBytes(full), file.Bytes))
            {
                differences.Add(file.Path);
            }
        }

        var manifestPath = Path.Combine(outputRoot, OutputPlan.ManifestFileName);
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath).Replace("\r\n", "\n", StringComparison.Ordinal) != ManifestText(plan.Manifest))
        {
            differences.Add(OutputPlan.ManifestFileName);
        }

        return differences;
    }

    /// <summary>Writes the plan. Fails closed on foreign files, edited owned files (unless <paramref name="force"/>), traversal and reparse points.</summary>
    public static bool Write(string outputRoot, OutputPlan plan, GenerationManifest? previous, bool force, DiagnosticBag bag)
    {
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            bag.Error(TisiliaCodes.OutputPath, "SV38", "", $"output root '{root}' is a symlink or reparse point");
            return false;
        }

        var owned = previous?.Files.ToDictionary(f => f.Path, f => f.Digest, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var file in plan.Files)
        {
            var full = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                bag.Error(TisiliaCodes.OutputPath, "SV38", "", $"generated path '{file.Path}' escapes the output root");
                ok = false;
                continue;
            }

            if (File.Exists(full))
            {
                if (new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    bag.Error(TisiliaCodes.OutputPath, "SV38", "", $"'{file.Path}' is a symlink or reparse point; refusing to write through it");
                    ok = false;
                    continue;
                }

                // a CRLF checkout of what generation wrote is neither foreign nor an edit
                var current = File.ReadAllBytes(full);
                var alreadyGenerated = LineEndings.SameText(current, file.Bytes);
                if (!owned.TryGetValue(file.Path, out var recorded))
                {
                    if (!alreadyGenerated)
                    {
                        bag.Error(TisiliaCodes.OwnershipConflict, "SV48", "", $"'{file.Path}' exists but is not owned by the previous generation manifest; refusing to overwrite a foreign file", fix: "move or delete the file, or generate into an empty directory");
                        ok = false;
                    }
                }
                else if (!alreadyGenerated && !LineEndings.StillWritten(current, recorded) && !force)
                {
                    bag.Error(TisiliaCodes.OwnershipConflict, "SV48", "", $"'{file.Path}' was edited after generation (digest differs from the manifest); use --force to overwrite", fix: "generated code is not a place for manual edits; move custom code out of the output root");
                    ok = false;
                }
            }
        }

        if (!ok)
        {
            return false;
        }

        // stage everything first, then replace per file. A file that already holds its text is left alone, whatever its line
        // endings: watchers see no change, and a CRLF checkout is not rewritten with LF, which git (core.autocrlf=true) would
        // then list as modified although its content is the same
        var staged = new List<(string Temp, string Final)>();
        try
        {
            foreach (var file in plan.Files)
            {
                var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full) && LineEndings.SameText(File.ReadAllBytes(full), file.Bytes))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var temp = full + ".tisilia-tmp";
                File.WriteAllBytes(temp, file.Bytes);
                staged.Add((temp, full));
            }

            var manifestFull = Path.Combine(root, OutputPlan.ManifestFileName);
            var manifestText = ManifestText(plan.Manifest);
            if (!File.Exists(manifestFull) || File.ReadAllText(manifestFull).Replace("\r\n", "\n", StringComparison.Ordinal) != manifestText)
            {
                var manifestTemp = manifestFull + ".tisilia-tmp";
                File.WriteAllText(manifestTemp, manifestText);
                staged.Add((manifestTemp, manifestFull));
            }
            foreach (var (temp, final) in staged)
            {
                File.Move(temp, final, overwrite: true);
            }
        }
        catch
        {
            foreach (var (temp, _) in staged)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // best effort cleanup
                }
            }

            throw;
        }

        // remove files the previous manifest owned that are no longer generated (never anything else)
        if (previous is not null)
        {
            var generated = plan.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var old in previous.Files)
            {
                if (generated.Contains(old.Path))
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(root, old.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) && LineEndings.StillWritten(File.ReadAllBytes(full), old.Digest))
                {
                    File.Delete(full);
                }
            }
        }

        return true;
    }
}
