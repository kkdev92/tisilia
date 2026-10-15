#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050;CA1069
// What JsonStringEnumConverter reads and writes with integers allowed and refused, and what Enum.TryParse binds from a route or query
// value. Run: dotnet run tests/oracle/EnumNames/observe.cs
using System.Text.Json;
using System.Text.Json.Serialization;

foreach (var allow in new[] { true, false })
{
    var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: allow) } };
    Console.WriteLine($"== camel case, allowIntegerValues: {allow}");

    // names the naming policy made, integers, integer strings, padding and lists of names
    foreach (var json in new[] { "\"stock\"", "\"STOCK\"", "\"Stock\"", "1", "\"1\"", "7", "\"7\"", "\" stock\"", "\"usage, stock\"" })
    {
        Read<Kind>(options, json);
    }

    Write(options, Kind.Stock);
    Write(options, (Kind)7);

    // dictionary keys
    Write(options, new Dictionary<Kind, int> { [Kind.Usage] = 1 });
    Write(options, new Dictionary<Kind, int> { [(Kind)7] = 1 });
    foreach (var json in new[] { "{\"stock\":1}", "{\"STOCK\":1}", "{\"1\":1}", "{\"7\":1}" })
    {
        Read<Dictionary<Kind, int>>(options, json);
    }

    // two members of one value
    Write(options, Alias.Second);
    Read<Alias>(options, "\"second\"");
    Read<Alias>(options, "\"other\"");

    // a name from [JsonStringEnumMemberName]
    Write(options, Named.Fancy);
    foreach (var json in new[] { "\"fancy-name\"", "\"FANCY-NAME\"", "\"Fancy-Name\"", "\"Fancy\"", "\"fancy\"", "\"plain\"", "\"PLAIN\"" })
    {
        Read<Named>(options, json);
    }

    foreach (var json in new[] { "{\"fancy-name\":1}", "{\"FANCY-NAME\":1}", "{\"Fancy\":1}" })
    {
        Read<Dictionary<Named, int>>(options, json);
    }
}

// two values a naming policy writes as one name, and the same members without a policy
foreach (var policy in new[] { JsonNamingPolicy.CamelCase, null })
{
    var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(policy, allowIntegerValues: false) } };
    Console.WriteLine($"== {(policy is null ? "no naming policy" : "camel case")}, members Ab = 1 and AB = 2");
    Write(options, Clash.Ab);
    Write(options, Clash.AB);
    foreach (var json in new[] { "\"ab\"", "\"Ab\"", "\"AB\"", "\"aB\"" })
    {
        Console.WriteLine($"read  {json,-16} -> {Value<Clash>(options, json)}");
    }
}

// what minimal APIs bind an enum route or query value with: Enum.TryParse<T>(string, out T), case-sensitive
Console.WriteLine("== Enum.TryParse");
foreach (var text in new[] { "Fancy", "fancy", "fancy-name", "Plain", "1", "7", "Plain, Fancy" })
{
    Console.WriteLine($"parse {text,-16} -> {(Enum.TryParse<Named>(text, out var parsed) ? $"{parsed} ({(int)parsed})" : "refused")}");
}

static void Read<T>(JsonSerializerOptions options, string json)
{
    try
    {
        Console.WriteLine($"read  {Label<T>(),-22} {json,-18} -> {JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(json, options), options)}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"read  {Label<T>(),-22} {json,-18} -> {e.GetType().Name}");
    }
}

static void Write<T>(JsonSerializerOptions options, T value)
{
    var shown = value is System.Collections.IDictionary d ? string.Join(",", d.Keys.Cast<object>()) : value?.ToString();
    try
    {
        Console.WriteLine($"write {Label<T>(),-22} {shown,-18} -> {JsonSerializer.Serialize(value, options)}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"write {Label<T>(),-22} {shown,-18} -> {e.GetType().Name}");
    }
}

static string Value<T>(JsonSerializerOptions options, string json) where T : struct, Enum
{
    try
    {
        var value = JsonSerializer.Deserialize<T>(json, options);
        return $"{value} ({Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)})";
    }
    catch (JsonException)
    {
        return nameof(JsonException);
    }
}

static string Label<T>() => typeof(T).IsGenericType ? $"{typeof(T).Name[..^2]}<{string.Join(",", typeof(T).GetGenericArguments().Select(a => a.Name))}>" : typeof(T).Name;

internal enum Kind
{
    Usage,
    Stock,
}

internal enum Alias
{
    First = 1,
    Second = 2,
    Other = 2,
}

internal enum Named
{
    Plain,
    [JsonStringEnumMemberName("fancy-name")] Fancy,
}

internal enum Clash
{
    Ab = 1,
    AB = 2,
}
