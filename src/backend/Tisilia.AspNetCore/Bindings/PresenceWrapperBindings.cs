using System.Reflection;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// A presence wrapper: a generic struct that holds whether an object member was sent, such as <c>Optional&lt;T&gt;</c>. Its default is
/// the absent state; <see cref="PresentMember"/> answers whether the member was sent and <see cref="ValueMember"/> holds what was sent.
/// </summary>
public sealed record PresenceWrapperRegistration(Type OpenType, string PresentMember, string ValueMember)
{
    /// <summary>Whether a wrapper value holds a member that was sent.</summary>
    public bool IsPresent(object wrapper) => (bool)PresenceWrapperMembers.Read(wrapper, PresentMember)!;

    /// <summary>What a present wrapper holds.</summary>
    public object? ValueOf(object wrapper) => PresenceWrapperMembers.Read(wrapper, ValueMember);
}

/// <summary>
/// The presence wrappers of an application: generic structs that hold whether an object member was sent, so that a request can leave a
/// member out, send null or send a value (a PATCH body). A member of a declared wrapper type is the wrapper's value in the contract, and
/// optional: <c>Optional&lt;string?&gt; Name</c> is <c>name?: string | null</c> in TypeScript.
/// </summary>
public sealed class PresenceWrapperCollection
{
    private readonly Dictionary<Type, PresenceWrapperRegistration> _wrappers = [];

    /// <summary>
    /// Declares a presence wrapper by its generic type definition (<c>typeof(Optional&lt;&gt;)</c>) and the names of its public members
    /// that answer whether the member was sent (a <c>bool</c>) and hold what was sent (of the type parameter). Export observes, with each
    /// member's own converter, whether JSON null reads as a present wrapper that holds null and whether a response leaves the absent
    /// state out. The declaration states what export cannot observe: the converter System.Text.Json uses for the member reads the
    /// member's JSON as the value, with the same options, into a present wrapper, and writes a present wrapper as its value.
    /// </summary>
    public void Add(Type openType, string presentMember = "HasValue", string valueMember = "Value")
    {
        ArgumentNullException.ThrowIfNull(openType);
        ArgumentException.ThrowIfNullOrEmpty(presentMember);
        ArgumentException.ThrowIfNullOrEmpty(valueMember);
        if (!openType.IsGenericTypeDefinition || !openType.IsValueType || openType.GetGenericArguments().Length != 1)
        {
            throw new ArgumentException($"'{openType}' is not the definition of a generic struct with one type parameter, such as typeof(Optional<>): a presence wrapper's absent state is the struct's default", nameof(openType));
        }

        var parameter = openType.GetGenericArguments()[0];
        if (PresenceWrapperMembers.TypeOf(openType, presentMember) != typeof(bool))
        {
            throw new ArgumentException($"'{openType.Name}' has no public instance property or field '{presentMember}' of type bool", nameof(presentMember));
        }

        if (PresenceWrapperMembers.TypeOf(openType, valueMember) != parameter)
        {
            throw new ArgumentException($"'{openType.Name}' has no public instance property or field '{valueMember}' of its type parameter {parameter.Name}", nameof(valueMember));
        }

        if (!_wrappers.TryAdd(openType, new PresenceWrapperRegistration(openType, presentMember, valueMember)))
        {
            throw new InvalidOperationException($"presence wrapper '{openType}' is already declared; a scope cannot be claimed twice (SV24)");
        }
    }

    /// <summary>The declaration of a closed wrapper type (<c>Optional&lt;string&gt;</c>), or null when the type is not one.</summary>
    public PresenceWrapperRegistration? Find(Type type)
        => type.IsGenericType && !type.IsGenericTypeDefinition && _wrappers.TryGetValue(type.GetGenericTypeDefinition(), out var wrapper) ? wrapper : null;
}

/// <summary>Reads a presence wrapper's members by name: a public instance property, else a public instance field.</summary>
internal static class PresenceWrapperMembers
{
    public static Type? TypeOf(Type type, string member)
        => type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance)?.PropertyType ?? type.GetField(member, BindingFlags.Public | BindingFlags.Instance)?.FieldType;

    public static object? Read(object wrapper, string member)
    {
        var type = wrapper.GetType();
        if (type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is { } property)
        {
            return property.GetValue(wrapper);
        }

        return type.GetField(member, BindingFlags.Public | BindingFlags.Instance) is { } field
            ? field.GetValue(wrapper)
            : throw new InvalidOperationException($"presence wrapper '{type}' has no public instance property or field '{member}'");
    }
}
