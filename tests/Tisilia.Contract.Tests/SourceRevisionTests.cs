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

    /// <summary>
    /// A minimal assembly with an embedded portable PDB that carries <paramref name="sourceLink"/> (none when null) and, with
    /// <paramref name="references"/>, the record of the assemblies it was compiled against that the C# compiler writes; with
    /// <paramref name="pdb"/> false, an assembly built without a PDB (<c>DebugType</c> <c>none</c>).
    /// </summary>
    private string WriteAssembly(string name, string? sourceLink, bool references = false, bool pdb = true)
    {
        var metadata = new MetadataBuilder();
        var assemblyName = metadata.GetOrAddString(name);
        metadata.AddModule(0, assemblyName, metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(assemblyName, new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.Sha1);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        DebugDirectoryBuilder? debug = null;
        if (pdb)
        {
            debug = new DebugDirectoryBuilder();
            var debugMetadata = new MetadataBuilder();
            if (sourceLink is not null)
            {
                debugMetadata.AddCustomDebugInformation(EntityHandle.ModuleDefinition, debugMetadata.GetOrAddGuid(SourceRevision.SourceLinkKind), debugMetadata.GetOrAddBlobUTF8(sourceLink));
            }

            if (references)
            {
                // one reference, as the compiler writes it: file name, aliases, flags (an assembly), time stamp, size and MVID
                var reference = new BlobBuilder();
                reference.WriteUTF8("Direct.dll");
                reference.WriteByte(0);
                reference.WriteByte(0);
                reference.WriteByte(1);
                reference.WriteUInt32(0);
                reference.WriteUInt32(0);
                reference.WriteGuid(Guid.NewGuid());
                debugMetadata.AddCustomDebugInformation(EntityHandle.ModuleDefinition, debugMetadata.GetOrAddGuid(SourceRevision.CompilationMetadataReferencesKind), debugMetadata.GetOrAddBlob(reference));
            }

            var pdbBuilder = new PortablePdbBuilder(debugMetadata, metadata.GetRowCounts(), default);
            var pdbBlob = new BlobBuilder();
            var pdbId = pdbBuilder.Serialize(pdbBlob);
            debug.AddCodeViewEntry(name + ".pdb", pdbId, pdbBuilder.FormatVersion);
            debug.AddEmbeddedPortablePdbEntry(pdbBlob, pdbBuilder.FormatVersion);
        }

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
    public void Projects_the_assembly_depends_on_are_checked_for_the_commit_through_the_deps_file()
    {
        const string commit = "b661016562c639606f38ee2c9f22845bec3f86c5";
        var map = "{\"documents\":{\"C:\\\\repo\\\\*\":\"https://raw.githubusercontent.com/owner/repo/" + commit + "/*\"}}";
        var module = WriteAssembly("Module", null, references: true);
        WriteAssembly("Direct", map);
        WriteAssembly("Transitive", map);
        WriteAssembly("Clean", null);
        WriteAssembly("Package", map);
        WriteAssembly("Unrelated", map);
        File.WriteAllText(Path.Combine(_dir, "App.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "App/1.0.0": { "dependencies": { "Module": "1.0.0", "Unrelated": "1.0.0" }, "runtime": { "App.dll": {} } },
                  "Module/1.0.0": { "dependencies": { "Direct": "1.0.0", "Clean": "1.0.0", "Package": "2.0.0" }, "runtime": { "Module.dll": {} } },
                  "Direct/1.0.0": { "dependencies": { "Transitive": "1.0.0" }, "runtime": { "Direct.dll": {} } },
                  "Transitive/1.0.0": { "runtime": { "Transitive.dll": {} } },
                  "Clean/1.0.0": { "runtime": { "Clean.dll": {} } },
                  "Package/2.0.0": { "runtime": { "lib/net10.0/Package.dll": {} } },
                  "Unrelated/1.0.0": { "runtime": { "Unrelated.dll": {} } }
                }
              },
              "libraries": {
                "App/1.0.0": { "type": "project" },
                "Module/1.0.0": { "type": "project" },
                "Direct/1.0.0": { "type": "project" },
                "Transitive/1.0.0": { "type": "project" },
                "Clean/1.0.0": { "type": "project" },
                "Package/2.0.0": { "type": "package" },
                "Unrelated/1.0.0": { "type": "project" }
              }
            }
            """);

        var stamped = SourceRevision.StampedProjectReferences(module);

        // the projects it depends on, also through another project; not a package, a project without the commit, a project it
        // does not depend on, or the module itself
        Assert.Equal(["Direct.dll", "Transitive.dll"], stamped.Select(reference => Path.GetFileName(reference.Path)));
        Assert.All(stamped, reference => Assert.Equal(commit, reference.SourceLinkCommit));
    }

    [Fact]
    public void The_projects_this_test_assembly_references_carry_the_commit_only_when_built_by_CI()
    {
        // the SDK's own deps file and real assemblies: this project references Tisilia's projects, which carry the commit only in
        // CI builds (see Tisilia_carries_its_commit_only_when_built_by_CI)
        var stamped = SourceRevision.StampedProjectReferences(typeof(SourceRevisionTests).Assembly.Location);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            var commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
            Assert.False(string.IsNullOrEmpty(commit), "GITHUB_SHA is not set");
            Assert.Contains(stamped, reference => Path.GetFileName(reference.Path) == "Tisilia.Generator.dll"
                && (reference.InformationalVersion ?? "").EndsWith("+" + commit, StringComparison.Ordinal));
            return;
        }

        Assert.Empty(stamped);
    }

    [Fact]
    public void Without_a_deps_file_that_lists_the_assembly_no_reference_is_reported()
    {
        var module = WriteAssembly("Module", null, references: true);
        WriteAssembly("Direct", "{\"documents\":{\"C:\\\\repo\\\\*\":\"https://raw.githubusercontent.com/owner/repo/b661016562c639606f38ee2c9f22845bec3f86c5/*\"}}");
        Assert.Empty(SourceRevision.StampedProjectReferences(module));

        // the deps file of another application, and one that is not JSON
        File.WriteAllText(Path.Combine(_dir, "Other.deps.json"), """{ "targets": { ".NETCoreApp,Version=v10.0": { "Other/1.0.0": { "dependencies": { "Direct": "1.0.0" }, "runtime": { "Other.dll": {} } }, "Direct/1.0.0": { "runtime": { "Direct.dll": {} } } } }, "libraries": { "Other/1.0.0": { "type": "project" }, "Direct/1.0.0": { "type": "project" } } }""");
        File.WriteAllText(Path.Combine(_dir, "Broken.deps.json"), "{ not json");
        Assert.Empty(SourceRevision.StampedProjectReferences(module));
    }

    [Fact]
    public void Only_a_module_whose_PDB_records_its_references_depends_on_their_commits()
    {
        // the record of the assemblies it was compiled against is what ties an assembly to the projects it references: built
        // without a PDB (DebugType none), or with a PDB that has no such record, it does not change when they do
        WriteAssembly("Direct", "{\"documents\":{\"C:\\\\repo\\\\*\":\"https://raw.githubusercontent.com/owner/repo/b661016562c639606f38ee2c9f22845bec3f86c5/*\"}}");
        File.WriteAllText(Path.Combine(_dir, "App.deps.json"), """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "App/1.0.0": { "dependencies": { "Module": "1.0.0" }, "runtime": { "App.dll": {} } },
                  "Module/1.0.0": { "dependencies": { "Direct": "1.0.0" }, "runtime": { "Module.dll": {} } },
                  "Direct/1.0.0": { "runtime": { "Direct.dll": {} } }
                }
              },
              "libraries": {
                "App/1.0.0": { "type": "project" },
                "Module/1.0.0": { "type": "project" },
                "Direct/1.0.0": { "type": "project" }
              }
            }
            """);

        Assert.False(SourceRevision.RecordsReferences(WriteAssembly("Module", null, pdb: false)));
        Assert.Empty(SourceRevision.StampedProjectReferences(Path.Combine(_dir, "Module.dll")));
        Assert.False(SourceRevision.RecordsReferences(WriteAssembly("Module", null)));
        Assert.Empty(SourceRevision.StampedProjectReferences(Path.Combine(_dir, "Module.dll")));
        Assert.True(SourceRevision.RecordsReferences(WriteAssembly("Module", null, references: true)));
        Assert.Equal(["Direct.dll"], SourceRevision.StampedProjectReferences(Path.Combine(_dir, "Module.dll")).Select(reference => Path.GetFileName(reference.Path)));
    }

    [Fact]
    public void The_compiler_records_the_references_of_this_test_assembly_in_its_PDB()
    {
        // the record the check looks for, in a PDB the C# compiler wrote (this project builds with the default portable PDB)
        Assert.True(SourceRevision.RecordsReferences(typeof(SourceRevisionTests).Assembly.Location));
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
