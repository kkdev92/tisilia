#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property InvariantGlobalization=false
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);IL2026;IL3050;CS8604
// System.Text.Json defaults for Complex, Index, Range and IPNetwork (no converters), IPNetwork parsing/formatting, and the
// time zone / culture data of the host. Run: dotnet run tests/oracle/AdditionalTypes/observe.cs
using System.Net;
using System.Numerics;
using System.Text.Json;

var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
void Try(string label, Func<string> f) { try { Console.WriteLine($"{label}: {f()}"); } catch (Exception e) { Console.WriteLine($"{label}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); } }

// Complex
Try("Complex write", () => JsonSerializer.Serialize(new Complex(1.5, -2), web));
Try("Complex read", () => { var c = JsonSerializer.Deserialize<Complex>("{\"real\":1.5,\"imaginary\":-2}", web); return $"{c.Real} {c.Imaginary}"; });
Try("Complex read extra", () => { var c = JsonSerializer.Deserialize<Complex>("{\"real\":1.5,\"imaginary\":-2,\"magnitude\":9,\"phase\":9}", web); return $"{c.Real} {c.Imaginary}"; });
Try("Complex read missing", () => { var c = JsonSerializer.Deserialize<Complex>("{\"real\":1.5}", web); return $"{c.Real} {c.Imaginary}"; });
Try("Complex overflow write", () => JsonSerializer.Serialize(new Complex(1.5e308, 1.5e308), web));
Try("Complex default write", () => JsonSerializer.Serialize(default(Complex), web));

// Index / Range
Try("Index write", () => JsonSerializer.Serialize(^3, web));
Try("Index read", () => { var i = JsonSerializer.Deserialize<Index>("{\"value\":3,\"isFromEnd\":true}", web); return i.ToString(); });
Try("Index ToString", () => $"{new Index(3)} {^3} {Index.Start} {Index.End}");
Try("Range ToString", () => $"{1..^2} {Range.All} {..5} {3..}");
Try("Range write", () => JsonSerializer.Serialize(1..^2, web));

// IPNetwork
Try("IPNetwork ToString", () => $"{IPNetwork.Parse("10.0.0.0/8")} {IPNetwork.Parse("2001:db8::/32")} {IPNetwork.Parse("::ffff:10.0.0.0/104")}");
Try("IPNetwork host bits", () => IPNetwork.Parse("10.0.0.1/8").ToString());
Try("IPNetwork no prefix", () => IPNetwork.Parse("10.0.0.0").ToString());
Try("IPNetwork scope", () => IPNetwork.Parse("fe80::%2/64").ToString());
Try("IPNetwork upper", () => IPNetwork.Parse("2001:DB8::/32").ToString());
Try("IPNetwork 0", () => IPNetwork.Parse("0.0.0.0/0").ToString() + " " + IPNetwork.Parse("::/0"));
Try("IPNetwork /33", () => IPNetwork.Parse("10.0.0.0/33").ToString());
Try("IPNetwork leading zero prefix", () => IPNetwork.Parse("10.0.0.0/08").ToString());
Try("IPNetwork spaces", () => IPNetwork.Parse(" 10.0.0.0/8").ToString());
Try("IPNetwork write default", () => JsonSerializer.Serialize(IPNetwork.Parse("10.0.0.0/8"), web));
Try("IPNetwork interfaces", () => string.Join(",", typeof(IPNetwork).GetInterfaces().Select(i => i.Name)));
Try("IPNetwork equals", () => (IPNetwork.Parse("10.0.0.0/8") == IPNetwork.Parse("10.0.0.0/8")).ToString());

// TimeZoneInfo / CultureInfo
Try("TimeZone count", () => TimeZoneInfo.GetSystemTimeZones().Count + " first=" + TimeZoneInfo.GetSystemTimeZones()[0].Id);
Try("TimeZone IANA lookup", () => TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo").Id);
Try("TimeZone Windows lookup", () => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time").Id);
Try("Culture ja", () => System.Globalization.CultureInfo.GetCultureInfo("ja-JP").Name);
Try("Culture count", () => System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
Try("Invariant mode", () => AppContext.TryGetSwitch("System.Globalization.Invariant", out var b) ? b.ToString() : "unset");
