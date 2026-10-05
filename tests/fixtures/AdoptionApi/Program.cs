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
    o.AddDefaultPolicy(p => p.WithOrigins("http://127.0.0.1:4179").AllowAnyMethod().AllowAnyHeader());
    o.AddPolicy("exposed", p => p.WithOrigins("http://127.0.0.1:4179").AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Content-Disposition", "Content-Range"));
});
builder.Services.Configure<RouteOptions>(o => { o.ConstraintMap["incoming"] = typeof(IncomingConstraint); o.ConstraintMap["slug"] = typeof(SlugTransformer); });
var registered = mode != "before";
if (registered) { builder.Services.AddTisilia(o => o.ApiId = "adoption-api"); }
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
void Select(RouteHandlerBuilder e, string id) { if (registered) { e.WithTisiliaOperation(id); } }
var group = app.MapGroup("/v1");
Observed Observe(HttpContext c, string? first, string? second = null) => new(first, second, c.Request.PathBase.Value ?? "");
if (mode != "preserve")
{
    Select(group.MapGet("/required/{id:guid}", (HttpContext c, Guid id) => Observe(c, id.ToString())), "route.required");
    Select(group.MapGet("/optional/{id?}", (HttpContext c, string? id) => Observe(c, id)), "route.optional");
    Select(group.MapGet("/default/{page=1}", (HttpContext c, int page = 7) => Observe(c, page.ToString(System.Globalization.CultureInfo.InvariantCulture))), "route.default");
    Select(group.MapGet("/complex/{filename}.{ext?}", (HttpContext c, string filename, string? ext) => Observe(c, filename, ext)), "route.complex");
    Select(group.MapGet("/star/{*path}", (HttpContext c, string? path) => Observe(c, path)), "route.star");
    Select(group.MapGet("/stars/{**path}", (HttpContext c, string? path) => Observe(c, path)), "route.stars");
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
group.MapGet("/csrf", (HttpContext c, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(c).RequestToken);
group.MapGet("/preserve", () => { var shared = new object(); return Results.Json(new[] { shared, shared }, new System.Text.Json.JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve }); });
group.MapGet("/sse", () => TypedResults.ServerSentEvents(Known.Values()));
if (mode == "diagnostics")
{
    Select(group.MapGet("/date-one", () => new DateDto(DateTime.UtcNow)), "bad.date.one");
    Select(group.MapGet("/date-two", () => new DateDto(DateTime.Now)), "bad.date.two");
    Select(group.MapGet("/unknown", () => Results.File(Known.Bytes, "application/pdf")), "bad.unknown");
    Select(group.MapGet("/transform/{id:slug}", (string id) => id), "bad.transform");
}
if (mode == "excluded") { Select(group.MapGet("/excluded", () => 1).ExcludeFromDescription(), "bad.excluded"); }
app.MapControllers();
if (registered) { app.MapTisiliaContract(); app.MapTisiliaExplorer(); }
app.Run();

public sealed record Observed(string? First, string? Second, string PathBase);
public sealed record Failure(long Id);
public sealed record DateDto(DateTime At);
public static class Known
{
    public static byte[] Bytes { get; } = [0, 255, 1, 195, 40];
    public static byte[] Large { get; } = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
    public static async IAsyncEnumerable<long> Values() { await Task.Yield(); yield return 9007199254740993L; yield return long.MaxValue; }
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
    [HttpGet("content")]
    [ProducesResponseType(typeof(FileContentResult), 200, "application/pdf")]
    [TisiliaOperation("mvc.file")]
    public IActionResult ContentFile() => File(Known.Bytes, "application/pdf", "mvc.pdf");
    [HttpGet("stream")]
    [ProducesResponseType(typeof(FileStreamResult), 200, "application/pdf")]
    [TisiliaOperation("mvc.stream")]
    public IActionResult StreamFile() => File(new MemoryStream(Known.Bytes), "application/pdf", "mvc.pdf");
}
