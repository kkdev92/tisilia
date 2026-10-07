using System.Text.Json.Nodes;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Portable;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Portable project loading and generation (SV40–SV42) on the frozen sample portable project (tests/fixtures/portable) and mutated copies of
/// it: token unions, writer uniqueness, imports and refs, tagged-union rules, productive recursion, ownership.
/// </summary>
public class PortableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-portable-" + Guid.NewGuid().ToString("N"));

    public PortableTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "portable"));
        var fixtures = Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures", "portable");
        File.Copy(Path.Combine(fixtures, "tisilia.portable.json"), Path.Combine(_dir, "tisilia.portable.json"));
        foreach (var file in Directory.GetFiles(fixtures, "*.definition.json"))
        {
            File.Copy(file, Path.Combine(_dir, "portable", Path.GetFileName(file)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ProjectPath => Path.Combine(_dir, "tisilia.portable.json");

    private JsonObject Definition(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "portable", name + ".definition.json")))!.AsObject();

    private void SaveDefinition(string name, JsonObject node) => File.WriteAllText(Path.Combine(_dir, "portable", name + ".definition.json"), node.ToJsonString(TisiliaJson.IndentedOptions));

    private JsonObject Project() => JsonNode.Parse(File.ReadAllText(ProjectPath))!.AsObject();

    private void SaveProject(JsonObject node) => File.WriteAllText(ProjectPath, node.ToJsonString(TisiliaJson.IndentedOptions));

    private static string Messages(DiagnosticBag bag) => string.Join("; ", bag.Items.Select(d => d.Rule + " " + d.Message));

    [Fact]
    public async Task Mixed_DateTime_portable_program_runs_all_Kinds_in_the_generated_module()
    {
        var definition = Definition("invoice");
        const string program = """{"op":"object","unknownMembers":"reject","members":[{"name":"stamp","presence":"required","node":{"op":"scalar","scalarId":"tisilia.datetime@0.1","representation":"native"}}]}""";
        definition["request"]!["program"] = JsonNode.Parse(program);
        definition["response"]!["program"] = JsonNode.Parse(program);
        SaveDefinition("invoice", definition);
        var project = Project();
        var invoice = project["models"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == "demo.portable.Invoice")!;
        invoice["shape"]!["properties"] = JsonNode.Parse("""[{"name":"stamp","use":{"typeId":"std.datetime","codecId":"std.datetime.codec","semanticNullable":false},"presence":"required"}]""");
        SaveProject(project);
        var bag = new DiagnosticBag();
        var plan = PortableCodegen.Plan(ProjectPath, null, bag);
        Assert.True(plan is not null, Messages(bag)); Assert.NotNull(plan); Assert.Empty(bag.Items);
        Assert.Contains("readonly \"stamp\": PortableDateTime;", plan.TypescriptFiles.Single(f => f.Path.EndsWith(".d.ts", StringComparison.Ordinal)).Content, StringComparison.Ordinal);
        Assert.Contains("System.DateTime Stamp", plan.CsharpFiles.Single(f => f.Path == "Domain.g.cs").Content, StringComparison.Ordinal);
        var module = Path.Combine(_dir, "mixed.mjs");
        await File.WriteAllTextAsync(module, plan.TypescriptFiles.Single(f => f.Path.EndsWith(".js", StringComparison.Ordinal)).Content);
        var script = "import * as p from " + System.Text.Json.JsonSerializer.Serialize(new Uri(module).AbsoluteUri) + ";\n" + """
            import assert from 'node:assert/strict';
            const ctx = { path: '' };
            for (const [text, kind] of [['2026-09-30T06:04:05.1234567Z','datetime-utc'], ['2026-09-30T06:04:05.1234567','datetime-unspecified'], ['2026-09-30T15:04:05.1234567+09:00','datetime-local-wire']]) {
              const wire = { kind: 'object', entries: [{ name: 'stamp', value: { kind: 'string', value: text } }] };
              const value = p.invoiceResponseDecode.decodeResponse(wire, ctx);
              assert.equal(value.stamp.kind, kind);
              assert.equal(typeof value.stamp.ticks, 'bigint');
              assert.deepEqual(p.invoiceRequestEncode.encodeRequest(value, ctx), wire);
            }
            assert.throws(() => p.invoiceRequestEncode.encodeRequest({ stamp: { kind: 'datetime-offset', ticks: 0n, offsetMinutes: 0 } }, ctx));
            assert.throws(() => p.invoiceRequestEncode.encodeRequest({ stamp: { kind: 'datetime-local-wire', ticks: 0n, offsetMinutes: 840 } }, ctx));
            console.log('checked');
            """;
        // A script file, not `node -e`: a version-manager shim may drop a multi-line -e argument and exit 0 without running it.
        var check = Path.Combine(_dir, "check.mjs");
        await File.WriteAllTextAsync(check, script);
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(check);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.Equal("checked", (await output).Trim());
    }

    [Fact]
    public void Sample_project_loads_and_generates_deterministically()
    {
        var bag = new DiagnosticBag();
        var plan = PortableCodegen.Plan(ProjectPath, null, bag);
        Assert.NotNull(plan);
        Assert.Empty(bag.Items);

        // 4 declared definitions + one synthesized per inline object/union model (Card, Cash, Circle, Customer, Line, Payment, Rect)
        var declared = plan!.Project.Definitions.Where(d => !d.Synthesized).Select(d => d.Definition.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var synthesized = plan.Project.Definitions.Where(d => d.Synthesized).Select(d => d.Definition.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["demo.portable.invoice", "demo.portable.pmoney", "demo.portable.shape", "demo.portable.tree"], declared);
        Assert.Equal(["demo.portable.Card", "demo.portable.Cash", "demo.portable.Circle", "demo.portable.Customer", "demo.portable.Line", "demo.portable.Payment", "demo.portable.Rect"], synthesized);

        // the request of PMoney reads a token union, so its canonical writer is the response program
        var pmoney = plan.Project.DefinitionsById["demo.portable.pmoney"];
        Assert.Same(pmoney.Definition.Response!.Program, pmoney.CanonicalWriter);
        Assert.Equal("kind", plan.Project.Discriminators["demo.portable.Shape"]);
        Assert.Equal("method", plan.Project.Discriminators["demo.portable.Payment"]);

        var files = plan.CsharpFiles.Select(f => f.Path).ToList();
        Assert.Contains("Domain.g.cs", files);
        Assert.Contains("Registrations.g.cs", files);
        foreach (var converter in new[] { "Pmoney", "Shape", "Invoice", "Tree", "Circle", "Rect", "Customer", "Payment", "Card", "Cash", "Line" })
        {
            Assert.Contains(converter + "PortableConverter.g.cs", files);
        }

        var domain = plan.CsharpFiles.First(f => f.Path == "Domain.g.cs").Content;
        Assert.Contains("public abstract record Shape;", domain);
        Assert.Contains("public sealed record Circle(decimal Radius, string? Label = null) : Shape;", domain);
        Assert.Contains("public sealed record Cash() : Payment;", domain);
        Assert.Contains("public sealed record Tree(int Value, Demo.Portable.Tree? Left, System.Collections.Generic.IReadOnlyList<Demo.Portable.Tree> Children);", domain);
        // every record carries its converter so System.Text.Json applies it wherever the type is serialized (type attribute precedence)
        Assert.Contains("[System.Text.Json.Serialization.JsonConverter(typeof(Demo.Portable.PmoneyPortableConverter))]", domain);
        Assert.Contains("[System.Text.Json.Serialization.JsonConverter(typeof(Demo.Portable.ShapePortableConverter))]", domain);
        Assert.Contains("[System.Text.Json.Serialization.JsonConverter(typeof(Demo.Portable.CirclePortableConverter))]", domain);

        var shape = plan.CsharpFiles.First(f => f.Path == "ShapePortableConverter.g.cs").Content;
        Assert.Contains("var probe = reader;", shape); // discriminator lookahead on a copy of the reader
        Assert.Contains("CirclePortableConverter.ReadRequest(ref reader, options)", shape);
        Assert.Contains("case Demo.Portable.Rect v1:", shape);
        Assert.Contains("RectPortableConverter.WriteResponse(writer, value, options)", shape);
        var circle = plan.CsharpFiles.First(f => f.Path == "CirclePortableConverter.g.cs").Content;
        Assert.Contains("writer.WriteString(\"kind\", \"circle\");", circle);
        Assert.Contains("discriminator 'kind' must be 'circle'", circle);

        var pmoneyConverter = plan.CsharpFiles.First(f => f.Path == "PmoneyPortableConverter.g.cs").Content;
        // a nullable optional member is always written (null as null) because the C# record cannot distinguish absence from null
        Assert.DoesNotContain("if (value.Note is not null)", pmoneyConverter);
        Assert.Contains("writer.WritePropertyName(\"note\");", pmoneyConverter);

        var registrations = plan.CsharpFiles.First(f => f.Path == "Registrations.g.cs").Content;
        Assert.Contains("Origin = CodecOrigin.Portable", registrations);
        // ordinal order of the referenced definition ids (synthesized model ids keep their casing, declared ids are lowercase)
        Assert.Contains("Dependencies = [typeof(Demo.Portable.Card), typeof(Demo.Portable.Cash), typeof(Demo.Portable.Customer), typeof(Demo.Portable.Line), typeof(Demo.Portable.Payment), typeof(Demo.Portable.PMoney), typeof(Demo.Portable.Shape)],", registrations);
        Assert.Contains("new TaggedUnionWire { Discriminator = \"kind\"", registrations);
        Assert.Contains("Shape = new LiteralWire { Value = new JsonStringValue { Value = \"circle\" } }", registrations);

        var js = plan.TypescriptFiles.First(f => f.Path.EndsWith(".js", StringComparison.Ordinal)).Content;
        Assert.DoesNotContain("import ", js);
        foreach (var export in new[] { "export const invoiceResponseDecode", "export const shapeRequestEncode", "export function treeDomainRule", "export const circleValidate", "export const paymentRequestInput", "export const customerResponseDecode" })
        {
            Assert.Contains(export, js);
        }

        foreach (var helper in new[] { "function parseFloatLexeme(", "function parseDateTimeOffset(", "function parseDuration(", "function decodeBase64(", "function validateChar(" })
        {
            Assert.Contains(helper, js);
        }

        var dts = plan.TypescriptFiles.First(f => f.Path.EndsWith(".d.ts", StringComparison.Ordinal)).Content;
        Assert.Contains("export type Shape = Circle | Rect;", dts);
        Assert.Contains("readonly \"kind\": \"circle\";", dts);
        Assert.Contains("readonly \"issued\": PortableDateTimeOffset;", dts);
        Assert.Contains("readonly \"payload\": Uint8Array;", dts);

        Assert.Equal("tisilia.codec-manifest", plan.CodecManifest["format"]!.GetValue<string>());
        var manifestEquivalences = plan.CodecManifest["equivalences"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("demo.portable.Customer.request", manifestEquivalences);
        Assert.Contains("demo.portable.Shape.response", manifestEquivalences);

        var again = PortableCodegen.Plan(ProjectPath, null, new DiagnosticBag())!;
        Assert.Equal(plan.CsharpFiles.Select(f => f.Content), again.CsharpFiles.Select(f => f.Content));
        Assert.Equal(plan.TypescriptFiles.Select(f => f.Content), again.TypescriptFiles.Select(f => f.Content));
    }

    [Fact]
    public void Response_program_with_a_token_union_is_rejected_SV42()
    {
        var def = Definition("pmoney");
        def["response"]!["program"]!["members"]![0]!["node"] = JsonNode.Parse("""{"op":"token-union","branches":[{"token":"number","node":{"op":"scalar","scalarId":"tisilia.decimal@0.1","representation":"native"}},{"token":"string","node":{"op":"scalar","scalarId":"tisilia.decimal@0.1","representation":"string"}}]}""");
        SaveDefinition("pmoney", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("token unions are not allowed in response programs", StringComparison.Ordinal));
    }

    [Fact]
    public void Token_union_read_without_a_response_writer_is_rejected_SV41()
    {
        var def = Definition("pmoney");
        def.Remove("response");
        SaveDefinition("pmoney", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV41" && d.Message.Contains("no response program", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlapping_token_branches_and_unknown_scalars_are_rejected()
    {
        var def = Definition("pmoney");
        var union = def["request"]!["program"]!["members"]![0]!["node"]!.AsObject();
        union["branches"]![1]!["token"] = "number";
        SaveDefinition("pmoney", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV41" && d.Message.Contains("more than one read branch", StringComparison.Ordinal));

        def = Definition("pmoney");
        def["response"]!["program"]!["members"]![1]!["node"]!["scalarId"] = "tisilia.text@0.1";
        SaveDefinition("pmoney", def);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("is not a builtin scalar", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolved_ref_and_different_domain_ref_are_rejected_SV40_SV42()
    {
        var def = Definition("invoice");
        def["request"]!["program"]!["members"]![2]!["node"]!["definitionId"] = "demo.portable.nope";
        SaveDefinition("invoice", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV40" && d.Message.Contains("ref to unknown definition 'demo.portable.nope'", StringComparison.Ordinal));

        // the 'total' position expects PMoney but the ref produces Tree (differing-domain branches/refs are rejected)
        def = Definition("invoice");
        def["request"]!["program"]!["members"]![2]!["node"]!["definitionId"] = "demo.portable.tree";
        SaveDefinition("invoice", def);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("produces domain 'demo.portable.Tree' but this position expects 'demo.portable.PMoney'", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_productive_recursion_is_rejected_SV40()
    {
        // left: nullable(ref tree) → ref tree (a required direct self reference has no finite value)
        var def = Definition("tree");
        foreach (var direction in new[] { "request", "response" })
        {
            def[direction]!["program"]!["members"]![1]!["node"] = JsonNode.Parse("""{"op":"ref","definitionId":"demo.portable.tree"}""");
        }

        SaveDefinition("tree", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV40" && d.Message.Contains("non-productive recursion", StringComparison.Ordinal));
    }

    [Fact]
    public void Tagged_union_rules_are_enforced_SV42()
    {
        // a branch must not declare the discriminator: the union writes it
        var def = Definition("shape");
        var circle = def["request"]!["program"]!["branches"]![0]!["node"]!["members"]!.AsArray();
        circle.Insert(0, JsonNode.Parse("""{"name":"kind","presence":"required","node":{"op":"scalar","scalarId":"tisilia.string@0.1","representation":"native"}}"""));
        SaveDefinition("shape", def);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("is the discriminator of the enclosing tagged union", StringComparison.Ordinal));

        // every variant of the union model needs a branch
        def = Definition("shape");
        def["response"]!["program"]!["branches"]!.AsArray().RemoveAt(1);
        SaveDefinition("shape", def);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("variant 'rect' of union model 'demo.portable.Shape' has no branch", StringComparison.Ordinal));

        // the variant model must declare the discriminator as its first property (a required std.string)
        var project = Project();
        var rect = project["models"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == "demo.portable.Rect")!;
        rect["shape"]!["properties"]!.AsArray().RemoveAt(0);
        SaveProject(project);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("must declare the discriminator 'kind' as its first property", StringComparison.Ordinal));
    }

    [Fact]
    public void One_model_has_one_program_per_direction_SV42()
    {
        // Invoice.customer is read by the inline Customer program; a second, different inline program for the same model is a conflict
        var def = Definition("invoice");
        var members = def["request"]!["program"]!["members"]!.AsArray();
        var customer = JsonNode.Parse(members[1]!["node"]!.ToJsonString())!.AsObject();
        customer["unknownMembers"] = "reject";
        var lines = members.First(m => m!["name"]!.GetValue<string>() == "lines")!;
        lines["node"]!["element"] = customer; // an array element that is a Customer program with a different policy
        var project = Project();
        var lineList = project["models"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == "demo.portable.LineList")!;
        lineList["shape"]!["element"] = JsonNode.Parse("""{"typeId":"demo.portable.Customer","codecId":"demo.portable.Customer.codec","semanticNullable":false}""");
        SaveDefinition("invoice", def);
        SaveProject(project);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV42" && d.Message.Contains("is read by different programs", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_digest_mismatch_duplicate_ids_and_bad_output_paths_are_rejected_SV40_SV38()
    {
        var project = Project();
        File.WriteAllText(Path.Combine(_dir, "other.json"), "{}");
        project["imports"] = new JsonArray(new JsonObject { ["path"] = "other.json", ["digest"] = "sha256:" + new string('0', 64) });
        SaveProject(project);
        var bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV40" && d.Message.Contains("digest", StringComparison.Ordinal));

        // an import that declares a definition id of this project collides (an import conflict)
        var importDir = Path.Combine(_dir, "import");
        Directory.CreateDirectory(Path.Combine(importDir, "portable"));
        File.Copy(Path.Combine(_dir, "portable", "pmoney.definition.json"), Path.Combine(importDir, "portable", "pmoney.definition.json"));
        var imported = Project();
        imported["projectId"] = "demo.imported";
        imported["definitions"] = new JsonArray("portable/pmoney.definition.json");
        imported["models"] = new JsonArray(JsonNode.Parse(imported["models"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == "demo.portable.PMoney")!.ToJsonString()));
        imported["equivalences"] = new JsonArray(imported["equivalences"]!.AsArray().Where(e => e!["domainTypeId"]!.GetValue<string>() == "demo.portable.PMoney").Select(e => JsonNode.Parse(e!.ToJsonString())!).ToArray());
        var importPath = Path.Combine(importDir, "tisilia.portable.json");
        File.WriteAllText(importPath, imported.ToJsonString(TisiliaJson.IndentedOptions));
        project = Project();
        project["imports"] = new JsonArray(new JsonObject { ["path"] = "import/tisilia.portable.json", ["digest"] = TisiliaHash.Sha256OfBytes(File.ReadAllBytes(importPath)) });
        SaveProject(project);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Rule == "SV40" && d.Message.Contains("declared more than once in the import closure", StringComparison.Ordinal));

        project = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures", "portable", "tisilia.portable.json")))!.AsObject();
        project["output"]!["typescript"] = "../outside";
        SaveProject(project);
        bag = new DiagnosticBag();
        Assert.Null(PortableCodegen.Plan(ProjectPath, null, bag));
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.SchemaViolation);
    }

    [Fact]
    public void Writing_refuses_to_overwrite_files_it_does_not_own()
    {
        var bag = new DiagnosticBag();
        var plan = PortableCodegen.Plan(ProjectPath, null, bag);
        Assert.NotNull(plan);
        var csharp = plan!.Project.CsharpOutput;
        Directory.CreateDirectory(csharp);
        File.WriteAllText(Path.Combine(csharp, "Domain.g.cs"), "// hand written");
        Assert.False(PortableCodegen.Write(plan, force: false, bag));
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.OwnershipConflict && d.Message.Contains("not owned", StringComparison.Ordinal));
        Assert.Equal("// hand written", File.ReadAllText(Path.Combine(csharp, "Domain.g.cs")));

        File.Delete(Path.Combine(csharp, "Domain.g.cs"));
        bag = new DiagnosticBag();
        Assert.True(PortableCodegen.Write(plan, force: false, bag), Messages(bag));
        Assert.True(File.Exists(Path.Combine(csharp, PortableOwnership.FileName)));
        // a second generation replaces its own unchanged files; an edited owned file needs --force
        Assert.True(PortableCodegen.Write(plan, force: false, new DiagnosticBag()));
        File.AppendAllText(Path.Combine(csharp, "Domain.g.cs"), "// edited");
        bag = new DiagnosticBag();
        Assert.False(PortableCodegen.Write(plan, force: false, bag));
        Assert.Contains(bag.Items, d => d.Message.Contains("edited after generation", StringComparison.Ordinal));
        Assert.True(PortableCodegen.Write(plan, force: true, new DiagnosticBag()));
    }

    [Fact]
    public void A_crlf_checkout_of_the_generated_files_is_not_an_edit_and_the_module_is_pinned()
    {
        var bag = new DiagnosticBag();
        var plan = PortableCodegen.Plan(ProjectPath, null, bag);
        Assert.NotNull(plan);
        Assert.True(PortableCodegen.Write(plan!, force: false, bag), Messages(bag));
        // the module script is hashed into the contract byte for byte: its folder gets a .gitattributes naming the generated files
        var attributes = File.ReadAllText(Path.Combine(plan!.Project.TypescriptOutput, ".gitattributes"));
        Assert.All(plan.TypescriptFiles, f => Assert.Contains("/" + f.Path + " -text\n", attributes, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(plan.Project.CsharpOutput).Concat(plan.TypescriptFiles.Select(f => Path.Combine(plan.Project.TypescriptOutput, f.Path))))
        {
            LineEndingsTests.CheckOutWithCrLf(file);
        }

        var domain = Path.Combine(plan.Project.CsharpOutput, "Domain.g.cs");
        var checkedOut = File.ReadAllBytes(domain);
        bag = new DiagnosticBag();
        Assert.True(PortableCodegen.Write(plan, force: false, bag), Messages(bag));
        // C# with the same text is left as git wrote it; the module script is restored byte for byte (the contract records its bytes)
        Assert.Equal(checkedOut, File.ReadAllBytes(domain));
        Assert.All(plan.TypescriptFiles, f => Assert.Equal(System.Text.Encoding.UTF8.GetBytes(f.Content), File.ReadAllBytes(Path.Combine(plan.Project.TypescriptOutput, f.Path))));
        // a .gitattributes of the user's own is left alone
        File.WriteAllText(Path.Combine(plan.Project.TypescriptOutput, ".gitattributes"), "* text eol=lf\n");
        Assert.True(PortableCodegen.Write(plan, force: false, new DiagnosticBag()));
        Assert.Equal("* text eol=lf\n", File.ReadAllText(Path.Combine(plan.Project.TypescriptOutput, ".gitattributes")));
    }
}
