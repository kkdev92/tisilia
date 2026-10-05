using System.Text.Json;
using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

public class SchemaEvaluatorTests
{
    private static readonly Uri Id = new("https://schemas.tisilia.invalid/0.1/test.schema.json");

    private static SchemaEvaluator Load(string schemaBody, params (string File, string Body)[] others)
    {
        var documents = new List<(Uri, string)> { (Id, Document(Id, schemaBody)) };
        foreach (var (file, body) in others)
        {
            var id = new Uri(Id, file);
            documents.Add((id, Document(id, body)));
        }

        return SchemaEvaluator.Load(documents);
    }

    private static string Document(Uri id, string body) =>
        $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"{{id.AbsoluteUri}}",{{body}}}""";

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static IReadOnlyList<SchemaError> Errors(SchemaEvaluator evaluator, string instance)
    {
        var element = Json(instance);
        var errors = evaluator.Evaluate(Id, element);
        Assert.Equal(errors.Count == 0, evaluator.IsValid(Id, element));
        return errors;
    }

    private static string String(string text) => JsonSerializer.Serialize(text);

    // ---------------------------------------------------------------- loading fails closed

    [Theory]
    [InlineData("\"allOf\":[{\"type\":\"string\"}]", "allOf")]
    [InlineData("\"if\":{\"type\":\"string\"}", "if")]
    [InlineData("\"patternProperties\":{\"^a\":true}", "patternProperties")]
    [InlineData("\"unevaluatedProperties\":false", "unevaluatedProperties")]
    [InlineData("\"multipleOf\":2", "multipleOf")]
    [InlineData("\"format\":\"email\"", "email")]
    [InlineData("\"type\":[\"string\",\"null\"]", "expected a string")]
    [InlineData("\"type\":\"text\"", "unknown type")]
    [InlineData("\"enum\":[]", "enum must not be empty")]
    [InlineData("\"oneOf\":[]", "must not be empty")]
    [InlineData("\"minLength\":-1", "non-negative")]
    [InlineData("\"properties\":{\"a\":{\"examples\":[1]}}", "examples")]
    public void A_schema_using_anything_the_evaluator_does_not_implement_is_refused(string body, string named)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load(body));
        Assert.Contains(named, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_that_resolves_nowhere_is_refused_at_load()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load("\"$ref\":\"other.schema.json#/$defs/missing\"", ("other.schema.json", "\"$defs\":{}")));
        Assert.Contains("other.schema.json#/$defs/missing", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_names_its_dialect_and_its_own_id()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaEvaluator.Load([(Id, "{\"$id\":\"" + Id.AbsoluteUri + "\"}")]));
        Assert.Throws<InvalidOperationException>(() => SchemaEvaluator.Load([(Id, "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"$id\":\"" + Id.AbsoluteUri + "\"}")]));
        Assert.Throws<InvalidOperationException>(() => SchemaEvaluator.Load([(Id, "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$id\":\"https://elsewhere.invalid/x.json\"}")]));
    }

    [Fact]
    public void Annotations_are_read_and_ignored()
    {
        var evaluator = Load("\"title\":\"t\",\"$comment\":\"c\",\"description\":\"d\",\"type\":\"string\"");
        Assert.Empty(Errors(evaluator, "\"x\""));
    }

    // ---------------------------------------------------------------- keywords

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e2", true)]
    [InlineData("-0", true)]
    [InlineData("1.5", false)]
    [InlineData("\"1\"", false)]
    [InlineData("true", false)]
    public void An_integer_is_any_number_without_a_fractional_part(string instance, bool valid)
    {
        var evaluator = Load("\"type\":\"integer\"");
        Assert.Equal(valid, Errors(evaluator, instance).Count == 0);
    }

    [Fact]
    public void Type_mismatches_name_both_sides()
    {
        var error = Assert.Single(Errors(Load("\"type\":\"object\""), "[1]"));
        Assert.Equal(("", "type", "expected an object, found an array"), (error.InstanceLocation, error.Keyword, error.Message));
    }

    [Fact]
    public void Required_and_closed_objects_point_at_the_member()
    {
        var evaluator = Load("""
            "type":"object","properties":{"a":{"type":"string"},"b/c":{"type":"integer"}},"required":["a","z"],"additionalProperties":false
            """);
        var errors = Errors(evaluator, """{"a":1,"b/c":"x","extra":true}""");
        Assert.Equal(
            [
                ("", "required", "missing the required property 'z'"),
                ("/a", "type", "expected a string, found an integer"),
                ("/b~1c", "type", "expected an integer, found a string"),
                ("/extra", "additionalProperties", "the property 'extra' is not allowed here"),
            ],
            errors.Select(e => (e.InstanceLocation, e.Keyword, e.Message)));
    }

    [Fact]
    public void Additional_properties_can_be_a_schema()
    {
        var evaluator = Load("\"type\":\"object\",\"properties\":{\"a\":true},\"additionalProperties\":{\"type\":\"integer\"}");
        Assert.Empty(Errors(evaluator, """{"a":"anything","b":1}"""));
        Assert.Equal("/b", Assert.Single(Errors(evaluator, """{"b":"x"}""")).InstanceLocation);
    }

    [Fact]
    public void Const_and_enum_compare_json_values()
    {
        var evaluator = Load("\"enum\":[1,\"1\",{\"a\":[1,2],\"b\":null}]");
        Assert.Empty(Errors(evaluator, "1.0"));
        Assert.Empty(Errors(evaluator, "10e-1"));
        Assert.Empty(Errors(evaluator, "\"1\""));
        Assert.Empty(Errors(evaluator, """{"b":null,"a":[1,2.0]}"""));
        Assert.NotEmpty(Errors(evaluator, """{"a":[2,1],"b":null}"""));
        Assert.NotEmpty(Errors(evaluator, """{"a":[1,2]}"""));
        Assert.NotEmpty(Errors(evaluator, """{"a":[1,2],"b":null,"c":1}"""));
        Assert.Equal("expected one of 1, \"1\", {\"a\":[1,2],\"b\":null}", Assert.Single(Errors(evaluator, "2")).Message);

        var exact = Load("\"const\":true");
        Assert.Empty(Errors(exact, "true"));
        Assert.Equal("expected true", Assert.Single(Errors(exact, "1")).Message);
    }

    [Fact]
    public void Lengths_count_code_points()
    {
        var evaluator = Load("\"type\":\"string\",\"minLength\":2,\"maxLength\":2");
        var grinning = char.ConvertFromUtf32(0x1F600);
        Assert.Empty(Errors(evaluator, String(grinning + grinning)));           // four UTF-16 code units, two code points
        Assert.Empty(Errors(evaluator, String("e" + (char)0x0301)));           // one grapheme cluster, two code points
        Assert.Equal("minLength", Assert.Single(Errors(evaluator, String(grinning))).Keyword);
        Assert.Equal("maxLength", Assert.Single(Errors(evaluator, String("abc"))).Keyword);
    }

    [Fact]
    public void Patterns_are_read_as_ecma_262_reads_them()
    {
        var anchored = Load("\"type\":\"string\",\"pattern\":\"^sha256:[a-f0-9]{4}$\"");
        Assert.Empty(Errors(anchored, String("sha256:00ff")));
        Assert.Equal("does not match ^sha256:[a-f0-9]{4}$", Assert.Single(Errors(anchored, String("sha256:00ff\n"))).Message);

        var dot = Load("\"type\":\"string\",\"pattern\":\"^a.c$\"");
        Assert.Empty(Errors(dot, String("abc")));
        foreach (var terminator in new[] { '\n', '\r', (char)0x2028, (char)0x2029 })
        {
            Assert.NotEmpty(Errors(dot, String("a" + terminator + "c")));
        }

        // Unanchored, as ECMA-262 matches: anywhere in the string.
        Assert.Empty(Errors(Load("\"pattern\":\"b\""), String("abc")));
        // A "$" in a class or escaped, and an escaped ".", are literals.
        var literals = Load("\"pattern\":\"^[$]\\\\$\\\\.x$\"");
        Assert.Empty(Errors(literals, String("$$.x")));
        Assert.NotEmpty(Errors(literals, String("$$ax")));
    }

    [Theory]
    [InlineData("^a$", "^a\\z")]
    [InlineData("^[$.]$", "^[$.]\\z")]
    [InlineData("^\\$\\.$", "^\\$\\.\\z")]
    [InlineData("(?!.*(?:^|/)\\.\\.(?:/|$))", "(?![^\\n\\r{LS}{PS}]*(?:^|/)\\.\\.(?:/|\\z))")]
    public void Translation_rewrites_only_the_end_anchor_and_the_dot(string pattern, string expected)
    {
        Assert.Equal(expected.Replace("{LS}", ((char)0x2028).ToString()).Replace("{PS}", ((char)0x2029).ToString()), SchemaEvaluator.TranslatePattern(pattern));
    }

    [Theory]
    [InlineData("a[]b")]
    [InlineData("a[^]b")]
    public void Empty_character_classes_are_refused(string pattern)
    {
        Assert.Throws<InvalidOperationException>(() => SchemaEvaluator.TranslatePattern(pattern));
    }

    [Theory]
    [InlineData("2026-10-03T12:34:56Z", true)]
    [InlineData("2026-10-03t12:34:56.1234567z", true)]
    [InlineData("2026-10-03T12:34:56+09:00", true)]
    [InlineData("2026-10-03T00:00:00-23:59", true)]
    [InlineData("1990-12-31T23:59:60Z", true)]
    [InlineData("1990-12-31T15:59:60-08:00", true)]
    [InlineData("1991-01-01T08:59:60+09:00", true)]
    [InlineData("2024-02-29T00:00:00Z", true)]
    [InlineData("2000-02-29T00:00:00Z", true)]
    [InlineData("0000-02-29T00:00:00Z", true)]
    [InlineData("2026-10-03 12:34:56Z", false)]
    [InlineData("2026-10-03_12:34:56Z", false)]
    [InlineData("2026-10-03T12:34:56Z\n", false)]
    [InlineData("2026-10-03T12:34:56", false)]
    [InlineData("2026-10-03T12:34:56.Z", false)]
    [InlineData("2026-10-03T12:34:56+0900", false)]
    [InlineData("2026-10-03T12:34:56+24:00", false)]
    [InlineData("2026-10-03T12:34:56+09:60", false)]
    [InlineData("2023-02-29T00:00:00Z", false)]
    [InlineData("1900-02-29T00:00:00Z", false)]
    [InlineData("2026-04-31T00:00:00Z", false)]
    [InlineData("2026-13-01T00:00:00Z", false)]
    [InlineData("2026-00-01T00:00:00Z", false)]
    [InlineData("2026-10-00T00:00:00Z", false)]
    [InlineData("2026-10-03T24:00:00Z", false)]
    [InlineData("2026-10-03T12:60:00Z", false)]
    [InlineData("2026-10-03T12:34:60Z", false)]
    [InlineData("1990-12-31T23:59:60+01:00", false)]
    [InlineData("2026-1-03T12:34:56Z", false)]
    [InlineData("26-10-03T12:34:56Z", false)]
    public void Date_time_is_rfc_3339(string text, bool valid)
    {
        Assert.Equal(valid, SchemaEvaluator.IsDateTime(text));
        var evaluator = Load("\"type\":\"string\",\"format\":\"date-time\"");
        Assert.Equal(valid, Errors(evaluator, String(text)).Count == 0);
    }

    [Fact]
    public void Date_time_digits_are_ascii()
    {
        var fullwidthOne = (char)0xFF11;
        Assert.False(SchemaEvaluator.IsDateTime(fullwidthOne + "990-12-31T23:59:59Z"));
    }

    [Fact]
    public void Minimum_and_maximum_are_inclusive()
    {
        var evaluator = Load("\"type\":\"integer\",\"minimum\":200,\"maximum\":599");
        Assert.Empty(Errors(evaluator, "200"));
        Assert.Empty(Errors(evaluator, "599"));
        Assert.Equal("expected a number of at least 200", Assert.Single(Errors(evaluator, "199")).Message);
        Assert.Equal("expected a number of at most 599", Assert.Single(Errors(evaluator, "600")).Message);
        Assert.Equal("maximum", Assert.Single(Errors(evaluator, "1e400")).Keyword);
    }

    [Fact]
    public void Arrays_check_their_count_their_items_and_uniqueness()
    {
        var evaluator = Load("\"type\":\"array\",\"items\":{\"enum\":[\"a\",\"b\",\"c\"]},\"minItems\":1,\"uniqueItems\":true");
        Assert.Empty(Errors(evaluator, """["a","b"]"""));
        Assert.Equal("expected at least 1 item(s), found 0", Assert.Single(Errors(evaluator, "[]")).Message);
        Assert.Equal("items 0 and 2 are equal", Assert.Single(Errors(evaluator, """["a","b","a"]""")).Message);
        Assert.Equal("/1", Assert.Single(Errors(evaluator, """["a","x"]""")).InstanceLocation);
    }

    [Fact]
    public void References_resolve_within_and_across_documents()
    {
        var evaluator = Load(
            "\"type\":\"object\",\"properties\":{\"id\":{\"$ref\":\"common.schema.json#/$defs/id\"},\"tree\":{\"$ref\":\"common.schema.json#/$defs/tree\"}}",
            ("common.schema.json", """
                "$defs":{
                  "id":{"type":"string","pattern":"^[a-z]+$"},
                  "tree":{"type":"object","properties":{"name":{"$ref":"#/$defs/id"},"children":{"type":"array","items":{"$ref":"#/$defs/tree"}}},"additionalProperties":false}
                }
                """));
        Assert.Empty(Errors(evaluator, """{"id":"abc","tree":{"name":"root","children":[{"name":"leaf","children":[]}]}}"""));
        var error = Assert.Single(Errors(evaluator, """{"id":"abc","tree":{"children":[{"name":"Leaf"}]}}"""));
        Assert.Equal(("/tree/children/0/name", "pattern"), (error.InstanceLocation, error.Keyword));
    }

    [Fact]
    public void One_of_reports_the_alternative_its_discriminator_selected()
    {
        var evaluator = Load("""
            "oneOf":[
              {"type":"object","properties":{"kind":{"const":"json"},"mediaType":{"type":"string"}},"required":["kind","mediaType"],"additionalProperties":false},
              {"type":"object","properties":{"kind":{"const":"text"},"charset":{"type":"string"}},"required":["kind"],"additionalProperties":false},
              {"type":"object","properties":{"kind":{"enum":["none","empty"]}},"required":["kind"],"additionalProperties":false}
            ]
            """);
        Assert.Empty(Errors(evaluator, """{"kind":"json","mediaType":"application/json"}"""));
        Assert.Equal(
            [("", "required", "missing the required property 'mediaType'"), ("/charset", "additionalProperties", "the property 'charset' is not allowed here")],
            Errors(evaluator, """{"kind":"json","charset":"utf-8"}""").Select(e => (e.InstanceLocation, e.Keyword, e.Message)));
        Assert.Equal(("/extra", "additionalProperties"), Errors(evaluator, """{"kind":"empty","extra":1}""").Select(e => (e.InstanceLocation, e.Keyword)).Single());
        // No alternative stands out: an unknown kind, or no kind at all.
        Assert.Equal(("", "oneOf", "matches none of the 3 alternatives"), Errors(evaluator, """{"kind":"form"}""").Select(e => (e.InstanceLocation, e.Keyword, e.Message)).Single());
        Assert.Equal("oneOf", Assert.Single(Errors(evaluator, "{}")).Keyword);
        Assert.Equal("oneOf", Assert.Single(Errors(evaluator, "\"json\"")).Keyword);
    }

    [Fact]
    public void One_of_refuses_an_instance_more_than_one_alternative_accepts()
    {
        var evaluator = Load("\"oneOf\":[{\"type\":\"integer\"},{\"minimum\":0}]");
        Assert.Empty(Errors(evaluator, "-1"));
        Assert.Empty(Errors(evaluator, "0.5"));
        Assert.Equal("matches 2 of the 2 alternatives; exactly one is allowed", Assert.Single(Errors(evaluator, "3")).Message);
    }

    [Fact]
    public void Any_of_needs_one_alternative()
    {
        var evaluator = Load("\"anyOf\":[{\"type\":\"integer\"},{\"minimum\":0}]");
        Assert.Empty(Errors(evaluator, "3"));
        Assert.Empty(Errors(evaluator, "0.5"));
        // The integer alternative rejects a fraction by its type, which leaves the other one to explain the failure.
        Assert.Equal(("minimum", "expected a number of at least 0"), Errors(evaluator, "-0.5").Select(e => (e.Keyword, e.Message)).Single());

        var neither = Load("\"anyOf\":[{\"minimum\":0},{\"maximum\":-1}]");
        Assert.Equal(("anyOf", "matches none of the 2 alternatives"), Errors(neither, "-0.5").Select(e => (e.Keyword, e.Message)).Single());
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0.0", true)]
    [InlineData("0e-999999999999", true)]
    [InlineData("100e-2", true)]
    [InlineData("150e-2", false)]
    [InlineData("1.5e1", true)]
    [InlineData("1.25e1", false)]
    [InlineData("12.50E+1", true)]
    [InlineData("1e400", true)]
    [InlineData("1e-400", false)]
    [InlineData("123456789012345678901234567890.0", true)]
    [InlineData("0.30000000000000004", false)]
    public void Integrality_is_decided_from_the_lexeme(string lexeme, bool integral)
    {
        Assert.Equal(integral, SchemaEvaluator.IsIntegral(Json(lexeme)));
    }

    [Fact]
    public void Numbers_beyond_binary_ranges_still_compare()
    {
        var evaluator = Load("\"minimum\":0,\"maximum\":599");
        Assert.Empty(Errors(evaluator, "1e-400"));
        Assert.Equal("minimum", Assert.Single(Errors(evaluator, "-1e-400")).Keyword);
        Assert.Equal("maximum", Assert.Single(Errors(evaluator, "1e400")).Keyword);
        Assert.Equal("minimum", Assert.Single(Errors(evaluator, "-1e400")).Keyword);
    }

    [Fact]
    public void Boolean_schemas()
    {
        Assert.Empty(Errors(Load("\"properties\":{\"a\":true}"), """{"a":[1]}"""));
        Assert.Equal(("/a", "false"), Errors(Load("\"properties\":{\"a\":false}"), """{"a":1}""").Select(e => (e.InstanceLocation, e.Keyword)).Single());
    }

    // ---------------------------------------------------------------- the embedded schemas

    [Fact]
    public void The_embedded_documents_of_the_fixtures_validate()
    {
        var root = FixtureTests.RepoRoot();
        var contracts = Directory.GetFiles(Path.Combine(root, "tests", "fixtures"), "*.contract.json");
        Assert.NotEmpty(contracts);
        foreach (var path in contracts)
        {
            var bag = new DiagnosticBag();
            var node = TisiliaSchemas.ParseStrict(File.ReadAllText(path), bag)!;
            Assert.True(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Contract, node, bag), path + "\n" + string.Join("\n", bag.Items));
        }
    }

    [Fact]
    public void Every_mutation_of_a_contract_is_judged_the_same_by_both_paths()
    {
        // The fast path (IsValid) and the reporting path (Evaluate) are written separately; they must agree on every
        // document, and every error must point at a location that exists in it.
        var text = File.ReadAllText(Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures", "sample-api.contract.json"));
        var random = new Random(20261003);
        var invalid = 0;
        for (var round = 0; round < 400; round++)
        {
            var node = JsonNode.Parse(text)!;
            Mutate(node, random);
            var bag = new DiagnosticBag();
            var valid = TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Contract, node, bag);
            Assert.Equal(valid, bag.Items.Count == 0);
            if (!valid)
            {
                invalid++;
                foreach (var diagnostic in bag.Items)
                {
                    Assert.True(Resolves(node, diagnostic.Path), $"round {round}: {diagnostic}");
                }
            }
        }

        Assert.True(invalid > 300, "the mutations should mostly break the document: " + invalid);
    }

    private static void Mutate(JsonNode root, Random random)
    {
        var containers = new List<JsonNode>();
        Walk(root, containers);
        var target = containers[random.Next(containers.Count)];
        switch (target)
        {
            case JsonObject obj when obj.Count > 0 && random.Next(3) == 0:
                obj.Remove(obj.ElementAt(random.Next(obj.Count)).Key);
                break;
            case JsonObject obj when random.Next(2) == 0:
                obj["unexpected" + random.Next(10)] = 1;
                break;
            case JsonObject obj when obj.Count > 0:
                obj[obj.ElementAt(random.Next(obj.Count)).Key] = Replacement(random);
                break;
            case JsonArray arr when arr.Count > 0 && random.Next(2) == 0:
                arr[random.Next(arr.Count)] = Replacement(random);
                break;
            case JsonArray arr when arr.Count > 0:
                arr.RemoveAt(random.Next(arr.Count));
                break;
            case JsonArray arr:
                arr.Add(Replacement(random));
                break;
        }
    }

    private static JsonNode? Replacement(Random random) => random.Next(7) switch
    {
        0 => null,
        1 => NodeValue.Create(true),
        2 => NodeValue.Create(random.Next(-5, 1000)),
        3 => NodeValue.Create("x" + random.Next(100)),
        4 => NodeValue.Create(""),
        5 => new JsonArray(),
        _ => new JsonObject(),
    };

    private static void Walk(JsonNode? node, List<JsonNode> containers)
    {
        switch (node)
        {
            case JsonObject obj:
                containers.Add(obj);
                foreach (var (_, child) in obj)
                {
                    Walk(child, containers);
                }

                break;
            case JsonArray arr:
                containers.Add(arr);
                foreach (var child in arr)
                {
                    Walk(child, containers);
                }

                break;
        }
    }

    private static bool Resolves(JsonNode root, string pointer)
    {
        if (pointer.Length == 0)
        {
            return true;
        }

        JsonNode? current = root;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (current)
            {
                case JsonObject obj when obj.ContainsKey(token):
                    current = obj[token];
                    break;
                case JsonArray arr when int.TryParse(token, out var i) && i >= 0 && i < arr.Count:
                    current = arr[i];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
