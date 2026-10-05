using System.Text;
using System.Text.Json.Nodes;
using Tisilia.Generator.Canonical;
using Xunit;

namespace Tisilia.Contract.Tests;

public class JcsTests
{
    [Fact]
    public void Rfc8785_section_3_2_3_sorting_example()
    {
        // RFC 8785 §3.2.3: property names are sorted by UTF-16 code units, not by code points.
        var input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;
        var node = JsonNode.Parse(input);
        var canonical = Jcs.SerializeToString(node);
        Assert.Equal("{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\",\"\ud83d\ude00\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}", canonical);
    }

    [Fact]
    public void Rfc8785_section_3_2_2_primitive_example()
    {
        var input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        var canonical = Jcs.SerializeToString(JsonNode.Parse(input));
        Assert.Equal("{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}", canonical);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("1", "1")]
    [InlineData("1000000000000000000000", "1e+21")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("0.0000001", "1e-7")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("0.1", "0.1")]
    [InlineData("123.456e2", "12345.6")]
    public void Number_formatting_follows_ecmascript(string lexeme, string expected)
    {
        Assert.Equal(expected, Jcs.FormatNumber(lexeme));
    }

    [Theory]
    [InlineData("0000000000000000", "0")]
    [InlineData("8000000000000000", "0")]
    [InlineData("0000000000000001", "5e-324")]
    [InlineData("8000000000000001", "-5e-324")]
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
    [InlineData("ffefffffffffffff", "-1.7976931348623157e+308")]
    [InlineData("4340000000000000", "9007199254740992")]
    [InlineData("c340000000000000", "-9007199254740992")]
    [InlineData("4430000000000000", "295147905179352830000")]
    [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
    [InlineData("44b52d02c7e14af6", "1e+23")]
    [InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
    [InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("41b3de4355555553", "333333333.3333332")]
    [InlineData("41b3de4355555554", "333333333.33333325")]
    [InlineData("41b3de4355555555", "333333333.3333333")]
    [InlineData("41b3de4355555556", "333333333.3333334")]
    [InlineData("41b3de4355555557", "333333333.33333343")]
    [InlineData("becbf647612f3696", "-0.0000033333333333333333")]
    [InlineData("43143ff3c1cb0959", "1424953923781206.2")]
    public void Rfc8785_appendix_b_number_vectors(string ieeeHex, string expected)
    {
        var bits = Convert.ToInt64(ieeeHex, 16);
        var d = BitConverter.Int64BitsToDouble(bits);
        Assert.Equal(expected, Jcs.FormatNumber(d));
    }

    [Fact]
    public void NaN_and_infinity_are_rejected()
    {
        Assert.Throws<JcsException>(() => Jcs.FormatNumber(double.NaN));
        Assert.Throws<JcsException>(() => Jcs.FormatNumber(double.PositiveInfinity));
    }

    [Fact]
    public void Lone_surrogates_are_rejected()
    {
        var node = System.Text.Json.Nodes.JsonValue.Create("a\ud800b");
        Assert.Throws<JcsException>(() => { Jcs.Serialize(node); });
    }

    [Fact]
    public void Control_characters_use_lowercase_hex()
    {
        var node = System.Text.Json.Nodes.JsonValue.Create("\u0001\u001f\t\b\f");
        Assert.Equal("\"\\u0001\\u001f\\t\\b\\f\"", Jcs.SerializeToString(node));
    }

    [Fact]
    public void Output_is_utf8_without_bom()
    {
        var bytes = Jcs.Serialize(JsonNode.Parse("{\"a\":\"\u00e9\"}"));
        Assert.Equal(Encoding.UTF8.GetBytes("{\"a\":\"\u00e9\"}"), bytes);
    }
}
