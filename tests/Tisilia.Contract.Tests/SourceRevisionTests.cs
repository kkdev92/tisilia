using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Tisilia.Generator.Canonical;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Assemblies whose bytes depend on the commit they were built from (SV44 export warning, .NET 8+ SDK).</summary>
public sealed class SourceRevisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-revision-" + Guid.NewGuid().ToString("N"));

    public SourceRevisionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A minimal assembly with an embedded portable PDB that carries <paramref name="sourceLink"/> (none when null).</summary>
    private string WriteAssembly(string name, string? sourceLink)
    {
        var metadata = new MetadataBuilder();
        var assemblyName = metadata.GetOrAddString(name);
        metadata.AddModule(0, assemblyName, metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(assemblyName, new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.Sha1);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var debug = new DebugDirectoryBuilder();
        var pdb = new MetadataBuilder();
        if (sourceLink is not null)
        {
            pdb.AddCustomDebugInformation(EntityHandle.ModuleDefinition, pdb.GetOrAddGuid(SourceRevision.SourceLinkKind), pdb.GetOrAddBlobUTF8(sourceLink));
        }

        var pdbBuilder = new PortablePdbBuilder(pdb, metadata.GetRowCounts(), default);
        var pdbBlob = new BlobBuilder();
        var pdbId = pdbBuilder.Serialize(pdbBlob);
        debug.AddCodeViewEntry(name + ".pdb", pdbId, pdbBuilder.FormatVersion);
        debug.AddEmbeddedPortablePdbEntry(pdbBlob, pdbBuilder.FormatVersion);

        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder(), debugDirectoryBuilder: debug).Serialize(image);
        var path = Path.Combine(_dir, name + ".dll");
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    [Fact]
    public void The_commit_of_a_Source_Link_map_is_found_in_an_embedded_pdb()
    {
        const string commit = "b661016562c639606f38ee2c9f22845bec3f86c5";
        var withSourceLink = WriteAssembly("WithSourceLink", "{\"documents\":{\"C:\\\\repo\\\\*\":\"https://raw.githubusercontent.com/owner/repo/" + commit + "/*\"}}");
        Assert.Equal(commit, SourceRevision.SourceLinkCommit(withSourceLink));
        Assert.Null(SourceRevision.SourceLinkCommit(WriteAssembly("WithoutSourceLink", null)));
        // a map that names no commit (a branch or a local path) does not tie the bytes to one
        Assert.Null(SourceRevision.SourceLinkCommit(WriteAssembly("BranchMap", "{\"documents\":{\"C:\\\\repo\\\\*\":\"https://example.com/repo/main/*\"}}")));
    }

    [Fact]
    public void A_file_that_is_no_assembly_reports_nothing()
    {
        var text = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(text, "not a portable executable");
        Assert.Null(SourceRevision.SourceLinkCommit(text));
        Assert.Null(SourceRevision.InformationalVersion(text));
    }

    [Fact]
    public void Tisilia_carries_its_commit_only_when_built_by_CI()
    {
        // Directory.Build.props: IncludeSourceRevisionInInformationalVersion and EnableSourceLink are on for CI builds
        // (ContinuousIntegrationBuild, set where GITHUB_ACTIONS is true), which are the builds that get packed, and off elsewhere
        var assembly = typeof(SourceRevision).Assembly.Location;
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            var commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
            Assert.False(string.IsNullOrEmpty(commit), "GITHUB_SHA is not set");
            Assert.EndsWith("+" + commit, SourceRevision.InformationalVersion(assembly) ?? "", StringComparison.Ordinal);
            Assert.Equal(commit, SourceRevision.SourceLinkCommit(assembly));
            return;
        }

        Assert.Null(SourceRevision.InformationalVersion(assembly));
        Assert.Null(SourceRevision.SourceLinkCommit(assembly));
    }
}
