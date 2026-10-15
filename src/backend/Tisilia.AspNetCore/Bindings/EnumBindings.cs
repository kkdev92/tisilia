namespace Tisilia.AspNetCore.Bindings;

/// <summary>What the client holds for a value of an enum.</summary>
public enum EnumDomain
{
    /// <summary>
    /// The integer values: a <c>number</c> (a <c>bigint</c> for a 64-bit base) and a const object of the members. The default, and the
    /// domain of every enum whose converter reads or writes integers.
    /// </summary>
    Values,

    /// <summary>
    /// The names the server writes: a union of string literals (<c>"usage" | "stock"</c>) and a const object of them. Only for an enum
    /// that the converter in effect reads and writes by name alone, so that every value the server reads or writes has a name: a
    /// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> that refuses integers (<c>allowIntegerValues: false</c>),
    /// without <see cref="FlagsAttribute"/>. Another enum keeps its values, with a warning.
    /// </summary>
    Names,
}

/// <summary>The domain of enums: a default for every enum, and exceptions for single enum types.</summary>
public sealed class EnumDomainCollection
{
    private readonly Dictionary<Type, EnumDomain> _types = [];

    /// <summary>The domain of every enum that no declaration names.</summary>
    public EnumDomain Default { get; set; } = EnumDomain.Values;

    /// <summary>Declares the domain of one enum type.</summary>
    public void Add(Type enumType, EnumDomain domain)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        if (!enumType.IsEnum)
        {
            throw new ArgumentException($"'{enumType}' is not an enum", nameof(enumType));
        }

        if (!_types.TryAdd(enumType, domain))
        {
            throw new InvalidOperationException($"the domain of enum '{enumType}' is already declared; a scope cannot be claimed twice (SV24)");
        }
    }

    /// <summary>The declared domain of the enum, else the default; and whether a declaration named it.</summary>
    public (EnumDomain Domain, bool Declared) Find(Type enumType) => _types.TryGetValue(enumType, out var domain) ? (domain, true) : (Default, false);
}
