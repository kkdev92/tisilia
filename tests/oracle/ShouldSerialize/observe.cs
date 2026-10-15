#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050
// Which decides whether System.Text.Json writes a member: [JsonIgnore(Condition = WhenWritingDefault)], the options'
// DefaultIgnoreCondition, or a ShouldSerialize predicate that a type info modifier sets. Run: dotnet run tests/oracle/ShouldSerialize/observe.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

foreach (var global in new[] { JsonIgnoreCondition.Never, JsonIgnoreCondition.WhenWritingDefault })
{
    foreach (var predicate in new bool?[] { null, true, false })
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (predicate is { } writes)
        {
            resolver.Modifiers.Add(info =>
            {
                foreach (var property in info.Properties)
                {
                    if (property.PropertyType == typeof(Box))
                    {
                        property.ShouldSerialize = writes ? static (_, _) => true : static (_, _) => false;
                    }
                }
            });
        }

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = global, TypeInfoResolver = resolver };
        var label = predicate is null ? "none" : predicate.Value ? "always writes" : "never writes";
        Console.WriteLine($"options {global,-18} predicate {label,-13}: [JsonIgnore(WhenWritingDefault)] {JsonSerializer.Serialize(new Attributed(), options),-22} no attribute {JsonSerializer.Serialize(new Plain(), options)}");
    }
}

// the default value of each member: a struct, so neither member is ever null
public readonly record struct Box(int Value);

public sealed class Attributed
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Box Member { get; set; }
}

public sealed class Plain
{
    public Box Member { get; set; }
}
