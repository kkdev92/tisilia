using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// Oracle: MVC reads a complex form model under its BinderModelName, else under the parameter's name when a key starts with it,
/// else at the root (ParameterBinder, aspnetcore v10.0.0), and ApiExplorer describes the model's leaves without that prefix. The
/// client writes the prefix: through Kestrel, a model with nested models, collections of models and of values and a file, a record
/// under an explicit [FromForm(Name)], and a collection of models bind from the runtime's encoder.
/// </summary>
public sealed class MvcComplexFormTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-mvc-forms-" + Guid.NewGuid().ToString("N"));

    public MvcComplexFormTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task MVC_form_models_bind_under_their_prefix_from_the_runtime_encoder()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MvcFormModelController))));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-form-models");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var order = Assert.IsType<FormRequestBody>(export.Index!.Operations["mvc.order"].RequestBody);
        Assert.Equal(["note", "order"], order.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        var model = order.Fields.Single(f => f.Name == "order");
        Assert.Equal(("object", false), (model.Kind, model.Repeated));
        Assert.Equal(["Details", "File", "Lines", "Name", "Tags"], model.Fields!.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.True(model.Fields!.Single(f => f.Name == "Details").Fields!.Single(f => f.Name == "Amount").RequestCulture);
        Assert.Equal("r", Assert.Single(Assert.IsType<FormRequestBody>(export.Index.Operations["mvc.record"].RequestBody).Fields).Name);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("mvc.order", new
            {
                body = new Dictionary<string, object>
                {
                    ["order"] = new Dictionary<string, object>
                    {
                        ["Name"] = "日本", ["Details"] = new Dictionary<string, object> { ["Amount"] = Input("-1.50"), ["Label"] = "x" },
                        ["Lines"] = new[] { new Dictionary<string, object> { ["Id"] = Input("9007199254740993"), ["Sku"] = "a" }, new Dictionary<string, object> { ["Id"] = Input("2"), ["Sku"] = "b" } },
                        ["Tags"] = new[] { "a", "b" }, ["File"] = new Dictionary<string, object> { ["$file"] = new { name = "f.txt", text = "abc" } },
                    },
                    ["note"] = "n",
                },
            }),
            ("mvc.record", new { body = new { r = new Dictionary<string, object> { ["Name"] = "z", ["Count"] = Input("7") } } }),
            ("mvc.lines", new { body = new { lines = new[] { new Dictionary<string, object> { ["Id"] = Input("1"), ["Sku"] = "a" } } } }),
        ]);
        Assert.Equal("200 日本|-1.50|x|9007199254740993:a,2:b|a,b|f.txt:3|n", sent[0]);
        Assert.Equal("200 z|7", sent[1]);
        Assert.Equal("200 1:a", sent[2]);

        // Kestrel: an explicit [FromForm(Name)] prefix is always read, so the names ApiExplorer reports bind nothing
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var unprefixed = await client.PostAsync("/mvc/record", new FormUrlEncodedContent([KeyValuePair.Create("Name", "z"), KeyValuePair.Create("Count", "7")]));
        Assert.Equal("|0", await unprefixed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MVC_form_models_keep_the_binding_rules_of_their_metadata()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MvcFormRulesController))));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-form-rules");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        // [BindRequired], and under [ApiController] a non-nullable reference, are required; [BindNever] and members outside
        // [Bind("…")] are not bound; [Bind(Prefix)] names the model
        var rules = Assert.Single(Assert.IsType<FormRequestBody>(export.Index!.Operations["mvc.rules"].RequestBody).Fields);
        Assert.Equal("x", rules.Name);
        Assert.Equal([("Count", Presence.Required), ("Note", Presence.Optional), ("Title", Presence.Required)], rules.Fields!.Select(f => (f.Name, f.Presence)).OrderBy(f => f.Name, StringComparer.Ordinal));
        var filtered = Assert.Single(Assert.IsType<FormRequestBody>(export.Index.Operations["mvc.filtered"].RequestBody).Fields);
        Assert.Equal(["Title"], filtered.Fields!.Select(f => f.Name));

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("mvc.rules", new { body = new { x = new Dictionary<string, object> { ["Count"] = Input("3"), ["Title"] = "t" } } }),
            ("mvc.rules", new { body = new { x = new Dictionary<string, object> { ["Title"] = "t" } } }),
        ]);
        Assert.Equal("200 3|t|", sent[0]);
        Assert.Equal("missing-required: form field is required", sent[1]);

        // Kestrel: what the required presence avoids — a missing [BindRequired] value is a model state error, an automatic 400
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var missing = await client.PostAsync("/mvc/rules", new FormUrlEncodedContent([KeyValuePair.Create("x.Title", "t")]));
        Assert.Equal(400, (int)missing.StatusCode);
    }

    [Fact]
    public async Task MVC_form_model_members_read_from_elsewhere_or_by_their_own_binder_are_diagnosed()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(MvcFormMixedController))));
        builder.Services.AddTisilia(o => o.ApiId = "mvc-form-mixed");
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.True(export.Diagnostics.HasErrors); Assert.Null(export.Text);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.RelatedIds.Contains("mvc.query-member") && d.Message.Contains("'Page' is bound from Query", StringComparison.Ordinal));
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV30" && d.RelatedIds.Contains("mvc.binder-member") && d.Message.Contains("[ModelBinder]", StringComparison.Ordinal));

        // Kestrel: once a key starts with the model's name, its [FromQuery] member is read as order.Page from the query, not as Page
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var prefixed = await client.PostAsync("/mvc/query-member?Page=5", new FormUrlEncodedContent([KeyValuePair.Create("order.Name", "n")]));
        Assert.Equal("n|0", await prefixed.Content.ReadAsStringAsync());
    }

    private static object Input(string text) => new Dictionary<string, string> { ["$input"] = text };

    private sealed class OneController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[NonController]
[ApiExplorerSettings(IgnoreApi = false)]
public sealed class MvcFormModelController : ControllerBase
{
    [HttpPost("/mvc/order")]
    [TisiliaOperation("mvc.order")]
    public ActionResult<string> Order([FromForm] MvcOrder order, [FromForm] string note) =>
        $"{order.Name}|{order.Details?.Amount.ToString(CultureInfo.InvariantCulture)}|{order.Details?.Label}|{string.Join(",", order.Lines?.Select(l => l.Id + ":" + l.Sku) ?? [])}|{string.Join(",", order.Tags ?? [])}|{order.File?.FileName}:{order.File?.Length}|{note}";

    [HttpPost("/mvc/record")]
    [TisiliaOperation("mvc.record")]
    public ActionResult<string> Record([FromForm(Name = "r")] MvcRecord record) => $"{record.Name}|{record.Count}";

    [HttpPost("/mvc/lines")]
    [TisiliaOperation("mvc.lines")]
    public ActionResult<string> Lines([FromForm] List<MvcLine> lines) => string.Join(",", lines.Select(l => l.Id + ":" + l.Sku));
}

[ApiController]
[NonController]
public sealed class MvcFormRulesController : ControllerBase
{
    [HttpPost("/mvc/rules")]
    [TisiliaOperation("mvc.rules")]
    public ActionResult<string> Rules([FromForm, Bind(Prefix = "x")] MvcRules rules) => $"{rules.Count}|{rules.Title}|{rules.Note}";

    [HttpPost("/mvc/filtered")]
    [TisiliaOperation("mvc.filtered")]
    public ActionResult<string> Filtered([FromForm, Bind(nameof(MvcRules.Title))] MvcRules rules) => rules.Title;
}

[NonController]
[ApiExplorerSettings(IgnoreApi = false)]
public sealed class MvcFormMixedController : ControllerBase
{
    [HttpPost("/mvc/query-member")]
    [TisiliaOperation("mvc.query-member")]
    public ActionResult<string> QueryMember([FromForm] MvcPagedOrder order) => $"{order.Name}|{order.Page}";

    [HttpPost("/mvc/binder-member")]
    [TisiliaOperation("mvc.binder-member")]
    public ActionResult<string> BinderMember([FromForm] MvcBoundOrder order) => order.Name ?? "";
}

public sealed class MvcPagedOrder
{
    public string? Name { get; set; }
    [FromQuery] public int Page { get; set; }
}

public sealed class MvcBoundOrder
{
    public string? Name { get; set; }
    [ModelBinder(typeof(MvcUpperBinder))] public string? Code { get; set; }
}

public sealed class MvcUpperBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        bindingContext.Result = ModelBindingResult.Success(bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue?.ToUpperInvariant());
        return Task.CompletedTask;
    }
}

public sealed class MvcRules
{
    [BindRequired] public int Count { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    [BindNever] public string? Server { get; set; }
}

public sealed class MvcOrder
{
    public string? Name { get; set; }
    public MvcDetail? Details { get; set; }
    public List<MvcLine>? Lines { get; set; }
    public List<string>? Tags { get; set; }
    public IFormFile? File { get; set; }
}

public sealed class MvcDetail
{
    public decimal Amount { get; set; }
    public string? Label { get; set; }
}

public sealed class MvcLine
{
    public long Id { get; set; }
    public string? Sku { get; set; }
}

public sealed record MvcRecord(string Name, int Count);
