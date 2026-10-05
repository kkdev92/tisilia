using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public sealed record RoutePlan
{
    public required IReadOnlyList<RouteSegment> Segments { get; init; }
}

public sealed record RouteSegment
{
    public required IReadOnlyList<RoutePart> Parts { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RouteLiteral), "literal")]
[JsonDerivedType(typeof(RouteSeparator), "separator")]
[JsonDerivedType(typeof(RouteParameter), "parameter")]
public abstract record RoutePart;

public sealed record RouteLiteral : RoutePart
{
    public required string Value { get; init; }
}

public sealed record RouteSeparator : RoutePart
{
    public required string Value { get; init; }
}

public enum CatchAllKind
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("encode-slashes")] EncodeSlashes,
    [JsonStringEnumMemberName("preserve-slashes")] PreserveSlashes,
}

public sealed record RouteParameter : RoutePart
{
    public required string ParameterId { get; init; }
    public required string Name { get; init; }
    public required bool Optional { get; init; }
    public required CatchAllKind CatchAll { get; init; }
    public required bool HasDefault { get; init; }
    public string? DefaultValue { get; init; }
    public required IReadOnlyList<string> Policies { get; init; }
}
