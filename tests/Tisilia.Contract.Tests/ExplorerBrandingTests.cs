using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tisilia.AspNetCore;
using Tisilia.Explorer;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class ExplorerBrandingTests
{
    [Fact]
    public void Embedded_artwork_carries_the_canonical_policy()
    {
        using var stream = typeof(TisiliaExplorerExtensions).Assembly.GetManifestResourceStream("explorer/BRAND-ASSET-POLICY.md");
        Assert.NotNull(stream);
        var original = File.ReadAllBytes(Path.Combine(FixtureTests.RepoRoot(), "assets", "brand", "tisilia", "BRAND-ASSET-POLICY.md"));
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(stream));
    }

    [Theory]
    [InlineData("mascot.jpg")]
    [InlineData("mascot.jpeg")]
    [InlineData("mascot.JPG")]
    [InlineData("mascot.JPEG")]
    public void Jpeg_content_type(string path) => Assert.Equal("image/jpeg", TisiliaExplorerExtensions.ContentTypeOf(path));

    [Theory]
    [InlineData("", "/__tisilia", false)]
    [InlineData("/app", "/__tisilia", false)]
    [InlineData("/app", "/tools/api", true)]
    public async Task Embedded_images_keep_bytes_headers_routes_and_authorization(string pathBase, string route, bool protectedRoute)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = protectedRoute ? "Production" : "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "branding-check";
            o.ExplorerRoute = route;
            o.AllowProduction = protectedRoute;
            o.AuthorizationPolicy = protectedRoute ? "test" : null;
        });
        builder.Services.AddAuthentication("test").AddCookie("test");
        builder.Services.AddAuthorization(o => o.AddPolicy("test", p => p.RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.UsePathBase(pathBase);
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-User"))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"));
            }

            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTisiliaExplorer();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        var assembly = typeof(TisiliaExplorerExtensions).Assembly;
        var images = assembly.GetManifestResourceNames().Where(n => n.EndsWith(".jpg", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, images.Length);
        foreach (var image in images)
        {
            var url = pathBase + route + "/" + image["explorer/".Length..].Replace('\\', '/');
            if (protectedRoute)
            {
                using var denied = await client.GetAsync(url);
                Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Test-User", "test");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("img-src 'self' data:", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            var theme = image.Contains("-dark-", StringComparison.Ordinal) ? "dark" : "light";
            var original = await File.ReadAllBytesAsync(Path.Combine(FixtureTests.RepoRoot(), "assets", "brand", "tisilia", "illustrations", $"mascot-chibi-{theme}.jpg"));
            Assert.Equal(SHA256.HashData(original), SHA256.HashData(await response.Content.ReadAsByteArrayAsync()));
        }

        await app.StopAsync();
    }

    [Fact]
    public async Task Production_guard_also_protects_static_images()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddTisilia(o => o.ApiId = "branding-check");
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapTisiliaExplorer());
    }
}
