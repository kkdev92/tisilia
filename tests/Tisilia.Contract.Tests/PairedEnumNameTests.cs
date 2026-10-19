using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Conformance;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.TypeScript;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A paired converter that reads and writes an enum by its names can have the client hold those names: the registration's shape
/// is an enum whose domain is its names, and <c>Project</c> and <c>Construct</c> go through the names. The generated type is the
/// union of the names, and conformance runs the converter on them.
/// </summary>
public sealed class PairedEnumNameTests
{
    private const string ModuleId = "test.finishes";
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("export const finishValidate = { validateDomain: (value) => value };\n");

    [Fact]
    public async Task A_paired_converter_can_have_the_client_hold_its_enum_by_name()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;

        var shape = Assert.IsType<EnumShape>(index.Types["test.Finish"].Shape);
        Assert.True(shape.NameDomain);
        Assert.Equal(["matte", "high-gloss"], shape.Members.Select(m => m.SerializedName));

        var bag = new DiagnosticBag();
        var files = TsGenerator.Generate(index, new TsGenerationOptions { GeneratorVersion = "test", ModuleMode = ModuleMode.NodeNext, ModuleImportResolver = (_, artifact) => "./" + artifact.Path }, bag, out _);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items)); Assert.NotNull(files);
        var models = files.Single(f => f.Path == "models/index.ts").Content;
        Assert.Contains("export type Finish = \"matte\" | \"high-gloss\";", models, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conformance_runs_the_converter_on_the_names()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var runner = new DotnetRunnerCore(index, export.Adapters!, app.Services.GetRequiredService<IOptions<TisiliaOptions>>().Value.Codecs.Paired);
        const string codecId = "test.Finish.codec";
        var profile = index.Codecs[codecId].ProfileIds[0];
        JsonValue Run(RunnerAction action, string text) => runner.Handle(RunnerProtocol.Request("finishes", "run", action, codecId, profile, [], [DomainAst.String(text)]));

        // the converter reads and writes the names, and refuses a member's C# name
        Assert.Equal(DomainAst.String("high-gloss"), Run(RunnerAction.DotnetRead, "high-gloss"));
        Assert.Equal(DomainAst.String("matte"), Run(RunnerAction.DotnetWrite, "matte"));
        Assert.Equal(RunnerFailureCode.Codec, Assert.Throws<RunnerFailureException>(() => Run(RunnerAction.DotnetRead, nameof(Finish.HighGloss))).Code);

        // the cases hold names, as the client does
        var suite = SuiteBuilder.Build(index, ContractClosure.Compute(index, ["paints.create"]), new SuiteOptions { Seed = 7, CasesPerCodec = 4 });
        var roundTrips = suite.Cases.OfType<RequestRoundTripCase>().Where(c => c.CodecId == codecId).ToList();
        Assert.NotEmpty(roundTrips);
        Assert.All(roundTrips, c => Assert.Contains(Assert.IsType<JsonStringValue>(c.Domain).Value, new[] { "matte", "high-gloss" }));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new FinishNameConverter()));
        builder.Services.AddTisilia(o => { o.ApiId = "paired-enum-names"; o.Codecs.AddPaired(Registration()); });
        var app = builder.Build();
        app.MapPost("/paints", (Paint paint) => TypedResults.Ok(paint)).WithTisiliaOperation("paints.create");
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    /// <summary>The finish by the names the converter writes; the client holds the names, so its type is their union.</summary>
    private static PairedCodecRegistration Registration() => new()
    {
        ClrType = typeof(Finish),
        ConverterType = typeof(FinishNameConverter),
        ModelId = "test.Finish",
        TsName = "Finish",
        Shape = _ => new EnumShape
        {
            UnderlyingPrimitiveId = Builtins.Scalar("int32"),
            Flags = false,
            AllowUndefinedInteger = false,
            Members =
            [
                new EnumMember { Name = nameof(Finish.Matte), Value = "0", SerializedName = "matte" },
                new EnumMember { Name = nameof(Finish.HighGloss), Value = "1", SerializedName = "high-gloss" },
            ],
            NameDomain = true,
        },
        ModuleId = ModuleId,
        ModuleVersion = "1.0.0",
        License = "MIT",
        Artifacts =
        [
            new ModuleArtifactSpec(ArtifactTarget.Dotnet, "bin/Tisilia.Contract.Tests.dll", Bytes: () => File.ReadAllBytes(typeof(PairedEnumNameTests).Assembly.Location)),
            new ModuleArtifactSpec(ArtifactTarget.Browser, "modules/test.finishes/finishes.js", Bytes: () => Script),
            new ModuleArtifactSpec(ArtifactTarget.Node, "modules/test.finishes/finishes.js", Bytes: () => Script),
        ],
        DotnetConverterExport = nameof(FinishNameConverter),
        DomainRuleExport = "finishDomainRule",
        DotnetOracleExport = "FinishOracle",
        TypescriptOracleExport = "finishOracle",
        Request = new PairedDirection(f => f.StringWire("test.Finish.read", WireDirection.ServerRead, Builtins.Grammar("enum-name")), "finishRequestEncode", "finishValidate", "finishRequestInput"),
        Response = new PairedDirection(f => f.StringWire("test.Finish.write", WireDirection.ServerWrite, Builtins.Grammar("enum-name")), "finishResponseDecode", "finishValidate"),
        ConverterContext = [],
        SettingsText = "FinishNameConverter: matte and high-gloss, numbers refused",
        Project = value => DomainAst.String(FinishNameConverter.NameOf((Finish)value)),
        Construct = ast => ast is JsonStringValue text ? FinishNameConverter.FromName(text.Value) : throw new FormatException("a finish is one of its names"),
        Oracle = (a, b) => a.Equals(b),
    };

    public enum Finish
    {
        Matte,
        HighGloss,
    }

    public sealed record Paint(Finish Finish, string Note);

    /// <summary>The application's converter: writes each finish by its name and reads only those names.</summary>
    public sealed class FinishNameConverter : JsonConverter<Finish>
    {
        public static string NameOf(Finish finish) => finish switch
        {
            Finish.Matte => "matte",
            Finish.HighGloss => "high-gloss",
            _ => throw new JsonException("not a finish"),
        };

        public static Finish FromName(string name) => name switch
        {
            "matte" => Finish.Matte,
            "high-gloss" => Finish.HighGloss,
            _ => throw new JsonException("a finish is matte or high-gloss"),
        };

        public override Finish Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String ? FromName(reader.GetString()!) : throw new JsonException("a finish is a JSON string");

        public override void Write(Utf8JsonWriter writer, Finish value, JsonSerializerOptions options) => writer.WriteStringValue(NameOf(value));
    }
}
