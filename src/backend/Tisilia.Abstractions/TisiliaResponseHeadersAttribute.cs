namespace Tisilia;

/// <summary>
/// Response headers a client may read. Declarations on an endpoint, controller or route group are additive;
/// the names are an allowlist, not a promise that a response carries each header. Does not write headers or configure CORS.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class TisiliaResponseHeadersAttribute : Attribute
{
    /// <summary>Exposes the named headers on every declared response case.</summary>
    public TisiliaResponseHeadersAttribute(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names) { ArgumentException.ThrowIfNullOrWhiteSpace(name); }
        Names = Array.AsReadOnly((string[])names.Clone());
    }

    /// <summary>Exposes the named headers on the declared cases of this status (all media types).</summary>
    public TisiliaResponseHeadersAttribute(int statusCode, params string[] names) : this(names)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 200);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
        StatusCode = statusCode;
    }

    /// <summary>The header names as declared; the contract records them in lowercase.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The status whose cases expose <see cref="Names"/>, or <see langword="null"/> for every case.</summary>
    public int? StatusCode { get; }
}
