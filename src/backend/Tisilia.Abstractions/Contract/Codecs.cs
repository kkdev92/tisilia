using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public enum NullBehavior
{
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("bypass")] Bypass,
    [JsonStringEnumMemberName("converter")] Converter,
}

public enum CodecOrigin
{
    [JsonStringEnumMemberName("builtin")] Builtin,
    [JsonStringEnumMemberName("portable")] Portable,
    [JsonStringEnumMemberName("paired")] Paired,
}

public enum InputKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("json-value")] JsonValue,
}

public sealed record ValueCapability
{
    public required WireRef Wire { get; init; }
    public required Impl Implementation { get; init; }
    public required NullBehavior NullBehavior { get; init; }
    public required string EquivalenceId { get; init; }
    public required string DomainRuleId { get; init; }
}

public sealed record KeyCapability
{
    public required Impl Implementation { get; init; }
    public required string GrammarId { get; init; }
    public required string EquivalenceId { get; init; }
    public required string Collision { get; init; }
}

public sealed record InputCapability
{
    public required Impl Implementation { get; init; }
    public required InputKind InputKind { get; init; }
    public required string EditorId { get; init; }
}

/// <summary>Independent ABI 0.1 capabilities. Absent capabilities are simply not implemented.</summary>
public sealed record Capabilities
{
    public ValueCapability? Request { get; init; }
    public ValueCapability? Response { get; init; }
    public KeyCapability? RequestKey { get; init; }
    public KeyCapability? ResponseKey { get; init; }
    public InputCapability? RequestInput { get; init; }
}

public sealed record Codec
{
    public required string Id { get; init; }
    public required string TypeId { get; init; }
    public required CodecOrigin Origin { get; init; }
    public required string BindingId { get; init; }
    public required Impl ValidateDomain { get; init; }
    public required Capabilities Capabilities { get; init; }
    public required IReadOnlyList<string> Dependencies { get; init; }
    public required IReadOnlyList<string> ProfileIds { get; init; }
}

public enum EquivalenceScope
{
    [JsonStringEnumMemberName("request")] Request,
    [JsonStringEnumMemberName("response")] Response,
    [JsonStringEnumMemberName("key")] Key,
    [JsonStringEnumMemberName("round-trip")] RoundTrip,
}

public enum Grade
{
    G1,
    G2,
    G3,
}

public sealed record Equivalence
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string DomainTypeId { get; init; }
    public required EquivalenceScope Scope { get; init; }
    public required Grade Grade { get; init; }
    public required string DomainRuleId { get; init; }
    public string? ProjectionId { get; init; }
    public required Impl DotnetOracle { get; init; }
    public required Impl TypescriptOracle { get; init; }
    public required string NormalizationId { get; init; }
    public required IReadOnlyList<string> Preserved { get; init; }
    public required IReadOnlyList<string> NotPreserved { get; init; }
}

public sealed record Projection
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string SourceTypeId { get; init; }
    public required string TargetTypeId { get; init; }
    public required Impl DotnetImplementation { get; init; }
    public required Impl TypescriptImplementation { get; init; }
    public required IReadOnlyList<string> Preserved { get; init; }
    public required IReadOnlyList<string> NotPreserved { get; init; }
}

public sealed record Comparer
{
    public required string Id { get; init; }
    public required string BindingId { get; init; }
    public required string EquivalenceId { get; init; }
    public required string Collision { get; init; }
}

public enum BindingKind
{
    [JsonStringEnumMemberName("converter")] Converter,
    [JsonStringEnumMemberName("factory")] Factory,
    [JsonStringEnumMemberName("binder")] Binder,
    [JsonStringEnumMemberName("result")] Result,
    [JsonStringEnumMemberName("behavior")] Behavior,
    [JsonStringEnumMemberName("domain-rule")] DomainRule,
    [JsonStringEnumMemberName("normalization")] Normalization,
    [JsonStringEnumMemberName("pipeline")] Pipeline,
    [JsonStringEnumMemberName("comparer")] Comparer,
    [JsonStringEnumMemberName("auth-policy")] AuthPolicy,
    [JsonStringEnumMemberName("csrf-policy")] CsrfPolicy,
    [JsonStringEnumMemberName("grammar")] Grammar,
    [JsonStringEnumMemberName("name-matching")] NameMatching,
    [JsonStringEnumMemberName("duplicate-policy")] DuplicatePolicy,
    [JsonStringEnumMemberName("editor")] Editor,
    [JsonStringEnumMemberName("naming-policy")] NamingPolicy,
    [JsonStringEnumMemberName("encoder")] Encoder,
    [JsonStringEnumMemberName("resolver")] Resolver,
    [JsonStringEnumMemberName("server-acceptance")] ServerAcceptance,
}

public sealed record BindingContextEntry
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required bool Confidential { get; init; }
}

public sealed record Binding
{
    public required string Id { get; init; }
    public required BindingKind Kind { get; init; }
    public required string Version { get; init; }
    public required Impl Implementation { get; init; }
    public required string SettingsDigest { get; init; }
    public required IReadOnlyList<string> DependencyIds { get; init; }
    public required IReadOnlyList<BindingContextEntry> Context { get; init; }
}

public enum ParameterLocation
{
    [JsonStringEnumMemberName("path")] Path,
    [JsonStringEnumMemberName("query")] Query,
    [JsonStringEnumMemberName("header")] Header,
}

public enum Cardinality
{
    [JsonStringEnumMemberName("single")] Single,
    [JsonStringEnumMemberName("repeated")] Repeated,
}

public enum NullPolicy
{
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("omit")] Omit,
    [JsonStringEnumMemberName("literal")] Literal,
}

public enum EmptyPolicy
{
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("allow")] Allow,
}

public enum BinderEncoding
{
    [JsonStringEnumMemberName("path-segment")] PathSegment,
    [JsonStringEnumMemberName("query-component")] QueryComponent,
    [JsonStringEnumMemberName("header-text")] HeaderText,
}

public sealed record Binder
{
    public required string Id { get; init; }
    public required string BindingId { get; init; }
    public required ParameterLocation Location { get; init; }
    public required string TypeId { get; init; }
    public required Impl Implementation { get; init; }
    public required Cardinality Cardinality { get; init; }
    public required NullPolicy NullPolicy { get; init; }
    public string? NullLiteral { get; init; }
    public required EmptyPolicy EmptyPolicy { get; init; }
    public required BinderEncoding Encoding { get; init; }
    public required string GrammarId { get; init; }
    public required string NormalizationId { get; init; }
    public required string ServerAcceptanceId { get; init; }
    public required bool OrderSensitive { get; init; }
}

public enum ResultAdapterKind
{
    [JsonStringEnumMemberName("minimal-json")] MinimalJson,
    [JsonStringEnumMemberName("minimal-result")] MinimalResult,
    [JsonStringEnumMemberName("mvc-object")] MvcObject,
    [JsonStringEnumMemberName("mvc-json")] MvcJson,
    [JsonStringEnumMemberName("bodyless")] Bodyless,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("binary")] Binary,
    [JsonStringEnumMemberName("custom")] Custom,
}

public sealed record ResultAdapter
{
    public required string Id { get; init; }
    public required string BindingId { get; init; }
    public required ResultAdapterKind Kind { get; init; }
    public required Impl Implementation { get; init; }
    public required IReadOnlyList<string> ProfileIds { get; init; }
    public required IReadOnlyList<string> BehaviorIds { get; init; }
}
