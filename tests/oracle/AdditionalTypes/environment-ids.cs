#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050
#:property InvariantGlobalization=
#:project ../../../src/backend/Tisilia.AspNetCore/Tisilia.AspNetCore.csproj
// The zone ids and culture names the environment-bound codecs accept on this host, and their round trips through the converters.
// InvariantGlobalization is left unset (the repository default is true), so DOTNET_SYSTEM_GLOBALIZATION_INVARIANT decides:
//   dotnet run tests/oracle/AdditionalTypes/environment-ids.cs                                        (ICU)
//   DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run tests/oracle/AdditionalTypes/environment-ids.cs  (invariant)
using System.Globalization;
using System.Text.Json;
using Tisilia.AspNetCore.Codecs;

var options = new JsonSerializerOptions().AddTisiliaAdditionalConverters();
var zones = TimeZoneInfoJsonConverter.KnownIds();
var cultures = CultureInfoJsonConverter.KnownNames();
int bad = 0;
foreach (var id in zones) { var j = JsonSerializer.Serialize(id); if (JsonSerializer.Serialize(JsonSerializer.Deserialize<TimeZoneInfo>(j, options), options) != j) bad++; }
foreach (var n in cultures) { var j = JsonSerializer.Serialize(n); if (JsonSerializer.Serialize(JsonSerializer.Deserialize<CultureInfo>(j, options), options) != j) bad++; }
Console.WriteLine($"invariant={CultureInfo.CurrentCulture.Name.Length == 0 && CultureInfo.GetCultures(CultureTypes.AllCultures).Length == 1} system zones={TimeZoneInfo.GetSystemTimeZones().Count} known zone ids={zones.Count} cultures={cultures.Count} round-trip failures={bad}");
Console.WriteLine("zones sample: " + string.Join(", ", zones.Where(z => z is "UTC" or "Asia/Tokyo" or "Tokyo Standard Time" or "America/New_York" or "Etc/GMT+5")));
Console.WriteLine("cultures sample: " + string.Join(", ", cultures.Take(5).Select(c => c.Length == 0 ? "\"\"" : c)) + " … ja-JP:" + cultures.Contains("ja-JP") + " ja-jp:" + cultures.Contains("ja-jp"));
foreach (var probe in new[] { "\"asia/tokyo\"", "\"ja-jp\"", "\"Asia/Tokyo \"", "\"Local\"" })
{
    try { Console.WriteLine($"{probe} → zone {JsonSerializer.Deserialize<TimeZoneInfo>(probe, options)!.Id}"); } catch (JsonException e) { Console.WriteLine($"{probe} → zone JsonException ({e.Message})"); }
    try { Console.WriteLine($"{probe} → culture \"{JsonSerializer.Deserialize<CultureInfo>(probe, options)!.Name}\""); } catch (JsonException e) { Console.WriteLine($"{probe} → culture JsonException ({e.Message})"); }
}
