using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tisilia.Generator.Validation;

/// <summary>One failed assertion: where in the instance, which keyword, and what was expected.</summary>
internal readonly record struct SchemaError(string InstanceLocation, string Keyword, string Message);

/// <summary>
/// Evaluates JSON documents against Tisilia's own JSON Schema (draft 2020-12) documents. It implements the keywords those
/// schemas use and nothing more, and a schema that uses any other keyword, or a keyword in a shape this evaluator does
/// not read, is refused when it is loaded: a keyword passed over in silence would let through documents the schema was
/// written to stop.
/// </summary>
/// <remarks>
/// Semantics follow the specification rather than any one implementation: string lengths count Unicode code points,
/// <c>pattern</c> is matched as ECMA-262 reads it (<c>$</c> is the end of the input, <c>.</c> stops at line
/// terminators), <c>integer</c> is any number with no fractional part, and <c>format</c> is asserted.
/// </remarks>
internal sealed class SchemaEvaluator
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    // Keywords that annotate and never assert.
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal) { "$comment", "title", "description" };

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, Schema> _byLocation = new(StringComparer.Ordinal);
    private readonly Dictionary<Uri, Schema> _roots = new();

    private SchemaEvaluator()
    {
    }

    /// <summary>Loads a set of schema documents whose <c>$ref</c>s point only at each other.</summary>
    /// <exception cref="InvalidOperationException">A document uses a keyword, a value shape or a reference this evaluator does not support.</exception>
    public static SchemaEvaluator Load(IEnumerable<(Uri Id, string Text)> documents)
    {
        var evaluator = new SchemaEvaluator();
        var references = new List<Schema>();
        foreach (var (id, text) in documents)
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Refuse(id, "", "a schema document must be an object");
            }

            if (!root.TryGetProperty("$schema", out var dialect) || dialect.ValueKind != JsonValueKind.String || dialect.GetString() != Draft202012)
            {
                throw Refuse(id, "", "$schema must name draft 2020-12");
            }

            if (!root.TryGetProperty("$id", out var declared) || declared.ValueKind != JsonValueKind.String || declared.GetString() != id.AbsoluteUri)
            {
                throw Refuse(id, "", "$id must be the URI the document is loaded under, " + id.AbsoluteUri);
            }

            evaluator._roots[id] = evaluator.Compile(id, "", root, references);
        }

        foreach (var schema in references)
        {
            if (!evaluator._byLocation.TryGetValue(schema.RefTarget!, out var target))
            {
                throw new InvalidOperationException($"schema reference '{schema.RefTarget}' does not resolve to a loaded schema");
            }

            schema.Ref = target;
        }

        return evaluator;
    }

    /// <summary>Whether <paramref name="instance"/> satisfies the document loaded as <paramref name="schemaId"/>.</summary>
    public bool IsValid(Uri schemaId, JsonElement instance) => Valid(Root(schemaId), instance);

    /// <summary>Every failed assertion, in document order. Empty when the instance is valid.</summary>
    public IReadOnlyList<SchemaError> Evaluate(Uri schemaId, JsonElement instance)
    {
        var root = Root(schemaId);
        if (Valid(root, instance))
        {
            return [];
        }

        var errors = new List<SchemaError>();
        Collect(root, instance, "", errors);
        if (errors.Count == 0)
        {
            // Every assertion the fast path makes has a report here; this keeps a gap between the two failing closed.
            errors.Add(new SchemaError("", "schema", "the document does not satisfy its schema"));
        }

        return errors;
    }

    private Schema Root(Uri schemaId) =>
        _roots.TryGetValue(schemaId, out var root) ? root : throw new ArgumentException("no schema was loaded as " + schemaId, nameof(schemaId));

    // ---------------------------------------------------------------- loading

    private sealed class Schema
    {
        public bool? Boolean;
        public string? Type;
        public Dictionary<string, Schema>? Properties;
        public string[]? Required;
        public Schema? AdditionalProperties;
        public Schema? Items;
        public JsonElement? Const;
        public JsonElement[]? Enum;
        public int? MinItems;
        public int? MaxItems;
        public bool UniqueItems;
        public int? MinLength;
        public int? MaxLength;
        public NumberLexeme? Minimum;
        public NumberLexeme? Maximum;
        public Regex? Pattern;
        public string? PatternSource;
        public string? Format;
        public Schema[]? OneOf;
        public Schema[]? AnyOf;
        public string? RefTarget;
        public Schema? Ref;
    }

    private Schema Compile(Uri document, string pointer, JsonElement node, List<Schema> references)
    {
        var schema = new Schema();
        _byLocation[document.AbsoluteUri + "#" + pointer] = schema;
        if (node.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            schema.Boolean = node.ValueKind == JsonValueKind.True;
            return schema;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw Refuse(document, pointer, "a schema must be an object or a boolean");
        }

        foreach (var keyword in node.EnumerateObject())
        {
            var at = pointer + "/" + Diagnostics.JsonPointer.Escape(keyword.Name);
            var value = keyword.Value;
            switch (keyword.Name)
            {
                case "$schema" or "$id" when pointer.Length == 0:
                    break;
                case "$defs":
                    foreach (var definition in ObjectOf(document, at, value).EnumerateObject())
                    {
                        Compile(document, at + "/" + Diagnostics.JsonPointer.Escape(definition.Name), definition.Value, references);
                    }

                    break;
                case "$ref":
                    var reference = StringOf(document, at, value);
                    var target = new Uri(document, reference);
                    schema.RefTarget = target.GetLeftPart(UriPartial.Query) + "#" + Uri.UnescapeDataString(target.Fragment.TrimStart('#'));
                    references.Add(schema);
                    break;
                case "type":
                    schema.Type = StringOf(document, at, value) switch
                    {
                        var t and ("object" or "array" or "string" or "boolean" or "integer" or "number" or "null") => t,
                        var t => throw Refuse(document, at, $"unknown type '{t}'"),
                    };
                    break;
                case "properties":
                    schema.Properties = new Dictionary<string, Schema>(StringComparer.Ordinal);
                    foreach (var property in ObjectOf(document, at, value).EnumerateObject())
                    {
                        schema.Properties[property.Name] = Compile(document, at + "/" + Diagnostics.JsonPointer.Escape(property.Name), property.Value, references);
                    }

                    break;
                case "required":
                    schema.Required = [.. ArrayOf(document, at, value).EnumerateArray().Select(v => StringOf(document, at, v))];
                    break;
                case "additionalProperties":
                    schema.AdditionalProperties = Compile(document, at, value, references);
                    break;
                case "items":
                    schema.Items = Compile(document, at, value, references);
                    break;
                case "const":
                    schema.Const = value.Clone();
                    break;
                case "enum":
                    schema.Enum = [.. ArrayOf(document, at, value).EnumerateArray().Select(v => v.Clone())];
                    if (schema.Enum.Length == 0)
                    {
                        throw Refuse(document, at, "enum must not be empty");
                    }

                    break;
                case "minItems":
                    schema.MinItems = CountOf(document, at, value);
                    break;
                case "maxItems":
                    schema.MaxItems = CountOf(document, at, value);
                    break;
                case "uniqueItems":
                    schema.UniqueItems = value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw Refuse(document, at, "uniqueItems must be a boolean"),
                    };
                    break;
                case "minLength":
                    schema.MinLength = CountOf(document, at, value);
                    break;
                case "maxLength":
                    schema.MaxLength = CountOf(document, at, value);
                    break;
                case "minimum":
                    schema.Minimum = NumberOf(document, at, value);
                    break;
                case "maximum":
                    schema.Maximum = NumberOf(document, at, value);
                    break;
                case "pattern":
                    schema.PatternSource = StringOf(document, at, value);
                    schema.Pattern = new Regex(TranslatePattern(schema.PatternSource), RegexOptions.ECMAScript, PatternTimeout);
                    break;
                case "format":
                    schema.Format = StringOf(document, at, value) switch
                    {
                        "date-time" => "date-time",
                        var f => throw Refuse(document, at, $"format '{f}' is not supported"),
                    };
                    break;
                case "oneOf":
                    schema.OneOf = CompileAll(document, at, value, references);
                    break;
                case "anyOf":
                    schema.AnyOf = CompileAll(document, at, value, references);
                    break;
                default:
                    if (!Annotations.Contains(keyword.Name))
                    {
                        throw Refuse(document, at, $"keyword '{keyword.Name}' is not supported");
                    }

                    break;
            }
        }

        return schema;
    }

    private Schema[] CompileAll(Uri document, string at, JsonElement value, List<Schema> references)
    {
        var alternatives = ArrayOf(document, at, value).EnumerateArray()
            .Select((alternative, i) => Compile(document, at + "/" + i.ToString(CultureInfo.InvariantCulture), alternative, references))
            .ToArray();
        return alternatives.Length > 0 ? alternatives : throw Refuse(document, at, "a list of subschemas must not be empty");
    }

    private static JsonElement ObjectOf(Uri document, string at, JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value : throw Refuse(document, at, "expected an object");

    private static JsonElement ArrayOf(Uri document, string at, JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value : throw Refuse(document, at, "expected an array");

    private static string StringOf(Uri document, string at, JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Refuse(document, at, "expected a string");

    private static int CountOf(Uri document, string at, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0
            ? count
            : throw Refuse(document, at, "expected a non-negative integer");

    private static NumberLexeme NumberOf(Uri document, string at, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? NumberLexeme.Parse(value.GetRawText()) : throw Refuse(document, at, "expected a number");

    private static InvalidOperationException Refuse(Uri document, string at, string message) =>
        new($"{document.AbsoluteUri}#{at}: {message}");

    /// <summary>
    /// Rewrites an ECMA-262 pattern so that .NET reads it the same way. Two constructs differ: <c>$</c>, which in .NET also
    /// matches before a final line feed, becomes the end of the input; and <c>.</c>, which in .NET also matches
    /// <c>\r</c>, U+2028 and U+2029, stops at every ECMA-262 line terminator. Escapes and character classes are copied as
    /// they are.
    /// </summary>
    internal static string TranslatePattern(string pattern)
    {
        var anyButLineTerminator = "[^\\n\\r" + (char)0x2028 + (char)0x2029 + "]";
        var sb = new StringBuilder(pattern.Length + 16);
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                sb.Append(c);
                if (i + 1 < pattern.Length)
                {
                    sb.Append(pattern[++i]);
                }

                continue;
            }

            if (inClass)
            {
                inClass = c != ']';
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    // "[]" and "[^]" mean "nothing" and "anything" in ECMA-262 but open a class that ends later in .NET.
                    if (i + 1 < pattern.Length && (pattern[i + 1] == ']' || (pattern[i + 1] == '^' && i + 2 < pattern.Length && pattern[i + 2] == ']')))
                    {
                        throw new InvalidOperationException("pattern '" + pattern + "': an empty character class is not supported");
                    }

                    inClass = true;
                    sb.Append(c);
                    break;
                case '$':
                    sb.Append("\\z");
                    break;
                case '.':
                    sb.Append(anyButLineTerminator);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- evaluation

    // The fast path: no error is built, and every keyword stops at its first failure.
    private bool Valid(Schema schema, JsonElement instance)
    {
        if (schema.Boolean is { } constant)
        {
            return constant;
        }

        if (schema.Ref is not null && !Valid(schema.Ref, instance))
        {
            return false;
        }

        if (schema.Type is not null && !HasType(instance, schema.Type))
        {
            return false;
        }

        if (schema.Const is { } expected && !JsonEquals(instance, expected))
        {
            return false;
        }

        if (schema.Enum is not null && !schema.Enum.Any(e => JsonEquals(instance, e)))
        {
            return false;
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                if (schema.Required is not null && schema.Required.Any(name => !instance.TryGetProperty(name, out _)))
                {
                    return false;
                }

                if (schema.Properties is not null || schema.AdditionalProperties is not null)
                {
                    foreach (var member in instance.EnumerateObject())
                    {
                        var declared = Declared(schema, member.Name);
                        if (declared is not null ? !Valid(declared, member.Value) : schema.AdditionalProperties is { } extra && !Valid(extra, member.Value))
                        {
                            return false;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                var count = instance.GetArrayLength();
                if (count < schema.MinItems || count > schema.MaxItems || (schema.UniqueItems && FirstDuplicate(instance) is not null))
                {
                    return false;
                }

                if (schema.Items is not null && instance.EnumerateArray().Any(item => !Valid(schema.Items, item)))
                {
                    return false;
                }

                break;
            case JsonValueKind.String:
                if (!StringValid(schema, instance.GetString()!))
                {
                    return false;
                }

                break;
            case JsonValueKind.Number:
                if (!NumberValid(schema, instance))
                {
                    return false;
                }

                break;
        }

        if (schema.OneOf is not null && Matches(schema.OneOf, instance, stopAt: 2) != 1)
        {
            return false;
        }

        return schema.AnyOf is null || Matches(schema.AnyOf, instance, stopAt: 1) == 1;
    }

    private int Matches(Schema[] alternatives, JsonElement instance, int stopAt)
    {
        var matched = 0;
        foreach (var alternative in alternatives)
        {
            if (Valid(alternative, instance) && ++matched == stopAt)
            {
                break;
            }
        }

        return matched;
    }

    // The reporting path, taken only for an instance already known to be invalid somewhere.
    private void Collect(Schema schema, JsonElement instance, string location, List<SchemaError> errors)
    {
        if (schema.Boolean is { } constant)
        {
            if (!constant)
            {
                errors.Add(new SchemaError(location, "false", "no value is allowed here"));
            }

            return;
        }

        if (schema.Ref is not null)
        {
            Collect(schema.Ref, instance, location, errors);
        }

        if (schema.Type is not null && !HasType(instance, schema.Type))
        {
            errors.Add(new SchemaError(location, "type", $"expected {Article(schema.Type)}, found {KindName(instance)}"));
        }

        if (schema.Const is { } expected && !JsonEquals(instance, expected))
        {
            errors.Add(new SchemaError(location, "const", "expected " + expected.GetRawText()));
        }

        if (schema.Enum is not null && !schema.Enum.Any(e => JsonEquals(instance, e)))
        {
            errors.Add(new SchemaError(location, "enum", "expected one of " + string.Join(", ", schema.Enum.Select(e => e.GetRawText()))));
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var name in schema.Required ?? [])
                {
                    if (!instance.TryGetProperty(name, out _))
                    {
                        errors.Add(new SchemaError(location, "required", $"missing the required property '{name}'"));
                    }
                }

                if (schema.Properties is not null || schema.AdditionalProperties is not null)
                {
                    foreach (var member in instance.EnumerateObject())
                    {
                        var at = Diagnostics.JsonPointer.Append(location, member.Name);
                        var declared = Declared(schema, member.Name);
                        if (declared is not null)
                        {
                            Collect(declared, member.Value, at, errors);
                        }
                        else if (schema.AdditionalProperties is { Boolean: false })
                        {
                            errors.Add(new SchemaError(at, "additionalProperties", $"the property '{member.Name}' is not allowed here"));
                        }
                        else if (schema.AdditionalProperties is { } extra)
                        {
                            Collect(extra, member.Value, at, errors);
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                var count = instance.GetArrayLength();
                if (count < schema.MinItems)
                {
                    errors.Add(new SchemaError(location, "minItems", $"expected at least {schema.MinItems} item(s), found {count}"));
                }

                if (count > schema.MaxItems)
                {
                    errors.Add(new SchemaError(location, "maxItems", $"expected at most {schema.MaxItems} item(s), found {count}"));
                }

                if (schema.UniqueItems && FirstDuplicate(instance) is var (first, second))
                {
                    errors.Add(new SchemaError(location, "uniqueItems", $"items {first} and {second} are equal"));
                }

                if (schema.Items is not null)
                {
                    var index = 0;
                    foreach (var item in instance.EnumerateArray())
                    {
                        Collect(schema.Items, item, Diagnostics.JsonPointer.Append(location, index++), errors);
                    }
                }

                break;
            case JsonValueKind.String:
                var text = instance.GetString()!;
                var length = CodePoints(text);
                if (length < schema.MinLength)
                {
                    errors.Add(new SchemaError(location, "minLength", $"expected at least {schema.MinLength} character(s), found {length}"));
                }

                if (length > schema.MaxLength)
                {
                    errors.Add(new SchemaError(location, "maxLength", $"expected at most {schema.MaxLength} character(s), found {length}"));
                }

                if (schema.Pattern is not null && !schema.Pattern.IsMatch(text))
                {
                    errors.Add(new SchemaError(location, "pattern", "does not match " + schema.PatternSource));
                }

                if (schema.Format == "date-time" && !IsDateTime(text))
                {
                    errors.Add(new SchemaError(location, "format", "expected an RFC 3339 date-time"));
                }

                break;
            case JsonValueKind.Number:
                var value = NumberLexeme.Parse(instance.GetRawText());
                if (schema.Minimum is { } minimum && value.CompareTo(minimum) < 0)
                {
                    errors.Add(new SchemaError(location, "minimum", "expected a number of at least " + minimum.Text));
                }

                if (schema.Maximum is { } maximum && value.CompareTo(maximum) > 0)
                {
                    errors.Add(new SchemaError(location, "maximum", "expected a number of at most " + maximum.Text));
                }

                break;
        }

        if (schema.OneOf is not null)
        {
            var matched = schema.OneOf.Count(alternative => Valid(alternative, instance));
            if (matched == 0)
            {
                ReportNoAlternative(schema.OneOf, instance, location, errors, "oneOf");
            }
            else if (matched > 1)
            {
                errors.Add(new SchemaError(location, "oneOf", $"matches {matched} of the {schema.OneOf.Length} alternatives; exactly one is allowed"));
            }
        }

        if (schema.AnyOf is not null && !schema.AnyOf.Any(alternative => Valid(alternative, instance)))
        {
            ReportNoAlternative(schema.AnyOf, instance, location, errors, "anyOf");
        }
    }

    /// <summary>
    /// When no alternative matches, the useful errors are those of the alternative the instance was evidently written
    /// for: the one whose discriminating member (a <c>const</c> or <c>enum</c> on a property, such as <c>kind</c>)
    /// accepted it. When no single alternative stands out, the report names the keyword and the count.
    /// </summary>
    private void ReportNoAlternative(Schema[] alternatives, JsonElement instance, string location, List<SchemaError> errors, string keyword)
    {
        List<SchemaError>? chosen = null;
        var candidates = 0;
        foreach (var alternative in alternatives)
        {
            var own = new List<SchemaError>();
            Collect(alternative, instance, location, own);
            var rejectedOutright = own.Any(e =>
                (e.Keyword == "type" && e.InstanceLocation == location)
                || (e.Keyword is "const" or "enum" && IsChildOf(e.InstanceLocation, location)));
            if (!rejectedOutright)
            {
                candidates++;
                chosen = own;
            }
        }

        if (candidates == 1)
        {
            errors.AddRange(chosen!);
        }
        else
        {
            errors.Add(new SchemaError(location, keyword, $"matches none of the {alternatives.Length} alternatives"));
        }
    }

    private static bool IsChildOf(string candidate, string parent) =>
        candidate.Length > parent.Length + 1
        && candidate.StartsWith(parent + "/", StringComparison.Ordinal)
        && candidate.IndexOf('/', parent.Length + 1) < 0;

    private static Schema? Declared(Schema schema, string name) =>
        schema.Properties is not null && schema.Properties.TryGetValue(name, out var declared) ? declared : null;

    private static bool StringValid(Schema schema, string text)
    {
        if (schema.MinLength is not null || schema.MaxLength is not null)
        {
            var length = CodePoints(text);
            if (length < schema.MinLength || length > schema.MaxLength)
            {
                return false;
            }
        }

        return (schema.Pattern is null || schema.Pattern.IsMatch(text))
            && (schema.Format != "date-time" || IsDateTime(text));
    }

    private static bool NumberValid(Schema schema, JsonElement number)
    {
        if (schema.Minimum is null && schema.Maximum is null)
        {
            return true;
        }

        var value = NumberLexeme.Parse(number.GetRawText());
        return (schema.Minimum is not { } minimum || value.CompareTo(minimum) >= 0)
            && (schema.Maximum is not { } maximum || value.CompareTo(maximum) <= 0);
    }

    private static bool HasType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && IsIntegral(instance),
        _ => false,
    };

    /// <summary>Draft 2020-12: an integer is a number with a zero fractional part, however it is spelled (<c>1.0</c>, <c>1e2</c>, <c>1e400</c>).</summary>
    internal static bool IsIntegral(JsonElement number) => NumberLexeme.Parse(number.GetRawText()).IsIntegral;

    private static string KindName(JsonElement instance) => instance.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => IsIntegral(instance) ? "an integer" : "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Article(string type) => type switch
    {
        "object" or "array" or "integer" => "an " + type,
        "null" => "null",
        _ => "a " + type,
    };

    private static int CodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    private static (int First, int Second)? FirstDuplicate(JsonElement array)
    {
        var items = array.EnumerateArray().ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            for (var j = i + 1; j < items.Length; j++)
            {
                if (JsonEquals(items[i], items[j]))
                {
                    return (i, j);
                }
            }
        }

        return null;
    }

    /// <summary>JSON value equality: numbers by value, strings by code unit, objects regardless of member order.</summary>
    internal static bool JsonEquals(JsonElement a, JsonElement b)
    {
        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                if (b.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }

                return NumberLexeme.Parse(a.GetRawText()).CompareTo(NumberLexeme.Parse(b.GetRawText())) == 0;
            case JsonValueKind.String:
                return b.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (b.ValueKind != JsonValueKind.Array || a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                using (var left = a.EnumerateArray())
                using (var right = b.EnumerateArray())
                {
                    while (left.MoveNext() && right.MoveNext())
                    {
                        if (!JsonEquals(left.Current, right.Current))
                        {
                            return false;
                        }
                    }
                }

                return true;
            case JsonValueKind.Object:
                if (b.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var count = 0;
                foreach (var member in a.EnumerateObject())
                {
                    count++;
                    if (!b.TryGetProperty(member.Name, out var other) || !JsonEquals(member.Value, other))
                    {
                        return false;
                    }
                }

                return count == b.EnumerateObject().Count();
            default:
                return a.ValueKind == b.ValueKind;
        }
    }

    /// <summary>
    /// RFC 3339 §5.6 <c>date-time</c>: <c>YYYY-MM-DDTHH:MM:SS[.fraction](Z|±HH:MM)</c>, with <c>T</c> and <c>Z</c> in
    /// either case, real calendar dates, and a leap second (<c>:60</c>) only where the time is 23:59 in UTC.
    /// </summary>
    internal static bool IsDateTime(string text)
    {
        // 19 = "YYYY-MM-DDTHH:MM:SS", then an optional fraction, then "Z" or "+HH:MM".
        if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] is not ('T' or 't') || text[13] != ':' || text[16] != ':')
        {
            return false;
        }

        if (!Digits(text, 0, 4, out var year) || !Digits(text, 5, 2, out var month) || !Digits(text, 8, 2, out var day)
            || !Digits(text, 11, 2, out var hour) || !Digits(text, 14, 2, out var minute) || !Digits(text, 17, 2, out var second))
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1 || day > DaysInMonth(year, month))
        {
            return false;
        }

        if (hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        var i = 19;
        if (text[i] == '.')
        {
            var start = ++i;
            while (i < text.Length && text[i] is >= '0' and <= '9')
            {
                i++;
            }

            if (i == start)
            {
                return false;
            }
        }

        int offsetMinutes;
        if (i < text.Length && text[i] is 'Z' or 'z')
        {
            offsetMinutes = 0;
            i++;
        }
        else if (i + 6 == text.Length && text[i] is '+' or '-' && text[i + 3] == ':'
            && Digits(text, i + 1, 2, out var offsetHour) && Digits(text, i + 4, 2, out var offsetMinute)
            && offsetHour <= 23 && offsetMinute <= 59)
        {
            offsetMinutes = (text[i] == '-' ? -1 : 1) * ((offsetHour * 60) + offsetMinute);
            i += 6;
        }
        else
        {
            return false;
        }

        if (i != text.Length)
        {
            return false;
        }

        if (second == 60)
        {
            var utc = ((((hour * 60) + minute - offsetMinutes) % 1440) + 1440) % 1440;
            return utc == (23 * 60) + 59;
        }

        return true;
    }

    // Proleptic Gregorian, as RFC 3339 counts, so the year 0000 (a leap year) is a valid date too.
    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    private static bool Digits(string text, int start, int count, out int value)
    {
        value = 0;
        for (var i = start; i < start + count; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (text[i] - '0');
        }

        return true;
    }
}

/// <summary>
/// A JSON number read exactly from its lexeme as <c>sign × 0.Digits × 10^Order</c>, with no leading or trailing zero in
/// <see cref="Digits"/>. Comparing two of them needs no binary type, so <c>1e400</c>, <c>-1e-400</c> and
/// <c>0.30000000000000004</c> are compared as the numbers they are rather than as what a <c>double</c> or a
/// <c>decimal</c> would round them to.
/// </summary>
internal readonly record struct NumberLexeme(int Sign, string Digits, long Order, string Text)
{
    public static NumberLexeme Parse(string text)
    {
        var exponentAt = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : ParseExponent(text[(exponentAt + 1)..]);
        var negative = mantissa.StartsWith('-');
        var unsigned = negative ? mantissa[1..] : mantissa;
        var point = unsigned.IndexOf('.');
        var integerPart = point < 0 ? unsigned : unsigned[..point];
        var all = integerPart + (point < 0 ? "" : unsigned[(point + 1)..]);
        var leading = all.Length - all.TrimStart('0').Length;
        var digits = all.Trim('0');
        if (digits.Length == 0)
        {
            return new NumberLexeme(0, "", 0, text);
        }

        return new NumberLexeme(negative ? -1 : 1, digits, integerPart.Length - leading + exponent, text);
    }

    // An exponent too large for a long is as good as infinite: no bound the schemas state is anywhere near it.
    private static long ParseExponent(string text) =>
        long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, -(long.MaxValue / 4), long.MaxValue / 4)
            : text.StartsWith('-') ? -(long.MaxValue / 4) : long.MaxValue / 4;

    /// <summary>Zero, or no digit below the units: <c>Digits × 10^(Order − Digits.Length)</c> with a power that is not negative.</summary>
    public bool IsIntegral => Sign == 0 || Order - Digits.Length >= 0;

    public int CompareTo(NumberLexeme other)
    {
        if (Sign != other.Sign)
        {
            return Sign.CompareTo(other.Sign);
        }

        if (Sign == 0)
        {
            return 0;
        }

        int magnitude;
        if (Order != other.Order)
        {
            magnitude = Order.CompareTo(other.Order);
        }
        else
        {
            var width = Math.Max(Digits.Length, other.Digits.Length);
            magnitude = string.CompareOrdinal(Digits.PadRight(width, '0'), other.Digits.PadRight(width, '0'));
        }

        return Sign * Math.Sign(magnitude);
    }
}
