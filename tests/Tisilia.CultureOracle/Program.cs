using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Tisilia;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;

// Posts one MVC form body — written by the runtime's form encoder from the exported contract — under every culture this host knows,
// and prints, as JSON, how many cultures bound each field to its value, how many refused it, and any other value bound.
// Usage: dotnet Tisilia.CultureOracle.dll <runtime dist/index.js> <work directory>
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Tisilia.CultureOracle <runtime dist/index.js> <work directory>");
    return 2;
}

var (runtime, work) = (args[0], args[1]);
// the report goes to a reader that decodes UTF-8, whatever the console code page
Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new CultureFormOnly()));
builder.Services.AddTisilia(o => o.ApiId = "mvc-culture");
await using var app = builder.Build();
// RequestLocalization in miniature: the culture under test is the request's culture, which FormValueProviderFactory reads
app.Use((context, next) =>
{
    var culture = CultureInfo.GetCultureInfo(context.Request.Headers["X-Test-Culture"].ToString());
    CultureInfo.CurrentCulture = culture;
    CultureInfo.CurrentUICulture = culture;
    return next(context);
});
app.MapControllers();
await app.StartAsync();

var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
if (export.Diagnostics.HasErrors || export.Index is null)
{
    Console.Error.WriteLine(string.Join("\n", export.Diagnostics.Items));
    return 1;
}

var form = (FormRequestBody)export.Index.Operations["culture-form"].RequestBody;
await File.WriteAllTextAsync(Path.Combine(work, "contract.json"), export.Text);
await File.WriteAllTextAsync(Path.Combine(work, "inputs.json"), JsonSerializer.Serialize(CultureFormController.Samples.ToDictionary(s => s.Field, s => s.Input)));
await File.WriteAllTextAsync(Path.Combine(work, "prepare.mjs"), """
    import { readFileSync } from "node:fs";
    import { pathToFileURL } from "node:url";
    const [runtime, contract, inputs] = process.argv.slice(2);
    const { createContractRegistry, createCodecContext, prepareRequest } = await import(pathToFileURL(runtime).href);
    const document = JSON.parse(readFileSync(contract, "utf8"));
    const { operations, registry } = createContractRegistry(document);
    const texts = JSON.parse(readFileSync(inputs, "utf8"));
    const body = {};
    for (const field of document.operations.find(o => o.id === "culture-form").requestBody.fields) {
      body[field.name] = registry.get(field.use.codecId).parseRequestInput(texts[field.name], createCodecContext());
    }
    const prepared = prepareRequest(operations.get("culture-form"), { body }, { baseUrl: "http://localhost" });
    console.log(JSON.stringify({ contentType: prepared.headers.find(([name]) => name.toLowerCase() === "content-type")[1], text: prepared.bodyText, body: Buffer.from(prepared.bodyBytes).toString("base64") }));
    """);
var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
foreach (var argument in new[] { Path.Combine(work, "prepare.mjs"), runtime, Path.Combine(work, "contract.json"), Path.Combine(work, "inputs.json") })
{
    start.ArgumentList.Add(argument);
}

using var node = Process.Start(start)!;
var output = node.StandardOutput.ReadToEndAsync();
var error = node.StandardError.ReadToEndAsync();
await node.WaitForExitAsync();
if (node.ExitCode != 0)
{
    Console.Error.WriteLine(await output + await error);
    return 1;
}

using var prepared = JsonDocument.Parse(await output);
var contentType = prepared.RootElement.GetProperty("contentType").GetString()!;
var body = Convert.FromBase64String(prepared.RootElement.GetProperty("body").GetString()!);
var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
var exact = CultureFormController.Samples.ToDictionary(s => s.Field, _ => 0);
var refused = CultureFormController.Samples.ToDictionary(s => s.Field, _ => new List<string>());
var wrong = new List<string>();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
foreach (var culture in cultures)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/culture-form") { Content = new ByteArrayContent(body) };
    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    request.Headers.Add("X-Test-Culture", culture.Name);
    using var response = await client.SendAsync(request);
    var bound = JsonSerializer.Deserialize<Dictionary<string, string?>>(await response.Content.ReadAsStringAsync())!;
    var name = culture.Name.Length == 0 ? "(invariant)" : culture.Name;
    foreach (var (field, _, expected) in CultureFormController.Samples)
    {
        var actual = bound[field];
        if (actual == expected) { exact[field]++; }
        else if (actual is null) { refused[field].Add(name); }
        else { wrong.Add($"{name} {field}={actual}"); }
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    cultures = cultures.Length,
    requestCulture = form.Fields.Where(f => f.RequestCulture).Select(f => f.Name).ToArray(),
    body = prepared.RootElement.TryGetProperty("text", out var text) ? text.GetString() : contentType,
    exact,
    refused = refused.ToDictionary(r => r.Key, r => r.Value.Count),
    refusedExamples = refused.Where(r => r.Value.Count > 0).ToDictionary(r => r.Key, r => string.Join(" ", r.Value.Take(8))),
    wrong,
}));
return 0;

internal sealed class CultureFormOnly : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        feature.Controllers.Clear();
        feature.Controllers.Add(typeof(CultureFormController).GetTypeInfo());
    }
}

/// <summary>Echoes each bound field as invariant text, or null when MVC refused the field's value (model state).</summary>
[ApiExplorerSettings(IgnoreApi = false)]
public sealed class CultureFormController : ControllerBase
{
    /// <summary>Field, the editor input the runtime parses into the value it sends, and the value the server echoes.</summary>
    public static readonly (string Field, string Input, string Expected)[] Samples =
    [
        ("i8", "-128", "-128"),
        ("i32", "2147483647", "2147483647"),
        ("negative", "-3", "-3"),
        ("i64", "-9007199254740993", "-9007199254740993"),
        ("u64", "18446744073709551615", "18446744073709551615"),
        ("dec", "1.50", "1.50"),
        ("decTiny", "-0.0000000000000000000000000001", "-0.0000000000000000000000000001"),
        ("decMax", "79228162514264337593543950335", "79228162514264337593543950335"),
        ("decWhole", "100", "100"),
        ("dbl", "1.5", "1.5"),
        ("dblMax", "1.7976931348623157E+308", "1.7976931348623157E+308"),
        ("dblTiny", "5E-324", "5E-324"),
        ("dblNegativeZero", "-0", "-0"),
        ("f32", "3.4028235E+38", "3.4028235E+38"),
        ("f32Fraction", "0.1", "0.1"),
        ("flag", "true", "True"),
        ("id", "0f8fad5b-d9cb-469f-a165-70867728950e", "0f8fad5b-d9cb-469f-a165-70867728950e"),
        ("letter", "x", "x"),
        ("utc", "2026-10-08T12:34:56.1234567Z", Ticks(new DateTime(2026, 10, 8, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567))),
        ("unspecified", "0001-01-01T00:00:00", Ticks(DateTime.MinValue)),
        // an offset binds as UTC (DateTimeModelBinder: AdjustToUniversal)
        ("local", "2026-10-08T12:34:56+09:00", Ticks(new DateTime(2026, 10, 8, 3, 34, 56, DateTimeKind.Utc))),
        ("day", "2026-10-08", "2026-10-08"),
        ("lastDay", "9999-12-31", "9999-12-31"),
        ("time", "15:04:05.1234567", "15:04:05.1234567"),
        ("midnight", "00:00:00", "00:00:00.0000000"),
        ("offset", "2026-10-08T12:34:56.1234567+09:00", "2026-10-08T12:34:56.1234567+09:00"),
        ("span", "1.02:03:04.5000000", "1.02:03:04.5000000"),
        ("negativeSpan", "-00:00:00.0000001", "-00:00:00.0000001"),
    ];

    private static string Ticks(DateTime value) => value.Ticks.ToString(CultureInfo.InvariantCulture) + " " + value.Kind;

    [HttpPost("/culture-form")]
    [TisiliaOperation("culture-form")]
    public ActionResult<Dictionary<string, string?>> Post(
        [FromForm] sbyte i8, [FromForm] int i32, [FromForm] int negative, [FromForm] long i64, [FromForm] ulong u64,
        [FromForm] decimal dec, [FromForm] decimal decTiny, [FromForm] decimal decMax, [FromForm] decimal decWhole,
        [FromForm] double dbl, [FromForm] double dblMax, [FromForm] double dblTiny, [FromForm] double dblNegativeZero, [FromForm] float f32, [FromForm] float f32Fraction,
        [FromForm] bool flag, [FromForm] Guid id, [FromForm] char letter,
        [FromForm] DateTime utc, [FromForm] DateTime unspecified, [FromForm] DateTime local, [FromForm] DateOnly day, [FromForm] DateOnly lastDay,
        [FromForm] TimeOnly time, [FromForm] TimeOnly midnight, [FromForm] DateTimeOffset offset, [FromForm] TimeSpan span, [FromForm] TimeSpan negativeSpan)
    {
        var invariant = CultureInfo.InvariantCulture;
        var values = new Dictionary<string, string?>
        {
            ["i8"] = i8.ToString(invariant),
            ["i32"] = i32.ToString(invariant),
            ["negative"] = negative.ToString(invariant),
            ["i64"] = i64.ToString(invariant),
            ["u64"] = u64.ToString(invariant),
            ["dec"] = dec.ToString(invariant),
            ["decTiny"] = decTiny.ToString(invariant),
            ["decMax"] = decMax.ToString(invariant),
            ["decWhole"] = decWhole.ToString(invariant),
            ["dbl"] = dbl.ToString("R", invariant),
            ["dblMax"] = dblMax.ToString("R", invariant),
            ["dblTiny"] = dblTiny.ToString("R", invariant),
            ["dblNegativeZero"] = dblNegativeZero.ToString("R", invariant),
            ["f32"] = f32.ToString("R", invariant),
            ["f32Fraction"] = f32Fraction.ToString("R", invariant),
            ["flag"] = flag.ToString(invariant),
            ["id"] = id.ToString("D"),
            ["letter"] = letter.ToString(),
            ["utc"] = Ticks(utc),
            ["unspecified"] = Ticks(unspecified),
            ["local"] = Ticks(local),
            ["day"] = day.ToString("O", invariant),
            ["lastDay"] = lastDay.ToString("O", invariant),
            ["time"] = time.ToString("O", invariant),
            ["midnight"] = midnight.ToString("O", invariant),
            ["offset"] = offset.ToString("O", invariant),
            ["span"] = span.ToString("c", invariant),
            ["negativeSpan"] = negativeSpan.ToString("c", invariant),
        };
        foreach (var key in values.Keys.ToList())
        {
            if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
            {
                values[key] = null;
            }
        }

        return values;
    }
}
