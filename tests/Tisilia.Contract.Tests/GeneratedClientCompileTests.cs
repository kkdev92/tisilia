using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Codecs;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Additional;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.TypeScript;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The generated client compiles with the TypeScript compiler (the repository's typescript package and the runtime's built types),
/// not only through a bundler: index.ts re-exports models/index.ts, which TypeScript accepts only from a module (TS2306). A usage
/// file compiled with it checks that the generated signatures accept the request the test writes.
/// </summary>
public sealed class GeneratedClientCompileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-compile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_contract_of_builtin_scalars_only_generates_a_client_that_compiles()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "scalars-only");
        await using var app = builder.Build();
        app.MapGet("/count", () => TypedResults.Ok(1L)).WithTisiliaOperation("count");
        app.MapPost("/echo", ([FromBody] string text) => TypedResults.Ok(text)).WithTisiliaOperation("echo");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.All(export.Index!.Types.Values, t => Assert.IsType<Tisilia.Contract.PrimitiveShape>(t.Shape)); // no model of its own
        await CompileAsync(export.Index!);
    }

    [Fact]
    public async Task A_declared_server_time_zone_reaches_the_generated_DateTime_codecs()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => { o.ApiId = "server-zone-client"; o.DateTimes.ServerTimeZone = TimeZoneInfo.Utc; });
        await using var app = builder.Build();
        app.MapPost("/keys", (Dictionary<DateTime, int> keys) => TypedResults.Ok(keys.Count)).WithTisiliaOperation("keys");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!);
        // the binding's context is declared once and handed to the builtin codec
        var generated = string.Concat(Directory.EnumerateFiles(Path.Combine(_dir, "generated"), "*.ts", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Matches("""scalarCodec<DateTime>\("datetime", \{ id: "std\.datetime\.codec", .*, context: boundContext\d+ \}\);""", generated);
        Assert.Contains("\"serverTimeZone\": ", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_preservation_reaches_the_generated_codecs_of_both_directions()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve);
        builder.Services.AddTisilia(o => o.ApiId = "preserve-client");
        await using var app = builder.Build();
        app.MapPost("/nodes", (List<PreservedTreeNode> nodes) => TypedResults.Ok(nodes)).WithTisiliaOperation("nodes");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!);
        var generated = string.Concat(Directory.EnumerateFiles(Path.Combine(_dir, "generated"), "*.ts", SearchOption.AllDirectories).Select(File.ReadAllText));
        // the server reads references in the request's object and list, and writes them in the response's
        var codecs = generated.Split("export const ");
        Assert.Contains(codecs, c => c.Contains("= objectCodec<", StringComparison.Ordinal) && c.Contains("request: true,", StringComparison.Ordinal) && c.Contains("requestReferenceMetadata: true,", StringComparison.Ordinal));
        Assert.Contains(codecs, c => c.Contains("= objectCodec<", StringComparison.Ordinal) && c.Contains("response: true,", StringComparison.Ordinal) && c.Contains("referenceMetadata: true,", StringComparison.Ordinal));
        Assert.Contains(codecs, c => c.Contains("= arrayCodec<", StringComparison.Ordinal) && c.Contains("requestReferenceMetadata: true", StringComparison.Ordinal));
    }

    public sealed class PreservedTreeNode
    {
        public string Name { get; set; } = "";
        public PreservedTreeNode? Parent { get; set; }
    }

    [Fact]
    public async Task Form_member_names_with_path_or_index_delimiters_generate_a_client_that_compiles()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-names");
        await using var app = builder.Build();
        app.MapPost("/names", ([FromForm] FormMemberNameTests.DelimitedNames value) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("names");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!, """
            import { createFormNamesClient } from "./generated/index.js";
            const client = createFormNamesClient({ baseUrl: "http://localhost" });
            export const sent = client.names({ body: { "a.b": "1", "x[y]": "2", "p]q": "3", "q[": "4", "Inner.a.b": "5", Items: [{ "a.b": "6" }] } });
            """);
    }

    [Fact]
    public async Task Form_values_of_additional_codec_types_generate_a_client_that_compiles()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => { o.ApiId = "form-codecs"; o.Codecs.AddAdditionalCodecs(_dir); });
        await using var app = builder.Build();
        app.MapPost("/codecs", ([FromForm] FormCodecValueTests.CodecModel model, [FromForm] Version version) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("codecs");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var modules = Path.Combine(_dir, "modules", "tisilia-additional");
        Directory.CreateDirectory(modules);
        await File.WriteAllBytesAsync(Path.Combine(modules, AdditionalModule.ScriptFile), AdditionalModule.Script());
        await File.WriteAllBytesAsync(Path.Combine(modules, AdditionalModule.TypesFile), AdditionalModule.Types());
        // the values are brands in the generated models, written with a cast as Explorer's client snippets write them
        await CompileAsync(export.Index!, """
            import { createFormCodecsClient, type Int128, type UInt128, type BigInteger, type Half, type Uri, type IPAddress, type IPNetwork, type Version } from "./generated/index.js";
            const client = createFormCodecsClient({ baseUrl: "http://localhost" });
            export const sent = client.codecs({ body: {
              Signed: "1" as Int128, Unsigned: "2" as UInt128, Big: "-3" as BigInteger, Half: 1.5 as Half, Link: "https://example.test/" as Uri,
              Address: "::1" as IPAddress, Network: "10.0.0.0/8" as IPNetwork, Many: ["4" as Int128], Items: [{ Address: "10.0.0.1" as IPAddress }],
              version: "1.2" as Version,
            } });
            """, "../modules/tisilia-additional/tisilia-additional.js");
        // each value is written as its request codec's canonical text, as its parameter binder writes it
        var operations = await File.ReadAllTextAsync(Path.Combine(_dir, "generated", "operations", "index.ts"));
        foreach (var name in new[] { "Signed", "Half", "Link", "Many", "version" })
        {
            Assert.Matches($"name: \"{name}\", kind: \"value\",[^}}]*format: codecBinder<unknown>\\(\\(\\) => codecs\\.\\w+, \"query\"\\)\\.format", operations);
        }
    }

    [Fact]
    public async Task Form_dictionaries_generate_a_client_that_compiles()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-maps");
        await using var app = builder.Build();
        app.MapPost("/maps", ([FromForm] FormDictionaryTests.MapModel model) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("maps");
        app.MapPost("/root", ([FromForm] Dictionary<string, string> values) => TypedResults.NoContent()).DisableAntiforgery().WithTisiliaOperation("root");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!, """
            import { int64, guid } from "@kkdev92/tisilia-runtime";
            import { createFormMapsClient } from "./generated/index.js";
            const client = createFormMapsClient({ baseUrl: "http://localhost" });
            export const sent = client.maps({ body: {
              Labels: new Map([["first", "one"]]), Counts: new Map([[int64(-1n), int64(2n)]]),
              Ids: new Map([[guid("0f8fad5b-d9cb-469f-a165-70867728950e"), true]]), Modes: new Map([[1, 1]]),
            } });
            export const root = client.root({ body: { values: new Map([["a", "b"]]) } });
            """);
        var operations = await File.ReadAllTextAsync(Path.Combine(_dir, "generated", "operations", "index.ts"));
        Assert.Contains("{ name: \"Counts\", kind: \"map\", presence: \"optional\", repeated: false, key: \"int64\", scalar: \"int64\" }", operations, StringComparison.Ordinal);
        Assert.Contains("{ name: \"values\", kind: \"map\", presence: \"required\", repeated: false, wireName: \"\", key: \"string\", scalar: \"string\" }", operations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preserve_responses_generate_codecs_that_read_reference_metadata()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve);
        builder.Services.AddTisilia(o => o.ApiId = "preserved");
        await using var app = builder.Build();
        app.MapGet("/order", () => PreservedOrder.Sample()).WithTisiliaOperation("order");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        await CompileAsync(index);
        var codecs = await File.ReadAllTextAsync(Path.Combine(_dir, "generated", "codecs", "index.ts"));
        static string Base(string codecId) => codecId.EndsWith(".nullable", StringComparison.Ordinal) ? codecId[..^".nullable".Length] : codecId;
        bool Marked(string codecId)
        {
            var at = codecs.IndexOf("id: \"" + codecId + "\",", StringComparison.Ordinal);
            Assert.True(at >= 0, codecId);
            var end = codecs.IndexOf("export const", at, StringComparison.Ordinal);
            return codecs[at..(end < 0 ? codecs.Length : end)].Contains("referenceMetadata: true", StringComparison.Ordinal);
        }
        var root = ((JsonResponseBody)index.Operations["order"].Responses.Single(r => r.Status == 200).Body!).Use;
        var order = (ObjectShape)index.Types[root.TypeId].Shape;
        string Member(string name) => Base(order.Properties.Single(p => p.Name == name).Use.CodecId);
        // marked where System.Text.Json writes metadata: objects, List<T>, IReadOnlyList<T>, Dictionary<K,V>, the union and its variant
        Assert.True(Marked(Base(root.CodecId)));
        foreach (var name in new[] { "customer", "lines", "readOnly", "tags", "shape" })
        {
            Assert.True(Marked(Member(name)), name);
        }
        var union = (UnionShape)index.Types[index.Codecs[Member("shape")].TypeId].Shape;
        Assert.True(Marked(Base(union.Variants.Single().Use.CodecId)));
        // arrays, immutable collections, structs and JSON nodes carry none
        foreach (var name in new[] { "numbers", "fixed", "point", "dollar", "node" })
        {
            Assert.False(Marked(Member(name)), name);
        }
    }

    [Fact]
    public async Task Resuming_events_generate_a_client_that_reconnects_on_request()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "resuming");
        await using var app = builder.Build();
        app.MapGet("/ticks", () => TypedResults.ServerSentEvents(SseTests.Values(1, 2))).WithTisiliaEventResume().WithTisiliaOperation("ticks");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!, """
            import { createResumingClient } from "./generated/index.js";
            const client = createResumingClient({ baseUrl: "http://localhost" });
            export const ticks = client.ticksSubscribe(event => { void event.data; }, {}, { reconnect: { maxAttempts: 3, delayMs: 1000 } });
            """);
        var operations = await File.ReadAllTextAsync(Path.Combine(_dir, "generated", "operations", "index.ts"));
        Assert.Contains("kind: \"sse\", mediaType: \"text/event-stream\", dataFormat: \"json\"", operations, StringComparison.Ordinal);
        Assert.Contains(", resume: \"last-event-id\" }", operations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XML_bodies_generate_a_client_that_compiles()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddXmlSerializerFormatters().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController(typeof(XmlOrdersController))));
        builder.Services.AddTisilia(o => o.ApiId = "xml-client");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        await CompileAsync(export.Index!, """
            import { createXmlClientClient } from "./generated/index.js";
            const client = createXmlClientClient({ baseUrl: "http://localhost" });
            export const sent = client.xmlOrdersEcho({ body: { id: 7, Note: null, Lines: [{ Sku: "a", Quantity: 1 }, null], tag: ["x"], Color: 2, Access: 3 } });
            export const lines = client.xmlLines().then(result => result.kind === "response" ? result.data.map(line => line?.Quantity) : []);
            """);
        var generated = string.Concat(Directory.EnumerateFiles(Path.Combine(_dir, "generated"), "*.ts", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("= xmlElementCodec<", generated, StringComparison.Ordinal);
        Assert.Contains("= xmlItemsCodec<", generated, StringComparison.Ordinal);
        Assert.Contains("= xmlEnumCodec(", generated, StringComparison.Ordinal);
        Assert.Contains("kind: \"xml\", mediaType: \"application/xml\"", generated, StringComparison.Ordinal);
        Assert.Contains("root: { name: \"order\", ns: \"urn:shop\" }", generated, StringComparison.Ordinal);
    }

    private sealed class OnlyController(Type controller) : Microsoft.AspNetCore.Mvc.ApplicationParts.IApplicationFeatureProvider<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPart> parts, Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(System.Reflection.IntrospectionExtensions.GetTypeInfo(controller));
        }
    }

    private async Task CompileAsync(ContractIndex index, string? usage = null, string? moduleImport = null)
    {
        var bag = new DiagnosticBag();
        var files = TsGenerator.Generate(index, new TsGenerationOptions { GeneratorVersion = "0.1.0-alpha", ModuleMode = ModuleMode.Bundler, ModuleImportResolver = (_, _) => moduleImport }, bag, out _);
        Assert.NotNull(files); Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
        foreach (var file in files)
        {
            var path = Path.Combine(_dir, "generated", file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content);
        }
        if (usage is not null) { await File.WriteAllTextAsync(Path.Combine(_dir, "usage.ts"), usage); }

        var root = FixtureTests.RepoRoot();
        var runtimeTypes = Path.Combine(root, "src", "frontend", "runtime", "dist", "index.d.ts");
        Assert.True(File.Exists(runtimeTypes), "build the runtime first (npm run build): " + runtimeTypes);
        var tsconfig = new
        {
            compilerOptions = new
            {
                target = "ES2022",
                module = "ESNext",
                moduleResolution = "Bundler",
                strict = true,
                exactOptionalPropertyTypes = true,
                noUncheckedIndexedAccess = true,
                noEmit = true,
                skipLibCheck = true,
                paths = new Dictionary<string, string[]> { ["@kkdev92/tisilia-runtime"] = [runtimeTypes.Replace('\\', '/')] },
            },
            include = new[] { "generated/**/*.ts", "usage.ts" },
        };
        await File.WriteAllTextAsync(Path.Combine(_dir, "tsconfig.json"), JsonSerializer.Serialize(tsconfig));

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
        start.ArgumentList.Add(Path.Combine(root, "node_modules", "typescript", "bin", "tsc"));
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(Path.Combine(_dir, "tsconfig.json"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
