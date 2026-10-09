using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class AdoptionPlanTests
{
    [Theory]
    [InlineData(DateTimeWire.Utc)]
    [InlineData(DateTimeWire.Unspecified)]
    [InlineData(DateTimeWire.Local)]
    [InlineData(DateTimeWire.Mixed)]
    public async Task DX06_DX07_JSON_Kind_and_route_query_header_binders_keep_their_distinct_contracts(DateTimeWire wire)
    {
        await using var json = await App(a => a.MapPost("/time", ([FromBody] UndeclaredTime value) => value).WithTisiliaOperation("time"), options: o => o.DateTimes.Default = wire);
        Success(json);
        var report = json.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.Equal(wire.ToString(), report.DateTimeDefault); Assert.Equal("supported", Assert.Single(report.Operations).Readiness);
        await using var http = await App(a =>
        {
            a.MapGet("/route/{at}", (DateTime at) => TypedResults.NoContent()).WithTisiliaOperation("route");
            a.MapGet("/query", (DateTime at) => TypedResults.NoContent()).WithTisiliaOperation("query");
            a.MapGet("/header", ([FromHeader] DateTime at) => TypedResults.NoContent()).WithTisiliaOperation("header");
        }, options: o => o.DateTimes.Default = wire);
        var result = Export(http);
        if (wire == DateTimeWire.Local)
        {
            Assert.True(result.Diagnostics.HasErrors); Assert.Null(result.Root);
            foreach (var id in new[] { "route", "query", "header" })
            {
                Assert.Contains(result.Diagnostics.Items, d => d.Message.Contains("AdjustToUniversal", StringComparison.Ordinal) && d.RelatedIds.Contains(id));
            }
        }
        else { Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Items)); }
    }

    [Fact]
    public async Task DX06_Custom_DateTime_converter_requires_its_existing_binding_without_inventing_UTC()
    {
        await using var app = await App(a => a.MapGet("/time", () => new UndeclaredTime(DateTime.MinValue)).WithTisiliaOperation("time"),
            b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new CustomTimeConverter())));
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        var operation = Assert.Single(report.Operations); Assert.NotEqual("supported", operation.Readiness);
        Assert.Contains(operation.Diagnostics, d => d.Message.Contains("custom converter", StringComparison.Ordinal) && d.Message.Contains("no registered", StringComparison.Ordinal));
        Assert.Null(report.DateTimeDefault); Assert.Contains("mixed Kind", report.DateTimeScope, StringComparison.Ordinal);
    }

    private sealed class CustomTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue("application-specific-date");
    }

    [Fact]
    public async Task EV03_EV11_Mixed_JSON_binary_evidence_is_explicitly_codec_only()
    {
        await using var app = await App(a => a.MapGet("/mixed", () => Results.File(AdoptionFixture.Bytes, "application/pdf"))
            .Produces<FileContentResult>(200, "application/pdf").Produces<long>(400).WithTisiliaOperation("mixed"));
        var index = Success(app).Index!;
        var closure = Tisilia.Generator.Closure.ContractClosure.Compute(index, ["mixed"]);
        var (evidence, _) = ConformanceTests.SyntheticEvidence(index, closure);
        var path = Path.Combine(Path.GetTempPath(), "tisilia-mixed-evidence-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, evidence.ToJsonString());
        try
        {
            var checkedEvidence = EvidenceValidator.Check(path, index, new HashSet<string> { "ci" }, new DiagnosticBag());
            Assert.True(checkedEvidence.Valid, string.Join(",", checkedEvidence.ReasonCodes));
            Assert.Equal(CoverageStatus.Qualified, Assert.Single(EvidenceValidator.Coverage(index, [checkedEvidence])).Status);
            var bag = new DiagnosticBag();
            var generated = Tisilia.Generator.TypeScript.TsGenerator.Generate(index, new Tisilia.Generator.TypeScript.TsGenerationOptions
            { GeneratorVersion = "0.1.0-alpha", ModuleMode = ModuleMode.NodeNext, ModuleImportResolver = (_, _) => null }, bag, out _);
            Assert.NotNull(generated); Assert.False(bag.HasErrors);
            Assert.Contains(generated, f => f.Content.Contains("HTTP routing and binary transfer are unobserved", StringComparison.Ordinal));
            Assert.All(SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 1, CasesPerCodec = 2 }).Cases, c => Assert.DoesNotContain("http", c.Category, StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BD31_Distinct_concrete_binary_media_do_not_collide_in_case_identifiers()
    {
        await using var app = await App(a => a.MapGet("/file", () => Results.File(AdoptionFixture.Bytes, "application/a+b"))
            .Produces<FileContentResult>(200, "application/a+b", "application/a.b", "application/vnd_example").WithTisiliaOperation("file"));
        var result = Success(app); var responses = result.Index!.Operations["file"].Responses;
        Assert.Equal(3, responses.Count); Assert.Equal(3, responses.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RT32_Duplicate_operation_IDs_and_multiple_methods_are_not_silently_merged(bool methods)
    {
        await using var app = await App(a =>
        {
            if (methods) { a.MapMethods("/many", ["GET", "POST"], () => TypedResults.NoContent()).WithTisiliaOperation("duplicate"); }
            else { a.MapGet("/one", () => TypedResults.NoContent()).WithTisiliaOperation("duplicate"); a.MapGet("/two", () => TypedResults.NoContent()).WithTisiliaOperation("duplicate"); }
        });
        var result = Export(app); Assert.True(result.Diagnostics.HasErrors); Assert.Null(result.Root);
    }

    [Fact]
    public async Task EV10_Binary_only_generates_normally_and_qualified_only_explains_zero_codec_coverage()
    {
        await using var app = await App(a => a.MapGet("/file", () => Results.File(AdoptionFixture.Bytes, "application/pdf")).Produces<FileContentResult>(200, "application/pdf").WithTisiliaOperation("file"));
        var result = Success(app);
        var directory = Path.Combine(Path.GetTempPath(), "tisilia-binary-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var contractPath = Path.Combine(directory, "contract.json"); File.WriteAllText(contractPath, result.Text);
            var config = new ConfigDocument
            {
                Format = TisiliaJson.Formats.Config,
                Version = TisiliaJson.DraftVersion,
                ApiId = "plan-tests",
                Contract = "contract.json",
                Output = "generated",
                Target = new ConfigTarget { TypescriptMinimumMajor = 6, EcmaScript = "ES2022", ModuleMode = ModuleMode.NodeNext },
                Selection = "explicit",
                CoveragePolicy = CoveragePolicy.Development,
                Modules = [],
                PortableProjects = [],
                Limits = Limits.Default,
                Nuxt = new NuxtConfig { Enabled = false, Hydration = "browser-safe-only", SharedCache = false },
            };
            var loaded = new Pipeline.LoadedConfig(config, directory, contractPath, Path.Combine(directory, "generated"));
            var normalBag = new DiagnosticBag(); Assert.NotNull(Pipeline.BuildPlan(loaded, normalBag)); Assert.False(normalBag.HasErrors, string.Join("\n", normalBag.Items));
            var qualifiedBag = new DiagnosticBag(); Assert.Null(Pipeline.BuildPlan(loaded with { Config = config with { CoveragePolicy = CoveragePolicy.QualifiedOnly } }, qualifiedBag));
            Assert.Contains(qualifiedBag.Items, d => d.Message.Contains("codec-not-applicable", StringComparison.Ordinal) && d.Message.Contains("http-unobserved", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<WebApplication> App(Action<WebApplication> map, Action<WebApplicationBuilder>? configure = null, Action<TisiliaOptions>? options = null)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders();
        b.Services.AddTisilia(o => { o.ApiId = "plan-tests"; options?.Invoke(o); });
        configure?.Invoke(b);
        var app = b.Build(); map(app); await app.StartAsync(); return app;
    }
    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
    private static ExportResult Success(WebApplication app)
    {
        var result = Export(app); Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Items)); Assert.NotNull(result.Index); return result;
    }

    [Fact]
    public async Task Preserve_allows_builtin_scalar_JSON_without_reference_metadata()
    {
        await using var app = await App(a =>
        {
            a.MapPost("/number", ([FromBody] long value) => TypedResults.Ok(value)).WithTisiliaOperation("number");
            a.MapPost("/nullable", ([FromBody] long? value) => value).WithTisiliaOperation("nullable");
            a.MapPost("/text", ([FromBody] string value) => TypedResults.Ok(value)).WithTisiliaOperation("text");
            a.MapPost("/bytes", ([FromBody] byte[] value) => TypedResults.Ok(value)).WithTisiliaOperation("bytes");
        }, b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var (path, json) in new[] { ("/number", "9007199254740993"), ("/nullable", "null"), ("/text", "\"hello\""), ("/bytes", "\"AP8=\"") })
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(path, content);
            response.EnsureSuccessStatusCode();
            Assert.Equal(json, await response.Content.ReadAsStringAsync());
        }
        var exported = Success(app);
        Assert.Contains("\"referenceHandling\": \"Preserve\"", exported.Text!, StringComparison.Ordinal);
        Assert.Equal(4, exported.OperationCount);
    }

    [Fact]
    public async Task Preserve_marks_where_the_server_writes_reference_metadata_and_custom_reference_handlers_stay_refused()
    {
        await using var structured = await App(a =>
        {
            a.MapGet("/object", () => new { value = 1 }).WithTisiliaOperation("object");
            a.MapGet("/array", () => new[] { 1, 2 }).WithTisiliaOperation("array");
            a.MapGet("/list", () => new List<int> { 1, 2 }).WithTisiliaOperation("list");
        }, b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve));
        var index = Success(structured).Index!;
        bool Marked(string op)
        {
            var body = (Tisilia.Contract.JsonResponseBody)index.Operations[op].Responses.Single().Body!;
            return index.Wires[index.Codecs[body.Use.CodecId].Capabilities.Response!.Wire.WireId].Shape
                is Tisilia.Contract.ObjectWire { ReferenceMetadata: true } or Tisilia.Contract.ArrayWire { ReferenceMetadata: true };
        }
        // System.Text.Json writes $id on an object and a List<T> inside {"$id","$values"}; an array carries no metadata
        Assert.True(Marked("object"));
        Assert.False(Marked("array"));
        Assert.True(Marked("list"));
        await using var custom = await App(a => a.MapGet("/number", () => 1).WithTisiliaOperation("number"),
            b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = new UnknownReferenceHandler()));
        Assert.Contains(Export(custom).Diagnostics.Items, d => d.Code == TisiliaCodes.ReferencePreserve && d.Message.Contains("custom ReferenceHandler", StringComparison.Ordinal));
    }

    private sealed class UnknownReferenceHandler : ReferenceHandler
    {
        public override ReferenceResolver CreateResolver() => throw new NotSupportedException();
    }

    [Fact]
    public async Task BD30_Bodyless_and_binary_do_not_depend_on_unused_Preserve()
    {
        await using var app = await App(a =>
        {
            a.MapGet("/file", () => Results.File(AdoptionFixture.Bytes, "application/pdf")).Produces<FileContentResult>(200, "application/pdf").WithTisiliaOperation("file");
            a.MapGet("/none", () => TypedResults.NoContent()).WithTisiliaOperation("none");
        }, b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve));
        var result = Success(app);
        Assert.Empty(result.Index!.Profiles);
        Assert.All(EvidenceValidator.Coverage(result.Index, []), c => { Assert.Equal(CoverageStatus.Unqualified, c.Status); Assert.Contains("codec-not-applicable", c.ReasonCodes); Assert.Contains("http-unobserved", c.ReasonCodes); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BD31_Equivalent_file_markers_normalize_but_conflicting_JSON_is_rejected(bool conflict)
    {
        await using var app = await App(a =>
        {
            var e = a.MapGet("/file", () => Results.File(AdoptionFixture.Bytes, "application/pdf"))
                .Produces<FileContentResult>(200, "application/json").Produces<FileStreamResult>(200, "application/json").WithTisiliaOperation("file");
            if (conflict) { e.Produces<RouteObservation>(200, "application/json"); }
        });
        var result = Export(app);
        if (conflict) { Assert.Contains(result.Diagnostics.Items, d => d.Rule == "SV28"); Assert.Null(result.Root); }
        else { Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Items)); Assert.Single(result.Index!.Operations["file"].Responses); }
    }

    [Fact]
    public async Task BD01_BD02_BD03_BD04_Concrete_and_explicit_file_metadata_are_distinguished()
    {
        await using var app = await App(a =>
        {
            a.MapGet("/concrete", () => TypedResults.File(AdoptionFixture.Bytes, "application/pdf")).Produces(200, contentType: "application/pdf").WithTisiliaOperation("concrete");
            a.MapGet("/missing", () => Results.File(AdoptionFixture.Bytes, "application/pdf")).WithTisiliaOperation("missing");
            a.MapGet("/opaque", () => Results.File(AdoptionFixture.Bytes, "application/pdf")).Produces(200).WithTisiliaOperation("opaque");
            a.MapGet("/media", () => Results.File(AdoptionFixture.Bytes, "application/pdf")).WithMetadata(new Microsoft.AspNetCore.Http.ProducesResponseTypeMetadata(200, typeof(FileContentResult), [])).WithTisiliaOperation("media");
        });
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.Equal("supported", report.Operations.Single(o => o.OperationId == "concrete").Readiness);
        Assert.Equal("requires-declaration", report.Operations.Single(o => o.OperationId == "missing").Readiness);
        Assert.Equal("requires-declaration", report.Operations.Single(o => o.OperationId == "opaque").Readiness);
        Assert.NotEqual("supported", report.Operations.Single(o => o.OperationId == "media").Readiness);
        Assert.Null(Export(app).Root);
    }

    [Fact]
    public async Task DX02_DX04_DX09_DX10_Diagnoses_are_isolated_aggregated_and_never_probe_handlers()
    {
        var calls = 0;
        await using var app = await App(a =>
        {
            a.MapGet("/one", () => { calls++; return new UndeclaredTime(DateTime.UtcNow); }).WithTisiliaOperation("one");
            a.MapGet("/two", () => { calls++; return new UndeclaredTime(DateTime.Now); }).WithTisiliaOperation("two");
            a.MapGet("/ok", () => { calls++; return TypedResults.NoContent(); }).WithTisiliaOperation("ok");
        }, b => b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new CustomTimeConverter())));
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.True(report.AnalysisComplete); Assert.Equal(3, report.SelectedCount); Assert.Equal(3, report.AnalyzedCount); Assert.Equal(0, report.UnanalyzedCount);
        var cause = Assert.Single(report.Causes, c => c.Message.Contains("custom converter", StringComparison.Ordinal));
        Assert.Equal(["one", "two"], cause.OperationIds);
        Assert.Equal("supported", report.Operations.Single(o => o.OperationId == "ok").Readiness);
        Assert.All(report.Operations, o => Assert.Equal("unobserved", o.HttpVerification));
        Assert.Null(Export(app).Root); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DX05_DX06_DX07_Existing_default_member_exception_and_Local_HTTP_boundary()
    {
        await using var app = await App(a => a.MapGet("/time", () => new DeclaredTime(DateTime.UtcNow, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified))).WithTisiliaOperation("time"),
            options: o => { o.DateTimes.Default = DateTimeWire.Utc; o.DateTimes.Add(typeof(DeclaredTime), nameof(DeclaredTime.ZoneLess), DateTimeWire.Unspecified); });
        Success(app);
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.Equal("Utc", report.DateTimeDefault); Assert.Contains(report.DateTimeMembers, s => s.EndsWith("ZoneLess: Unspecified", StringComparison.Ordinal));
        await using var local = await App(a => a.MapGet("/local", (DateTime at) => TypedResults.NoContent()).WithTisiliaOperation("local"), options: o => o.DateTimes.Default = DateTimeWire.Local);
        Assert.True(Export(local).Diagnostics.HasErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DX03_DX11_DX12_DX13_Metadata_failure_is_incomplete_and_scrubbed(bool throwing)
    {
        await using var app = await App(a => a.MapGet("/missing", () => 1).ExcludeFromDescription().WithTisiliaOperation("missing"),
            b => { if (throwing) { b.Services.AddSingleton<IApiDescriptionGroupCollectionProvider, ThrowingDescriptions>(); } });
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.False(report.AnalysisComplete); Assert.Equal(6, report.ExitCode); Assert.Equal(1, report.UnanalyzedCount);
        var text = JsonSerializer.Serialize(report, TisiliaJson.Options);
        Assert.DoesNotContain("TEST_SECRET", text, StringComparison.Ordinal); Assert.DoesNotContain("private", text.Replace("privately", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RT05_RT20_RT21_RT33_Resolved_defaults_case_and_parameter_IDs()
    {
        await using var app = await App(a =>
        {
            a.MapGroup("/group").MapGet("/value/{ID}", ([FromRoute(Name = "id")] int value = 7) => value)
                .AddResolvedDefault().WithTisiliaOperation("default");
            a.MapGet("/name/{名前}", ([FromRoute(Name = "名前")] string value) => value).WithTisiliaOperation("unicode");
        });
        var result = Success(app);
        var operation = result.Index!.Operations["default"];
        Assert.Equal("/group/value/{ID=1}", operation.Route);
        Assert.True(operation.Parameters[0].HasServerDefault);
        Assert.Equal("7", Assert.IsType<JsonNumberValue>(operation.Parameters[0].ServerDefault).Text);
        var part = Assert.IsType<RouteParameter>(operation.RoutePlan!.Segments.Last().Parts[0]);
        Assert.Equal("1", part.DefaultValue); Assert.Equal(operation.Parameters[0].Id, part.ParameterId);
        Assert.Equal("名前", result.Index.Operations["unicode"].Parameters[0].Name);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("reference")]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("version-0.3")]
    [InlineData("version-0.4")]
    public void RT24_RT33_EV07_Invalid_plan_or_version_fails_closed(string mutation)
    {
        var root = SampleContracts.UsersApiJson(); var op = root["operations"]![0]!;
        if (mutation == "route") { op["route"] = "/other"; }
        if (mutation == "reference") { op["routePlan"]!["segments"]![1]!["parts"]![0]!["parameterId"] = "missing"; }
        if (mutation == "kind") { op["responses"]![0]!["body"]!["kind"] = "future"; }
        if (mutation == "version") { root["version"] = "9.9"; }
        if (mutation.StartsWith("version-", StringComparison.Ordinal)) { root["version"] = mutation[8..]; }
        var bag = new DiagnosticBag(); var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        if (loaded is not null) { SemanticValidator.Validate(loaded, bag, verifyHashes: false); }
        Assert.True(bag.HasErrors);
        if (mutation.StartsWith("version", StringComparison.Ordinal)) { Assert.Contains(bag.Items, d => d.Fix?.Contains("re-export", StringComparison.Ordinal) == true); }
    }

    private sealed record UndeclaredTime(DateTime At);
    private sealed record DeclaredTime(DateTime Utc, DateTime ZoneLess);
    private sealed class ThrowingDescriptions : IApiDescriptionGroupCollectionProvider
    {
        public ApiDescriptionGroupCollection ApiDescriptionGroups => throw new InvalidOperationException("Password=TEST_SECRET; C:\\private\\connection.json");
    }
}

internal static class ResolvedDefaultTestExtensions
{
    public static RouteHandlerBuilder AddResolvedDefault(this RouteHandlerBuilder endpoint)
    {
        endpoint.Add(b => { var route = (RouteEndpointBuilder)b; route.RoutePattern = RoutePatternFactory.Parse(route.RoutePattern.RawText!, new { ID = "1" }, parameterPolicies: null); });
        return endpoint;
    }
}
