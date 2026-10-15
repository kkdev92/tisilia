using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Conformance;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// An enum the server reads and writes by name alone can be held by name on the client (<c>TisiliaOptions.Enums</c>). On .NET 10, a
/// JsonStringEnumConverter with <c>allowIntegerValues: false</c> refuses a JSON number and an integer string, and refuses to write a
/// value no member defines; it writes each member's serialized name and reads it as written, and reads a member's C# name in any
/// case, except a member that <c>[JsonStringEnumMemberName]</c> names. Oracle: <c>tests/oracle/EnumNames/observe.cs</c>.
/// </summary>
public sealed class EnumNameDomainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-enum-names-" + Guid.NewGuid().ToString("N"));

    public EnumNameDomainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task An_enum_read_and_written_by_name_alone_is_held_by_name_when_asked()
    {
        await using var app = await StartAsync(o => o.Enums.Default = EnumDomain.Names);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;

        var kind = EnumModel(index, nameof(Kind));
        var shape = (EnumShape)kind.Shape;
        Assert.True(shape.NameDomain);
        Assert.False(shape.AllowUndefinedInteger);
        Assert.Equal("fancy-name", shape.Members.Single(m => m.Name == nameof(Kind.Fancy)).SerializedName);
        var codec = index.Codecs[kind.Id + ".codec"];
        Assert.IsType<StringWire>(index.Wires[codec.Capabilities.Request!.Wire.WireId].Shape);
        Assert.IsType<StringWire>(index.Wires[codec.Capabilities.Response!.Wire.WireId].Shape);

        // two members of one value: the server writes one name for both, which is the only name the client holds for the value
        var alias = (EnumShape)EnumModel(index, nameof(Alias)).Shape;
        Assert.True(alias.NameDomain);
        Assert.Equal(["first", "second", "second"], alias.Members.Select(m => m.SerializedName ?? m.Name));

        // the others keep their integers: one reads integers, one is written as an integer, one is a [Flags] enum
        Assert.All([nameof(Loose), nameof(Count), nameof(Access)], n => Assert.Null(((EnumShape)EnumModel(index, n).Shape).NameDomain));
        var warnings = export.Diagnostics.Items.Where(d => d.Rule == "SV49").ToList();
        Assert.All(warnings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains(warnings, d => d.Message.Contains(nameof(Loose), StringComparison.Ordinal) && d.Message.Contains("reads integers", StringComparison.Ordinal));
        Assert.Contains(warnings, d => d.Message.Contains(nameof(Access), StringComparison.Ordinal) && d.Message.Contains("[Flags]", StringComparison.Ordinal));
        // an enum written as an integer was never in reach of names: only an explicit declaration is answered for it
        Assert.DoesNotContain(warnings, d => d.Message.Contains(nameof(Count), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_declaration_every_enum_keeps_its_integers()
    {
        await using var app = await StartAsync(_ => { });
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.All(export.Index!.Types.Values.Where(t => t.Shape is EnumShape), t => Assert.Null(((EnumShape)t.Shape).NameDomain));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Rule == "SV49");

        // two members of one value travel as the one name the server writes for it
        Assert.Equal(["first", "second", "second"], ((EnumShape)EnumModel(export.Index, nameof(Alias)).Shape).Members.Select(m => m.SerializedName ?? m.Name));
    }

    [Fact]
    public async Task A_declaration_for_one_enum_overrides_the_default_and_is_answered_when_it_cannot_hold()
    {
        await using var app = await StartAsync(o =>
        {
            o.Enums.Add(typeof(Kind), EnumDomain.Names);
            o.Enums.Add(typeof(Count), EnumDomain.Names);
        });
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Contains(export.Index!.Types.Values, t => t.Shape is EnumShape { NameDomain: true } && t.ClrIdentity.EndsWith(nameof(Kind), StringComparison.Ordinal));
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV49" && d.Message.Contains(nameof(Count), StringComparison.Ordinal) && d.Message.Contains("as an integer", StringComparison.Ordinal));
        var declarations = new EnumDomainCollection();
        declarations.Add(typeof(Kind), EnumDomain.Names);
        Assert.Throws<InvalidOperationException>(() => declarations.Add(typeof(Kind), EnumDomain.Values));
        Assert.Throws<ArgumentException>(() => declarations.Add(typeof(int), EnumDomain.Names));
    }

    [Fact]
    public async Task Names_travel_both_ways_and_a_route_value_is_written_as_the_member_name()
    {
        await using var app = await StartAsync(o => o.Enums.Default = EnumDomain.Names);
        var export = Export(app);
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "stock",
            ["note"] = "n",
            ["counts"] = new Dictionary<string, object> { ["$entries"] = new object[] { new object[] { "fancy-name", 2 } } },
        };
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
            [("orders.create", new { kind = "fancy-name", body }), ("orders.create", new { kind = "Fancy", body })], execute: true);

        // the server bound the route value "Fancy" (Enum.TryParse reads C# names) and wrote the names back
        using var echo = JsonDocument.Parse(results[0]);
        var data = echo.RootElement.GetProperty("data");
        Assert.Equal("fancy-name", data.GetProperty("route").GetString());
        Assert.Equal("stock", data.GetProperty("kind").GetString());
        Assert.Equal("fancy-name", data.GetProperty("counts").GetProperty("$entries")[0][0].GetString());

        // a C# name is not a value the client holds: refused before sending
        using var refused = JsonDocument.Parse(results[1]);
        Assert.Equal(("transport-failure", "request-encoding"), (refused.RootElement.GetProperty("kind").GetString(), refused.RootElement.GetProperty("reason").GetString()));
        Assert.Contains("unknown enum member name", refused.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runner_reads_writes_and_generates_names()
    {
        await using var app = await StartAsync(o => o.Enums.Default = EnumDomain.Names);
        var export = Export(app);
        var index = export.Index!;
        var codecId = EnumModel(index, nameof(Kind)).Id + ".codec";
        var profile = index.Codecs[codecId].ProfileIds[0];
        var runner = new DotnetRunnerCore(index, export.Adapters!, []);
        JsonValue Run(RunnerAction action, string name) => runner.Handle(RunnerProtocol.Request("names", "run", action, codecId, profile, [], [DomainAst.String(name)]));

        // the server reads a member's C# name in any case ("STOCK" for Stock), and a [JsonStringEnumMemberName] name only as written;
        // it writes the names
        Assert.Equal(DomainAst.String("stock"), Run(RunnerAction.DotnetRead, "STOCK"));
        Assert.Equal(DomainAst.String("fancy-name"), Run(RunnerAction.DotnetRead, "fancy-name"));
        Assert.Equal(RunnerFailureCode.Codec, Assert.Throws<RunnerFailureException>(() => Run(RunnerAction.DotnetRead, "FANCY-NAME")).Code);
        Assert.Equal(RunnerFailureCode.Codec, Assert.Throws<RunnerFailureException>(() => Run(RunnerAction.DotnetRead, "Fancy")).Code);
        Assert.Equal(DomainAst.String("stock"), Run(RunnerAction.DotnetWrite, "stock"));
        Assert.Equal(DomainAst.String("fancy-name"), Run(RunnerAction.DotnetWrite, "fancy-name"));

        var suite = SuiteBuilder.Build(index, ContractClosure.Compute(index, ["orders.create"]), new SuiteOptions { Seed = 7, CasesPerCodec = 4 });
        var roundTrips = suite.Cases.OfType<RequestRoundTripCase>().Where(c => c.CodecId == codecId).ToList();
        Assert.NotEmpty(roundTrips);
        Assert.All(roundTrips, c => Assert.IsType<JsonStringValue>(c.Domain));
    }

    [Theory]
    [InlineData("flags", "neither flags nor open")]
    [InlineData("open", "neither flags nor open")]
    [InlineData("ambiguous", "stands for two values")]
    public async Task A_name_domain_admits_no_integer_and_no_name_for_two_values(string change, string message)
    {
        await using var app = await StartAsync(o => o.Enums.Default = EnumDomain.Names);
        var root = JsonNode.Parse(Export(app).Text!)!.AsObject();
        var kind = root["types"]!.AsArray().Select(t => t!.AsObject()).Single(t => t["shape"]!["nameDomain"] is not null && t["clrIdentity"]!.GetValue<string>().EndsWith(nameof(Kind), StringComparison.Ordinal))["shape"]!.AsObject();
        var members = kind["members"]!.AsArray();
        switch (change)
        {
            case "flags":
                kind["flags"] = true;
                break;
            case "open":
                kind["allowUndefinedInteger"] = true;
                break;
            default:
                // Usage keeps its own name, and Stock is written as "Usage" too
                members[0]!.AsObject().Remove("serializedName");
                members[1]!["serializedName"] = "Usage";
                break;
        }

        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        Assert.NotNull(loaded);
        SemanticValidator.Validate(loaded!, bag, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Rule == "SV49" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains(message, StringComparison.Ordinal));
    }

    private static async Task<WebApplication> StartAsync(Action<TisiliaOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
        builder.Services.AddTisilia(o => { o.ApiId = "enum-names"; configure(o); });
        var app = builder.Build();
        app.MapPost("/orders/{kind}", (Kind kind, Order order) => TypedResults.Ok(new OrderEcho(kind, order.Kind, order.Counts))).WithTisiliaOperation("orders.create");
        app.MapGet("/levels", () => TypedResults.Ok(new Levels(Loose.High, Count.Two, Access.Read | Access.Write, Alias.Second))).WithTisiliaOperation("levels.get");
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    private static Model EnumModel(ContractIndex index, string name)
        => Assert.Single(index.Types.Values, t => t.Shape is EnumShape && t.ClrIdentity?.Split('.', '+')[^1] == name);

    public enum Kind
    {
        Usage,
        Stock,
        [JsonStringEnumMemberName("fancy-name")] Fancy,
    }

    public enum Loose
    {
        Low,
        High,
    }

    public enum Count
    {
        One,
        Two,
    }

    [Flags]
    public enum Access
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    public enum Alias
    {
        First = 1,
        Second = 2,
        Other = 2,
    }

    public sealed record Order(Kind Kind, string Note, Dictionary<Kind, int> Counts);

    public sealed record OrderEcho(Kind Route, Kind Kind, Dictionary<Kind, int> Counts);

    // a member converter takes precedence over the options: one that reads integers, one that writes integers
    public sealed record Levels(
        [property: JsonConverter(typeof(JsonStringEnumConverter<Loose>))] Loose Loose,
        [property: JsonConverter(typeof(JsonNumberEnumConverter<Count>))] Count Count,
        Access Access,
        Alias Alias);
}
