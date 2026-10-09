using System.Text.Json;
using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Tisilia;
using Tisilia.AspNetCore;
using Tisilia.Explorer;

var mode = Environment.GetEnvironmentVariable("ADOPTION_MODE") ?? "valid";
var startupProbe = Environment.GetEnvironmentVariable("ADOPTION_STARTUP_PROBE");
if (startupProbe is not null) { using var probe = new HttpClient(); await probe.GetAsync(startupProbe); }
if (mode == "startup-failure") { throw new InvalidOperationException("Password=TEST_SECRET; /private/connection.json"); }
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.WithOrigins("http://127.0.0.1:4179").AllowAnyMethod().AllowAnyHeader().AllowCredentials());
    o.AddPolicy("exposed", p => p.WithOrigins("http://127.0.0.1:4179").AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Content-Disposition", "Content-Range"));
});
builder.Services.Configure<RouteOptions>(o => { o.ConstraintMap["incoming"] = typeof(IncomingConstraint); o.ConstraintMap["slug"] = typeof(SlugTransformer); });
var registered = mode != "before";
if (registered)
{
    builder.Services.AddTisilia(o =>
    {
        o.ApiId = "adoption-api";
        // Paging.BindAsync also reads size, which this declaration leaves out: doctor --allow-execute-binders reports it
        if (mode == "binders") { o.CustomBinding.BindAsync<Paging>(reads => reads.Query<int>("page")); }
    });
}
if (mode == "preserve") { builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve); }
if (mode == "provider-failure") { builder.Services.AddSingleton<IApiDescriptionGroupCollectionProvider, BrokenDescriptions>(); }
var app = builder.Build();
app.UsePathBase("/base");
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
var calls = 0;
var posts = 0;
app.Use(async (c, next) => { if (!c.Request.Path.StartsWithSegments("/__tisilia") && c.Request.Path != "/counts") { Interlocked.Increment(ref calls); } await next(c); });
app.MapGet("/counts", () => new { calls, posts });
void Select(RouteHandlerBuilder e, string id) { if (registered && (mode != "preserve" || !id.StartsWith("forms.", StringComparison.Ordinal) || id == "forms.secure")) { e.WithTisiliaOperation(id); } }
var group = app.MapGroup("/v1");
var endpointJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
if (mode != "preserve")
{
    Select(group.MapPost("/custom-json", (CustomJsonValue value) => TypedResults.Json(value, endpointJson)).WithTisiliaJsonOptions<CustomJsonValue>(endpointJson), "json.custom");
}
async Task<IResult> Upload(Stream body)
{
    using var bytes = new MemoryStream();
    await body.CopyToAsync(bytes);
    return Results.Bytes(bytes.ToArray(), "application/octet-stream");
}
Select(group.MapPost("/upload", (Stream body) => Upload(body)).Accepts<Stream>("application/octet-stream").Produces<FileContentResult>(200, "application/octet-stream"), "upload.stream");
Select(group.MapPost("/upload-pipe", (System.IO.Pipelines.PipeReader body) => Upload(body.AsStream())).Accepts<System.IO.Pipelines.PipeReader>("application/octet-stream").Produces<FileContentResult>(200, "application/octet-stream"), "upload.pipe");
Select(group.MapPost("/upload-optional", (Stream body) => Upload(body)).Accepts<Stream>(isOptional: true, "application/octet-stream").Produces<FileContentResult>(200, "application/octet-stream"), "upload.optional");
Observed Observe(HttpContext c, string? first, string? second = null) => new(first, second, c.Request.PathBase.Value ?? "");
if (mode != "preserve")
{
    Select(group.MapGet("/required/{id:guid}", (HttpContext c, Guid id) => Observe(c, id.ToString())), "route.required");
    Select(group.MapGet("/optional/{id?}", (HttpContext c, string? id) => Observe(c, id)), "route.optional");
    Select(group.MapGet("/default/{page=1}", (HttpContext c, int page = 7) => Observe(c, page.ToString(System.Globalization.CultureInfo.InvariantCulture))), "route.default");
    Select(group.MapGet("/complex/{filename}.{ext?}", (HttpContext c, string filename, string? ext) => Observe(c, filename, ext)), "route.complex");
    Select(group.MapGet("/star/{*path}", (HttpContext c, string? path) => Observe(c, path)), "route.star");
    Select(group.MapGet("/stars/{**path}", (HttpContext c, string? path) => Observe(c, path)), "route.stars");
    Select(group.MapGet("/transform/{id:slug}", (HttpContext c, string id) => Observe(c, id)), "route.transform");
    Select(group.MapGet("/incoming/{id:incoming}", (HttpContext c, string id) => Observe(c, id)), "route.incoming");
    Select(group.MapGet("/name/{名前}", (HttpContext c, [FromRoute(Name = "名前")] string value) => Observe(c, value)), "route.name");
    Select(group.MapGet("/casing/{ID}", (HttpContext c, [FromRoute(Name = "id")] string value) => Observe(c, value)), "route.casing");
    Select(group.MapGet("/bytes", () => TypedResults.Ok(Known.Bytes)), "json.bytes");
    Select(group.MapGet("/async", Known.Values), "json.async");
}
Select(group.MapGet("/file", () => Results.File(Known.Bytes, "application/pdf", "report.pdf")).Produces<FileContentResult>(200, "application/pdf").RequireCors("exposed"), "file.get");
Select(group.MapPost("/file", () => { Interlocked.Increment(ref posts); return Results.File(Known.Bytes, "text/html", "report.html"); }).Produces<FileContentResult>(200, "text/html").RequireCors("exposed"), "file.post");
Select(group.MapGet("/hidden", () => Results.File(Known.Bytes, "image/svg+xml", "report.svg")).Produces<FileContentResult>(200, "image/svg+xml"), "file.hidden");
Select(group.MapGet("/stream", () => TypedResults.File(new MemoryStream(Known.Bytes), "application/pdf", "stream.pdf")).Produces<FileStreamResult>(200, "application/pdf"), "file.stream");
Select(group.MapGet("/concrete", () => TypedResults.File(Known.Bytes, "application/pdf", "concrete.pdf")).Produces(200, contentType: "application/pdf"), "file.concrete");
Select(group.MapGet("/empty", () => TypedResults.File(Array.Empty<byte>(), "application/pdf")).Produces<FileContentResult>(200, "application/pdf"), "file.empty");
Select(group.MapGet("/json-file", () => Results.File(Known.Bytes, "application/json; charset=iso-8859-1")).Produces<FileContentResult>(200, "application/json"), "file.json");
Select(group.MapGet("/none", () => TypedResults.NoContent()), "file.none");
if (mode != "preserve")
{
    Select(group.MapGet("/union/{status:int}", (int status) => status == 200 ? Results.File(Known.Bytes, "application/pdf") : Results.Json(new Failure(9007199254740993L), statusCode: status))
        .Produces<FileContentResult>(200, "application/pdf").Produces<Failure>(400).Produces<Failure>(404).Produces<Failure>(412).Produces<Failure>(416), "file.union");
}
Select(group.MapGet("/gzip", (Delegate)(async (HttpContext c) =>
{
    c.Response.ContentType = "application/pdf";
    c.Response.Headers.ContentEncoding = "gzip";
    using var memory = new MemoryStream();
    await using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true)) { await gzip.WriteAsync(Known.Large); }
    c.Response.ContentLength = memory.Length;
    await c.Response.Body.WriteAsync(memory.ToArray());
})).Produces<FileContentResult>(200, "application/pdf"), "file.gzip");
Select(group.MapGet("/slow", (Delegate)(async (HttpContext c) =>
{
    c.Response.ContentType = "application/pdf";
    await c.Response.Body.WriteAsync(Known.Bytes.AsMemory(0, 2)); await c.Response.Body.FlushAsync();
    try { await Task.Delay(5000, c.RequestAborted); await c.Response.Body.WriteAsync(Known.Bytes.AsMemory(2)); } catch (OperationCanceledException) { }
})).Produces<FileContentResult>(200, "application/pdf"), "file.slow");
Select(group.MapGet("/partial", (HttpContext c) => { c.Response.StatusCode = 206; c.Response.Headers.ContentRange = "bytes 0-4/10"; return Results.File(Known.Bytes, "application/pdf"); })
    .Produces<FileContentResult>(206, "application/pdf").RequireCors("exposed"), "file.partial");
Select(group.MapMethods("/head", ["HEAD"], () => TypedResults.File(Known.Bytes, "application/pdf")).Produces<FileContentResult>(200, "application/pdf"), "file.head");
Select(group.MapGet("/not-modified", () => Results.StatusCode(304)).Produces(304), "file.not-modified");
group.MapGet("/redirect", () => Results.Redirect("/v1/file"));
group.MapGet("/auth", () => TypedResults.Ok(1)).RequireAuthorization();
group.MapPost("/form", ([FromForm] string value) => value);
Select(group.MapPost("/form-values", ([FromForm] string value, [FromForm] long id, [FromForm] decimal amount, [FromForm] int[] tags) => new FormObservation(value, id, amount, tags))
    .Accepts<IFormCollection>("application/x-www-form-urlencoded").DisableAntiforgery(), "forms.values");
Select(group.MapPost("/form-multipart", ([FromForm] string value, [FromForm] long id, [FromForm] decimal amount, [FromForm] int[] tags) => new FormObservation(value, id, amount, tags))
    .Accepts<IFormCollection>("multipart/form-data").DisableAntiforgery(), "forms.multipart");
Select(group.MapPost("/form-secure", ([FromForm] string value) => value), "forms.secure");
async Task<UploadedFile> FileData(IFormFile file)
{
    using var data = new MemoryStream(); await file.CopyToAsync(data); return new UploadedFile(file.FileName, data.ToArray());
}
Select(group.MapPost("/form-file", (IFormFile file) => FileData(file)).DisableAntiforgery(), "forms.file");
Select(group.MapPost("/form-files", async (IFormFileCollection files) =>
{
    var values = new List<UploadedFile>(); foreach (var file in files) { values.Add(await FileData(file)); } return values.ToArray();
}).DisableAntiforgery(), "forms.files");
if (mode != "preserve")
{
    Select(group.MapPost("/form-order", ([FromForm] OrderForm value) => value).DisableAntiforgery(), "forms.order");
    Select(group.MapPost("/form-lines", ([FromForm] List<OrderLine> values) => values).DisableAntiforgery(), "forms.lines");
    Select(group.MapPost("/form-list", ([FromForm] List<long> values) => values).DisableAntiforgery(), "forms.list");
    Select(group.MapPost("/form-attachments", ([FromForm] AttachmentForm value) => value.Items!.Select(i => new AttachmentObservation(i.Label, i.File!.FileName, i.File.Length)).ToArray()).DisableAntiforgery(), "forms.attachments");
    Select(group.MapPost("/form-nested", ([FromForm] FormModel value) => new NestedObservation(value.Title, value.Details!.Id, value.Details.Tags!, (long)value.Mode))
        .DisableAntiforgery(), "forms.nested");
    Select(group.MapPost("/form-enum", ([FromForm] FormMode mode) => (long)mode).DisableAntiforgery(), "forms.enum");
}
if (mode != "preserve")
{
    Select(group.MapPost("/datetime-form", ([FromForm] DateTime at) => DateObservation.From(at))
        .Accepts<IFormCollection>("application/x-www-form-urlencoded").DisableAntiforgery(), "datetime.form");
    Select(group.MapPost("/datetime-form-model", ([FromForm] DateFormModel value) => DateObservation.From(value.At))
        .Accepts<DateFormModel>("multipart/form-data").DisableAntiforgery(), "datetime.form-model");
    Select(group.MapGet("/datetime-query", (DateTime at) => DateObservation.From(at)), "datetime.query");
    Select(group.MapGet("/datetime-route/{at}", (DateTime at) => DateObservation.From(at)), "datetime.route");
    Select(group.MapGet("/datetime-header", ([FromHeader] DateTime at) => DateObservation.From(at)), "datetime.header");
    Select(group.MapPost("/datetime", (MixedDateDto value) => value), "datetime.echo");
    Select(group.MapPost("/datetime-observe", (DateTime[] values) => values.Select(v => new DateObservation(v, v.Kind.ToString(), v.Ticks, v.Kind == DateTimeKind.Local ? TimeZoneInfo.Local.GetUtcOffset(v).Ticks : 0)).ToArray()), "datetime.observe");
}
group.MapGet("/csrf", (HttpContext c, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(c).RequestToken);
group.MapGet("/preserve", () => { var shared = new object(); return Results.Json(new[] { shared, shared }, new System.Text.Json.JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve }); });
Select(group.MapGet("/sse", () => TypedResults.ServerSentEvents(Known.Values())), "events.json");
Select(group.MapGet("/sse-text", () => TypedResults.ServerSentEvents(Known.TextEvents())), "events.text");
Select(group.MapGet("/sse-slow", (CancellationToken token) => TypedResults.ServerSentEvents(Known.SlowEvents(token))), "events.slow");
if (mode == "diagnostics")
{
    Select(group.MapGet("/date-one", () => new DateDto(DateTime.UtcNow)), "bad.date.one");
    Select(group.MapGet("/date-two", () => new DateDto(DateTime.Now)), "bad.date.two");
    Select(group.MapGet("/unknown", () => Results.File(Known.Bytes, "application/pdf")), "bad.unknown");
}
if (mode == "excluded") { Select(group.MapGet("/excluded", () => 1).ExcludeFromDescription(), "bad.excluded"); }
if (mode == "binders") { Select(group.MapGet("/paging", (Paging paging) => paging.Page), "binders.paging"); }
app.MapControllers().WithTisiliaJsonOptions<CustomJsonValue>("mvc.json", JsonSettings.Response);
if (registered) { app.MapTisiliaContract(); app.MapTisiliaExplorer(); }
app.Run();

public sealed record Observed(string? First, string? Second, string PathBase);
public sealed record Paging(int Page, int Size)
{
    public static ValueTask<Paging?> BindAsync(HttpContext context) => ValueTask.FromResult<Paging?>(new(
        int.TryParse(context.Request.Query["page"], out var page) ? page : 1, int.TryParse(context.Request.Query["size"], out var size) ? size : 10));
}
public sealed record Failure(long Id);
public sealed record DateDto([property: JsonConverter(typeof(UnsupportedDateConverter))] DateTime At);
public sealed class UnsupportedDateConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTime();
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
public sealed record MixedDateDto(DateTime At, DateTime? Optional, DateTime[] Items, Dictionary<DateTime, string> Keys);
public sealed class DateFormModel { public DateTime At { get; set; } }
public sealed record DateObservation(DateTime Value, string Kind, long Ticks, long OffsetTicks)
{
    public static DateObservation From(DateTime value) => new(value, value.Kind.ToString(), value.Ticks, value.Kind == DateTimeKind.Local ? TimeZoneInfo.Local.GetUtcOffset(value).Ticks : 0);
}
public sealed record FormObservation(string Value, long Id, decimal Amount, int[] Tags);
public sealed record UploadedFile(string Name, byte[] Bytes);
public sealed record CustomJsonValue(long LargeNumber);
public static class JsonSettings
{
    public static System.Text.Json.JsonSerializerOptions Response { get; } = new(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
}
public enum FormMode : long { Small = 1, Large = 9007199254740993L }
public sealed record NestedObservation(string? Title, long Id, string[] Tags, long Mode);
public sealed class FormModel
{
    [System.Runtime.Serialization.DataMember(Name = "title_text", IsRequired = true)]
    public string? Title { get; set; }
    public FormDetails? Details { get; set; }
    public FormMode Mode { get; set; }
}
public sealed class FormDetails
{
    public long Id { get; set; }
    public string[]? Tags { get; set; }
}
public static class Known
{
    public static byte[] Bytes { get; } = [0, 255, 1, 195, 40];
    public static byte[] Large { get; } = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
    public static async IAsyncEnumerable<long> Values() { await Task.Yield(); yield return 9007199254740993L; yield return long.MaxValue; }
    public static async IAsyncEnumerable<System.Net.ServerSentEvents.SseItem<string>> TextEvents()
    {
        yield return new("日本😀\nsecond line", "update") { EventId = "event-1", ReconnectionInterval = TimeSpan.FromMilliseconds(123) };
        await Task.Yield(); yield return new("");
    }
    public static async IAsyncEnumerable<long> SlowEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return 9007199254740993L;
        await Task.Delay(5000, token);
        yield return 2;
    }
}
public sealed class IncomingConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) => values[routeKey]?.ToString()?.StartsWith('x') == true;
}
public sealed class SlugTransformer : IOutboundParameterTransformer { public string? TransformOutbound(object? value) => value?.ToString()?.ToLowerInvariant(); }
public sealed class BrokenDescriptions : IApiDescriptionGroupCollectionProvider
{
    public ApiDescriptionGroupCollection ApiDescriptionGroups => throw new InvalidOperationException("Password=TEST_SECRET; C:\\private\\connection.json");
}
[ApiController]
[Route("mvc/[controller]")]
public sealed class FilesController : ControllerBase
{
    [HttpGet("datetime/{path}")]
    [TisiliaOperation("datetime.mvc")]
    public DateObservation[] Dates([FromRoute] DateTime path, [FromQuery] DateTime query, [FromHeader] DateTime header)
        => [DateObservation.From(path), DateObservation.From(query), DateObservation.From(header)];
    [HttpPost("form")]
    [TisiliaOperation("mvc.form")]
    public string Form([FromForm] string value) => value;

    [HttpPost("enum")]
    [TisiliaOperation("mvc.enum")]
    public long EnumValue([FromForm] FormMode mode) => (long)mode;

    [HttpPost("json")]
    [TisiliaOperation("mvc.json")]
    public JsonResult CustomJson([FromBody] CustomJsonValue value) => new(value, JsonSettings.Response);

    [HttpPost("upload")]
    [TisiliaOperation("mvc.upload")]
    public async Task<UploadedFile> Upload(IFormFile file)
    {
        using var data = new MemoryStream(); await file.CopyToAsync(data); return new UploadedFile(file.FileName, data.ToArray());
    }
    [HttpGet("content")]
    [ProducesResponseType(typeof(FileContentResult), 200, "application/pdf")]
    [TisiliaOperation("mvc.file")]
    public IActionResult ContentFile() => File(Known.Bytes, "application/pdf", "mvc.pdf");
    [HttpGet("stream")]
    [ProducesResponseType(typeof(FileStreamResult), 200, "application/pdf")]
    [TisiliaOperation("mvc.stream")]
    public IActionResult StreamFile() => File(new MemoryStream(Known.Bytes), "application/pdf", "mvc.pdf");
}

public sealed record OrderForm(List<OrderLine> Lines);
public sealed record OrderLine(long Id, OrderDetail Details);
public sealed record OrderDetail(string Label, string[] Tags, List<OrderNote> Notes);
public sealed record OrderNote(string Text);
public sealed class AttachmentForm { public List<AttachmentLine>? Items { get; set; } }
public sealed class AttachmentLine { public string? Label { get; set; } public IFormFile? File { get; set; } }
public sealed record AttachmentObservation(string? Label, string FileName, long Length);
