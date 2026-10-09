using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Conformance;
using Tisilia.AspNetCore.Export;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The suite sends the .NET runner only codecs that meet the server's JSON converters. A route parameter or a form field travels
/// through a binder; its codec has no profile adapter, and a JSON case for it failed the whole verdict (adapter.unknown-for-profile).
/// A key round trip writes a dictionary, so it runs only where the contract carries one: a ReferenceHandler.Preserve profile wraps a
/// dictionary in $id metadata, and key cases for its scalars failed the verdict (key.unexpected-write).
/// </summary>
public sealed class ConformanceScopeTests
{
    [Fact]
    public async Task Codecs_reached_only_from_parameters_and_form_fields_get_no_JSON_cases()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "conformance-scope");
        await using var app = builder.Build();
        app.MapGet("/pages/{page}", (int page) => TypedResults.Ok(page.ToString(System.Globalization.CultureInfo.InvariantCulture))).WithTisiliaOperation("route");
        app.MapPost("/form", ([FromForm] long id) => TypedResults.Ok(id.ToString(System.Globalization.CultureInfo.InvariantCulture))).DisableAntiforgery().WithTisiliaOperation("form");
        app.MapPost("/json", (Payload value) => TypedResults.Ok(value)).WithTisiliaOperation("json");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));

        var closure = ContractClosure.Compute(export.Index!, ["route", "form", "json"]);
        var suite = SuiteBuilder.Build(export.Index!, closure, new SuiteOptions { Seed = 7, CasesPerCodec = 4, KeyCasesPerCodec = 2 });
        var dotnetCases = suite.Cases.Where(c => c is RequestRoundTripCase or ResponseRoundTripCase or KeyRoundTripCase || c is NegativeWireCase { Target: RunnerTarget.Dotnet }).ToList();
        Assert.NotEmpty(dotnetCases); // the JSON operation still brings round trips
        Assert.All(dotnetCases, c =>
        {
            var codecId = c switch { RequestRoundTripCase r => r.CodecId, ResponseRoundTripCase r => r.CodecId, KeyRoundTripCase k => k.CodecId, NegativeWireCase n => n.CodecId, _ => "" };
            Assert.True(export.Adapters!.Find(codecId, c.ProfileId) is not null, $"{c.Id}: the .NET runner has no adapter for {codecId} under {c.ProfileId}");
        });

        // the parameter and form-field codecs still have their domain validated on the client
        var parameterCodec = export.Index!.Operations["route"].Parameters.Single().Use.CodecId;
        Assert.DoesNotContain(parameterCodec, closure.JsonCodecIds);
        Assert.Contains(suite.Cases, c => c is DomainValidationCase d && d.CodecId == parameterCodec);
    }

    [Fact]
    public async Task Key_round_trips_run_for_dictionary_keys_only_under_the_profile_that_carries_the_dictionary()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new CountsControllerFeatureProvider()));
        builder.Services.AddTisilia(o => o.ApiId = "key-scope");
        await using var app = builder.Build();
        // minimal APIs map first, so the shared int64 codec lists the Preserve profile before the MVC one
        app.MapPost("/scalar", ([FromBody] long value) => TypedResults.Ok(value)).WithTisiliaOperation("scalar");
        app.MapPost("/id", ([FromBody] Guid value) => TypedResults.Ok(value)).WithTisiliaOperation("id");
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        var index = export.Index!;
        var preserve = Assert.Single(index.Profiles.Values, p => p.Options.ReferenceHandling == ReferenceHandling.Preserve).Id;
        var keyed = Assert.Single(index.Types.Values, t => t.Shape is MapShape);
        var keyCodec = ((MapShape)keyed.Shape).Key.CodecId;
        Assert.Equal(preserve, index.Codecs[keyCodec].ProfileIds[0]);
        var guidCodec = index.Operations["id"].RequestBody is JsonRequestBody body ? body.Use.CodecId : "";
        Assert.NotNull(index.Codecs[guidCodec].Capabilities.RequestKey);

        var closure = ContractClosure.Compute(index, ["scalar", "id", "counts"]);
        var suite = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 7, CasesPerCodec = 4, KeyCasesPerCodec = 2 });
        // the Guid is a JSON value with a key capability but never a dictionary key: round trips, no key cases
        Assert.Contains(suite.Cases, c => c is RequestRoundTripCase r && r.CodecId == guidCodec);
        Assert.DoesNotContain(suite.Cases, c => c is KeyRoundTripCase k && k.CodecId == guidCodec);
        var keyCases = suite.Cases.OfType<KeyRoundTripCase>().ToList();
        Assert.Contains(keyCases, k => k.CodecId == keyCodec && k.RequestDirection);
        Assert.Contains(keyCases, k => k.CodecId == keyCodec && !k.RequestDirection);
        Assert.All(keyCases, k => Assert.NotEqual(preserve, k.ProfileId));

        // the .NET half of every key case: System.Text.Json writes and reads one dictionary entry under the case's profile
        var runner = new DotnetRunnerCore(index, export.Adapters!, []);
        foreach (var k in keyCases)
        {
            var written = Assert.IsType<JsonStringValue>(runner.Handle(RunnerProtocol.Request("keys", k.Id, RunnerAction.DotnetWriteKey, k.CodecId, k.ProfileId, [], [k.Key])));
            var read = runner.Handle(RunnerProtocol.Request("keys", k.Id, RunnerAction.DotnetReadKey, k.CodecId, k.ProfileId, [], [written]));
            Assert.Equal(DomainAst.ToJsonText(k.Expected ?? k.Key), DomainAst.ToJsonText(read));
        }
    }

    public sealed record Payload(long LargeNumber, Dictionary<string, long> Labels);

    private sealed class CountsControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(KeyCountsController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class KeyCountsController : ControllerBase
{
    [HttpPost("/counts")]
    [TisiliaOperation("counts")]
    public ActionResult<int> Counts([FromBody] Dictionary<long, int> counts) => counts.Count;
}
