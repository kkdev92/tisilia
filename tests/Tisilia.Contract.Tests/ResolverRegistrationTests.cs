using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// A resolver with type info modifiers changes the contracts System.Text.Json builds, so export refuses it until a registration
/// names it. <c>WithAddedModifier</c> returns an instance of an internal type: the application keeps the one it creates and the
/// registration recognizes it by reference, as Getting started shows.
/// </summary>
public sealed class ResolverRegistrationTests
{
    [Fact]
    public async Task A_resolver_with_modifiers_is_refused_until_a_registration_recognizes_it()
    {
        await using var unnamed = await StartAsync(register: false);
        var refused = unnamed.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Contains(refused.Diagnostics.Items, d => d.Rule == "SV16" && d.Message.Contains("requires a resolver binding", StringComparison.Ordinal));

        await using var named = await StartAsync(register: true);
        var export = named.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Contains("shop.resolver.conventions", export.Index!.Profiles.Values.Single().Options.ResolverIds);
        Assert.Equal(BindingKind.Resolver, export.Index.Bindings["shop.resolver.conventions"].Kind);
    }

    private static async Task<WebApplication> StartAsync(bool register)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        IJsonTypeInfoResolver? conventions = null;
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            conventions = (o.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(Configure);
            o.SerializerOptions.TypeInfoResolver = conventions;
        });
        builder.Services.AddTisilia(o =>
        {
            o.ApiId = "resolvers";
            if (register)
            {
                o.Resolvers.Add(new ResolverRegistration
                {
                    Matches = resolver => ReferenceEquals(resolver, conventions),
                    Id = "shop.resolver.conventions",
                    ModuleId = "shop.json",
                    DotnetExport = nameof(ResolverRegistrationTests),
                    Artifacts = [new(ArtifactTarget.Dotnet, "bin/Shop.Json.dll", Bytes: () => File.ReadAllBytes(typeof(ResolverRegistrationTests).Assembly.Location))],
                    SettingsText = "Configure: changes no member",
                });
            }
        });
        var app = builder.Build();
        app.MapGet("/items/{id}", (int id) => TypedResults.Ok(new Item(id))).WithTisiliaOperation("items.get");
        await app.StartAsync();
        return app;
    }

    private static void Configure(JsonTypeInfo info)
    {
    }

    public sealed record Item(int Id);
}
