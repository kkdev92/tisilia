using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// A presence wrapper (<c>TisiliaOptions.PresenceWrappers</c>) holds whether an object member was sent, so that a request can leave a
/// member out, send null or send a value. The wrapper here is the usual shape: a struct whose default is the absent state, a converter
/// factory that reads the member's JSON into a present wrapper (null only where the value type takes it, and not where a non-nullable
/// annotation says otherwise), and a type info modifier that leaves the absent state out of responses. A ShouldSerialize predicate
/// that a modifier sets decides over any ignore condition. Oracle: <c>tests/oracle/ShouldSerialize/observe.cs</c>.
/// </summary>
public sealed class PresenceWrapperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-presence-" + Guid.NewGuid().ToString("N"));

    public PresenceWrapperTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_wrapped_member_is_its_value_and_optional_in_both_directions()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;

        foreach (var direction in new[] { "request", "response" })
        {
            var patch = (ObjectShape)index.Types[index.Codecs[direction == "request" ? RequestCodec(index) : ResponseCodec(index)].TypeId].Shape;
            Assert.All(patch.Properties, p => Assert.Equal(Presence.Optional, p.Presence));
            // null is a value where the member's converter reads it as a present wrapper that holds null
            Assert.Equal(new Dictionary<string, (string, bool)>
            {
                ["name"] = ("std.string", true),
                ["count"] = ("std.int32", false),
                ["title"] = ("std.string", false),
                ["limit"] = ("std.int32", true),
                ["since"] = ("std.int32", true),
            }, patch.Properties.ToDictionary(p => p.Name, p => (index.Types[p.Use.TypeId].Id, p.Use.SemanticNullable)));
        }

        var scopes = index.Profiles.Values.Single().Scopes.Where(s => s.PresenceWrapper is not null).ToList();
        Assert.Equal(["count", "limit", "name", "since", "title"], scopes.Select(s => s.ClrPath.Split('.')[^1]).Order(StringComparer.Ordinal));
        Assert.All(scopes, s => Assert.Equal(new PresenceWrapper { ClrType = typeof(Maybe<>).FullName![..^2] + "<T>", PresentMember = "HasValue", ValueMember = "Value" }, s.PresenceWrapper));
    }

    [Fact]
    public async Task Without_the_declaration_the_wrapper_is_a_converter_without_a_binding()
    {
        await using var app = await StartAsync(declare: false);
        var export = Export(app);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV05" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains("MaybeConverter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_response_that_writes_the_absent_state_is_refused()
    {
        await using var app = await StartAsync(omitAbsent: false);
        var export = Export(app);
        // [JsonIgnore(Condition = WhenWritingDefault)] leaves the absent state (the struct's default) out without a predicate
        var refusals = export.Diagnostics.Items.Where(d => d.Rule == "SV19" && d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Equal(["count", "limit", "name", "title"], refusals.Select(d => d.Message.Split('\'')[1].Split('.')[1]).Order(StringComparer.Ordinal));
        Assert.All(refusals, d => Assert.Contains("writes the wrapper's absent state too", d.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_predicate_that_writes_the_absent_state_overrides_an_ignore_condition()
    {
        // a ShouldSerialize a modifier sets replaces [JsonIgnore(Condition = WhenWritingDefault)]: the absent state is written after all
        await using var app = await StartAsync(writeSince: true);
        var export = Export(app);
        var refusal = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV19" && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("'ItemPatch.since'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("writes the wrapper's absent state too", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_whose_ignore_predicate_a_modifier_removed_writes_the_absent_state()
    {
        // before: [JsonIgnore(Condition = WhenWritingDefault)] was taken to leave the absent state out, though without its predicate
        // System.Text.Json writes the member always
        await using var app = await StartAsync(removeSince: true);
        var export = Export(app);
        var refusal = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV19" && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("'ItemPatch.since'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("writes the wrapper's absent state too", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_that_is_present_when_left_out_is_refused()
    {
        await using var app = await StartAsync(defaulted: true);
        var export = Export(app);
        var refusal = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV19" && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("'Defaulted.note'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("holds a present", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Left_out_null_and_values_travel_as_sent()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(),
        [
            ("items.patch", new { id = 1, body = new Dictionary<string, object?> { ["count"] = 3, ["limit"] = null } }),
            ("items.patch", new { id = 1, body = new Dictionary<string, object?> { ["name"] = null, ["title"] = "t" } }),
            ("items.patch", new { id = 1, body = new Dictionary<string, object?>() }),
            ("items.patch", new { id = 1, body = new Dictionary<string, object?> { ["title"] = null } }),
        ], execute: true);

        // the server echoes what it received: a member left out stays out, null stays null
        Assert.Equal("""{"count":3,"limit":null}""", Data(results[0]));
        Assert.Equal("""{"name":null,"title":"t"}""", Data(results[1]));
        Assert.Equal("{}", Data(results[2]));
        using var refused = JsonDocument.Parse(results[3]);
        Assert.Equal(("transport-failure", "request-encoding"), (refused.RootElement.GetProperty("kind").GetString(), refused.RootElement.GetProperty("reason").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the profile leaves nulls out: a wrapper is a struct, never null, so what it holds is written, null included
    public async Task The_runner_keeps_a_member_left_out_and_the_suite_expects_it(bool ignoreNulls)
    {
        await using var app = await StartAsync(ignoreNulls: ignoreNulls);
        var export = Export(app);
        var index = export.Index!;
        var runner = new DotnetRunnerCore(index, export.Adapters!, []);
        var requestCodec = RequestCodec(index);
        var responseCodec = ResponseCodec(index);
        var profile = index.Codecs[requestCodec].ProfileIds[0];
        string Run(RunnerAction action, string codec, string json)
            => DomainAst.ToJsonText(runner.Handle(RunnerProtocol.Request("presence", "run", action, codec, profile, [], [DomainAst.ParseJsonText(json, 64)])));

        Assert.Equal("""{"count":1}""", Run(RunnerAction.DotnetRead, requestCodec, """{"count":1}"""));
        Assert.Equal("""{"name":null,"limit":null}""", Run(RunnerAction.DotnetRead, requestCodec, """{"name":null,"limit":null}"""));
        Assert.Equal("""{"title":"x","limit":null}""", Run(RunnerAction.DotnetWrite, responseCodec, """{"title":"x","limit":null}"""));
        Assert.Equal("{}", Run(RunnerAction.DotnetWrite, responseCodec, "{}"));

        // generated cases leave wrapped members out in both directions, and expect them left out after the server's read and write
        var suite = SuiteBuilder.Build(index, ContractClosure.Compute(index, ["items.patch"]), new SuiteOptions { Seed = 7, CasesPerCodec = 6 });
        var requests = suite.Cases.OfType<RequestRoundTripCase>().Where(c => c.CodecId == requestCodec).ToList();
        var responses = suite.Cases.OfType<ResponseRoundTripCase>().Where(c => c.CodecId == responseCodec).ToList();
        Assert.Contains(requests, c => DomainAst.Entry((JsonObjectValue)c.Domain, "count") is null);
        Assert.Contains(responses, c => DomainAst.Entry((JsonObjectValue)c.Domain, "count") is null);
        foreach (var c in requests)
        {
            Assert.Equal(DomainAst.ToJsonText(c.Expected!), Run(RunnerAction.DotnetRead, requestCodec, DomainAst.ToJsonText(c.Domain)));
        }

        foreach (var c in responses)
        {
            Assert.Equal(DomainAst.ToJsonText(c.Expected!), Run(RunnerAction.DotnetWrite, responseCodec, DomainAst.ToJsonText(c.Domain)));
        }
    }

    [Fact]
    public async Task A_presence_wrapper_marks_member_scopes_only()
    {
        await using var app = await StartAsync();
        var root = JsonNode.Parse(Export(app).Text!)!.AsObject();
        var scopes = root["profiles"]![0]!["scopes"]!.AsArray();
        var wrapped = scopes.First(s => s!["presenceWrapper"] is not null)!["presenceWrapper"]!.DeepClone();
        scopes.First(s => s!["kind"]!.GetValue<string>() == "type")!["presenceWrapper"] = wrapped;

        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        Assert.NotNull(loaded);
        SemanticValidator.Validate(loaded!, bag, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Rule == "SV19" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains("this scope is not a member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outside_an_object_member_a_wrapper_is_a_converter_like_any_other()
    {
        // there its converter needs a binding, as any converter does (SV05); the object members stay wrapped
        await using var app = await StartAsync(elsewhere: true);
        var export = Export(app);
        var errors = export.Diagnostics.Items.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.All(errors, d => Assert.Equal("SV05", d.Rule));
        Assert.All(errors, d => Assert.Contains($"converter factory '{typeof(MaybeConverterFactory)}'", d.Message, StringComparison.Ordinal));
        Assert.Equal(["parameters/0", "responses/0/value/element"], errors.Select(d => d.Path.Split('/', 4)[3]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_declaration_names_a_generic_struct_and_its_members()
    {
        var wrappers = new PresenceWrapperCollection();
        wrappers.Add(typeof(Maybe<>));
        Assert.Equal(new PresenceWrapperRegistration(typeof(Maybe<>), "HasValue", "Value"), wrappers.Find(typeof(Maybe<string>)));
        Assert.Null(wrappers.Find(typeof(List<string>)));
        Assert.Throws<InvalidOperationException>(() => wrappers.Add(typeof(Maybe<>)));
        Assert.Throws<ArgumentException>(() => wrappers.Add(typeof(Maybe<int>)));
        Assert.Throws<ArgumentException>(() => wrappers.Add(typeof(List<>)));
        Assert.Throws<ArgumentException>(() => wrappers.Add(typeof(KeyValuePair<,>)));
        Assert.Throws<ArgumentException>(() => wrappers.Add(typeof(Flagged<>), "IsSet"));
        Assert.Throws<ArgumentException>(() => wrappers.Add(typeof(Flagged<>), "Set", "Missing"));
        wrappers.Add(typeof(Flagged<>), "Set", "Held");
        Assert.Equal("Held", wrappers.Find(typeof(Flagged<int>))!.ValueMember);
    }

    private static string RequestCodec(ContractIndex index) => ((JsonRequestBody)index.Operations["items.patch"].RequestBody).Use.CodecId;

    private static string ResponseCodec(ContractIndex index)
        => index.Operations["items.patch"].Responses.Select(r => r.Body).OfType<JsonResponseBody>().Single().Use.CodecId;

    private static string Data(string result)
    {
        using var document = JsonDocument.Parse(result);
        Assert.Equal("response", document.RootElement.GetProperty("kind").GetString());
        return document.RootElement.GetProperty("data").GetRawText();
    }

    internal static async Task<WebApplication> StartAsync(bool declare = true, bool omitAbsent = true, bool defaulted = false, bool ignoreNulls = false, bool writeSince = false, bool elsewhere = false, bool removeSince = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        IJsonTypeInfoResolver? resolver = null;
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new MaybeConverterFactory());
            if (ignoreNulls)
            {
                o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            }

            resolver = (o.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(info => MaybeMembers(info, omitAbsent, writeSince, removeSince));
            o.SerializerOptions.TypeInfoResolver = resolver;
        });
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "presence";
            o.Resolvers.Add(new ResolverRegistration
            {
                Matches = r => ReferenceEquals(r, resolver),
                Id = "presence.resolver.maybe",
                ModuleId = "presence.maybe",
                Artifacts = [new ModuleArtifactSpec(ArtifactTarget.Dotnet, "bin/Presence.dll", "sha256:" + new string('c', 64))],
                DotnetExport = "MaybeMembers",
                SettingsText = "Maybe<T> members: written only when present; null refused where the value is a non-nullable reference",
            });
            if (declare)
            {
                o.PresenceWrappers.Add(typeof(Maybe<>));
            }
        });
        var app = builder.Build();
        app.MapPatch("/items/{id}", (int id, ItemPatch patch) => TypedResults.Ok(patch)).WithTisiliaOperation("items.patch");
        if (defaulted)
        {
            app.MapPost("/defaulted", (Defaulted body) => TypedResults.NoContent()).WithTisiliaOperation("defaulted");
        }

        if (elsewhere)
        {
            // a body, a list element and a dictionary value are not object members
            app.MapPost("/elsewhere", ([FromBody] Maybe<int> body) => TypedResults.Ok(new Dictionary<string, List<Maybe<long>>>())).WithTisiliaOperation("elsewhere");
        }
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    /// <summary>The usual modifier: a response leaves the absent state out, and a non-nullable reference value refuses null.</summary>
    private static void MaybeMembers(JsonTypeInfo info, bool omitAbsent, bool writeSince, bool removeSince)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(Maybe<>))
            {
                continue;
            }

            if (omitAbsent && property.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) != true)
            {
                property.ShouldSerialize = static (_, value) => value is IMaybe { HasValue: true };
            }
            else if (writeSince && property.Name == "since")
            {
                property.ShouldSerialize = static (_, _) => true;
            }
            else if (removeSince && property.Name == "since")
            {
                property.ShouldSerialize = null;
            }

            var valueType = property.PropertyType.GetGenericArguments()[0];
            if (!valueType.IsValueType && property.AttributeProvider is PropertyInfo member
                && new NullabilityInfoContext().Create(member).GenericTypeArguments[0].ReadState == NullabilityState.NotNull)
            {
                property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(MaybeConverter<>).MakeGenericType(valueType), [false])!;
            }
        }
    }

    public interface IMaybe
    {
        bool HasValue { get; }
    }

    public readonly struct Maybe<T> : IMaybe
    {
        private readonly T _value;

        private Maybe(T value)
        {
            _value = value;
            HasValue = true;
        }

        public bool HasValue { get; }

        public T Value => HasValue ? _value : throw new InvalidOperationException("no value was sent");

        public static Maybe<T> Of(T value) => new(value);
    }

    public readonly struct Flagged<T>
    {
        public bool Set { get; init; }

        public T Held { get; init; }
    }

    public sealed class MaybeConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Maybe<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(MaybeConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]), [true])!;
    }

    public sealed class MaybeConverter<T>(bool allowNull) : JsonConverter<Maybe<T>>
    {
        public override bool HandleNull => true;

        public override Maybe<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return allowNull && default(T) is null ? Maybe<T>.Of(default!) : throw new JsonException("null is not a value of this member");
            }

            return Maybe<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        public override void Write(Utf8JsonWriter writer, Maybe<T> value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    public sealed class ItemPatch
    {
        public Maybe<string?> Name { get; init; }

        public Maybe<int> Count { get; init; }

        public Maybe<string> Title { get; init; }

        public Maybe<int?> Limit { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Maybe<int?> Since { get; init; }
    }

    public sealed class Defaulted
    {
        public Maybe<string?> Note { get; init; } = Maybe<string?>.Of("set by the server");
    }
}
