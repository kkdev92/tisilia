using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    [GeneratedRegex("\\+[0-9a-f]{7,40}$")]
    private static partial Regex CommitSuffix();

    [GeneratedRegex("(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])")]
    private static partial Regex Commit();
}
