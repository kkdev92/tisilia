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

    /// <summary>
    /// Oracle: Node's fetch sends a ReadableStream body chunked as it is read (duplex "half"), and Kestrel hands the handler every byte;
    /// the stream is never buffered into one array first.
    /// </summary>
    [Fact]
    public async Task A_streamed_raw_upload_reaches_Kestrel_chunked_and_whole()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "streamed-upload");
        await using var app = builder.Build();
        app.MapPost("/upload", async (HttpContext context, Stream body) =>
        {
            long count = 0, sum = 0;
            var buffer = new byte[8192];
            for (int read; (read = await body.ReadAsync(buffer)) > 0;)
            {
                for (var i = 0; i < read; i++) { if (buffer[i] != (byte)((count + i) % 251)) { return Results.Text($"byte {count + i} differs"); } sum += buffer[i]; }
                count += read;
            }
            return Results.Text($"{count}:{sum}:{context.Request.ContentLength?.ToString() ?? "chunked"}");
        }).Accepts<Stream>("application/octet-stream").Produces<string>(200, "text/plain").WithTisiliaOperation("upload");
        await app.StartAsync();
        var exported = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(exported.Diagnostics.HasErrors, string.Join("\n", exported.Diagnostics.Items));
        var dir = Directory.CreateTempSubdirectory("tisilia-streamed-upload-");
        try
        {
            // the runtime's own send path (execute): the prepared request carries the stream, not bytes
            var sent = await InterpreterRequests.SendAsync(dir.FullName, exported.Text!, app.Urls.Single(), [("upload", new { body = new Dictionary<string, object> { ["$stream"] = new { size = 65539 } } })], execute: true);
            long expected = 0;
            for (var i = 0; i < 65539; i++) { expected += i % 251; }
            using var result = JsonDocument.Parse(sent[0]);
            Assert.Equal($"65539:{expected}:chunked", result.RootElement.GetProperty("data").GetString());
        }
        finally { dir.Delete(recursive: true); }
    }
}
