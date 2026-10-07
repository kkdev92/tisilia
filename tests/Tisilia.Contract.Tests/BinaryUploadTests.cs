using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

public sealed class BinaryUploadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_upload_metadata_and_Kestrel_preserve_bytes(bool pipe)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "upload");
        await using var app = builder.Build();
        var calls = 0;
        async Task<IResult> Echo(Stream body)
        {
            calls++;
            using var bytes = new MemoryStream();
            await body.CopyToAsync(bytes);
            return Results.Bytes(bytes.ToArray(), "application/octet-stream");
        }
        var endpoint = pipe
            ? app.MapPost("/upload", (PipeReader body) => Echo(body.AsStream())).Accepts<PipeReader>("application/octet-stream")
            : app.MapPost("/upload", (Stream body) => Echo(body)).Accepts<Stream>("application/octet-stream");
        endpoint.Produces<Microsoft.AspNetCore.Mvc.FileContentResult>(200, "application/octet-stream").WithTisiliaOperation("upload");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        Assert.Equal(0, calls);
        using var contract = JsonDocument.Parse(exported.Text!);
        Assert.Equal("binary", contract.RootElement.GetProperty("operations")[0].GetProperty("requestBody").GetProperty("kind").GetString());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var data in new byte[][] { [], [0, 255, 195, 40, 13, 10] })
        {
            using var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PostAsync("/upload", content);
            response.EnsureSuccessStatusCode();
            Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*/*")]
    [InlineData("multipart/form-data")]
    public async Task Undeclared_or_ambiguous_raw_bodies_are_diagnosed(string? mediaType)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "upload");
        await using var app = builder.Build();
        var endpoint = app.MapPost("/upload", (Stream body) => TypedResults.NoContent());
        if (mediaType is not null) { endpoint.Accepts<Stream>(mediaType); }
        endpoint.WithTisiliaOperation("upload");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(exported.Diagnostics.HasErrors);
        Assert.Null(exported.Text);
    }
}
