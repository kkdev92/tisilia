using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tisilia.Generator.Canonical;

/// <summary>
/// Whether an assembly's bytes depend on the git commit it was built from. The .NET 8+ SDK puts the commit into the assembly's
/// InformationalVersion (SourceRevisionId) and into the Source Link map of its portable PDB, whose content a deterministic build
/// hashes into the assembly's PDB id and checksum. A contract hashes the assemblies of its modules byte for byte, so either makes
/// every commit change the semanticHash.
/// </summary>
public static partial class SourceRevision
{
    /// <summary>Portable PDB custom debug information "Source Link" (on the module), dotnet/runtime PortablePdb-Metadata.md.</summary>
    public static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    /// <summary>The InformationalVersion when it ends in a commit (<c>1.0.0+3f2a…</c>), else null.</summary>
    public static string? InformationalVersion(string assembly)
        => System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly).ProductVersion is { } version && CommitSuffix().IsMatch(version) ? version : null;

    /// <summary>
    /// The commit the Source Link map of the assembly's portable PDB (next to it or embedded) points at, else null — also when
    /// the assembly or its PDB cannot be read: the digest still binds whatever the bytes are.
    /// </summary>
    public static string? SourceLinkCommit(string assembly)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            if (!pe.TryOpenAssociatedPortablePdb(assembly, path => File.Exists(path) ? File.OpenRead(path) : null, out var provider, out _) || provider is null)
            {
                return null;
            }

            using (provider)
            {
                var reader = provider.GetMetadataReader();
                foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
                {
                    var information = reader.GetCustomDebugInformation(handle);
                    if (reader.GetGuid(information.Kind) == SourceLinkKind)
                    {
                        var map = System.Text.Encoding.UTF8.GetString(reader.GetBlobBytes(information.Value));
                        return Commit().Match(map) is { Success: true } commit ? commit.Value : null;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // not readable as a managed assembly with a portable PDB: nothing to report
        }

        return null;
    }

    /// <summary>
    /// The projects <paramref name="assembly"/> depends on, directly or through other projects, whose assemblies embed the source
    /// revision. A deterministic build records the identity of every assembly it references in its portable PDB, which the
    /// assembly's PDB id, checksum and MVID hash, so a commit stamped into a referenced project changes the assembly too, even when
    /// the assembly itself carries no commit. The projects come from the application's <c>.deps.json</c> next to the assembly
    /// (libraries of type <c>project</c>); packages are left out, since a released package does not change between commits.
    /// Empty when no deps file there lists the assembly.
    /// </summary>
    public static IReadOnlyList<StampedReference> StampedProjectReferences(string assembly)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assembly));
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        var fileName = Path.GetFileName(assembly);
        foreach (var depsFile in Directory.EnumerateFiles(directory, "*.deps.json").Order(StringComparer.Ordinal))
        {
            var files = ReferencedProjectFiles(depsFile, fileName);
            if (files is null)
            {
                continue;
            }

            return files
                .Select(name => Path.Combine(directory, name))
                .Where(File.Exists)
                .Select(path => new StampedReference(path, InformationalVersion(path), SourceLinkCommit(path)))
                .Where(reference => reference.InformationalVersion is not null || reference.SourceLinkCommit is not null)
                .OrderBy(reference => reference.Path, StringComparer.Ordinal)
                .ToList();
        }

        return [];
    }

    /// <summary>
    /// The runtime file names of the project libraries that <paramref name="fileName"/> depends on in <paramref name="depsFile"/>,
    /// or null when that deps file does not list it.
    /// </summary>
    private static List<string>? ReferencedProjectFiles(string depsFile, string fileName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(depsFile));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var projects = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind == JsonValueKind.Object && library.Value.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String && type.GetString() == "project")
                    {
                        projects.Add(library.Name);
                    }
                }
            }

            foreach (var target in targets.EnumerateObject())
            {
                if (target.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entries = target.Value.EnumerateObject()
                    .Where(entry => entry.Value.ValueKind == JsonValueKind.Object)
                    .ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
                var start = entries.FirstOrDefault(entry => RuntimeFiles(entry.Value).Contains(fileName, StringComparer.OrdinalIgnoreCase)).Key;
                if (start is null)
                {
                    continue;
                }

                // walk the dependencies from the assembly's own entry; entries and dependencies name "library/version"
                var seen = new HashSet<string>(StringComparer.Ordinal) { start };
                var pending = new Queue<string>([start]);
                var files = new List<string>();
                while (pending.TryDequeue(out var key))
                {
                    if (!entries.TryGetValue(key, out var entry))
                    {
                        continue;
                    }

                    if (key != start && projects.Contains(key))
                    {
                        files.AddRange(RuntimeFiles(entry));
                    }

                    if (entry.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var dependency in dependencies.EnumerateObject())
                        {
                            if (dependency.Value.ValueKind == JsonValueKind.String && seen.Add($"{dependency.Name}/{dependency.Value.GetString()}"))
                            {
                                pending.Enqueue($"{dependency.Name}/{dependency.Value.GetString()}");
                            }
                        }
                    }
                }

                return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            return null;
        }
    }

    private static IEnumerable<string> RuntimeFiles(JsonElement entry)
        => entry.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object
            ? runtime.EnumerateObject().Select(file => Path.GetFileName(file.Name)).Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            : [];

    [GeneratedRegex("\\+[0-9a-f]{7,40}$")]
    private static partial Regex CommitSuffix();

    [GeneratedRegex("(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])")]
    private static partial Regex Commit();
}

/// <summary>A referenced project's assembly and where it embeds the source revision (either may be null, not both).</summary>
public sealed record StampedReference(string Path, string? InformationalVersion, string? SourceLinkCommit);
