#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050
// Which decides whether System.Text.Json writes a member: its [JsonIgnore(Condition = ...)], the options' DefaultIgnoreCondition,
// or a ShouldSerialize predicate that a type info modifier sets or removes; and whose code the predicate of a [JsonIgnore]
// condition is, in reflection and in source-generated metadata. Run: dotnet run tests/oracle/ShouldSerialize/observe.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// before any modifier runs: the assembly whose code each member's predicate is (none: the member has no predicate)
foreach (var (source, metadata) in new (string, IJsonTypeInfoResolver)[] { ("reflection", new DefaultJsonTypeInfoResolver()), ("source-generated", MembersContext.Default) })
{
    var info = metadata.GetTypeInfo(typeof(Members), new JsonSerializerOptions())!;
    Console.WriteLine($"{source,-16} predicates: " + string.Join(", ", info.Properties.Select(p => $"{p.Name} {(p.ShouldSerialize is { } f ? f.Method.Module.Assembly.GetName().Name : "none")}")));
}

// every member holds its default (null, or a default struct) when written
foreach (var global in new[] { JsonIgnoreCondition.Never, JsonIgnoreCondition.WhenWritingNull, JsonIgnoreCondition.WhenWritingDefault })
{
    foreach (var predicate in new[] { "none", "removed", "always", "never" })
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (predicate != "none")
        {
            resolver.Modifiers.Add(info =>
            {
                if (info.Type != typeof(Members))
                {
                    return;
                }

                foreach (var property in info.Properties)
                {
                    property.ShouldSerialize = predicate switch { "removed" => null, "always" => static (_, _) => true, _ => static (_, _) => false };
                }
            });
        }

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = global, TypeInfoResolver = resolver };
        Console.WriteLine($"options {global,-18} predicate {predicate,-7}: {JsonSerializer.Serialize(new Members(), options)}");
    }
}

public readonly record struct Box(int Value);

public sealed class Members
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Box WhenDefault { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WhenNull { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Never { get; set; }

    public string? Plain { get; set; }
}

[JsonSerializable(typeof(Members))]
internal sealed partial class MembersContext : JsonSerializerContext;
