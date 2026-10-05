using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tisilia.Contract;

namespace Tisilia.Generator.Conformance;

/// <summary>
/// The projected domain AST exchanged by the runner protocol and the canonical scalar text it uses.
/// Both runners (C# and TypeScript) project public values with exactly these rules, so that the registered oracles
/// compare like with like: integers and decimals are number lexemes (scale preserved, no exponent, no sign on zero),
/// binary floats use the .NET shortest round-trip form, non-finite floats are the string literals NaN/Infinity/-Infinity,
/// dates and durations are the System.Text.Json writer text, bytes are base64, guids are lowercase D-format.
/// </summary>
public static class DomainAst
{
    public static readonly JsonValue Null = new JsonNullValue();

    public static JsonValue Boolean(bool value) => new JsonBooleanValue { Value = value };

    public static JsonValue String(string value) => new JsonStringValue { Value = value };

    public static JsonValue Number(string text) => new JsonNumberValue { Text = text };

    public static JsonValue Array(IEnumerable<JsonValue> items) => new JsonArrayValue { Items = items.ToList() };

    public static JsonValue Object(IEnumerable<(string Name, JsonValue Value)> entries)
        => new JsonObjectValue { Entries = entries.Select(e => new JsonObjectEntry { Name = e.Name, Value = e.Value }).ToList() };

    public static JsonValue Object(params (string Name, JsonValue Value)[] entries) => Object((IEnumerable<(string, JsonValue)>)entries);

    public static JsonValue? Entry(JsonObjectValue obj, string name)
    {
        foreach (var entry in obj.Entries)
        {
            if (entry.Name == name)
            {
                return entry.Value;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ canonical scalar text

    public static string FormatInteger(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatInteger(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatInteger(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Fixed-point text from the 96-bit coefficient and scale; the sign of zero is dropped.</summary>
    public static string FormatDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var lo = (uint)bits[0];
        var mid = (uint)bits[1];
        var hi = (uint)bits[2];
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var coefficient = (new BigInteger(hi) << 64) | (new BigInteger(mid) << 32) | lo;
        return FormatDecimal(negative, coefficient, scale);
    }

    public static string FormatDecimal(bool negative, BigInteger coefficient, int scale)
    {
        var digits = coefficient.ToString(CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits[..^scale] + "." + digits[^scale..];
        }

        return negative && !coefficient.IsZero ? "-" + digits : digits;
    }

    /// <summary>.NET shortest round-trip text ("R"), which is also what System.Text.Json writes; non-finite values become string literals.</summary>
    public static JsonValue Float64(double value)
        => double.IsFinite(value) ? Number(value.ToString("R", CultureInfo.InvariantCulture)) : String(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");

    public static JsonValue Float32(float value)
        => float.IsFinite(value) ? Number(value.ToString("R", CultureInfo.InvariantCulture)) : String(float.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");

    public static string FormatGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Text exactly as System.Text.Json writes the value (dates, times, durations).</summary>
    public static string StjText<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, TisiliaJson.Plain);
        return JsonSerializer.Deserialize<string>(json, TisiliaJson.Plain) ?? throw new InvalidOperationException("System.Text.Json did not write a string");
    }

    // ------------------------------------------------------------------ AST ⇄ JSON text (lossless)

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = false, Indented = false };

    /// <summary>Writes the AST as JSON text keeping number lexemes and duplicate names exactly.</summary>
    public static string ToJsonText(JsonValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteTo(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] ToJsonBytes(JsonValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteTo(writer, value);
        }

        return stream.ToArray();
    }

    public static void WriteTo(Utf8JsonWriter writer, JsonValue value)
    {
        switch (value)
        {
            case JsonNullValue:
                writer.WriteNullValue();
                break;
            case JsonBooleanValue b:
                writer.WriteBooleanValue(b.Value);
                break;
            case JsonStringValue s:
                writer.WriteStringValue(s.Value);
                break;
            case JsonNumberValue n:
                writer.WriteRawValue(n.Text, skipInputValidation: false);
                break;
            case JsonArrayValue a:
                writer.WriteStartArray();
                foreach (var item in a.Items)
                {
                    WriteTo(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonObjectValue o:
                writer.WriteStartObject();
                foreach (var entry in o.Entries)
                {
                    writer.WritePropertyName(entry.Name);
                    WriteTo(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException("unknown AST node");
        }
    }

    /// <summary>Parses JSON text into the lossless AST (duplicates kept in order, number lexemes untouched).</summary>
    public static JsonValue ParseJsonText(string text, int maxDepth = 64) => ParseJsonBytes(Encoding.UTF8.GetBytes(text), maxDepth);

    public static JsonValue ParseJsonBytes(ReadOnlySpan<byte> utf8, int maxDepth = 64)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = maxDepth });
        if (!reader.Read())
        {
            throw new JsonException("empty JSON text");
        }

        var value = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("trailing content after the JSON value");
        }

        return value;
    }

    private static JsonValue ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return Null;
            case JsonTokenType.True:
                return Boolean(true);
            case JsonTokenType.False:
                return Boolean(false);
            case JsonTokenType.String:
                return String(reader.GetString()!);
            case JsonTokenType.Number:
                return Number(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray().AsSpan() : reader.ValueSpan));
            case JsonTokenType.StartArray:
            {
                var items = new List<JsonValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadValue(ref reader));
                }

                return new JsonArrayValue { Items = items };
            }

            case JsonTokenType.StartObject:
            {
                var entries = new List<JsonObjectEntry>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString()!;
                    reader.Read();
                    entries.Add(new JsonObjectEntry { Name = name, Value = ReadValue(ref reader) });
                }

                return new JsonObjectValue { Entries = entries };
            }

            default:
                throw new JsonException("unexpected token " + reader.TokenType);
        }
    }

    public static JsonValue FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Null,
        JsonValueKind.True => Boolean(true),
        JsonValueKind.False => Boolean(false),
        JsonValueKind.String => String(element.GetString()!),
        JsonValueKind.Number => Number(element.GetRawText()),
        JsonValueKind.Array => new JsonArrayValue { Items = element.EnumerateArray().Select(FromElement).ToList() },
        JsonValueKind.Object => new JsonObjectValue { Entries = element.EnumerateObject().Select(p => new JsonObjectEntry { Name = p.Name, Value = FromElement(p.Value) }).ToList() },
        _ => throw new InvalidOperationException("unknown JsonValueKind"),
    };

    /// <summary>
    /// Applies <paramref name="transform"/> to the value(s) at a slash-separated path of object member names, where <c>*</c>
    /// selects every array item; the empty path is the root. Used by the projection runner actions: a
    /// behavior's projection is declared on its request type, which may sit under members or arrays of the case root.
    /// </summary>
    public static JsonValue ApplyAt(JsonValue value, string path, Func<JsonValue, JsonValue> transform)
    {
        var segments = path.Length == 0 ? [] : path.Split('/');
        return Apply(value, segments, 0, transform);
    }

    private static JsonValue Apply(JsonValue value, string[] segments, int index, Func<JsonValue, JsonValue> transform)
    {
        if (index == segments.Length)
        {
            return transform(value);
        }

        var segment = segments[index];
        switch (value)
        {
            case JsonArrayValue array when segment == "*":
                return Array(array.Items.Select(item => Apply(item, segments, index + 1, transform)));
            case JsonObjectValue obj:
                return Object(obj.Entries.Select(e => (e.Name, e.Name == segment ? Apply(e.Value, segments, index + 1, transform) : e.Value)));
            default:
                return value; // absent member or null: nothing to project
        }
    }

    public static int Depth(JsonValue value) => value switch
    {
        JsonArrayValue a => 1 + (a.Items.Count == 0 ? 0 : a.Items.Max(Depth)),
        JsonObjectValue o => 1 + (o.Entries.Count == 0 ? 0 : o.Entries.Max(e => Depth(e.Value))),
        _ => 0,
    };

    public static string Describe(JsonValue value)
    {
        var text = ToJsonText(value);
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}

/// <summary>
/// The builtin comparison oracles (tisilia.oracle.structural / numeric / exact-wire) on projected ASTs.
/// The same three functions exist in the TypeScript runner; the suite checks that both agree.
/// </summary>
public static class AstOracles
{
    /// <summary>Structural equality: object members by name (order-insensitive when names are unique), arrays ordered, numbers by exact lexeme.</summary>
    public static bool Structural(JsonValue a, JsonValue b) => Equal(a, b, numeric: false, ordered: false);

    /// <summary>Like structural but numbers compare by mathematical value (1.0 == 1, 1e2 == 100, -0 == 0).</summary>
    public static bool Numeric(JsonValue a, JsonValue b) => Equal(a, b, numeric: true, ordered: false);

    /// <summary>Exact wire equality: member order and every lexeme must match.</summary>
    public static bool ExactWire(JsonValue a, JsonValue b) => Equal(a, b, numeric: false, ordered: true);

    public static bool? Evaluate(string builtinOracleId, JsonValue a, JsonValue b) => builtinOracleId switch
    {
        Validation.Builtins.OracleStructural => Structural(a, b),
        Validation.Builtins.OracleNumeric => Numeric(a, b),
        Validation.Builtins.OracleExactWire => ExactWire(a, b),
        _ => null,
    };

    private static bool Equal(JsonValue a, JsonValue b, bool numeric, bool ordered)
    {
        switch (a)
        {
            case JsonNullValue:
                return b is JsonNullValue;
            case JsonBooleanValue ab:
                return b is JsonBooleanValue bb && ab.Value == bb.Value;
            case JsonStringValue asv:
                return b is JsonStringValue bs && string.Equals(asv.Value, bs.Value, StringComparison.Ordinal);
            case JsonNumberValue an:
                return b is JsonNumberValue bn && (numeric ? NumericEqual(an.Text, bn.Text) : string.Equals(an.Text, bn.Text, StringComparison.Ordinal));
            case JsonArrayValue aa:
            {
                if (b is not JsonArrayValue ba || aa.Items.Count != ba.Items.Count)
                {
                    return false;
                }

                for (var i = 0; i < aa.Items.Count; i++)
                {
                    if (!Equal(aa.Items[i], ba.Items[i], numeric, ordered))
                    {
                        return false;
                    }
                }

                return true;
            }

            case JsonObjectValue ao:
            {
                if (b is not JsonObjectValue bo || ao.Entries.Count != bo.Entries.Count)
                {
                    return false;
                }

                var unique = ao.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() == ao.Entries.Count
                    && bo.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() == bo.Entries.Count;
                if (ordered || !unique)
                {
                    for (var i = 0; i < ao.Entries.Count; i++)
                    {
                        if (!string.Equals(ao.Entries[i].Name, bo.Entries[i].Name, StringComparison.Ordinal) || !Equal(ao.Entries[i].Value, bo.Entries[i].Value, numeric, ordered))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                var map = bo.Entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
                foreach (var entry in ao.Entries)
                {
                    if (!map.TryGetValue(entry.Name, out var other) || !Equal(entry.Value, other, numeric, ordered))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool NumericEqual(string x, string y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return true;
        }

        var nx = Normalize(x);
        var ny = Normalize(y);
        return nx is not null && ny is not null && nx.Value.Mantissa == ny.Value.Mantissa && nx.Value.Exponent == ny.Value.Exponent;
    }

    /// <summary>Exact rational normalization of a JSON number lexeme: mantissa without trailing zeros and decimal exponent.</summary>
    public static (BigInteger Mantissa, int Exponent)? Normalize(string lexeme)
    {
        try
        {
            var negative = lexeme.StartsWith('-');
            var s = negative ? lexeme[1..] : lexeme;
            var e = s.IndexOfAny(['e', 'E']);
            var exponent = 0;
            if (e >= 0)
            {
                exponent = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                s = s[..e];
            }

            var dot = s.IndexOf('.', StringComparison.Ordinal);
            var intPart = dot >= 0 ? s[..dot] : s;
            var frac = dot >= 0 ? s[(dot + 1)..] : "";
            var mantissa = BigInteger.Parse(intPart + frac, NumberStyles.None, CultureInfo.InvariantCulture);
            exponent = checked(exponent - frac.Length);
            if (mantissa.IsZero)
            {
                return (BigInteger.Zero, 0);
            }

            while (mantissa % 10 == 0)
            {
                mantissa /= 10;
                exponent = checked(exponent + 1);
            }

            return (negative ? -mantissa : mantissa, exponent);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return null;
        }
    }
}
