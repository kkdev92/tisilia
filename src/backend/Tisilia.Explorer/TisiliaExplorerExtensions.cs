using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Generator.Canonical;

namespace Tisilia.Explorer;

public static class TisiliaExplorerExtensions
{
    private const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    /// <summary>
    /// Maps the Explorer under <see cref="TisiliaOptions.ExplorerRoute"/> (default <c>/__tisilia</c>): the SPA assets, the
    /// contract (<see cref="TisiliaEndpointExtensions.MapTisiliaContract"/> route) and the browser artifacts of the contract's
    /// modules, all behind the same publication guard. Assets carry a CSP that forbids remote code.
    /// </summary>
    public static IEndpointConventionBuilder MapTisiliaExplorer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<TisiliaOptions>>().Value;
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        TisiliaEndpointExtensions.GuardPublication(options, environment, "MapTisiliaExplorer");
        var route = options.ExplorerRoute.TrimEnd('/');
        var assembly = typeof(TisiliaExplorerExtensions).Assembly;
        // keyed by the request path's form: %(RecursiveDir) gives the names a Windows build embeds a backslash (explorer/assets\x.js)
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("explorer/", StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);

        var group = endpoints.MapGroup(route);
        if (options.AuthorizationPolicy is not null)
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        // A relative redirect resolves against the URL the browser asked for, so it keeps what precedes the route: a PathBase
        // (IIS virtual application, X-Forwarded-Prefix) and the prefix of a proxy that strips it before the application sees it
        var lastSegment = Uri.EscapeDataString(route[(route.LastIndexOf('/') + 1)..]);
        group.MapGet("/", (HttpContext context) => Results.Redirect(context.Request.Path.Value?.EndsWith('/') != false ? "index.html" : lastSegment + "/index.html", permanent: false))
            .ExcludeFromDescription();
        group.MapGet("/{**path}", async (string path, HttpContext context, TisiliaContractExporter exporter) =>
        {
            if (path.StartsWith("modules/", StringComparison.Ordinal))
            {
                return ServeModule(path["modules/".Length..], exporter, environment);
            }

            if (!resources.TryGetValue("explorer/" + path.Replace('\\', '/'), out var name))
            {
                return resources.Count == 0
                    ? Results.Problem(title: "Tisilia Explorer bundle not built", detail: "run `npm run build -w src/frontend/explorer` before building Tisilia.Explorer", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.NotFound();
            }

            context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "no-store";
            if (path == "index.html")
            {
                return Results.Content(IndexHtml(assembly, name, route, options.SemanticHashHeader, await AuthHintsAsync(context.RequestServices)), "text/html; charset=utf-8");
            }

            var stream = assembly.GetManifestResourceStream(name)!;
            return Results.Stream(stream, ContentTypeOf(path));
        }).ExcludeFromDescription();
        return group;
    }

    /// <summary>
    /// The page with what it cannot learn from the contract, in meta elements: the Explorer's route (the API is called at the page's
    /// URL up to it, which keeps a PathBase or a proxy's prefix the application itself may not know), the response header that
    /// carries the semantic hash, and the application's authentication schemes (what the Authorize dialog offers first).
    /// </summary>
    private static string IndexHtml(Assembly assembly, string resource, string route, string semanticHashHeader, string authHints)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var html = reader.ReadToEnd();
        var meta = $"<meta name=\"tisilia-explorer-route\" content=\"{System.Net.WebUtility.HtmlEncode(route.Length == 0 ? "/" : route)}\" />\n  "
            + $"<meta name=\"tisilia-explorer-hash-header\" content=\"{System.Net.WebUtility.HtmlEncode(semanticHashHeader)}\" />\n  "
            + $"<meta name=\"tisilia-explorer-auth\" content=\"{System.Net.WebUtility.HtmlEncode(authHints)}\" />\n  ";
        var head = html.IndexOf("</head>", StringComparison.Ordinal);
        return head < 0 ? html : html.Insert(head, meta);
    }

    /// <summary>
    /// The registered authentication schemes as JSON (<c>[{"scheme","kind","handler"}]</c>), classified by handler type: names and
    /// kinds only, nothing configured on them. Forwarding (policy) schemes are left out; they select one of the others.
    /// </summary>
    internal static async Task<string> AuthHintsAsync(IServiceProvider services)
    {
        if (services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() is not { } provider)
        {
            return "[]";
        }

        var hints = new System.Text.Json.Nodes.JsonArray();
        foreach (var scheme in await provider.GetAllSchemesAsync().ConfigureAwait(false))
        {
            var handler = scheme.HandlerType;
            if (handler.FullName == "Microsoft.AspNetCore.Authentication.PolicySchemeHandler")
            {
                continue;
            }

            hints.Add(new System.Text.Json.Nodes.JsonObject { ["scheme"] = scheme.Name, ["kind"] = KindOf(handler), ["handler"] = handler.Name });
        }

        return hints.ToJsonString();
    }

    private static string KindOf(Type handler) => handler.FullName switch
    {
        "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler" or "Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenHandler" => "bearer",
        "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler" => "cookie",
        "Microsoft.AspNetCore.Authentication.Negotiate.NegotiateHandler" => "negotiate",
        "Microsoft.AspNetCore.Authentication.Certificate.CertificateAuthenticationHandler" => "certificate",
        _ when handler.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) => "api-key",
        _ when handler.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase) => "basic",
        _ when handler.Name.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || handler.Name.Contains("Jwt", StringComparison.OrdinalIgnoreCase) => "bearer",
        _ => "other",
    };

    /// <summary>Browser artifacts are served from the module's declared path under the content root only when the bytes still match the contract's digest.</summary>
    private static IResult ServeModule(string rest, TisiliaContractExporter exporter, IHostEnvironment environment)
    {
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return Results.NotFound();
        }

        var moduleId = Uri.UnescapeDataString(rest[..slash]);
        var artifactPath = string.Join('/', rest[(slash + 1)..].Split('/').Select(Uri.UnescapeDataString));
        var export = exporter.Export();
        if (export.Diagnostics.HasErrors || export.Index is null)
        {
            return Results.Problem(title: "contract export failed", statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!export.Index.Modules.TryGetValue(moduleId, out var module))
        {
            return Results.NotFound();
        }

        var artifact = module.Artifacts.FirstOrDefault(a => a.Target == ArtifactTarget.Browser && a.Path == artifactPath);
        if (artifact is null || artifactPath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(artifactPath))
        {
            return Results.NotFound();
        }

        var full = Path.GetFullPath(Path.Combine(environment.ContentRootPath, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(environment.ContentRootPath), StringComparison.Ordinal) || !File.Exists(full))
        {
            return Results.NotFound();
        }

        var bytes = File.ReadAllBytes(full);
        if (TisiliaHash.Sha256OfBytes(bytes) != artifact.Digest)
        {
            return Results.Problem(title: "module artifact digest mismatch", detail: $"module '{moduleId}' artifact '{artifactPath}' on disk is not the one the contract was exported against" + (LineEndings.ArtifactHint(bytes, artifact.Digest, full) is { } hint ? ": " + hint : ""), statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Bytes(bytes, "text/javascript; charset=utf-8");
    }

    internal static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".map" => "application/json",
        _ => "application/octet-stream",
    };
}
