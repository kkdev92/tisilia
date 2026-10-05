using Tisilia.Generator.Closure;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

public class FixtureTests
{
    /// <summary>Locates the repository root (the directory containing src/backend/Tisilia.slnx) from the test output directory.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Tisilia.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Sample_contract_fixture_is_written_deterministically()
    {
        var text = SampleContracts.UsersApi().BuildText();
        Assert.Equal(text, SampleContracts.UsersApi().BuildText());
        var path = Path.Combine(RepoRoot(), "tests", "fixtures", "sample-api.contract.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.LoadFile(path, bag);
        Assert.NotNull(loaded);
        Assert.NotNull(SemanticValidator.Validate(loaded!, bag));
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void Generated_codecs_declare_each_bound_context_once()
    {
        // a binding's context (a converter instance's settings, the ids an environment-bound codec accepts) is one module-level constant
        // shared by every method of its codecs, not a literal repeated in each of them
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(SampleContracts.UsersApi().BuildText(), bag)!;
        var index = SemanticValidator.Validate(loaded, bag)!;
        var options = new Generator.TypeScript.TsGenerationOptions
        {
            ModuleMode = Documents.ModuleMode.NodeNext,
            GeneratorVersion = "test",
            ModuleImportResolver = (_, artifact) => "./" + artifact.Path,
        };
        var codecs = Generator.TypeScript.TsGenerator.Generate(index, options, bag, out _)!.Single(f => f.Path == "codecs/index.ts").Content;
        Assert.Empty(bag.Items);
        const string declaration = "const boundContext1: Readonly<Record<string, string>> = { \"currency\": \"JPY\" };";
        Assert.Single(codecs.Split('\n'), line => line == declaration);
        Assert.DoesNotContain("boundContext2", codecs, StringComparison.Ordinal);
        Assert.DoesNotContain("withContext(context, {", codecs, StringComparison.Ordinal);
        Assert.True(codecs.Split("withContext(context, boundContext1)").Length > 2, "the methods of the Money codec share the constant");
        Assert.True(codecs.IndexOf(declaration, StringComparison.Ordinal) < codecs.IndexOf("withContext(context, boundContext1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Models_may_be_named_like_runtime_types_and_enum_keys_never_set_the_prototype()
    {
        // an enum Duration next to a TimeSpan member: the models file imports the runtime's Duration under an alias, the codecs file
        // refers to the enum as models.Duration and the enum's codec gets a number instead of taking durationCodec from the scalar
        var b = SampleContracts.UsersApi();
        var plan = b.EnumOf("demo.Duration.number", "Duration", "Demo.Duration", "int32", [new Contract.EnumMember { Name = "__proto__", Value = "1" }, new Contract.EnumMember { Name = "Yearly", Value = "2" }], flags: false, stringForm: false);
        b.ObjectOf("demo.Plan", "PlanResponse", "Demo.Plan",
        [
            new Generator.Building.PropertySpec("plan", plan, Presence.Required, Presence.Required, Presence.Required),
            new Generator.Building.PropertySpec("length", b.Scalar("duration"), Presence.Required, Presence.Required, Presence.Required),
        ], Builtins.NamesOrdinal, Builtins.DuplicatesReject);
        var bag = new DiagnosticBag();
        var index = SemanticValidator.Validate(ContractLoader.Load(b.BuildText(), bag)!, bag, verifyHashes: false)!;
        var options = new Generator.TypeScript.TsGenerationOptions { ModuleMode = Documents.ModuleMode.NodeNext, GeneratorVersion = "test", ModuleImportResolver = (_, artifact) => "./" + artifact.Path };
        var files = Generator.TypeScript.TsGenerator.Generate(index, options, bag, out _);
        Assert.DoesNotContain(bag.Items, d => d.Severity == DiagnosticSeverity.Error);
        var models = files!.Single(f => f.Path == "models/index.ts").Content;
        var codecs = files!.Single(f => f.Path == "codecs/index.ts").Content;
        Assert.Contains("Duration as TisiliaDuration", models, StringComparison.Ordinal);
        Assert.Contains("readonly length: TisiliaDuration;", models, StringComparison.Ordinal);
        Assert.Contains("export type Duration = number;", models, StringComparison.Ordinal);
        Assert.Contains("[\"__proto__\"]: 1,", models, StringComparison.Ordinal);
        Assert.Contains("export const durationCodec: Codec<Duration> = scalarCodec<Duration>(\"duration\"", codecs, StringComparison.Ordinal);
        Assert.Contains("export const durationCodec2: Codec<models.Duration> = enumCodec(", codecs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name", "name")]
    [InlineData("名前", "名前")] // ECMAScript identifiers include Unicode letters
    [InlineData("a-b", "\"a-b\"")]
    [InlineData("1st", "\"1st\"")]
    [InlineData("class", "\"class\"")]
    [InlineData("", "\"\"")]
    [InlineData("quote\"back\\slash", "\"quote\\\"back\\\\slash\"")]
    public void Property_keys_are_bare_identifiers_or_quoted(string name, string key) => Assert.Equal(key, Generator.TypeScript.TsNames.PropertyKey(name));

    [Fact]
    public void Object_literal_keys_never_set_the_prototype()
    {
        Assert.Equal("[\"__proto__\"]", Generator.TypeScript.TsNames.ObjectLiteralKey("__proto__"));
        Assert.Equal("\"constructor\"", Generator.TypeScript.TsNames.ObjectLiteralKey("constructor"));
        Assert.Equal("赤", Generator.TypeScript.TsNames.ObjectLiteralKey("赤"));
    }

    [Fact]
    public void Closure_reaches_only_used_definitions_and_sorts_deterministically()
    {
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(SampleContracts.UsersApi().BuildText(), bag)!;
        var index = SemanticValidator.Validate(loaded, bag)!;
        var closure = ContractClosure.Compute(index, ["users.get"]);
        Assert.Contains(("types", "demo.UserResponse"), closure.RegistryRefs.Select(r => (r.Registry.ToString().ToLowerInvariant(), r.Id)));
        Assert.DoesNotContain(closure.RegistryRefs, r => r.Id == "demo.UserPutRequest");
        Assert.Contains(SampleContracts.MoneyModuleId, closure.ModuleIds);
        Assert.Contains(SampleContracts.ProfileId, closure.ProfileIds);
        var record = closure.ToRecord(loaded.Document.SemanticHash, closure.DeclaredModuleArtifacts(), [new Artifact { Target = ArtifactTarget.Browser, Path = "dist/client.js", Digest = "sha256:" + new string('b', 64) }],
            new RuntimeMatrix { Dotnet = "10.0.12", Aspnetcore = "10.0.12", Stj = "10.0.0", Typescript = "6.0.3", Node = "24.13.1", Os = "windows", Architecture = "arm64" }, [new NameValue { Name = "culture", Value = "invariant" }], Limits.Default);
        Assert.Equal(record.RegistryRefs.Select(r => r.Registry + "|" + r.Id).ToList(), record.RegistryRefs.Select(r => r.Registry + "|" + r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
        var node = System.Text.Json.JsonSerializer.SerializeToNode(record, TisiliaJson.Options)!.AsObject();
        Assert.True(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.ClosureRecord, node, bag), string.Join("\n", bag.Items));
        var digest = Generator.Canonical.TisiliaHash.ClosureDigest(node);
        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);
        Assert.Equal(digest, Generator.Canonical.TisiliaHash.ClosureDigest(node));
    }
}
