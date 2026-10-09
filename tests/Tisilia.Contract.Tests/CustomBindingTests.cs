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
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A type's BindAsync and an MVC model binder read the request in the application's own code: the contract describes them as
/// TisiliaOptions.CustomBinding declares, each declared value a parameter whose canonical text that code parses. Oracle: through
/// Kestrel, the runtime's encoder sends the declared values and the code binds them.
/// </summary>
public sealed class CustomBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-custom-binding-" + Guid.NewGuid().ToString("N"));

    public CustomBindingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Declared_BindAsync_reads_are_parameters_the_binding_code_reads()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "declared-bindings";
            o.CustomBinding.BindAsync<PageRequest>(reads => reads.Query<int>("page").Query<int>("size", optional: true))
                .BindAsync<Tenant>(reads => reads.Header<string>("X-Tenant"))
                .BindAsync<CurrentUser>(reads => reads.NotFromRequest());
        });
        await using var app = builder.Build();
        app.MapGet("/items/{id}", (int id, PageRequest paging, Tenant tenant, CurrentUser user) => $"{id}|{paging.Page}|{paging.Size}|{tenant.Name}|{user.Name}")
            .WithTisiliaOperation("items");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var parameters = export.Index!.Operations["items"].Parameters.ToDictionary(p => p.Name);
        Assert.Equal(["X-Tenant", "id", "page", "size"], parameters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((ParameterLocation.Query, Presence.Required), (parameters["page"].Location, parameters["page"].Presence));
        Assert.Equal(Presence.Optional, parameters["size"].Presence);
        Assert.Equal(ParameterLocation.Header, parameters["X-Tenant"].Location);
        // the application's code parses the declared values; the route value binds as usual
        Assert.All(new[] { "page", "size", "X-Tenant" }, name => Assert.Equal(Builtins.AcceptServerParsed, export.Index.Binders[parameters[name].BinderId].ServerAcceptanceId));
        Assert.NotEqual(Builtins.AcceptServerParsed, export.Index.Binders[parameters["id"].BinderId].ServerAcceptanceId);

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("items", new Dictionary<string, object> { ["id"] = 7, ["page"] = 2, ["size"] = 20, ["X-Tenant"] = "acme" }),
            ("items", new Dictionary<string, object> { ["id"] = 7, ["page"] = 3, ["X-Tenant"] = "acme" }),
        ]);
        Assert.Equal(["200 7|2|20|acme|anonymous", "200 7|3|10|acme|anonymous"], sent);
    }

    [Fact]
    public async Task An_undeclared_BindAsync_parameter_names_the_declaration_it_needs()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "undeclared-binding");
        await using var app = builder.Build();
        app.MapGet("/items", (PageRequest paging) => paging.Page).WithTisiliaOperation("items");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.Rule == "SV30");
        Assert.Contains("PageRequest.BindAsync", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("options.CustomBinding.BindAsync<PageRequest>", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_MVC_model_binder_reads_under_its_model_name()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(CsvController))));
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "declared-binder";
            o.CustomBinding.ModelBinder<CsvBinder>(reads => reads.Query<string>(RequestReads.ModelName));
        });
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var parameter = Assert.Single(export.Index!.Operations["csv"].Parameters);
        Assert.Equal(("tags", ParameterLocation.Query), (parameter.Name, parameter.Location));

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("csv", new { tags = "a,b,c" })]);
        Assert.Equal("200 a|b|c", sent[0]);
    }

    [Fact]
    public async Task Doctor_calls_declared_bindings_with_a_recording_request_and_reports_undeclared_reads()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(typeof(CsvController))));
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "probed-bindings";
            // PageRequest.BindAsync also reads size, which this declaration leaves out
            o.CustomBinding.BindAsync<PageRequest>(reads => reads.Query<int>("page"))
                .BindAsync<Tenant>(reads => reads.Header<string>("X-Tenant"))
                .BindAsync<CurrentUser>(reads => reads.NotFromRequest())
                .BindAsync<Broken>(reads => reads.NotFromRequest())
                .ModelBinder<CsvBinder>(reads => reads.Query<string>(RequestReads.ModelName));
        });
        await using var app = builder.Build();
        app.MapGet("/items", (PageRequest paging, Tenant tenant, CurrentUser user) => $"{paging.Page}|{tenant.Name}|{user.Name}").WithTisiliaOperation("items");
        app.MapGet("/broken", (Broken broken) => "unreachable").WithTisiliaOperation("broken");
        app.MapControllers();
        await app.StartAsync();
        var exporter = app.Services.GetRequiredService<TisiliaContractExporter>();
        Assert.False(exporter.Diagnose().BindingProbesRun);

        var report = exporter.Diagnose(probeBindings: true);
        Assert.True(report.BindingProbesRun);
        var probes = report.BindingProbes.ToDictionary(p => p.Parameter);
        Assert.Equal("mismatch", probes["paging"].Status);
        Assert.Equal(["query:size"], probes["paging"].UndeclaredReads);
        Assert.Equal("matches", probes["tenant"].Status);
        Assert.Equal("matches", probes["user"].Status);
        Assert.Equal("matches", probes["tags"].Status);
        // a binding that throws is reported by exception type; its message stays private
        Assert.Equal(("failed", "the binding threw InvalidOperationException"), (probes["broken"].Status, probes["broken"].Failure));
        Assert.Equal("blockers", report.Overall);
        Assert.Contains(report.Causes, c => c.ReasonCode == "binding-declaration-mismatch" && c.Message.Contains("query:size", StringComparison.Ordinal));
        Assert.Contains(report.Causes, c => c.ReasonCode == "binding-probe-failed" && c.OperationIds.SequenceEqual(["broken"]));
        Assert.DoesNotContain("TEST_SECRET", System.Text.Json.JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    public sealed class PageRequest
    {
        public int Page { get; init; }
        public int Size { get; init; }

        public static ValueTask<PageRequest?> BindAsync(HttpContext context, ParameterInfo parameter) => ValueTask.FromResult<PageRequest?>(new PageRequest
        {
            Page = int.TryParse(context.Request.Query["page"], out var page) ? page : 1,
            Size = int.TryParse(context.Request.Query["size"], out var size) ? size : 10,
        });
    }

    public sealed class Tenant
    {
        public string Name { get; init; } = "";

        public static ValueTask<Tenant?> BindAsync(HttpContext context) => ValueTask.FromResult<Tenant?>(new Tenant { Name = context.Request.Headers["X-Tenant"].ToString() });
    }

    public sealed class CurrentUser
    {
        public string Name { get; init; } = "";

        public static ValueTask<CurrentUser?> BindAsync(HttpContext context) => ValueTask.FromResult<CurrentUser?>(new CurrentUser { Name = context.User.Identity?.Name ?? "anonymous" });
    }

    public sealed class Broken
    {
        public static ValueTask<Broken?> BindAsync(HttpContext context) => throw new InvalidOperationException("Password=TEST_SECRET");
    }

    public sealed class CsvBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            var text = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue ?? "";
            bindingContext.Result = ModelBindingResult.Success(text.Split(',', StringSplitOptions.RemoveEmptyEntries));
            return Task.CompletedTask;
        }
    }

    private sealed class OneController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class CsvController : ControllerBase
{
    [HttpGet("/csv")]
    [TisiliaOperation("csv")]
    public ActionResult<string> Get([ModelBinder(typeof(CustomBindingTests.CsvBinder))] string[] tags) => string.Join("|", tags);
}
