using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Tisilia.AspNetCore.Codecs;
using Tisilia.Contract;
using Tisilia.Generator.Additional;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The additional codec module: its installation, the embedded copy of the module, the conformance samples (every one a
/// value of the .NET type), and the converters Tisilia ships for the types System.Text.Json has none for.
/// </summary>
public class AdditionalCodecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-additional-" + Guid.NewGuid().ToString("N"));

    public AdditionalCodecTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void The_embedded_module_is_the_source_in_the_repository()
    {
        var source = Path.Combine(FixtureTests.RepoRoot(), "src", "backend", "Tisilia.Generator", "Additional");
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, AdditionalModule.ScriptFile)), AdditionalModule.Script());
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, AdditionalModule.TypesFile)), AdditionalModule.Types());
    }

    [Fact]
    public void Install_writes_once_and_never_replaces_other_content_without_force()
    {
        var first = AdditionalModule.Install(_dir, force: false);
        Assert.All(first, f => Assert.Equal(AdditionalModule.InstallStatus.Written, f.Status));
        var script = Path.Combine(_dir, "modules", "tisilia-additional", AdditionalModule.ScriptFile);
        Assert.Equal(AdditionalModule.Script(), File.ReadAllBytes(script));

        Assert.All(AdditionalModule.Install(_dir, force: false), f => Assert.Equal(AdditionalModule.InstallStatus.Unchanged, f.Status));

        File.WriteAllText(script, "// edited");
        var conflict = AdditionalModule.Install(_dir, force: false);
        Assert.Contains(conflict, f => f.Status == AdditionalModule.InstallStatus.Conflict);
        Assert.Equal("// edited", File.ReadAllText(script));

        var forced = AdditionalModule.Install(_dir, force: true);
        Assert.Contains(forced, f => f.Status == AdditionalModule.InstallStatus.Replaced);
        Assert.Equal(AdditionalModule.Script(), File.ReadAllBytes(script));
    }

    [Fact]
    public void Install_pins_its_folder_and_repairs_a_crlf_checkout_without_force()
    {
        AdditionalModule.Install(_dir, force: false);
        var folder = Path.Combine(_dir, "modules", "tisilia-additional");
        var attributes = Path.Combine(folder, ".gitattributes");
        Assert.EndsWith("\n* -text\n", File.ReadAllText(attributes), StringComparison.Ordinal);

        // a checkout made before the folder was pinned: same module, CRLF line endings
        var script = Path.Combine(folder, AdditionalModule.ScriptFile);
        LineEndingsTests.CheckOutWithCrLf(script);
        File.WriteAllText(attributes, "# the user's own\n");
        var repaired = AdditionalModule.Install(_dir, force: false);
        Assert.Contains(repaired, f => f.Path == script && f.Status == AdditionalModule.InstallStatus.Replaced);
        Assert.DoesNotContain(repaired, f => f.Status == AdditionalModule.InstallStatus.Conflict);
        Assert.Equal(AdditionalModule.Script(), File.ReadAllBytes(script));
        Assert.Equal("# the user's own\n", File.ReadAllText(attributes));
    }

    [Theory]
    [InlineData("Int128")]
    [InlineData("UInt128")]
    [InlineData("BigInteger")]
    [InlineData("Half")]
    [InlineData("Uri")]
    [InlineData("Version")]
    [InlineData("IPAddress")]
    [InlineData("Rune")]
    [InlineData("IPNetwork")]
    [InlineData("Index")]
    [InlineData("Range")]
    [InlineData("JsonScalar")]
    public void Every_conformance_sample_is_a_value_of_the_dotnet_type(string type)
    {
        var rng = new Random(20261002);
        for (var i = 0; i < 64; i++)
        {
            var sample = AdditionalModule.Sample(AdditionalModule.ModelId(type), i, rng.Next);
            Assert.NotNull(sample);
            if (type == "JsonScalar")
            {
                // a JSON scalar a JsonValue node reads and writes back with the same token
                var json = Generator.Conformance.DomainAst.ToJsonText(sample!);
                var written = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsValue().ToJsonString();
                if (sample is JsonStringValue s)
                {
                    Assert.Equal(s.Value, System.Text.Json.Nodes.JsonNode.Parse(written)!.GetValue<string>()); // the value; escapes are the writer's
                }
                else
                {
                    Assert.Equal(json, written); // numbers keep their lexeme
                }
                continue;
            }

            var text = sample switch
            {
                JsonStringValue s => s.Value,
                JsonNumberValue n => n.Text,
                _ => throw new InvalidOperationException("unexpected sample " + sample),
            };
            Assert.True(IsValue(type, text), $"{type} sample {i} '{text}' is not a canonical {type}");
        }
    }

    [Theory]
    [InlineData("Int128")]
    [InlineData("UInt128")]
    [InlineData("BigInteger")]
    [InlineData("Version")]
    [InlineData("IPAddress")]
    [InlineData("Rune")]
    [InlineData("IPNetwork")]
    [InlineData("Index")]
    [InlineData("Range")]
    public void Invalid_samples_are_not_canonical_values(string type)
    {
        foreach (var (domain, reason) in AdditionalModule.InvalidSamples(AdditionalModule.ModelId(type)))
        {
            Assert.False(IsValue(type, ((JsonStringValue)domain).Value), $"{type} invalid sample '{reason}' is a canonical value");
        }
    }

    /// <summary>Whether <paramref name="text"/> is the canonical text of a value of the .NET type (the domain of the TypeScript codec).</summary>
    private static bool IsValue(string type, string text) => type switch
    {
        "Int128" => Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) && i.ToString(CultureInfo.InvariantCulture) == text,
        "UInt128" => UInt128.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var u) && u.ToString(CultureInfo.InvariantCulture) == text,
        "BigInteger" => text.Length <= BigIntegerJsonConverter.MaxLength && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b) && b.ToString(CultureInfo.InvariantCulture) == text,
        // a Half sample is the float64 projection of a binary16 value
        "Half" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && ((double)(Half)d).Equals(d) && Half.IsFinite((Half)d),
        "Uri" => Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri) && uri.OriginalString == text,
        "Version" => Version.TryParse(text, out var v) && v.ToString() == text,
        "IPAddress" => IPAddressJsonConverter.ParseCanonical(text) is not null,
        "Rune" => RuneJsonConverter.ParseScalar(text) is not null,
        "IPNetwork" => IPNetworkJsonConverter.ParseCanonical(text) is not null,
        "Index" => IndexJsonConverter.ParseCanonical(text) is not null,
        "Range" => RangeJsonConverter.ParseCanonical(text) is not null,
        _ => false,
    };

    // ------------------------------------------------------------------ converters

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions().AddTisiliaAdditionalConverters();

    [Fact]
    public void AddTisiliaAdditionalConverters_adds_the_converters_System_Text_Json_lacks()
    {
        Assert.Equal(
            [typeof(BigIntegerJsonConverter), typeof(IPAddressJsonConverter), typeof(RuneJsonConverter), typeof(IPNetworkJsonConverter), typeof(IndexJsonConverter), typeof(RangeJsonConverter), typeof(ComplexJsonConverter), typeof(JsonValueJsonConverter), typeof(TimeZoneInfoJsonConverter), typeof(CultureInfoJsonConverter)],
            Options.Converters.Select(c => c.GetType()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890")]
    public void BigInteger_is_a_number_token_with_canonical_digits(string text)
    {
        var value = JsonSerializer.Deserialize<BigInteger>(text, Options);
        Assert.Equal(text, JsonSerializer.Serialize(value, Options));
        Assert.Equal("{\"" + text + "\":1}", JsonSerializer.Serialize(new Dictionary<BigInteger, int> { [value] = 1 }, Options));
        Assert.Equal(value, JsonSerializer.Deserialize<Dictionary<BigInteger, int>>("{\"" + text + "\":1}", Options)!.Keys.Single());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    public void BigInteger_refuses_fractions_exponents_and_other_tokens(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BigInteger>(json, Options));
    }

    [Fact]
    public void BigInteger_text_is_limited_to_4096_characters_both_ways()
    {
        var largest = "9" + new string('0', BigIntegerJsonConverter.MaxLength - 1);
        Assert.Equal(largest, JsonSerializer.Serialize(JsonSerializer.Deserialize<BigInteger>(largest, Options), Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BigInteger>(largest + "0", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(BigInteger.Pow(10, BigIntegerJsonConverter.MaxLength), Options));
    }

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("::")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fe80::1%3")]
    public void IPAddress_reads_and_writes_the_canonical_text(string text)
    {
        var json = JsonSerializer.Serialize(text);
        var address = JsonSerializer.Deserialize<IPAddress>(json, Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(address, Options));
        Assert.Equal("{" + json + ":1}", JsonSerializer.Serialize(new Dictionary<IPAddress, int> { [address] = 1 }, Options));
    }

    [Theory]
    [InlineData("1")] // IPAddress.TryParse reads 0.0.0.1
    [InlineData("0x7f.0.0.1")]
    [InlineData("010.0.0.1")] // octal in IPAddress.TryParse
    [InlineData("::FFFF:1.2.3.4")]
    [InlineData("0:0:0:0:0:0:0:1")]
    public void IPAddress_refuses_other_spellings(string text)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IPAddress>(JsonSerializer.Serialize(text), Options));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("\u00e9")]
    [InlineData("\U0001F600")]
    [InlineData("\0")]
    public void Rune_is_a_string_of_one_scalar(string text)
    {
        var json = JsonSerializer.Serialize(text);
        var rune = JsonSerializer.Deserialize<Rune>(json, Options);
        Assert.Equal(text, rune.ToString());
        Assert.Equal(JsonSerializer.Serialize(text), JsonSerializer.Serialize(rune, Options));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"ab\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("97")]
    public void Rune_refuses_anything_but_one_scalar(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Rune>(json, Options));
    }

    [Fact]
    public void Registrations_cover_the_fifteen_types_and_name_the_converters_in_effect()
    {
        var registrations = AdditionalCodecs.Create(_dir);
        Assert.Equal(["Int128", "UInt128", "BigInteger", "Half", "Uri", "Version", "IPAddress", "Rune", "IPNetwork", "Index", "Range", "Complex", "JsonScalar", "TimeZoneId", "CultureName"], registrations.Select(r => r.TsName));
        foreach (var registration in registrations)
        {
            // the converter a registration names is the one System.Text.Json uses for the type once the application adds Tisilia's converters
            Assert.Equal(registration.ConverterType, Options.GetConverter(registration.ClrType).GetType());
            Assert.StartsWith(AdditionalModule.IdPrefix, registration.ModelId, StringComparison.Ordinal);
        }

        Assert.Null(registrations.Single(r => r.TsName == "Uri").Key); // Uri.Equals merges keys the TypeScript map keeps apart
        Assert.Null(registrations.Single(r => r.TsName == "Rune").ParameterGrammarId); // ASP.NET Core has no TryParse to bind a Rune
        Assert.NotNull(registrations.Single(r => r.TsName == "IPNetwork").ParameterGrammarId); // IPNetwork is IParsable
        Assert.All(registrations.Where(r => r.TsName is "Int128" or "UInt128" or "Half"), r => Assert.True(r.NumberHandling));
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0/0")]
    [InlineData("2001:db8::/32")]
    [InlineData("fe80::%2/64")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void IPNetwork_reads_and_writes_the_canonical_cidr_text(string text)
    {
        var json = JsonSerializer.Serialize(text);
        var network = JsonSerializer.Deserialize<IPNetwork>(json, Options);
        Assert.Equal(json, JsonSerializer.Serialize(network, Options));
        Assert.Equal("{" + json + ":1}", JsonSerializer.Serialize(new Dictionary<IPNetwork, int> { [network] = 1 }, Options));
    }

    [Theory]
    [InlineData("10.0.0.1/8")] // IPNetwork.TryParse clears the host bits
    [InlineData("10.0.0.0/08")]
    [InlineData("fe80::1%2/64")] // clearing the host bits drops the scope
    [InlineData("10.0.0.0")]
    [InlineData("2001:DB8::/32")]
    public void IPNetwork_refuses_texts_IPNetwork_would_normalize(string text)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IPNetwork>(JsonSerializer.Serialize(text), Options));
    }

    [Fact]
    public void Index_and_Range_use_csharp_syntax()
    {
        Assert.Equal("\"^3\"", JsonSerializer.Serialize(^3, Options));
        Assert.Equal(^3, JsonSerializer.Deserialize<Index>("\"^3\"", Options));
        Assert.Equal("\"1..^2\"", JsonSerializer.Serialize(1..^2, Options));
        Assert.Equal("\"0..^0\"", JsonSerializer.Serialize(Range.All, Options));
        Assert.Equal(new Range(1, ^2), JsonSerializer.Deserialize<Range>("\"1..^2\"", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Range>("\"..5\"", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Index>("\"^2147483648\"", Options));
    }

    [Fact]
    public void Complex_is_an_object_of_two_finite_doubles()
    {
        Assert.Equal("{\"real\":1.5,\"imaginary\":-0}", JsonSerializer.Serialize(new Complex(1.5, -0.0), Options));
        var read = JsonSerializer.Deserialize<Complex>("{\"imaginary\":2,\"real\":-0.1}", Options);
        Assert.Equal((-0.1, 2.0), (read.Real, read.Imaginary));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Complex>("{\"real\":1}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Complex>("{\"real\":1,\"imaginary\":2,\"phase\":0}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Complex>("{\"real\":1,\"real\":2,\"imaginary\":0}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Complex>("{\"real\":1e400,\"imaginary\":0}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new Complex(double.NaN, 0), Options));
    }

    [Theory]
    [InlineData("1.50")]
    [InlineData("\"s\"")]
    [InlineData("true")]
    [InlineData("1E+400")]
    [InlineData("-0")]
    public void JsonValue_keeps_the_scalar_token_it_read(string json)
    {
        var node = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonValue>(json, Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(node, Options));
    }

    [Fact]
    public void JsonValue_refuses_objects_and_arrays_with_a_JsonException()
    {
        // System.Text.Json's own converter throws InvalidOperationException here, which ASP.NET Core answers with 500
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonValue>("[]", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonValue>("{}", Options));
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonValue>("[]"));
        Assert.Null(JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonValue>("null", Options));
    }

    [Fact]
    public void Every_known_time_zone_id_and_culture_name_round_trips_through_the_converters()
    {
        var zones = TimeZoneInfoJsonConverter.KnownIds();
        Assert.Contains(TimeZoneInfo.Utc.Id, zones);
        foreach (var id in zones)
        {
            var json = JsonSerializer.Serialize(id);
            Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<TimeZoneInfo>(json, Options), Options));
        }

        var cultures = CultureInfoJsonConverter.KnownNames();
        Assert.Contains("", cultures); // the invariant culture, in every environment (this test host runs with invariant globalization)
        foreach (var name in cultures)
        {
            var json = JsonSerializer.Serialize(name);
            Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<CultureInfo>(json, Options), Options));
        }

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TimeZoneInfo>("\"Mars/Olympus_Mons\"", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CultureInfo>("\"xx-NOT-A-CULTURE\"", Options));
    }

    [Fact]
    public void The_environment_bound_codecs_record_the_ids_the_server_accepts_as_binding_context()
    {
        var registrations = AdditionalCodecs.Create(_dir);
        var zones = registrations.Single(r => r.TsName == "TimeZoneId").ConverterContext.Single();
        Assert.Equal("zones", zones.Name);
        Assert.False(zones.Confidential);
        Assert.Equal(TimeZoneInfoJsonConverter.KnownIds(), JsonSerializer.Deserialize<string[]>(zones.Value));
        var cultures = registrations.Single(r => r.TsName == "CultureName").ConverterContext.Single();
        Assert.Equal(CultureInfoJsonConverter.KnownNames(), JsonSerializer.Deserialize<string[]>(cultures.Value));
    }

    [Fact]
    public void Projections_and_factories_agree_with_each_other()
    {
        foreach (var registration in AdditionalCodecs.Create(_dir).Where(r => r.TsName != "Complex"))
        {
            var rng = new Random(7);
            for (var i = 0; i < 16; i++)
            {
                var context = registration.ConverterContext.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
                var ast = AdditionalModule.Sample(registration.ModelId, i, rng.Next, context)!;
                var value = registration.Construct!(ast);
                Assert.Equal(DomainText(ast), DomainText(registration.Project!(value)));
                Assert.True(registration.Oracle!(value, registration.Construct(registration.Project(value))));
            }
        }

        static string DomainText(JsonValue ast) => ast switch
        {
            JsonStringValue s => "s:" + s.Value,
            JsonNumberValue n => "n:" + n.Text,
            _ => ast.GetType().Name,
        };
    }
}
