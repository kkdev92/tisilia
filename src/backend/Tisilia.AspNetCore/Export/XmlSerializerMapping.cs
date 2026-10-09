using System.Reflection;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// The mapping XmlSerializer itself makes for a type — the one MVC's XmlSerializer formatters serialize with (<c>new XmlSerializer(type)</c>
/// imports it with <c>XmlReflectionImporter.ImportTypeMapping</c>) — read from System.Private.Xml's internal model (Mappings.cs,
/// dotnet/runtime v10.0.0), which the reader and the writer interpret. Nothing here derives a name: the names, namespaces, order and
/// flags are XmlSerializer's own. A member this .NET does not have makes the read fail, and the body stays opaque.
/// </summary>
internal static class XmlSerializerMapping
{
    /// <summary>The System.Private.Xml major version whose internal model was read and verified.</summary>
    public const int VerifiedMajorVersion = 10;

    private static readonly Assembly SerializationAssembly = typeof(XmlSerializer).Assembly;

    private static readonly Lazy<MethodInfo?> GetAllMembersMethod = new(() =>
        SerializationAssembly.GetType("System.Xml.Serialization.TypeScope")?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetAllMembers" && m.GetParameters().Length == 1));

    /// <summary>The root element accessor of a type's mapping (<c>XmlMapping.Accessor</c>); throws what XmlSerializer throws for an unsupported type.</summary>
    public static Accessor Import(Type type)
    {
        var version = SerializationAssembly.GetName().Version?.Major;
        if (version != VerifiedMajorVersion)
        {
            throw new NotSupportedException($"Tisilia reads XmlSerializer's mapping as System.Private.Xml {VerifiedMajorVersion} has it; this application runs {version}");
        }

        var mapping = new XmlReflectionImporter().ImportTypeMapping(type);
        return new Accessor(Get(mapping, "Accessor") ?? throw new MissingMemberException("XmlMapping", "Accessor"));
    }

    private static object? Get(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } property)
            {
                return property.GetValue(target);
            }

            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } field)
            {
                return field.GetValue(target);
            }
        }

        throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static T Get<T>(object target, string name) => Get(target, name) is T value ? value : throw new MissingMemberException(target.GetType().FullName, name);

    /// <summary>An element, attribute or text accessor: a name, a namespace and the mapping of the value.</summary>
    public sealed class Accessor(object raw)
    {
        public string Kind => raw.GetType().Name;

        public bool IsElement => Kind == "ElementAccessor";

        public string Name => Get<string>(raw, "Name");

        /// <summary>The namespace the XML carries: an unqualified element or attribute has none.</summary>
        public string Namespace => Form == XmlSchemaForm.Qualified ? Get(raw, "Namespace") as string ?? "" : "";

        public XmlSchemaForm Form => Get<XmlSchemaForm>(raw, "Form");

        public bool Any => Get<bool>(raw, "Any");

        /// <summary>The value the writer leaves out (<c>[DefaultValue]</c>), or null.</summary>
        public object? Default => Get(raw, "Default") is { } value && value != DBNull.Value ? value : null;

        public bool IsNullable => IsElement && Get<bool>(raw, "IsNullable");

        public bool IsUnbounded => IsElement && Get<bool>(raw, "IsUnbounded");

        public bool IsList => Kind == "AttributeAccessor" && Get<bool>(raw, "IsList");

        public bool IsSpecialXmlNamespace => Kind == "AttributeAccessor" && Get<bool>(raw, "IsSpecialXmlNamespace");

        public Mapping Mapping => new(Get(raw, "Mapping") ?? throw new MissingMemberException(raw.GetType().FullName, "Mapping"));
    }

    /// <summary>A type mapping: primitive, enum, nullable, array, struct or special (XmlNode, IXmlSerializable).</summary>
    public sealed class Mapping(object raw)
    {
        public object Raw => raw;

        public string Kind => raw.GetType().Name;

        private object TypeDesc => Get(raw, "TypeDesc") ?? throw new MissingMemberException(raw.GetType().FullName, "TypeDesc");

        public Type? Type => Get(TypeDesc, "Type") as Type;

        public string? FormatterName => Get(TypeDesc, "FormatterName") as string;

        public bool CollapseWhitespace => Get<bool>(TypeDesc, "CollapseWhitespace");

        public bool IsRoot => Get<bool>(TypeDesc, "IsRoot");

        public bool IsAbstract => Get<bool>(TypeDesc, "IsAbstract");

        public bool IsValueType => Get<bool>(TypeDesc, "IsValueType");

        public string? Namespace => Get(raw, "Namespace") as string;

        public string? TypeName => Get(raw, "TypeName") as string;

        public bool IsSoap => Get<bool>(raw, "IsSoap");

        // NullableMapping
        public Mapping BaseMapping => new(Get(raw, "BaseMapping") ?? throw new MissingMemberException(raw.GetType().FullName, "BaseMapping"));

        // EnumMapping
        public bool IsFlags => Get<bool>(raw, "IsFlags");

        public IReadOnlyList<(string Name, string XmlName, long Value)> Constants => ((Array?)Get(raw, "Constants") ?? Array.Empty<object>()).Cast<object>()
            .Select(c => (Get<string>(c, "Name"), Get<string>(c, "XmlName"), Get<long>(c, "Value"))).ToList();

        // ArrayMapping
        public IReadOnlyList<Accessor> Items => ((Array?)Get(raw, "Elements") ?? Array.Empty<object>()).Cast<object>().Select(e => new Accessor(e)).ToList();

        // StructMapping
        public bool HasDerivedMappings => Get(raw, "DerivedMappings") is not null;

        public bool IsOpenModel => Get<bool>(raw, "IsOpenModel");

        public bool HasSimpleContent => Get<bool>(raw, "HasSimpleContent");

        /// <summary>The members in the order the writer writes them, the base type's first (<c>TypeScope.GetAllMembers</c>).</summary>
        public IReadOnlyList<Member> Members
        {
            get
            {
                var method = GetAllMembersMethod.Value ?? throw new MissingMethodException("System.Xml.Serialization.TypeScope", "GetAllMembers");
                return ((Array?)method.Invoke(null, [raw]) ?? Array.Empty<object>()).Cast<object>().Select(m => new Member(m)).ToList();
            }
        }
    }

    /// <summary>A member of a struct mapping: its CLR member, the accessor it is written with and the conditions of the writer.</summary>
    public sealed class Member(object raw)
    {
        public string Name => Get<string>(raw, "Name");

        public MemberInfo? MemberInfo => Get(raw, "MemberInfo") as MemberInfo;

        public IReadOnlyList<Accessor> Elements => ((Array?)Get(raw, "Elements") ?? Array.Empty<object>()).Cast<object>().Select(e => new Accessor(e)).ToList();

        public Accessor? Attribute => Get(raw, "Attribute") is { } attribute ? new Accessor(attribute) : null;

        public Accessor? Text => Get(raw, "Text") is { } text ? new Accessor(text) : null;

        public bool HasChoiceIdentifier => Get(raw, "ChoiceIdentifier") is not null;

        public bool IsXmlns => Get(raw, "Xmlns") is not null;

        public bool Ignore => Get<bool>(raw, "Ignore");

        /// <summary><c>ShouldSerializeX()</c>: the writer asks it.</summary>
        public bool CheckShouldPersist => Get<bool>(raw, "CheckShouldPersist");

        /// <summary><c>XSpecified</c>: the writer asks it (None, ReadOnly or ReadWrite).</summary>
        public bool CheckSpecified => Get(raw, "CheckSpecified")?.ToString() is { } specified && specified != "None";

        private object TypeDesc => Get(raw, "TypeDesc") ?? throw new MissingMemberException(raw.GetType().FullName, "TypeDesc");

        public bool IsArrayLike => Get<bool>(TypeDesc, "IsArrayLike");

        public bool IsArray => Get<bool>(TypeDesc, "IsArray");

        /// <summary>The member's own type is a reference type or <see cref="Nullable{T}"/>: it can be null on the server.</summary>
        public bool CanBeNull
        {
            get
            {
                var type = Get(TypeDesc, "Type") as Type;
                return type is null || !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
            }
        }

        public Type? ClrType => Get(TypeDesc, "Type") as Type;
    }
}
