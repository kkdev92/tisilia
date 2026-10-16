using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Conformance;
using Tisilia.AspNetCore.Export;
using Tisilia.Documents;
using Tisilia.Generator.Conformance;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The .NET runner projects what the server read, and what it is about to write, into the domain the suite compares. A
/// <c>Memory&lt;byte&gt;</c> or <c>ReadOnlyMemory&lt;byte&gt;</c> member is bytes in the contract, like <c>byte[]</c>, but the
/// projection cast the value to <c>byte[]</c>, so every request round trip of a model with such a member failed
/// (projection.clr-type-mismatch).
/// </summary>
public sealed class ConformanceProjectionTests
{
    [Fact]
    public async Task Byte_memory_members_project_as_the_bytes_they_hold()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "byte-memory");
        await using var app = builder.Build();
        app.MapPost("/blobs", (Blob blob) => TypedResults.Ok(blob)).WithTisiliaOperation("blobs.echo");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var request = ((JsonRequestBody)index.Operations["blobs.echo"].RequestBody!).Use.CodecId;
        var response = index.Operations["blobs.echo"].Responses.Select(r => r.Body).OfType<JsonResponseBody>().Single().Use.CodecId;
        var profile = index.Codecs[request].ProfileIds[0];
        var runner = new DotnetRunnerCore(index, export.Adapters!, []);
        string Run(RunnerAction action, string codec, string json)
            => DomainAst.ToJsonText(runner.Handle(RunnerProtocol.Request("bytes", "run", action, codec, profile, [], [DomainAst.ParseJsonText(json, 64)])));

        // the empty value too: the case that reached the runner first
        foreach (var json in new[] { """{"array":"AAEC/w==","memory":"CQ==","readOnly":"AAEC/w=="}""", """{"array":"","memory":"","readOnly":""}""" })
        {
            Assert.Equal(json, Run(RunnerAction.DotnetRead, request, json));
            Assert.Equal(json, Run(RunnerAction.DotnetWrite, response, json));
        }
    }

    public sealed record Blob(byte[] Array, Memory<byte> Memory, ReadOnlyMemory<byte> ReadOnly);
}
