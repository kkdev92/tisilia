using Tisilia.Contract;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// The effect of Populate / initializer / setter / callback on a scope, registered explicitly so that the
/// contract records it as a behavior classified identity / normalized / opaque. A normalized effect carries a projection
/// with a .NET and a TypeScript implementation: both runners project the received domain value to what the server
/// constructs, and the conformance suite compares the server's value with that projection. Nothing is
/// inferred from a CLR value that happened to match (N13).
/// </summary>
public sealed class BehaviorRegistration
{
    public required Type ClrType { get; init; }

    /// <summary>The member the behavior belongs to (CLR or JSON name); null when it applies to the type itself.</summary>
    public string? MemberName { get; init; }

    public required BehaviorKind Kind { get; init; }
    public required BehaviorEffect Effect { get; init; }

    /// <summary>Module of the behavior/projection exports; null only for an identity effect, which uses the builtin identity implementation.</summary>
    public string? ModuleId { get; init; }
    public string ModuleVersion { get; init; } = "1.0.0";
    public string License { get; init; } = "MIT";

    /// <summary>Artifacts of the module when no paired codec registers the same module.</summary>
    public IReadOnlyList<ModuleArtifactSpec> Artifacts { get; init; } = [];

    /// <summary>.NET export (role <c>behavior</c>, target dotnet) that documents the effect on the server side; null only for an identity effect.</summary>
    public string? DotnetBehaviorExport { get; init; }

    /// <summary>Projection exports (role <c>projection</c>); both are required for a normalized effect.</summary>
    public string? ProjectionDotnetExport { get; init; }
    public string? ProjectionTypescriptExport { get; init; }

    /// <summary>Non-secret settings text hashed into <c>behavior.settingsDigest</c>.</summary>
    public required string SettingsText { get; init; }
    public IReadOnlyList<string> Preserved { get; init; } = [];
    public IReadOnlyList<string> NotPreserved { get; init; } = [];

    /// <summary>
    /// Trusted .NET projection over the projected domain AST of the request type (as received → as constructed by the
    /// server); the .NET runner executes it for <c>dotnet-project</c> and the suite checks that it agrees with the TypeScript
    /// projection. Required for conformance runs of a normalized effect.
    /// </summary>
    public Func<JsonValue, JsonValue>? Project { get; init; }
}

/// <summary>Registered behaviors, one per (type, member) scope (SV24: a scope is claimed once).</summary>
public sealed class BehaviorBindingCollection
{
    private readonly List<BehaviorRegistration> _items = [];

    public void Add(BehaviorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_items.Any(b => b.ClrType == registration.ClrType && string.Equals(b.MemberName, registration.MemberName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"a behavior for '{registration.ClrType}{(registration.MemberName is null ? "" : "." + registration.MemberName)}' is already registered; a scope cannot be claimed twice (SV24)");
        }

        _items.Add(registration);
    }

    /// <summary>The registration of a member, matched by CLR member name or JSON property name (never the type's own registration).</summary>
    public BehaviorRegistration? FindMember(Type type, string? memberName, string? jsonName)
        => _items.FirstOrDefault(b => b.ClrType == type && b.MemberName is not null && (b.MemberName == memberName || b.MemberName == jsonName));

    /// <summary>The registration of the type itself (constructor, initializer, callbacks, other).</summary>
    public BehaviorRegistration? FindType(Type type) => _items.FirstOrDefault(b => b.ClrType == type && b.MemberName is null);

    public IReadOnlyList<BehaviorRegistration> All => _items;
}
