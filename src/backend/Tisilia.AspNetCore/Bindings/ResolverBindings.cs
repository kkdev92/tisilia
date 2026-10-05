using System.Text.Json.Serialization.Metadata;
using Tisilia.Contract;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// A custom <see cref="IJsonTypeInfoResolver"/> in a profile's resolver chain — a source-generated context, a
/// hand-written resolver, or the reflection resolver with contract modifiers (<c>WithAddedModifier</c>) — changes the
/// contracts System.Text.Json builds, so the profile must name it through a resolver binding instead of pretending the
/// reflection resolver is in effect. The registration names the module and .NET export that implement it; its non-secret
/// settings are hashed into the binding's <c>settingsDigest</c>.
/// </summary>
public sealed class ResolverRegistration
{
    /// <summary>Recognizes the resolver instance in the chain (reference equality with the instance the application created, or a type test).</summary>
    public required Func<IJsonTypeInfoResolver, bool> Matches { get; init; }

    /// <summary>Binding id (e.g. <c>demo.resolver.audit-secret</c>); the profile's <c>resolverIds</c> reference it.</summary>
    public required string Id { get; init; }

    public required string ModuleId { get; init; }
    public string ModuleVersion { get; init; } = "1.0.0";
    public string License { get; init; } = "MIT";

    /// <summary>Artifacts of the module when no paired codec or behavior registers the same module.</summary>
    public IReadOnlyList<ModuleArtifactSpec> Artifacts { get; init; } = [];

    /// <summary>.NET export (role <c>resolver</c>, target dotnet) that implements the resolver or the modifiers.</summary>
    public required string DotnetExport { get; init; }

    /// <summary>Non-secret description of what the resolver changes (hashed into the binding's settings digest).</summary>
    public required string SettingsText { get; init; }

    /// <summary>Non-secret context entries recorded on the binding.</summary>
    public IReadOnlyList<BindingContextEntry> Context { get; init; } = [];
}

/// <summary>Registered resolver bindings; each resolver instance of a chain is matched by the first registration that recognizes it.</summary>
public sealed class ResolverBindingCollection
{
    private readonly List<ResolverRegistration> _items = [];

    public void Add(ResolverRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_items.Any(r => r.Id == registration.Id))
        {
            throw new InvalidOperationException($"a resolver binding '{registration.Id}' is already registered");
        }

        _items.Add(registration);
    }

    public ResolverRegistration? Find(IJsonTypeInfoResolver resolver) => _items.FirstOrDefault(r => r.Matches(resolver));

    public IReadOnlyList<ResolverRegistration> All => _items;
}
