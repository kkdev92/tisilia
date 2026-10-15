using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// What decides whether System.Text.Json writes a member. A member's <c>[JsonIgnore(Condition = …)]</c> is a ShouldSerialize predicate
/// of System.Text.Json's own; a predicate that a type info modifier sets replaces it — and the options' DefaultIgnoreCondition, which
/// applies only to a member without a predicate — and one that a modifier removes leaves the member always written.
/// Oracle: <c>tests/oracle/ShouldSerialize/observe.cs</c>.
/// </summary>
public sealed class ShouldSerializeTests
{
    [Fact]
    public async Task Without_a_modifier_a_member_follows_its_ignore_condition()
    {
        await using var app = await StartAsync();
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal((Presence.Optional, IgnoreCondition.WhenWritingNull), Written(export, "note"));
        Assert.Equal((Presence.Required, IgnoreCondition.Never), Written(export, "title"));
        Assert.Equal((Presence.Required, IgnoreCondition.Never), Written(export, "tag"));
    }

    [Theory]
    [InlineData("note", JsonIgnoreCondition.Never, "[JsonIgnore(Condition = WhenWritingNull)] on the member")]
    [InlineData("title", JsonIgnoreCondition.Never, "[JsonIgnore(Condition = Never)] on the member")]
    [InlineData("tag", JsonIgnoreCondition.WhenWritingNull, "the options' WhenWritingNull")]
    public async Task A_predicate_a_modifier_sets_decides_over_any_ignore_condition_and_is_undescribed(string member, JsonIgnoreCondition options, string replaced)
    {
        // before: the member's or the options' condition was taken to explain the predicate, which the export then let through
        await using var app = await StartAsync(member, Modifier.Set, options);
        var export = Export(app);
        var error = Assert.Single(export.Diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SV19", error.Rule);
        Assert.Contains("has a ShouldSerialize delegate whose condition is not described", error.Message, StringComparison.Ordinal);
        Assert.Contains($"(it decides instead of {replaced})", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_predicate_leaves_the_member_optional_without_the_condition_it_replaced()
    {
        // before: the scope kept WhenWritingNull, and the suite expected a null left out, which the predicate decides
        await using var app = await StartAsync("note", Modifier.Set, register: true);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal((Presence.Optional, IgnoreCondition.Never), Written(export, "note"));
    }

    [Fact]
    public async Task A_member_whose_ignore_predicate_a_modifier_removed_is_always_written()
    {
        // before: optional with WhenWritingNull, though System.Text.Json writes the null
        await using var app = await StartAsync("note", Modifier.Remove);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal((Presence.Required, IgnoreCondition.Never), Written(export, "note"));
    }

    [Fact]
    public async Task Removing_a_predicate_leaves_a_member_without_a_condition_to_the_options()
    {
        // System.Text.Json applies the options' condition again to a member that has no predicate, so nothing changes
        await using var app = await StartAsync("tag", Modifier.Remove, JsonIgnoreCondition.WhenWritingNull);
        var export = Export(app);
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal((Presence.Optional, IgnoreCondition.WhenWritingNull), Written(export, "tag"));
    }

    private enum Modifier
    {
        None,
        Set,
        Remove,
    }

    /// <summary>The response member's presence and the ignore condition its scope records.</summary>
    private static (Presence, IgnoreCondition) Written(ExportResult export, string member)
    {
        var index = export.Index!;
        var use = index.Operations["items.get"].Responses.Select(r => r.Body).OfType<JsonResponseBody>().Single().Use;
        var shape = (ObjectShape)index.Types[index.Codecs[use.CodecId].TypeId].Shape;
        var property = shape.Properties.Single(p => p.Name == member);
        var scope = index.Profiles.Values.Single().Scopes.Single(s => s.Kind == ScopeKind.Member && s.ClrPath.EndsWith("." + member, StringComparison.Ordinal));
        return (property.Presence, scope.EffectiveIgnoreCondition);
    }

    private static async Task<WebApplication> StartAsync(string? member = null, Modifier modifier = Modifier.None, JsonIgnoreCondition options = JsonIgnoreCondition.Never, bool register = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        IJsonTypeInfoResolver? resolver = null;
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = options;
            resolver = (o.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(info =>
            {
                if (info.Type != typeof(Item) || modifier == Modifier.None)
                {
                    return;
                }

                // what the modifier does to the one member: a predicate that writes non-null values, or none at all
                var property = info.Properties.Single(p => p.Name == member);
                property.ShouldSerialize = modifier == Modifier.Set ? static (_, value) => value is not null : null;
            });
            o.SerializerOptions.TypeInfoResolver = resolver;
        });
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "should-serialize";
            o.Resolvers.Add(new ResolverRegistration
            {
                Matches = r => ReferenceEquals(r, resolver),
                Id = "should-serialize.resolver.members",
                ModuleId = "should-serialize.members",
                Artifacts = [new ModuleArtifactSpec(ArtifactTarget.Dotnet, "bin/Members.dll", "sha256:" + new string('d', 64))],
                DotnetExport = "Members",
                SettingsText = "the test's modifier of Item",
            });
            if (register)
            {
                o.Behaviors.Add(new BehaviorRegistration
                {
                    ClrType = typeof(Item),
                    MemberName = nameof(Item.Note),
                    Kind = BehaviorKind.ShouldSerialize,
                    Effect = BehaviorEffect.Identity,
                    SettingsText = "written when not null",
                });
            }
        });
        var app = builder.Build();
        app.MapGet("/items", () => TypedResults.Ok(new Item())).WithTisiliaOperation("items.get");
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app) => app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();

    public sealed class Item
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? Title { get; set; }

        public string? Tag { get; set; }
    }
}
