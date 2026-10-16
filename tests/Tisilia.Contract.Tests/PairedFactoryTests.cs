using System.Globalization;
using System.Reflection;
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
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A converter factory serializes many closed types by one rule — here identifiers that wrap a Guid. System.Text.Json reports the
/// factory as the converter in effect, so that is what a registration names. A paired factory is asked for the closed types the
/// endpoints use, and each closed converter is a binding of its own.
/// </summary>
public sealed class PairedFactoryTests
{
    private const string ModuleId = "test.keys";
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("export const keyValidate = { validateDomain: (value) => value };\n");

    [Fact]
    public async Task A_paired_factory_binds_the_closed_types_the_endpoints_use_and_only_those()
    {
        var asked = new List<Type>();
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => { asked.Add(type); return Registration(type); }), MapOrders);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        Assert.Equal([typeof(CustomerKey), typeof(OrderKey)], asked.OrderBy(t => t.Name));
        var index = export.Index!;
        Assert.IsType<BrandShape>(index.Types["test.OrderKey"].Shape);
        Assert.IsType<BrandShape>(index.Types["test.CustomerKey"].Shape);
        Assert.False(index.Types.ContainsKey("test.ProductKey"));

        // one binding per closed converter, each a factory product with the closed type in its settings
        var order = index.Bindings["test.OrderKey.converter"];
        var customer = index.Bindings["test.CustomerKey.converter"];
        Assert.Equal(BindingKind.Factory, order.Kind);
        Assert.Equal(BindingKind.Factory, customer.Kind);
        Assert.NotEqual(order.SettingsDigest, customer.SettingsDigest);
    }

    [Fact]
    public async Task Registrations_naming_one_factory_with_one_settings_text_are_separate_scopes()
    {
        // before: both closed converters claimed (kind, implementation, settings, context) and export failed with SV24
        await using var app = await StartAsync(o =>
        {
            o.Codecs.AddPaired(Registration(typeof(OrderKey)));
            o.Codecs.AddPaired(Registration(typeof(CustomerKey)));
        }, MapOrders);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Rule == "SV24");
    }

    [Fact]
    public async Task A_closed_type_without_a_binding_names_the_factory_to_register()
    {
        // before: the message named only the closed converter the factory created, which no registration can name
        await using var app = await StartAsync(_ => { }, MapOrders);
        var export = Export(app);
        // reported at each position the type is reached (the order's request and its response)
        var error = export.Diagnostics.Items.First(d => d.Rule == "SV05" && d.Message.Contains(nameof(OrderKey), StringComparison.Ordinal));
        Assert.Contains($"converter factory '{typeof(TestKeyConverterFactory)}'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"AddPairedFactory(typeof({nameof(TestKeyConverterFactory)})", error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "returned no registration")]
    [InlineData("throw", "threw InvalidOperationException")]
    [InlineData("other-type", "returned a registration for")]
    [InlineData("other-converter", "names converter")]
    public async Task A_factory_that_gives_no_registration_says_why(string answer, string reason)
    {
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => type != typeof(CustomerKey) ? Registration(type) : answer switch
        {
            "null" => null,
            "throw" => throw new InvalidOperationException("secret-in-message"),
            "other-type" => Registration(typeof(ProductKey)),
            _ => Registration(type, converterType: typeof(TestKeyConverter<CustomerKey>)),
        }), MapOrders);
        var export = Export(app);
        var error = export.Diagnostics.Items.First(d => d.Rule == "SV05");
        Assert.All(export.Diagnostics.Items.Where(d => d.Severity == DiagnosticSeverity.Error), d => Assert.Equal("SV05", d.Rule));
        Assert.Contains(nameof(CustomerKey), error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        // application code may put anything in an exception: only its type is reported
        Assert.All(export.Diagnostics.Items, d => Assert.DoesNotContain("secret-in-message", d.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_reports_a_factory_without_a_binding_once_with_every_closed_type()
    {
        // the causes are one per type; what one fix binds is the converter a registration names, here the factory
        await using var app = await StartAsync(_ => { }, MapOrdersAndCustomers);
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.Equal(2, report.Causes.Count(c => c.ReasonCode == "TIS1302/SV05"));
        var unbound = Assert.Single(report.UnboundConverters);
        Assert.Equal((typeof(TestKeyConverterFactory).ToString(), true), (unbound.Converter, unbound.Factory));
        Assert.Equal([typeof(CustomerKey).ToString(), typeof(OrderKey).ToString()], unbound.Types);
        Assert.Equal(["customers.list", "orders.create"], unbound.OperationIds);
        Assert.Contains($"AddPairedFactory(typeof({nameof(TestKeyConverterFactory)})", unbound.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_points_a_registered_factory_to_its_callback_for_the_types_it_left_unbound()
    {
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => type == typeof(CustomerKey) ? null : Registration(type)), MapOrdersAndCustomers);
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        var unbound = Assert.Single(report.UnboundConverters);
        Assert.Equal([typeof(CustomerKey).ToString()], unbound.Types);
        Assert.Equal(["customers.list", "orders.create"], unbound.OperationIds);
        Assert.Contains("from the callback given to TisiliaOptions.Codecs.AddPairedFactory", unbound.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_reports_a_generic_converter_without_a_binding_once_with_every_type_its_closed_forms_serialize()
    {
        // members' own converters decide there: the factory binds the order key, and two closed forms of one generic converter have no
        // binding, which is added type by type
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => Registration(type)),
            a => a.MapPost("/shipments", (Shipment shipment) => TypedResults.Ok(shipment)).WithTisiliaOperation("shipments.create"));
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        var unbound = Assert.Single(report.UnboundConverters);
        Assert.Equal((typeof(TestKeyConverter<>).ToString(), false), (unbound.Converter, unbound.Factory));
        Assert.Equal([typeof(CustomerKey).ToString(), typeof(ProductKey).ToString()], unbound.Types);
        Assert.Equal(["shipments.create"], unbound.OperationIds);
        Assert.Contains("AddPaired (ConverterType = the closed TestKeyConverter`1 that serializes it)", unbound.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_outside_the_contract_forms_is_reported_with_the_registration_field()
    {
        // before: SV01 at /bindings/<n>/implementation/exportName, without saying which registration field held the name
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => Registration(type, converterExport: "TestKeyConverter`1")), MapOrders);
        var export = Export(app);
        var error = export.Diagnostics.Items.First(d => d.Rule == "SV01");
        Assert.Contains("DotnetConverterExport 'TestKeyConverter`1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registration_with_builtin_oracles_names_no_oracle_exports()
    {
        // what the portable generator writes for every type of a module: the builtin oracles and empty oracle names. Before, the check of
        // a registration's names refused the empty ones (SV01), so an application with a portable codec module could not export
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => Registration(type, builtinOracles: true)), MapOrders);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var equivalences = index.Equivalences.Values.Where(e => e.DomainTypeId is "test.OrderKey" or "test.CustomerKey").ToList();
        Assert.NotEmpty(equivalences);
        Assert.All(equivalences, e =>
        {
            Assert.Equal(Builtins.OracleStructural, Assert.IsType<BuiltinImpl>(e.DotnetOracle).Id);
            Assert.Equal(Builtins.OracleStructural, Assert.IsType<BuiltinImpl>(e.TypescriptOracle).Id);
        });
        Assert.DoesNotContain(index.Modules[ModuleId].Exports, e => e.Role == ExportRole.Oracle);
    }

    [Fact]
    public async Task Route_values_of_a_closed_type_take_the_factory_registration()
    {
        // before: a type only route values reach had no registration yet, so the value went as unchecked text (SV30)
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => Registration(type, parameters: true)),
            a => a.MapGet("/orders/{id}", (OrderKey id) => TypedResults.Ok(id.Value.ToString("D", CultureInfo.InvariantCulture))).WithTisiliaOperation("orders.get"));
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Rule == "SV30");
        Assert.Equal("test.OrderKey", Assert.Single(export.Index!.Operations["orders.get"].Parameters).Use.TypeId);
    }

    [Fact]
    public async Task The_runner_compares_each_closed_type_with_the_oracle_of_its_own_registration()
    {
        // the registrations share one oracle export; before, the runner took the first registration with that export, and the
        // customer key's values reached the order key's oracle
        await using var app = await StartAsync(o => o.Codecs.AddPairedFactory<TestKeyConverterFactory>(type => Registration(type, oracle: (a, b) => a.GetType() == type && a.Equals(b))), MapOrders);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var runner = new DotnetRunnerCore(index, export.Adapters!, app.Services.GetRequiredService<IOptions<TisiliaOptions>>().Value.Codecs.Paired);
        var key = DomainAst.String("6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b");
        foreach (var model in new[] { "test.OrderKey", "test.CustomerKey" })
        {
            var profile = index.Codecs[model + ".codec"].ProfileIds[0];
            var same = runner.Handle(RunnerProtocol.Request("keys", model, RunnerAction.Compare, model + ".response", profile, [], [key, key]));
            Assert.Equal(DomainAst.Boolean(true), same);
        }
    }

    private static void MapOrders(WebApplication app)
        => app.MapPost("/orders", (Order order) => TypedResults.Ok(order)).WithTisiliaOperation("orders.create");

    private static void MapOrdersAndCustomers(WebApplication app)
    {
        MapOrders(app);
        app.MapGet("/customers", () => TypedResults.Ok(new[] { CustomerKey.From(Guid.Empty) })).WithTisiliaOperation("customers.list");
    }

    private static async Task<WebApplication> StartAsync(Action<TisiliaOptions> configure, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new TestKeyConverterFactory()));
        builder.Services.AddTisilia(o => { o.ApiId = "paired-factory"; configure(o); });
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    /// <summary>A key: a brand over the builtin Guid, read and written in the Guid's own grammar; one module export per role for every key type.</summary>
    private static PairedCodecRegistration Registration(Type type, string? converterExport = null, bool parameters = false, Func<object, object, bool>? oracle = null, Type? converterType = null, bool builtinOracles = false)
    {
        var modelId = "test." + type.Name;
        var value = type.GetProperty(nameof(OrderKey.Value))!;
        var from = type.GetMethod(nameof(OrderKey.From), BindingFlags.Public | BindingFlags.Static)!;
        return new PairedCodecRegistration
        {
            ClrType = type,
            ConverterType = converterType ?? typeof(TestKeyConverterFactory),
            ModelId = modelId,
            TsName = type.Name,
            Shape = f => new BrandShape { BrandId = modelId, Base = f.Builder.Scalar("guid") },
            ModuleId = ModuleId,
            ModuleVersion = "1.0.0",
            License = "MIT",
            Artifacts =
            [
                new ModuleArtifactSpec(ArtifactTarget.Dotnet, "bin/Tisilia.Contract.Tests.dll", Bytes: () => File.ReadAllBytes(typeof(PairedFactoryTests).Assembly.Location)),
                new ModuleArtifactSpec(ArtifactTarget.Browser, "modules/test.keys/keys.js", Bytes: () => Script),
                new ModuleArtifactSpec(ArtifactTarget.Node, "modules/test.keys/keys.js", Bytes: () => Script),
            ],
            DotnetConverterExport = converterExport ?? nameof(TestKeyConverterFactory),
            DomainRuleExport = "keyDomainRule",
            BuiltinOracles = builtinOracles,
            DotnetOracleExport = builtinOracles ? "" : "KeyOracle",
            TypescriptOracleExport = builtinOracles ? "" : "keyOracle",
            Request = new PairedDirection(f => f.StringWire(modelId + ".read", WireDirection.ServerRead, Builtins.Grammar("guid")), "keyRequestEncode", "keyValidate", "keyRequestInput"),
            Response = new PairedDirection(f => f.StringWire(modelId + ".write", WireDirection.ServerWrite, Builtins.Grammar("guid")), "keyResponseDecode", "keyValidate"),
            ConverterContext = [],
            SettingsText = "TestKeyConverterFactory: the Guid in its D form",
            ParameterGrammarId = parameters ? Builtins.Grammar("guid") : null,
            Project = key => DomainAst.String(((Guid)value.GetValue(key)!).ToString("D", CultureInfo.InvariantCulture)),
            Construct = ast => ast is JsonStringValue text ? from.Invoke(null, [Guid.ParseExact(text.Value, "D")])! : throw new FormatException("a key is a Guid string"),
            Oracle = oracle ?? ((a, b) => a.Equals(b)),
        };
    }

    public sealed record Order(OrderKey Id, CustomerKey Customer, string Note);

    public sealed record Shipment(OrderKey Order, [property: JsonConverter(typeof(TestKeyConverter<ProductKey>))] ProductKey Product,
        [property: JsonConverter(typeof(TestKeyConverter<CustomerKey>))] CustomerKey Receiver);
}

/// <summary>An identifier that wraps a Guid; <see cref="TestKeyConverterFactory"/> serializes every such type.</summary>
public interface ITestKey<TSelf>
    where TSelf : struct, ITestKey<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

public readonly record struct OrderKey(Guid Value) : ITestKey<OrderKey>, IParsable<OrderKey>
{
    public static OrderKey From(Guid value) => new(value);

    public static OrderKey Parse(string s, IFormatProvider? provider) => TryParse(s, provider, out var key) ? key : throw new FormatException("not an order key");

    public static bool TryParse(string? s, IFormatProvider? provider, out OrderKey result)
    {
        var parsed = Guid.TryParseExact(s, "D", out var value);
        result = new OrderKey(value);
        return parsed;
    }
}

public readonly record struct CustomerKey(Guid Value) : ITestKey<CustomerKey>
{
    public static CustomerKey From(Guid value) => new(value);
}

public readonly record struct ProductKey(Guid Value) : ITestKey<ProductKey>
{
    public static ProductKey From(Guid value) => new(value);
}

/// <summary>The application's factory: one converter per key type, writing the Guid's D form.</summary>
public sealed class TestKeyConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsValueType && typeToConvert.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITestKey<>) && i.GenericTypeArguments[0] == typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(TestKeyConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class TestKeyConverter<TKey> : JsonConverter<TKey>
    where TKey : struct, ITestKey<TKey>
{
    public override TKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Guid.TryParseExact(reader.GetString(), "D", out var value) ? TKey.From(value) : throw new JsonException("a key is a Guid in its D form");

    public override void Write(Utf8JsonWriter writer, TKey value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value.ToString("D", CultureInfo.InvariantCulture));
}
