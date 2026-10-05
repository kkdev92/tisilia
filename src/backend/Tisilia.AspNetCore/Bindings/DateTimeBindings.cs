using System.Reflection;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// The wire of a <see cref="DateTime"/>. System.Text.Json writes a DateTime according to its runtime
/// <see cref="DateTime.Kind"/> — <c>…Z</c> for Utc, the server zone's offset for Local, no suffix for Unspecified — and reads
/// <c>Z</c> as Utc, an offset as Local (converted to the server's zone) and no suffix as Unspecified. Which kind a member
/// holds is knowledge of the application, so it is declared; the contract then uses the builtin <c>datetime-utc</c>,
/// <c>datetime-unspecified</c> or <c>datetime-local-wire</c> scalar. A server value of another kind is written in another form,
/// which the client refuses (a codec failure, never a silently shifted instant).
/// </summary>
public enum DateTimeWire
{
    /// <summary>Values have Kind Utc: <c>datetime-utc</c> (<c>YYYY-MM-DDTHH:mm:ss[.fffffff]Z</c>).</summary>
    Utc,

    /// <summary>Values have Kind Unspecified: <c>datetime-unspecified</c> (no zone suffix).</summary>
    Unspecified,

    /// <summary>
    /// Values have Kind Local: <c>datetime-local-wire</c> (the server zone's offset). The meaning depends on the server's time
    /// zone, so its certification is bound to the time zone the conformance runner reports. Not available for route, query and
    /// header parameters, which ASP.NET Core parses with <c>DateTimeStyles.AdjustToUniversal</c> (an offset becomes UTC).
    /// </summary>
    Local,
}

/// <summary>
/// Declared <see cref="DateTime"/> wires: a default for every DateTime position (members, collection items, dictionary keys,
/// parameters) and exceptions for single members. A DateTime with no declaration is an export error (SV03).
/// </summary>
public sealed class DateTimeBindingCollection
{
    private readonly Dictionary<(Type Type, string Member), DateTimeWire> _members = [];

    /// <summary>The wire of every DateTime that no member declaration names; null when none is declared.</summary>
    public DateTimeWire? Default { get; set; }

    internal IReadOnlyList<string> DescribeMembers() => _members.OrderBy(p => p.Key.Type.FullName, StringComparer.Ordinal)
        .ThenBy(p => p.Key.Member, StringComparer.Ordinal).Select(p => $"{p.Key.Type.FullName}.{p.Key.Member}: {p.Value}").ToArray();

    /// <summary>Declares the wire of one member of a type, by its CLR name or its JSON name.</summary>
    public void Add(Type declaringType, string memberName, DateTimeWire wire)
    {
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        if (!_members.TryAdd((declaringType, memberName), wire))
        {
            throw new InvalidOperationException($"the DateTime wire of '{declaringType}.{memberName}' is already declared; a scope cannot be claimed twice (SV24)");
        }
    }

    /// <summary>The declared wire of a member (its declaring type or the type it was reflected from, CLR or JSON name), else the default.</summary>
    public DateTimeWire? Find(Type? declaringType, MemberInfo? member, string? jsonName)
    {
        foreach (var type in new[] { declaringType, member?.DeclaringType, member?.ReflectedType })
        {
            if (type is null)
            {
                continue;
            }

            if (member is not null && _members.TryGetValue((type, member.Name), out var byClrName))
            {
                return byClrName;
            }

            if (jsonName is not null && _members.TryGetValue((type, jsonName), out var byJsonName))
            {
                return byJsonName;
            }
        }

        return Default;
    }
}
