using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.TypeScript;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: MVC reads form values with the request culture (FormValueProviderFactory → CultureInfo.CurrentCulture, aspnetcore
/// v10.0.0), and the runtime writes the builtin scalars of such fields so that every culture reads the same value or refuses it.
/// Tisilia.CultureOracle, which keeps ICU (this project builds with InvariantGlobalization), posts one body — encoded by the
/// runtime from the exported contract — to a controller under every culture it knows: no field may bind to another value.
/// </summary>
public sealed class MvcFormCultureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-mvc-culture-" + Guid.NewGuid().ToString("N"));

    public MvcFormCultureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Request_culture_form_values_bind_to_the_same_value_or_are_refused_in_every_culture()
    {
        var runtime = Path.Combine(FixtureTests.RepoRoot(), "src", "frontend", "runtime", "dist", "index.js");
        Assert.True(File.Exists(runtime), "build the runtime first (npm run build): " + runtime);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Tisilia.CultureOracle.dll"), runtime, _dir }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        using var report = JsonDocument.Parse(await output);
        var root = report.RootElement;
        var cultures = root.GetProperty("cultures").GetInt32();
        Assert.True(cultures > 800, "ICU cultures: " + cultures);
        Assert.Equal(28, root.GetProperty("requestCulture").GetArrayLength());
        Assert.True(root.GetProperty("wrong").GetArrayLength() == 0, "bound to another value: " + root.GetProperty("wrong"));
        var exact = root.GetProperty("exact");
        // digits without a sign (a whole double is written out in full), dates, times, durations, Guids, characters and booleans read the
        // same in every culture
        foreach (var field in new[] { "i32", "u64", "decMax", "decWhole", "dblMax", "f32", "flag", "id", "letter", "utc", "unspecified", "local", "day", "lastDay", "time", "midnight", "offset", "span", "negativeSpan" })
        {
            Assert.True(exact.GetProperty(field).GetInt32() == cultures, $"{field}: {exact.GetProperty(field)} of {cultures}; refused in {root.GetProperty("refusedExamples")}");
        }
        // a sign — of the number or of its exponent — is refused where the culture's signs carry a direction mark (57 cultures on .NET 10)
        foreach (var field in new[] { "i8", "negative", "i64", "dec", "decTiny", "dbl", "dblTiny", "dblNegativeZero", "f32Fraction" })
        {
            Assert.True(exact.GetProperty(field).GetInt32() >= cultures - 60, $"{field}: {exact.GetProperty(field)} of {cultures}; refused in {root.GetProperty("refusedExamples")}");
        }
    }

    [Theory]
    [InlineData("string")]
    [InlineData("enum")]
    [InlineData("file")]
    [InlineData("none")]
    public async Task Only_builtin_scalars_MVC_reads_with_the_request_culture_carry_it(string mutation)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MixedFormController))));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-mixed-form");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var fields = Assert.IsType<Tisilia.Contract.FormRequestBody>(export.Index!.Operations["mixed-form"].RequestBody).Fields.ToDictionary(f => f.Name);
        Assert.True(fields["amount"].RequestCulture); Assert.True(fields["day"].RequestCulture);
        Assert.False(fields["title"].RequestCulture); Assert.False(fields["mode"].RequestCulture); Assert.False(fields["file"].RequestCulture);
        if (mutation == "none")
        {
            var bag = new DiagnosticBag();
            var files = TsGenerator.Generate(export.Index, new TsGenerationOptions { GeneratorVersion = "0.1.0-alpha", ModuleMode = ModuleMode.Bundler, ModuleImportResolver = (_, _) => null }, bag, out _);
            Assert.NotNull(files); Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
            var operations = files.Single(f => f.Path == "operations/index.ts").Content;
            var descriptors = string.Join("\n", operations.Split('\n').Where(l => l.Contains("name: \"", StringComparison.Ordinal)));
            Assert.True(operations.Contains("{ name: \"amount\", kind: \"value\", presence: \"optional\", repeated: false, requestCulture: true, scalar: \"decimal\" }", StringComparison.Ordinal), descriptors);
            Assert.Contains("{ name: \"title\", kind: \"value\", presence: \"optional\", repeated: false, rejectBlank: true, scalar: \"string\" }", operations, StringComparison.Ordinal);
            return;
        }

        var root = JsonNode.Parse(export.Text!)!;
        var body = root["operations"]!.AsArray().Single(o => (string?)o!["id"] == "mixed-form")!["requestBody"]!["fields"]!.AsArray();
        body.Single(f => (string?)f!["name"] == (mutation == "string" ? "title" : mutation == "enum" ? "mode" : "file"))!["requestCulture"] = true;
        var diagnostics = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), diagnostics);
        if (loaded is not null) { SemanticValidator.Validate(loaded, diagnostics, verifyHashes: false); }
        Assert.Contains(diagnostics.Items, d => d.Rule is "SV29" or "SV30");
    }

    private sealed class OneController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[NonController]
[ApiExplorerSettings(IgnoreApi = false)]
public sealed class MixedFormController : ControllerBase
{
    [HttpPost("/mixed-form")]
    [TisiliaOperation("mixed-form")]
    public ActionResult<string> Post([FromForm] decimal amount, [FromForm] DateOnly day, [FromForm] string? title, [FromForm] MixedMode mode, IFormFile file) => title ?? "";
}

public enum MixedMode { Draft, Final }
