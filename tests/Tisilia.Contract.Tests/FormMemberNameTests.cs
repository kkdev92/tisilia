using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: the minimal API form mapper (aspnetcore v10.0.0) reads a member named with '.', '[' or ']' from exactly that key,
/// nested or in collection items, through Kestrel. Two members that come out as one key both receive its value. (An unmatched '['
/// matters only to dictionary members, whose key scan looped on it before 10.0.12; forms with dictionaries are diagnosed.)
/// </summary>
public sealed class FormMemberNameTests
{
    [Fact]
    public async Task Member_names_with_path_or_index_delimiters_bind_from_exactly_that_key()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-names");
        await using var app = builder.Build();
        app.MapPost("/names", ([FromForm] DelimitedNames value) => new { value.Dotted, value.Bracketed, value.Closing, value.Opening, inner = value.Inner?.Dotted, items = value.Items?.Select(i => i.Dotted) })
            .DisableAntiforgery().WithTisiliaOperation("names");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var form = Assert.IsType<FormRequestBody>(export.Index!.Operations["names"].RequestBody);
        Assert.Equal(["Inner.a.b", "Items", "a.b", "p]q", "q[", "x[y]"], form.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal("a.b", Assert.Single(form.Fields.Single(f => f.Name == "Items").Fields!).Name);

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/names", new FormUrlEncodedContent([
            KeyValuePair.Create("a.b", "dotted"), KeyValuePair.Create("x[y]", "bracketed"), KeyValuePair.Create("p]q", "closing"), KeyValuePair.Create("q[", "opening"),
            KeyValuePair.Create("Inner.a.b", "inner"), KeyValuePair.Create("Items[0].a.b", "first"), KeyValuePair.Create("Items[1].a.b", "second")]));
        Assert.Equal(200, (int)response.StatusCode);
        using var bound = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dotted", bound.RootElement.GetProperty("dotted").GetString());
        Assert.Equal("bracketed", bound.RootElement.GetProperty("bracketed").GetString());
        Assert.Equal("closing", bound.RootElement.GetProperty("closing").GetString());
        Assert.Equal("opening", bound.RootElement.GetProperty("opening").GetString());
        Assert.Equal("inner", bound.RootElement.GetProperty("inner").GetString());
        Assert.Equal("[\"first\",\"second\"]", bound.RootElement.GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task Two_members_that_come_out_as_one_key_are_diagnosed()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "form-collision");
        await using var app = builder.Build();
        app.MapPost("/collision", ([FromForm] CollidingNames value) => new { nested = value.A?.B, dotted = value.Dotted })
            .DisableAntiforgery().WithTisiliaOperation("collision");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Message.Contains("overlapping wire names", StringComparison.Ordinal));

        // Kestrel: the one key A.B fills both members, so a client cannot send them separately
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/collision", new FormUrlEncodedContent([KeyValuePair.Create("A.B", "v")]));
        Assert.Equal("{\"nested\":\"v\",\"dotted\":\"v\"}", await response.Content.ReadAsStringAsync());
    }

    public sealed class DelimitedNames
    {
        [DataMember(Name = "a.b")] public string? Dotted { get; set; }
        [DataMember(Name = "x[y]")] public string? Bracketed { get; set; }
        [DataMember(Name = "p]q")] public string? Closing { get; set; }
        [DataMember(Name = "q[")] public string? Opening { get; set; }
        public DottedInner? Inner { get; set; }
        public List<DottedInner>? Items { get; set; }
    }

    public sealed class DottedInner { [DataMember(Name = "a.b")] public string? Dotted { get; set; } }

    public sealed class CollidingNames
    {
        public CollidingLeaf? A { get; set; }
        [DataMember(Name = "A.B")] public string? Dotted { get; set; }
    }

    public sealed class CollidingLeaf { public string? B { get; set; } }
}
