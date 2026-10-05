using System.Buffers;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tisilia.AspNetCore.Codecs;

/// <summary>
/// Adds the converters of the additional types for which System.Text.Json has none that round-trips (it writes their public
/// properties as an object it cannot read back, or throws): <see cref="BigIntegerJsonConverter"/>, <see cref="IPAddressJsonConverter"/>,
/// <see cref="RuneJsonConverter"/>, <see cref="IPNetworkJsonConverter"/>, <see cref="IndexJsonConverter"/>, <see cref="RangeJsonConverter"/>
/// and <see cref="ComplexJsonConverter"/>; and <see cref="JsonValueJsonConverter"/>, which refuses a JSON object or array in a
/// <see cref="System.Text.Json.Nodes.JsonValue"/> position with a <see cref="JsonException"/> (a 400) where System.Text.Json's own throws
/// <see cref="InvalidOperationException"/> (a 500); <see cref="TimeZoneInfoJsonConverter"/> and <see cref="CultureInfoJsonConverter"/> (ids the
/// server's environment knows). Tisilia never changes
/// the application's JSON options; the application calls this on the options it serves with, e.g.
/// <c>builder.Services.ConfigureHttpJsonOptions(o =&gt; o.SerializerOptions.AddTisiliaAdditionalConverters())</c>.
/// </summary>
public static class AdditionalJsonConverters
{
    public static JsonSerializerOptions AddTisiliaAdditionalConverters(this JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new BigIntegerJsonConverter());
        options.Converters.Add(new IPAddressJsonConverter());
        options.Converters.Add(new RuneJsonConverter());
        options.Converters.Add(new IPNetworkJsonConverter());
        options.Converters.Add(new IndexJsonConverter());
        options.Converters.Add(new RangeJsonConverter());
        options.Converters.Add(new ComplexJsonConverter());
        options.Converters.Add(new JsonValueJsonConverter());
        options.Converters.Add(new TimeZoneInfoJsonConverter());
        options.Converters.Add(new CultureInfoJsonConverter());
        return options;
    }
}

/// <summary>
/// <see cref="BigInteger"/> as a JSON number token with the canonical integer text (no fraction, exponent or leading zero), property
/// names as the same text. The text is limited to <see cref="MaxLength"/> characters in both directions: parsing is quadratic in the
/// length, and the TypeScript codec (like the runtime's default <c>maxNumberCharacters</c>) has the same limit.
/// </summary>
public sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public const int MaxLength = 4096;

    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException($"BigInteger requires a JSON number but found {reader.TokenType}");
        }

        return Parse(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) => writer.WriteRawValue(Format(value));

    public override BigInteger ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = reader.GetString()!;
        return name.Length <= MaxLength ? Parse(Encoding.UTF8.GetBytes(name)) : throw new JsonException($"BigInteger has more than {MaxLength} characters");
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) => writer.WritePropertyName(Format(value));

    private static BigInteger Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxLength)
        {
            throw new JsonException($"BigInteger has more than {MaxLength} characters");
        }

        // -?(0|[1-9][0-9]*): a JSON integer; "-0" is zero
        var digits = utf8.Length > 0 && utf8[0] == (byte)'-' ? utf8[1..] : utf8;
        if (digits.Length == 0 || digits.ContainsAnyExceptInRange((byte)'0', (byte)'9') || (digits.Length > 1 && digits[0] == (byte)'0'))
        {
            throw new JsonException("BigInteger requires an integer without fraction, exponent or leading zeros");
        }

        return BigInteger.Parse(Encoding.ASCII.GetString(utf8), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static string Format(BigInteger value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length <= MaxLength ? text : throw new JsonException($"BigInteger has more than {MaxLength} characters; the TypeScript codec would refuse it");
    }
}

/// <summary>
/// <see cref="IPAddress"/> as the canonical text <see cref="IPAddress.ToString()"/> writes ("192.168.0.1", "2001:db8::1", "fe80::1%3"),
/// in both directions and for property names. Other spellings <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> would accept
/// ("1", "0x7f.1", "010.0.0.1", uppercase or uncompressed IPv6) are refused, so one address has one text.
/// </summary>
public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    /// <summary>Longer than any canonical text (an IPv6 address with an embedded IPv4 part and a 10-digit scope id).</summary>
    private const int MaxLength = 64;

    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"IPAddress requires a JSON string but found {reader.TokenType}");
        }

        return Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    public override IPAddress ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());

    /// <summary>The address when <paramref name="text"/> is its canonical text; null otherwise.</summary>
    public static IPAddress? ParseCanonical(string text)
        => text.Length <= MaxLength && IPAddress.TryParse(text, out var address) && address.ToString() == text ? address : null;

    private static IPAddress Parse(string text)
        => ParseCanonical(text) ?? throw new JsonException("IPAddress requires the canonical text IPAddress.ToString() writes, such as \"192.168.0.1\" or \"2001:db8::1\"");
}

/// <summary><see cref="Rune"/> as a JSON string holding exactly one Unicode scalar value, in both directions and for property names.</summary>
public sealed class RuneJsonConverter : JsonConverter<Rune>
{
    public override Rune Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Rune requires a JSON string but found {reader.TokenType}");
        }

        return Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, Rune value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    public override Rune ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Rune value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());

    /// <summary>The scalar when <paramref name="text"/> is exactly one Unicode scalar value; null otherwise.</summary>
    public static Rune? ParseScalar(string text)
        => Rune.DecodeFromUtf16(text, out var rune, out var consumed) == OperationStatus.Done && consumed == text.Length ? rune : null;

    private static Rune Parse(string text)
        => ParseScalar(text) ?? throw new JsonException("Rune requires a string of exactly one Unicode scalar value");
}

/// <summary>
/// <see cref="IPNetwork"/> as its canonical CIDR text (<see cref="IPNetwork.ToString()"/>: "10.0.0.0/8", "2001:db8::/32"), in both
/// directions and for property names. System.Text.Json has no converter for it (writing one throws). Texts
/// <see cref="IPNetwork.TryParse(string?, out IPNetwork)"/> would normalize — host bits set ("10.0.0.1/8"), leading zeros in the prefix,
/// non-canonical addresses — are refused, so one network has one text.
/// </summary>
public sealed class IPNetworkJsonConverter : JsonConverter<IPNetwork>
{
    /// <summary>Longer than any canonical text (a canonical IPv6 address with scope, "/" and three digits).</summary>
    private const int MaxLength = 72;

    public override IPNetwork Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"IPNetwork requires a JSON string but found {reader.TokenType}");
        }

        return Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, IPNetwork value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    public override IPNetwork ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, IPNetwork value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());

    /// <summary>The network when <paramref name="text"/> is its canonical CIDR text; null otherwise.</summary>
    public static IPNetwork? ParseCanonical(string text)
        => text.Length <= MaxLength && IPNetwork.TryParse(text, out var network) && network.ToString() == text ? network : null;

    private static IPNetwork Parse(string text)
        => ParseCanonical(text) ?? throw new JsonException("IPNetwork requires the canonical CIDR text IPNetwork.ToString() writes, such as \"10.0.0.0/8\" (no host bits)");
}

/// <summary>
/// <see cref="Index"/> as C# index syntax (<see cref="Index.ToString()"/>: "3", "^3"), in both directions and for property names.
/// System.Text.Json's default object handling writes {value, isFromEnd} but reads it back without the from-end flag.
/// </summary>
public sealed class IndexJsonConverter : JsonConverter<Index>
{
    public override Index Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Index requires a JSON string but found {reader.TokenType}");
        }

        return Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, Index value, JsonSerializerOptions options) => writer.WriteStringValue(Format(value));

    public override Index ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Index value, JsonSerializerOptions options) => writer.WritePropertyName(Format(value));

    /// <summary>The canonical text: the value in invariant digits, "^" first for an index from the end.</summary>
    public static string Format(Index value) => (value.IsFromEnd ? "^" : "") + value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The index when <paramref name="text"/> is canonical ("^"? then digits without leading zeros, at most Int32.MaxValue); null otherwise.</summary>
    public static Index? ParseCanonical(ReadOnlySpan<char> text)
    {
        var fromEnd = text.Length > 0 && text[0] == '^';
        var digits = fromEnd ? text[1..] : text;
        if (digits.Length is 0 or > 10 || (digits.Length > 1 && digits[0] == '0') || digits.ContainsAnyExceptInRange('0', '9')
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return new Index(value, fromEnd);
    }

    private static Index Parse(string text) => ParseCanonical(text) ?? throw new JsonException("Index requires \"<n>\" or \"^<n>\" (0 <= n <= 2147483647)");
}

/// <summary>
/// <see cref="Range"/> as C# range syntax with both ends written (<see cref="Range.ToString()"/>: "1..^2", "0..^0"), in both
/// directions and for property names. System.Text.Json's default object handling loses the from-end flags of the ends.
/// </summary>
public sealed class RangeJsonConverter : JsonConverter<Range>
{
    public override Range Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Range requires a JSON string but found {reader.TokenType}");
        }

        return Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, Range value, JsonSerializerOptions options) => writer.WriteStringValue(Format(value));

    public override Range ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Range value, JsonSerializerOptions options) => writer.WritePropertyName(Format(value));

    public static string Format(Range value) => IndexJsonConverter.Format(value.Start) + ".." + IndexJsonConverter.Format(value.End);

    /// <summary>The range when <paramref name="text"/> is "&lt;index&gt;..&lt;index&gt;" with canonical ends; null otherwise.</summary>
    public static Range? ParseCanonical(string text)
    {
        var separator = text.IndexOf("..", StringComparison.Ordinal);
        if (separator < 0 || text.IndexOf("..", separator + 2, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        return IndexJsonConverter.ParseCanonical(text.AsSpan(0, separator)) is { } start && IndexJsonConverter.ParseCanonical(text.AsSpan(separator + 2)) is { } end
            ? new Range(start, end)
            : null;
    }

    private static Range Parse(string text) => ParseCanonical(text) ?? throw new JsonException("Range requires \"<index>..<index>\" with both ends written, such as \"1..^2\"");
}

/// <summary>
/// <see cref="Complex"/> as {"real":…,"imaginary":…} with System.Text.Json's double formatting; reads exactly those two members (no
/// others, no duplicates, both finite). System.Text.Json's default object handling writes four members and reads back (0, 0).
/// </summary>
public sealed class ComplexJsonConverter : JsonConverter<Complex>
{
    public override Complex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Complex requires a JSON object but found {reader.TokenType}");
        }

        double? real = null;
        double? imaginary = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value) || !double.IsFinite(value))
            {
                throw new JsonException($"Complex member '{name}' requires a finite JSON number");
            }

            switch (name)
            {
                case "real" when real is null:
                    real = value;
                    break;
                case "imaginary" when imaginary is null:
                    imaginary = value;
                    break;
                default:
                    throw new JsonException($"Complex has exactly the members real and imaginary, once each (found '{name}')");
            }
        }

        return real is { } r && imaginary is { } i ? new Complex(r, i) : throw new JsonException("Complex requires real and imaginary");
    }

    public override void Write(Utf8JsonWriter writer, Complex value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
        {
            throw new JsonException("Complex parts must be finite to be written as JSON numbers");
        }

        writer.WriteStartObject();
        writer.WriteNumber("real", value.Real);
        writer.WriteNumber("imaginary", value.Imaginary);
        writer.WriteEndObject();
    }
}

/// <summary>
/// <see cref="System.Text.Json.Nodes.JsonValue"/> as one JSON string, number or boolean, read into a node that keeps the token (a number
/// its lexeme) and written back as System.Text.Json writes the node — the same values and output as System.Text.Json's own converter.
/// The difference: a JSON object or array is refused with a <see cref="JsonException"/>, which ASP.NET Core answers with 400, where
/// System.Text.Json's converter throws <see cref="InvalidOperationException"/> ("The element cannot be an object or array.", a 500).
/// JSON null stays a null reference.
/// </summary>
public sealed class JsonValueJsonConverter : JsonConverter<System.Text.Json.Nodes.JsonValue>
{
    public override System.Text.Json.Nodes.JsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False))
        {
            throw new JsonException($"a JsonValue holds a JSON string, number or boolean, not {reader.TokenType}");
        }

        var element = JsonElement.ParseValue(ref reader);
        return System.Text.Json.Nodes.JsonValue.Create(element)!;
    }

    public override void Write(Utf8JsonWriter writer, System.Text.Json.Nodes.JsonValue value, JsonSerializerOptions options) => value.WriteTo(writer, options);
}

/// <summary>
/// <see cref="TimeZoneInfo"/> as its <see cref="TimeZoneInfo.Id"/>. Reading accepts an id <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
/// resolves to a zone with exactly that id (an IANA or a Windows id, as the server's environment supports); writing writes the Id.
/// Which ids exist is a property of the server's environment: <see cref="KnownIds"/> lists the ones it accepts.
/// </summary>
public sealed class TimeZoneInfoJsonConverter : JsonConverter<TimeZoneInfo>
{
    public override TimeZoneInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"TimeZoneInfo requires a JSON string (its id) but found {reader.TokenType}");
        }

        var id = reader.GetString()!;
        return ParseKnown(id) ?? throw new JsonException($"'{id}' is not a time zone id this server knows");
    }

    public override void Write(Utf8JsonWriter writer, TimeZoneInfo value, JsonSerializerOptions options) => writer.WriteStringValue(value.Id);

    /// <summary>The zone whose Id is <paramref name="id"/>; null when the environment has none.</summary>
    public static TimeZoneInfo? ParseKnown(string id)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return zone.Id == id ? zone : null;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ids this environment accepts, each checked to resolve to a zone of that id: the system zones and their IANA / Windows
    /// equivalents (ordinal order).
    /// </summary>
    public static IReadOnlyList<string> KnownIds()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string? id)
        {
            if (id is not null && ParseKnown(id) is not null)
            {
                ids.Add(id);
            }
        }

        Add(TimeZoneInfo.Utc.Id);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            Add(zone.Id);
            Add(TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null);
            Add(TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : null);
        }

        return [.. ids];
    }
}

/// <summary>
/// <see cref="CultureInfo"/> as its <see cref="CultureInfo.Name"/> ("" is the invariant culture). Reading accepts a name
/// <see cref="CultureInfo.GetCultureInfo(string, bool)"/> (predefined cultures only) resolves to a culture of exactly that name; writing
/// writes the Name. Which cultures exist is a property of the server's environment (ICU data, invariant globalization):
/// <see cref="KnownNames"/> lists the ones it accepts.
/// </summary>
public sealed class CultureInfoJsonConverter : JsonConverter<CultureInfo>
{
    public override CultureInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"CultureInfo requires a JSON string (its name) but found {reader.TokenType}");
        }

        var name = reader.GetString()!;
        return ParseKnown(name) ?? throw new JsonException($"'{name}' is not a culture name this server knows");
    }

    public override void Write(Utf8JsonWriter writer, CultureInfo value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);

    /// <summary>The predefined culture whose Name is <paramref name="name"/>; null when the environment has none.</summary>
    public static CultureInfo? ParseKnown(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return culture.Name == name ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The culture names this environment accepts, each checked to resolve to a culture of that name (ordinal order).</summary>
    public static IReadOnlyList<string> KnownNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal) { CultureInfo.InvariantCulture.Name };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            if (ParseKnown(culture.Name) is not null)
            {
                names.Add(culture.Name);
            }
        }

        return [.. names];
    }
}
