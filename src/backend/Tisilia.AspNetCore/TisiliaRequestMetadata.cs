using Tisilia.AspNetCore.Bindings;

namespace Tisilia.AspNetCore;

/// <summary>
/// Endpoint metadata of <see cref="TisiliaEndpointExtensions.WithTisiliaRequestReads{TBuilder}"/>: values the request pipeline — a
/// middleware or an endpoint filter — reads from the request outside the endpoint's parameters.
/// </summary>
public sealed class TisiliaRequestReadsMetadata(RequestReads reads)
{
    public RequestReads Reads { get; } = reads;
}

/// <summary>
/// Endpoint metadata of <see cref="TisiliaEndpointExtensions.WithTisiliaRequestHeaders{TBuilder}"/>: headers a caller may pass through
/// for the endpoint without a type.
/// </summary>
public sealed class TisiliaRequestHeadersMetadata(IReadOnlyList<string> names)
{
    public IReadOnlyList<string> Names { get; } = names;
}
