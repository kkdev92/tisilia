using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A minimal API fills an IFormFileCollection — a parameter (RequestDelegateFactory: form.Files) or a model member (the form mapper's
/// converter) — with every file of the request, whatever its name; MVC's FormFileModelBinder picks the files of its own name, ignoring
/// case (aspnetcore v10.0.0). Oracle: through Kestrel, the files the runtime's encoder sends arrive where the contract says.
/// </summary>
public sealed class FormFileCollectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-file-collections-" + Guid.NewGuid().ToString("N"));

    public FormFileCollectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> map, bool mvc = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        if (mvc) { builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController())); }
        builder.Services.AddTisilia(o => o.ApiId = "file-collections");
        var app = builder.Build();
        map(app);
        if (mvc) { app.MapControllers(); }
        await app.StartAsync();
        return app;
    }

    private static object File(string name, string text) => new Dictionary<string, object> { ["$file"] = new { name, text } };

    [Fact]
    public async Task A_member_IFormFileCollection_that_is_the_only_file_field_receives_its_files()
    {
        await using var app = await StartAsync(a => a.MapPost("/upload", ([FromForm] DocumentsForm form) => form.Title + ":" + string.Join(",", form.Docs!.Select(d => d.Name + "=" + d.FileName)))
            .DisableAntiforgery().WithTisiliaOperation("upload"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var body = (FormRequestBody)export.Index!.Operations["upload"].RequestBody!;
        Assert.Equal(("file", true), (body.Fields.Single(f => f.Name == "Docs").Kind, body.Fields.Single(f => f.Name == "Docs").Repeated));
        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("upload", new { body = new Dictionary<string, object> { ["Title"] = "t", ["Docs"] = new[] { File("a.txt", "A"), File("b.txt", "B") } } }),
        ]);
        Assert.Equal("200 t:Docs=a.txt,Docs=b.txt", sent[0]);
    }

    [Theory]
    [InlineData("member-and-file", "'Docs'")]
    [InlineData("parameter-and-nested-file", "'files'")]
    [InlineData("in-a-model-collection", "in a model collection")]
    public async Task An_IFormFileCollection_that_would_take_other_fields_files_is_diagnosed(string shape, string named)
    {
        await using var app = await StartAsync(a => (shape switch
        {
            // the member would also receive Avatar's file
            "member-and-file" => a.MapPost("/upload", ([FromForm] DocumentsAndAvatarForm form) => TypedResults.NoContent()),
            // the parameter would also receive Profile.Avatar's file, which sits in a nested object field
            "parameter-and-nested-file" => a.MapPost("/upload", (IFormFileCollection files, [FromForm] ProfileForm form) => TypedResults.NoContent()),
            _ => a.MapPost("/upload", ([FromForm] List<DocumentsForm> forms) => TypedResults.NoContent()),
        }).DisableAntiforgery().WithTisiliaOperation("upload"));
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.Message.Contains(named, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_MVC_IFormFileCollection_receives_the_files_of_its_name_only()
    {
        await using var app = await StartAsync(_ => { }, mvc: true);
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("mvc-upload", new { body = new Dictionary<string, object> { ["files"] = new[] { File("a.txt", "A"), File("b.txt", "B") }, ["avatar"] = File("me.png", "P") } }),
        ]);
        Assert.Equal("200 a.txt,b.txt|me.png", sent[0]);
    }

    private sealed class OnlyController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(FileCollectionController).GetTypeInfo());
        }
    }
}

public sealed class DocumentsForm
{
    public string? Title { get; set; }
    public IFormFileCollection? Docs { get; set; }
}

public sealed class DocumentsAndAvatarForm
{
    public IFormFileCollection? Docs { get; set; }
    public IFormFile? Avatar { get; set; }
}

// a constructor-bound member is a nested object field of the contract, its file one level down
public sealed record ProfileForm(string Name, AvatarPart Profile);

public sealed class AvatarPart
{
    public IFormFile? Avatar { get; set; }
}

[ApiController]
[NonController]
public sealed class FileCollectionController : ControllerBase
{
    [HttpPost("/mvc-upload")]
    [TisiliaOperation("mvc-upload")]
    public string Upload(IFormFileCollection files, IFormFile avatar) => string.Join(",", files.Select(f => f.FileName)) + "|" + avatar.FileName;
}
