using System.Globalization;
using System.Reflection;
using System.Xml;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Mapping = Tisilia.AspNetCore.Export.XmlSerializerMapping;

namespace Tisilia.AspNetCore.Export;

/// <summary>An XML body: the root element and the use of the value it carries.</summary>
internal sealed record XmlBody(XmlElementName Root, TypeUse Use, bool RootNillable);

/// <summary>
/// Describes the bodies MVC's XmlSerializer formatters read and write with XmlSerializer's own mapping (<see cref="XmlSerializerMapping"/>):
/// XML models per direction — a class's domain properties are its attributes' and elements' local names, its character content the CLR
/// member's name —, XML wires and the builtin XML codecs. A mapping with a form the client does not write or read is not described, and
/// the caller keeps the body as bytes with the reason: a derived type (xsi:type), a choice of elements, xs:any, an attribute list, mixed
/// content, encoded names, a time of day with an offset, a default value the server also writes for null.
/// </summary>
internal sealed class XmlTypeMapper(ContractBuilder builder, TisiliaOptions options)
{
    /// <summary>The XML object models with the CLR type behind each and its members: what documentation is read from.</summary>
    public List<DocumentedType> Documented { get; } = [];

    private readonly Dictionary<(Node, WireDirection), TypeUse> _emitted = [];

    private string Prefix => options.ApiId + ".xml.";

    /// <summary>The XML body of a type the formatter reads (server-read) or writes (server-write), or the reason it is not described.</summary>
    public XmlBody? Map(Type type, WireDirection direction, out string? reason)
    {
        Node node;
        Mapping.Accessor root;
        try
        {
            // the whole mapping is checked before anything is added to the contract: a form found unsupported leaves no partial models
            root = Mapping.Import(type);
            node = new Planner(options).Plan(root.Mapping, type.Name);
        }
        catch (Unsupported e)
        {
            reason = e.Message;
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or MissingMemberException or MissingMethodException or TargetInvocationException or InvalidCastException or ArgumentException or FormatException or OverflowException)
        {
            reason = $"XmlSerializer's mapping of '{type}' could not be read ({(e is TargetInvocationException { InnerException: { } inner } ? inner.Message : e.Message)})";
            return null;
        }

        reason = null;
        var use = Emit(node, direction);
        return new XmlBody(new XmlElementName { Name = root.Name, Namespace = root.Namespace.Length == 0 ? null : root.Namespace }, use, root.IsNullable);
    }

    // ------------------------------------------------------------------ plan: XmlSerializer's mapping as the client reads and writes it

    private abstract record Node;

    private sealed record TextNode(string Scalar, string Grammar) : Node;

    private sealed record EnumNode(Type EnumType, string Underlying, bool Flags, IReadOnlyList<(string Name, string XmlName, long Value)> Constants) : Node;

    private sealed record ArrayNode(Type ClrType, string ItemName, string ItemNamespace, bool ItemNillable, Node Item) : Node;

    /// <summary>
    /// A class in one namespace (XmlSerializer maps a type once per namespace); its members are filled after it is registered, so a class
    /// that contains itself refers to its own node. Equality compares the member list by reference, never recursing into it.
    /// </summary>
    private sealed record StructNode(Type ClrType, string Namespace) : Node
    {
        public List<MemberPlan> Members { get; } = [];
    }

    private sealed record MemberPlan(string Property, XmlMemberKind Kind, string? Name, string Namespace, Node Value, bool Nillable, bool Repeated, bool CanBeNull, bool Conditional, string? Default, MemberInfo? Member, Type ValueType);

    private sealed class Unsupported(string message) : Exception(message);

    private sealed class Planner(TisiliaOptions options)
    {
        private readonly Dictionary<object, Node> _nodes = new(ReferenceEqualityComparer.Instance);

        public Node Plan(Mapping.Mapping mapping, string path, MemberInfo? member = null)
        {
            if (_nodes.TryGetValue(mapping.Raw, out var known))
            {
                return known;
            }

            if (mapping.IsSoap)
            {
                throw new Unsupported($"{path}: SOAP-encoded mapping");
            }

            switch (mapping.Kind)
            {
                case "NullableMapping":
                    return Plan(mapping.BaseMapping, path, member);
                case "EnumMapping":
                {
                    var type = mapping.Type ?? throw new Unsupported($"{path}: an enum without its type");
                    var underlying = ClrTypeMapper.ScalarNameOf(Enum.GetUnderlyingType(type)) ?? throw new Unsupported($"{path}: enum '{type}' has no builtin underlying type");
                    if (mapping.Constants.Count == 0)
                    {
                        throw new Unsupported($"{path}: enum '{type}' has no constant XmlSerializer writes");
                    }

                    return Remember(mapping, new EnumNode(type, underlying, mapping.IsFlags, mapping.Constants));
                }

                case "PrimitiveMapping":
                    return Remember(mapping, Primitive(mapping, path, member));
                case "ArrayMapping":
                {
                    var items = mapping.Items;
                    if (items.Count != 1)
                    {
                        throw new Unsupported($"{path}: a collection whose items are a choice of elements ([XmlArrayItem] with several types)");
                    }

                    var item = items[0];
                    if (item.Any)
                    {
                        throw new Unsupported($"{path}: a collection of xs:any items");
                    }

                    var node = new ArrayNode(mapping.Type ?? typeof(object), item.Name, item.Namespace, item.IsNullable, Plan(item.Mapping, path + "[]"));
                    return Remember(mapping, node);
                }

                case "StructMapping":
                    return PlanStruct(mapping, path);
                default:
                    throw new Unsupported($"{path}: {(mapping.Kind == "SerializableMapping" ? "a type that serializes itself (IXmlSerializable)" : "an XML node (XmlNode, XmlElement, XmlAttribute)")}");
            }
        }

        private Node Remember(Mapping.Mapping mapping, Node node)
        {
            _nodes[mapping.Raw] = node;
            return node;
        }

        private TextNode Primitive(Mapping.Mapping mapping, string path, MemberInfo? member)
        {
            if (mapping.CollapseWhitespace)
            {
                throw new Unsupported($"{path}: a string with an XML Schema data type whose white space XmlSerializer collapses on reading (DataType='token', 'anyURI' …)");
            }

            // XmlSerializer's primitive table (TypeScope, Types.cs v10.0.0) by formatter name
            return mapping.FormatterName switch
            {
                "String" => new TextNode("string", "xml-string"),
                "Boolean" => new TextNode("boolean", "xml-boolean"),
                "Int32" => new TextNode("int32", "xml-integer"),
                "Int16" => new TextNode("int16", "xml-integer"),
                "Int64" => new TextNode("int64", "xml-integer"),
                "Byte" => new TextNode("uint8", "xml-integer"),
                "SByte" => new TextNode("int8", "xml-integer"),
                "UInt16" => new TextNode("uint16", "xml-integer"),
                "UInt32" => new TextNode("uint32", "xml-integer"),
                "UInt64" => new TextNode("uint64", "xml-integer"),
                "Single" => new TextNode("float32", "xml-float"),
                "Double" => new TextNode("float64", "xml-float"),
                "Decimal" => new TextNode("decimal", "xml-decimal"),
                // the round-trip form, by the DateTime's Kind (XmlCustomFormatter.FromDateTime): the declared wire narrows it as for JSON
                "DateTime" => new TextNode(options.DateTimes.Find(member?.DeclaringType, member, null) switch
                {
                    Bindings.DateTimeWire.Utc => "datetime-utc",
                    Bindings.DateTimeWire.Unspecified => "datetime-unspecified",
                    Bindings.DateTimeWire.Local => "datetime-local-wire",
                    _ => "datetime",
                }, "xml-datetime"),
                // DataType="date": the server writes the date of the DateTime and reads a date as midnight, Kind Unspecified
                "Date" => new TextNode("date-only", "xml-date"),
                "ByteArrayBase64" => new TextNode("bytes", "xml-base64"),
                "ByteArrayHex" => new TextNode("bytes", "xml-hex"),
                "Guid" => new TextNode("guid", "xml-guid"),
                "Char" => new TextNode("char", "xml-char"),
                "TimeSpan" => new TextNode("duration", "xml-duration"),
                "DateTimeOffset" => new TextNode("datetime-offset", "xml-datetime-offset"),
                "DateOnly" => new TextNode("date-only", "xml-date"),
                "TimeOnly" => new TextNode("time-only", "xml-time-only"),
                var formatter => throw new Unsupported($"{path}: XmlSerializer writes this value with its '{formatter}' form ({(formatter is "Time" or "TimeOnlyIgnoreOffset" ? "a time of day with an offset" : formatter is "XmlQualifiedName" ? "a qualified name" : "an encoded XML name")}), which Tisilia does not describe"),
            };
        }

        private StructNode PlanStruct(Mapping.Mapping mapping, string path)
        {
            var type = mapping.Type;
            if (type is null || mapping.IsRoot)
            {
                throw new Unsupported($"{path}: an object member, which XmlSerializer writes with xsi:type");
            }

            if (mapping.HasDerivedMappings)
            {
                throw new Unsupported($"{path}: '{type.Name}' has derived types ([XmlInclude]), which XmlSerializer writes with xsi:type");
            }

            if (mapping.IsAbstract)
            {
                throw new Unsupported($"{path}: '{type.Name}' is abstract");
            }

            if (mapping.IsOpenModel)
            {
                throw new Unsupported($"{path}: '{type.Name}' has [XmlAnyElement] or [XmlAnyAttribute] members");
            }

            var node = new StructNode(type, mapping.Namespace ?? "");
            _nodes[mapping.Raw] = node;
            foreach (var m in mapping.Members)
            {
                if (m.IsXmlns || m.Ignore)
                {
                    continue;
                }

                var memberPath = path + "." + m.Name;
                if (m.HasChoiceIdentifier)
                {
                    throw new Unsupported($"{memberPath}: [XmlChoiceIdentifier]");
                }

                var conditional = m.CheckSpecified || m.CheckShouldPersist;
                var valueType = m.ClrType ?? typeof(object);
                if (m.Attribute is { } attribute)
                {
                    if (attribute.Any || attribute.IsSpecialXmlNamespace || attribute.IsList || m.IsArrayLike)
                    {
                        throw new Unsupported($"{memberPath}: {(attribute.Any ? "[XmlAnyAttribute]" : attribute.IsSpecialXmlNamespace ? "an xml: attribute" : "an attribute that lists values")}");
                    }

                    var value = Plan(attribute.Mapping, memberPath, m.MemberInfo);
                    node.Members.Add(new MemberPlan(attribute.Name, XmlMemberKind.Attribute, attribute.Name, attribute.Namespace, Text(value, memberPath), false, false, m.CanBeNull, conditional, Default(attribute, value, m, memberPath), m.MemberInfo, valueType));
                }
                else if (m.Text is { } text)
                {
                    if (m.IsArrayLike || conditional)
                    {
                        throw new Unsupported($"{memberPath}: {(m.IsArrayLike ? "character content of a collection" : "character content with a Specified or ShouldSerialize condition")}");
                    }

                    var value = Plan(text.Mapping, memberPath, m.MemberInfo);
                    node.Members.Add(new MemberPlan(m.Name, XmlMemberKind.Text, null, "", Text(value, memberPath), false, false, m.CanBeNull, false, null, m.MemberInfo, valueType));
                }
                else if (m.Elements.Count == 1)
                {
                    var element = m.Elements[0];
                    if (element.Any)
                    {
                        throw new Unsupported($"{memberPath}: [XmlAnyElement]");
                    }

                    // [XmlElement] on a collection writes each value as an element of the parent (no ArrayMapping of its own)
                    var repeated = m.IsArrayLike && element.Mapping.Kind != "ArrayMapping";
                    var value = Plan(element.Mapping, memberPath, m.MemberInfo);
                    node.Members.Add(new MemberPlan(element.Name, XmlMemberKind.Element, element.Name, element.Namespace, value, element.IsNullable, repeated, m.CanBeNull, conditional,
                        repeated ? null : Default(element, value, m, memberPath), m.MemberInfo, valueType));
                }
                else if (m.Elements.Count > 1)
                {
                    throw new Unsupported($"{memberPath}: a choice of elements ([XmlElement] with several types)");
                }
            }

            if (node.Members.Any(p => p.Kind == XmlMemberKind.Text) && node.Members.Any(p => p.Kind == XmlMemberKind.Element))
            {
                throw new Unsupported($"{path}: character content next to child elements (mixed content)");
            }

            if (node.Members.GroupBy(p => p.Property, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } clash)
            {
                throw new Unsupported($"{path}: more than one member is named '{clash.Key}' in the XML (an attribute and an element, or two namespaces)");
            }

            if (node.Members.Any(p => p.Kind == XmlMemberKind.Element && !IsNcName(p.Name)) || node.Members.Any(p => p.Kind == XmlMemberKind.Attribute && !IsNcName(p.Name)))
            {
                throw new Unsupported($"{path}: a member name is not an XML local name");
            }

            return node;
        }

        private static bool IsNcName(string? name)
        {
            try
            {
                XmlConvert.VerifyNCName(name ?? "");
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
            catch (ArgumentNullException)
            {
                return false;
            }
        }

        private static Node Text(Node value, string path) => value is TextNode or EnumNode ? value : throw new Unsupported($"{path}: an attribute or character content of a structured value");

        /// <summary>
        /// The text of a member's default value, which the writer leaves out (ReflectionXmlSerializationWriter.WritePrimitive): absence then
        /// reads as it. A member that can be null is left out for null too, and a condition also leaves it out: both are not described.
        /// </summary>
        private static string? Default(Mapping.Accessor accessor, Node value, Mapping.Member member, string path)
        {
            if (accessor.Default is not { } value0)
            {
                return null;
            }

            if (member.CanBeNull || member.CheckSpecified || member.CheckShouldPersist)
            {
                throw new Unsupported($"{path}: a [DefaultValue] on a member that {(member.CanBeNull ? "can be null" : "has a Specified or ShouldSerialize condition")}, which the server leaves out in both cases");
            }

            switch (value)
            {
                case EnumNode en:
                {
                    // XmlReflectionImporter keeps an enum default as its CLR names joined by spaces, which the writer compares by name
                    var names = value0.ToString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var number = Convert.ToInt64(Enum.Parse(en.EnumType, string.Join(",", names), ignoreCase: false), CultureInfo.InvariantCulture);
                    return XmlEnumText(number, en.Constants, en.Flags) ?? throw new Unsupported($"{path}: a [DefaultValue] that is no value of '{en.EnumType.Name}'");
                }

                case TextNode text:
                    // the writer compares with value.Equals(o): a default of another type than the member's never equals it, so the member is
                    // always written
                    if (value0.GetType() != (Nullable.GetUnderlyingType(member.ClrType ?? typeof(object)) ?? member.ClrType))
                    {
                        return null;
                    }

                    return value0 switch
                    {
                        bool b => XmlConvert.ToString(b),
                        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value0, CultureInfo.InvariantCulture),
                        float f => XmlConvert.ToString(f),
                        double d => XmlConvert.ToString(d),
                        decimal m => XmlConvert.ToString(m),
                        char c => XmlConvert.ToString((ushort)c),
                        Guid g => g.ToString("D"),
                        TimeSpan t when text.Grammar == "xml-duration" => XmlConvert.ToString(t),
                        _ => throw new Unsupported($"{path}: a [DefaultValue] of type {value0.GetType().Name}"),
                    };
                default:
                    throw new Unsupported($"{path}: a [DefaultValue] of a structured value");
            }
        }
    }

    /// <summary>XmlSerializer's text of an enum value: the first constant with it, or for flags the constants inside it (XmlCustomFormatter.FromEnum).</summary>
    private static string? XmlEnumText(long value, IReadOnlyList<(string Name, string XmlName, long Value)> constants, bool flags)
    {
        foreach (var c in constants)
        {
            if (c.Value == value)
            {
                return c.XmlName;
            }
        }

        if (!flags)
        {
            return null;
        }

        var rest = value;
        var names = new List<string>();
        foreach (var c in constants)
        {
            if (c.Value == 0)
            {
                continue;
            }

            if (rest == 0)
            {
                break;
            }

            if ((c.Value & value) == c.Value)
            {
                names.Add(c.XmlName);
                rest &= ~c.Value;
            }
        }

        return rest == 0 ? string.Join(" ", names) : null;
    }

    // ------------------------------------------------------------------ emit: models, wires and codecs per direction

    private TypeUse Emit(Node node, WireDirection direction)
    {
        if (_emitted.TryGetValue((node, direction), out var known))
        {
            return known;
        }

        switch (node)
        {
            case TextNode text:
                return Remember(node, direction, builder.XmlScalar(text.Scalar, text.Grammar));
            case EnumNode en:
            {
                var id = Prefix + "enum." + ProfileContext.IdPart(Prefix + "enum.", ClrTypeMapper.CleanName(en.EnumType), "", 16);
                // the constants XmlSerializer writes (its EnumMapping, in its order): the domain has their values, the wire their XML names;
                // XmlSerializer keeps a value as a long, which an unsigned 64-bit constant above long.MaxValue wraps
                string Text(long value) => en.Underlying == "uint64" ? unchecked((ulong)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
                var members = en.Constants.Select(c => new Contract.EnumMember { Name = c.Name, Value = Text(c.Value) }).ToList();
                var names = en.Constants.Select(c => new XmlEnumName { Value = Text(c.Value), Name = c.XmlName }).ToList();
                return Remember(node, direction, builder.XmlEnumOf(id, UniqueTsName(ClrTypeMapper.BaseTsName(en.EnumType) + "Xml", id), ClrTypeMapper.CleanName(en.EnumType), en.Underlying, members, en.Flags, names));
            }

            case ArrayNode array:
            {
                var element = Emit(array.Item, direction) with { SemanticNullable = array.ItemNillable };
                return Remember(node, direction, Collection(array.ClrType, element, array.ItemName, array.ItemNamespace, array.ItemNillable, direction));
            }

            case StructNode type:
                return EmitStruct(type, direction);
            default:
                throw new InvalidOperationException("unknown XML plan node");
        }
    }

    private TypeUse Remember(Node node, WireDirection direction, TypeUse use)
    {
        _emitted[(node, direction)] = use;
        return use;
    }

    private static string Suffix(WireDirection direction) => direction == WireDirection.ServerRead ? "request" : "response";

    /// <summary>A collection model: XmlSerializer may write one CLR collection type with several item elements, so the id names the items.</summary>
    private TypeUse Collection(Type clrType, TypeUse element, string itemName, string itemNamespace, bool itemNillable, WireDirection direction)
    {
        var shape = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", itemName, itemNamespace, itemNillable ? "nil" : "", element.CodecId))))[..16];
        var id = Prefix + "array." + shape + "." + Suffix(direction);
        var elementName = Stem(builder.GetType(element.TypeId)?.TsName ?? "Item");
        return builder.XmlArrayOf(id, UniqueTsName(ClrTypeMapper.Capitalize(elementName) + "XmlArray" + (direction == WireDirection.ServerRead ? "Request" : "Response"), id),
            ClrTypeMapper.CleanName(clrType), direction, element, itemName, itemNamespace, itemNillable);
    }

    /// <summary>A model's TypeScript name without the XML suffix and counter an element model carries (<c>LineXmlResponse2</c> → <c>Line</c>).</summary>
    private static string Stem(string tsName) => System.Text.RegularExpressions.Regex.Replace(tsName, "Xml(?:Request|Response)?[0-9]*$", "") is { Length: > 0 } stem ? stem : tsName;

    private TypeUse EmitStruct(StructNode type, WireDirection direction)
    {
        var namespacePart = type.Namespace.Length == 0 ? "" : "." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(type.Namespace)))[..8];
        var stem = ClrTypeMapper.CleanName(type.ClrType) + namespacePart;
        var id = Prefix + ProfileContext.IdPart(Prefix, stem, "." + Suffix(direction), 16) + "." + Suffix(direction);
        // a class that contains itself refers to the codec it is building, by the builder's naming convention
        var use = new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = false };
        _emitted[(type, direction)] = use;
        if (builder.HasType(id))
        {
            return use;
        }

        var specs = new List<XmlMemberSpec>();
        var documented = new List<DocumentedMember>();
        foreach (var member in type.Members)
        {
            specs.Add(Spec(member, direction));
            documented.Add(new DocumentedMember(member.Property, member.ValueType, member.Member, null));
        }

        var tsName = UniqueTsName(ClrTypeMapper.BaseTsName(type.ClrType) + "Xml" + (direction == WireDirection.ServerRead ? "Request" : "Response"), id);
        var built = builder.XmlObjectOf(id, tsName, ClrTypeMapper.CleanName(type.ClrType), direction, specs);
        Documented.Add(new DocumentedType(id, type.ClrType, documented));
        return built;
    }

    /// <summary>
    /// A member in one direction. The server reads every member as optional (XmlSerializer requires none; an absent one keeps the value
    /// the constructor gave it) and null only as xsi:nil. It writes a nillable member always (null as xsi:nil), a value without a condition
    /// always, a value equal to its default never, a null it cannot write as xsi:nil never, a collection's values one element each, and a
    /// member whose Specified or ShouldSerialize condition says no never.
    /// </summary>
    private XmlMemberSpec Spec(MemberPlan member, WireDirection direction)
    {
        var value = Emit(member.Value, direction);
        if (member.Repeated)
        {
            var item = value with { SemanticNullable = member.Nillable };
            var collection = Collection(member.ValueType, item, member.Name!, member.Namespace, member.Nillable, direction);
            var required = direction == WireDirection.ServerWrite && !member.Conditional;
            return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, collection, item, required ? Presence.Required : Presence.Optional, Presence.Optional, member.Nillable, true, null);
        }

        if (direction == WireDirection.ServerRead)
        {
            return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, value with { SemanticNullable = member.Nillable }, null, Presence.Optional, Presence.Optional, member.Nillable, false, null);
        }

        if (member.Kind == XmlMemberKind.Text)
        {
            // XmlSerializer writes null character content as none and reads none as null; a string's empty content is null here
            var nullable = member.CanBeNull && member.Value is TextNode { Grammar: "xml-string" };
            return new XmlMemberSpec(member.Property, member.Kind, null, "", value with { SemanticNullable = nullable }, null, Presence.Required, Presence.Required, false, false, null);
        }

        if (member.Nillable)
        {
            var presence = member.Conditional ? Presence.Optional : Presence.Required;
            return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, value with { SemanticNullable = true }, null, presence, presence, true, false, null);
        }

        if (member.Default is not null)
        {
            return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, value, null, Presence.Required, Presence.Optional, false, false, member.Default);
        }

        if (member.CanBeNull && !member.Conditional)
        {
            // the server leaves a null member out: an absent member reads as null
            return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, value with { SemanticNullable = true }, null, Presence.Required, Presence.Optional, false, false, null);
        }

        var written = member.Conditional ? Presence.Optional : Presence.Required;
        return new XmlMemberSpec(member.Property, member.Kind, member.Name, member.Namespace, value, null, written, written, false, false, null);
    }

    private string UniqueTsName(string baseName, string modelId)
    {
        if (!builder.IsTsNameTaken(baseName, modelId))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = baseName + i.ToString(CultureInfo.InvariantCulture);
            if (!builder.IsTsNameTaken(candidate, modelId))
            {
                return candidate;
            }
        }
    }
}
