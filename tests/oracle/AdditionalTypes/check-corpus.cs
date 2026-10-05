#:property InvariantGlobalization=true
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NoWarn=$(NoWarn);CS8714;IL2026;IL3050
#:project ../../../src/backend/Tisilia.AspNetCore/Tisilia.AspNetCore.csproj
// .NET 10 side of the corpus check for IPNetwork, Index, Range, Complex and JsonScalar (System.Text.Json.Nodes.JsonValue):
// every value the TypeScript module accepts must be read by the server converter and written back unchanged.
// Run: dotnet run tests/oracle/AdditionalTypes/check-corpus.cs -- <corpus.json>   (writes complex-out.json beside the corpus)
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.AspNetCore.Codecs;

var corpus = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var options = new JsonSerializerOptions().AddTisiliaAdditionalConverters();
var bugs = new List<string>();
var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
var complexOut = new JsonArray();

foreach (var entry in corpus)
{
    var kind = entry![0]!.GetValue<string>();
    var text = new string(entry[1]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray());
    var ts = entry[2]!.GetValue<bool>();
    bool accepted;
    string detail;
    switch (kind)
    {
        case "ipnetwork":
            (accepted, detail) = RoundTripString<IPNetwork>(text, options);
            break;
        case "index":
            (accepted, detail) = RoundTripString<Index>(text, options);
            break;
        case "range":
            (accepted, detail) = RoundTripString<Range>(text, options);
            break;
        case "complex":
        {
            try
            {
                var c = JsonSerializer.Deserialize<Complex>(text, options);
                var expectedReal = ulong.Parse(entry[3]![0]!.GetValue<string>());
                var expectedImaginary = ulong.Parse(entry[3]![1]!.GetValue<string>());
                accepted = BitConverter.DoubleToUInt64Bits(c.Real) == expectedReal && BitConverter.DoubleToUInt64Bits(c.Imaginary) == expectedImaginary;
                detail = accepted ? "" : $"read ({c.Real:R}, {c.Imaginary:R}) from {text}";
                // what the server writes back, for the TypeScript decoder (second phase)
                complexOut.Add(new JsonArray(JsonSerializer.Serialize(c, options), entry[3]![0]!.GetValue<string>(), entry[3]![1]!.GetValue<string>()));
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
            {
                accepted = false;
                detail = e.GetType().Name + ": " + e.Message.Split('\n')[0];
            }

            break;
        }

        case "jsonscalar":
        {
            try
            {
                var node = JsonSerializer.Deserialize<JsonValue>(text, options);
                if (node is null)
                {
                    accepted = false;
                    detail = "read null";
                    break;
                }

                var written = JsonSerializer.Serialize(node, options);
                // the token survives: numbers keep their lexeme, strings their value, booleans their value
                var same = node.GetValueKind() switch
                {
                    JsonValueKind.Number => written == text,
                    JsonValueKind.String => JsonNode.Parse(written)!.GetValue<string>() == JsonNode.Parse(text)!.GetValue<string>(),
                    _ => written == text,
                };
                accepted = same;
                detail = same ? "" : $"wrote {written}";
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
            {
                accepted = false;
                detail = e.GetType().Name + ": " + e.Message.Split('\n')[0];
            }

            break;
        }

        default:
            throw new InvalidOperationException(kind);
    }

    var key = kind + (ts ? "+" : "-") + (accepted ? "/net+" : "/net-");
    counts[key] = counts.GetValueOrDefault(key) + 1;
    if (ts && !accepted)
    {
        bugs.Add($"{kind}: TypeScript accepts {JsonSerializer.Serialize(text)} but .NET: {detail}");
    }
    else if (!ts && accepted && kind is "ipnetwork" or "index" or "range")
    {
        bugs.Add($"{kind}: .NET reads and writes back {JsonSerializer.Serialize(text)} unchanged but TypeScript refuses it");
    }
}

foreach (var (k, v) in counts)
{
    Console.WriteLine($"{k,-24} {v}");
}

File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "complex-out.json"), complexOut.ToJsonString());
Console.WriteLine($"MISMATCHES: {bugs.Count}");
foreach (var b in bugs.Take(40))
{
    Console.WriteLine("  " + b);
}

// a JSON string token read by the converter and written back unchanged, as a value and as a property name
static (bool, string) RoundTripString<T>(string text, JsonSerializerOptions options)
{
    var json = JsonSerializer.Serialize(text);
    try
    {
        var value = JsonSerializer.Deserialize<T>(json, options)!;
        var written = JsonSerializer.Serialize(value, options);
        if (written != json)
        {
            return (false, "rewrote to " + written);
        }

        var dict = JsonSerializer.Deserialize<Dictionary<T, int>>("{" + json + ":0}", options)!;
        var keyWritten = JsonSerializer.Serialize(dict, options);
        return keyWritten == "{" + json + ":0}" ? (true, "") : (false, "property name rewrote to " + keyWritten);
    }
    catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
    {
        return (false, e.GetType().Name + ": " + e.Message.Split('\n')[0]);
    }
}
