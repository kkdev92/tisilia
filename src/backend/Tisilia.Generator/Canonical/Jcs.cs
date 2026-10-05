using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tisilia.Generator.Canonical;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme for Tisilia control documents.
/// Object members are sorted by UTF-16 code units, strings use the ECMAScript escaping rules, numbers use the
/// ECMAScript <c>Number::toString</c> shortest representation. Lone surrogates, NaN and Infinity are errors.
/// This is never applied to business payloads.
/// </summary>
public static class Jcs
{
    public static byte[] Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string SerializeToString(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (name, value) in obj.OrderBy(p => p.Key, Utf16CodeUnitComparer.Instance))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(name, sb);
                    sb.Append(':');
                    Write(value, sb);
                }

                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    Write(arr[i], sb);
                }

                sb.Append(']');
                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        break;
                    case JsonValueKind.String:
                        WriteString(value.GetValue<string>(), sb);
                        break;
                    case JsonValueKind.Number:
                        sb.Append(FormatNumber(value.ToJsonString()));
                        break;
                    default:
                        throw new JcsException("unsupported JSON value kind " + value.GetValueKind());
                }

                break;
            default:
                throw new JcsException("unsupported node type");
        }
    }

    /// <summary>RFC 8785 §3.2.2.2 string serialization.</summary>
    public static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }

                throw new JcsException("lone surrogate in string");
            }

            if (char.IsLowSurrogate(c))
            {
                throw new JcsException("lone surrogate in string");
            }

            switch (c)
            {
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>
    /// ECMAScript Number::toString (ECMA-262 §6.1.6.1.20) applied to the IEEE 754 double nearest to the lexeme.
    /// Control documents only contain safe integers (SV01), but the general algorithm is implemented for
    /// RFC 8785 Appendix B conformance.
    /// </summary>
    public static string FormatNumber(string lexeme)
    {
        if (!double.TryParse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            throw new JcsException("invalid number lexeme");
        }

        return FormatNumber(d);
    }

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new JcsException("NaN and Infinity are not permitted");
        }

        if (d == 0)
        {
            return "0";
        }

        var negative = d < 0;
        if (negative)
        {
            d = -d;
        }

        // .NET Core 3.0+ "R" yields the shortest round-trippable digit string; reshape it into ES notation.
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int exponent; // value = 0.digits * 10^exponent  (ES "n")
        var eIndex = r.IndexOfAny(['E', 'e']);
        string mantissa;
        var exp10 = 0;
        if (eIndex >= 0)
        {
            mantissa = r[..eIndex];
            exp10 = int.Parse(r[(eIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        else
        {
            mantissa = r;
        }

        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        string intPart, fracPart;
        if (dot >= 0)
        {
            intPart = mantissa[..dot];
            fracPart = mantissa[(dot + 1)..];
        }
        else
        {
            intPart = mantissa;
            fracPart = "";
        }

        digits = (intPart + fracPart).TrimStart('0');
        var leadingZerosRemoved = (intPart + fracPart).Length - digits.Length;
        // n = position of decimal point relative to the start of digits
        exponent = intPart.Length - leadingZerosRemoved + exp10;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        var k = digits.Length;
        var n = exponent;
        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1)
            {
                sb.Append('.').Append(digits, 1, k - 1);
            }

            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}

public sealed class JcsException(string message) : Exception(message);

/// <summary>Ordinal comparison of strings as sequences of unsigned UTF-16 code units (RFC 8785 §3.2.3).</summary>
public sealed class Utf16CodeUnitComparer : IComparer<string>
{
    public static Utf16CodeUnitComparer Instance { get; } = new();

    public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
}
