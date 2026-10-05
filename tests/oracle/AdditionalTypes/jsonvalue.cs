#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050
// System.Text.Json's own JsonValueConverter on JSON objects/arrays. Run: dotnet run tests/oracle/AdditionalTypes/jsonvalue.cs
using System.Text.Json;
using System.Text.Json.Nodes;
void Try(string label, Func<object?> f) { try { Console.WriteLine($"{label}: ok {f()}"); } catch (Exception e) { Console.WriteLine($"{label}: {e.GetType().FullName}: {e.Message.Split('\n')[0]}"); } }
Try("root []", () => JsonSerializer.Deserialize<JsonValue>("[]"));
Try("root {}", () => JsonSerializer.Deserialize<JsonValue>("{}"));
Try("root null", () => JsonSerializer.Deserialize<JsonValue>("null") is null ? "null" : "value");
Try("member []", () => JsonSerializer.Deserialize<R>("{\"V\":[]}"));
Try("member {}", () => JsonSerializer.Deserialize<R>("{\"V\":{}}"));
Try("converter", () => JsonSerializerOptions.Default.GetConverter(typeof(JsonValue)).GetType().FullName);
record R(JsonValue? V);
