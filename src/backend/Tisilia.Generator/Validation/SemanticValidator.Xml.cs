using System.Numerics;
using System.Xml;
using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

/// <summary>
/// SV55: XML wires (MVC's XmlSerializer formatters). XML wires reference only XML wires; a codec bound to the XmlSerializer binding
/// converts its model through an XML wire of the matching kind; the members of a class carry each domain property once, with a way to
/// write every value the domain allows and to read every value the server writes.
/// </summary>
public sealed partial class SemanticValidator
{
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private const string XmlReservedNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>The builtin scalars each XML text grammar carries.</summary>
    private static readonly Dictionary<string, string[]> XmlGrammarScalars = new(StringComparer.Ordinal)
    {
        ["xml-string"] = ["string"],
        ["xml-boolean"] = ["boolean"],
        ["xml-integer"] = Builtins.IntegerScalarNames,
        ["xml-decimal"] = ["decimal"],
        ["xml-float"] = ["float32", "float64"],
        ["xml-datetime"] = ["datetime", "datetime-utc", "datetime-unspecified", "datetime-local-wire"],
        ["xml-date"] = ["date-only"],
        ["xml-time-only"] = ["time-only"],
        ["xml-datetime-offset"] = ["datetime-offset"],
        ["xml-duration"] = ["duration"],
        ["xml-guid"] = ["guid"],
        ["xml-char"] = ["char"],
        ["xml-base64"] = ["bytes"],
        ["xml-hex"] = ["bytes"],
    };

    private static bool IsXmlWire(WireShape shape) => shape is XmlTextWire or XmlElementWire or XmlItemsWire;

    private static bool IsXmlCodec(Codec codec) => codec.BindingId == Builtins.BindingXmlSerializer;

    private static bool IsNcName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string GrammarName(string grammarId) => grammarId["tisilia.grammar.".Length..^"@0.1".Length];

    private static IEnumerable<XmlMember> MembersOf(XmlElementWire element) => element.Attributes.Concat(element.Elements).Concat(element.Text is null ? [] : [element.Text]);

    /// <summary>The wires a wire references directly.</summary>
    private static IEnumerable<WireRef> ChildWires(WireShape shape) => shape switch
    {
        ArrayWire array => [array.Element],
        ObjectWire obj => obj.Properties.Select(p => p.Wire).Concat(obj.Additional is CaptureAdditional capture ? [capture.Wire] : []),
        TokenUnionWire tokens => tokens.Branches.Select(b => b.Wire),
        TaggedUnionWire tagged => tagged.Variants.Select(v => v.Wire),
        XmlElementWire element => MembersOf(element).Select(m => m.Wire),
        XmlItemsWire items => [items.Item.Wire],
        _ => [],
    };

    /// <summary>An XML wire references only XML wires, and a JSON wire only JSON wires.</summary>
    private void CheckWireFamily(Wire wire, string sp)
    {
        var xml = IsXmlWire(wire.Shape);
        foreach (var child in ChildWires(wire.Shape))
        {
            if (_index.Wires.TryGetValue(child.WireId, out var target) && IsXmlWire(target.Shape) != xml)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", sp, $"wire '{wire.Id}' ({(xml ? "XML" : "JSON")}) references {(xml ? "JSON" : "XML")} wire '{target.Id}'", [wire.Id, target.Id]);
            }
        }
    }

    private void CheckXmlTextWire(Wire wire, XmlTextWire text, string sp)
    {
        if (!Builtins.IsXmlGrammar(text.GrammarId))
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(sp, "grammarId"), $"wire '{wire.Id}': '{text.GrammarId}' is not an XML text grammar", [wire.Id]);
            return;
        }

        var flags = text.GrammarId == Builtins.Grammar("xml-flags");
        var named = flags || text.GrammarId == Builtins.Grammar("xml-enum");
        if (named != text.Names is not null)
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", sp, named ? $"wire '{wire.Id}': an enum grammar lists the XML name of each constant" : $"wire '{wire.Id}': only an enum grammar lists names", [wire.Id]);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < (text.Names?.Count ?? 0); j++)
        {
            var entry = text.Names![j];
            var np = JsonPointer.Append(JsonPointer.Append(sp, "names"), j);
            if (!BigInteger.TryParse(entry.Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value.ToString(System.Globalization.CultureInfo.InvariantCulture) != entry.Value)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(np, "value"), $"wire '{wire.Id}': '{entry.Value}' is not canonical decimal integer text", [wire.Id]);
            }

            // XmlSerializer splits a flags value at white space (XmlCustomFormatter.ToEnum)
            if (entry.Name.Length == 0 || (flags && entry.Name.Any(char.IsWhiteSpace)) || !seen.Add(entry.Name))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(np, "name"), $"wire '{wire.Id}': enum name '{entry.Name}' must be unique and not empty{(flags ? ", without white space" : "")}", [wire.Id]);
            }
        }
    }

    private void CheckXmlElementWire(Wire wire, XmlElementWire element, string sp)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var attributeNames = new HashSet<(string, string)>();
        var elementNames = new HashSet<(string, string)>();
        void Member(XmlMember member, string mp, string kind)
        {
            if (!properties.Add(member.Property))
            {
                Error(TisiliaCodes.DuplicatePropertyName, "SV10", JsonPointer.Append(mp, "property"), $"wire '{wire.Id}': property '{member.Property}' is carried by more than one XML member", [wire.Id]);
            }

            if (kind == "text")
            {
                if (member.Name is not null || member.Namespace is not null)
                {
                    Error(TisiliaCodes.XmlWireInvalid, "SV55", mp, $"wire '{wire.Id}': character content has no name or namespace", [wire.Id]);
                }
            }
            else if (!IsNcName(member.Name))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(mp, "name"), $"wire '{wire.Id}': '{member.Name}' is not an XML local name (NCName)", [wire.Id]);
            }
            else if (!(kind == "attribute" ? attributeNames : elementNames).Add((member.Name!, member.Namespace ?? "")))
            {
                Error(TisiliaCodes.DuplicatePropertyName, "SV10", JsonPointer.Append(mp, "name"), $"wire '{wire.Id}': {kind} '{member.Name}'{(member.Namespace is null ? "" : $" in '{member.Namespace}'")} appears more than once", [wire.Id]);
            }

            // xsi:nil and xsi:type are XmlSerializer's own, namespace declarations are not attributes, and the xml: attributes are not supported
            if (kind == "attribute" && (member.Namespace is XsiNamespace or XmlnsNamespace or XmlReservedNamespace || (member.Namespace is null && member.Name == "xmlns")))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", mp, $"wire '{wire.Id}': attribute '{member.Name}' is reserved by XML", [wire.Id]);
            }

            var target = RequireWireInDirection(member.Wire, wire, JsonPointer.Append(mp, "wire"));
            if (target is not null && kind != "element" && target.Shape is not XmlTextWire)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(mp, "wire"), $"wire '{wire.Id}': {(kind == "text" ? "character content" : "an attribute")} carries text, but '{target.Id}' is '{target.Shape.Kind}'", [wire.Id, target.Id]);
            }

            if ((member.Repeated == true || member.Nillable == true) && kind != "element")
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", mp, $"wire '{wire.Id}': only an element member repeats or is nillable", [wire.Id]);
            }

            if (member.Default is not null && (wire.Direction != WireDirection.ServerWrite || kind == "text" || member.Repeated == true || member.Nillable == true
                || target?.Shape is not XmlTextWire || member.Presence != Presence.Optional))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(mp, "default"), $"wire '{wire.Id}': a default value is what the server leaves out, so it belongs to an optional attribute or text element of a server-write wire", [wire.Id]);
            }
        }

        for (var j = 0; j < element.Attributes.Count; j++)
        {
            Member(element.Attributes[j], JsonPointer.Append(JsonPointer.Append(sp, "attributes"), j), "attribute");
        }

        for (var j = 0; j < element.Elements.Count; j++)
        {
            Member(element.Elements[j], JsonPointer.Append(JsonPointer.Append(sp, "elements"), j), "element");
        }

        if (element.Text is not null)
        {
            Member(element.Text, JsonPointer.Append(sp, "text"), "text");
            if (element.Elements.Count > 0)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(sp, "text"), $"wire '{wire.Id}': an element with character content has no child elements (mixed content is not supported)", [wire.Id]);
            }
        }
    }

    private void CheckXmlItemsWire(Wire wire, XmlItemsWire items, string sp)
    {
        if (!IsNcName(items.Item.Name))
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(JsonPointer.Append(sp, "item"), "name"), $"wire '{wire.Id}': '{items.Item.Name}' is not an XML local name (NCName)", [wire.Id]);
        }

        RequireWireInDirection(items.Item.Wire, wire, JsonPointer.Append(JsonPointer.Append(sp, "item"), "wire"));
    }

    /// <summary>
    /// A codec bound to the XmlSerializer binding converts its model through an XML wire of the matching kind with the builtin XML
    /// implementation; a codec of another binding never reaches an XML wire.
    /// </summary>
    private void CheckXmlCodec(Codec codec, string cp)
    {
        var xml = IsXmlCodec(codec);
        if (xml && (codec.Origin != CodecOrigin.Builtin || codec.Capabilities.RequestKey is not null || codec.Capabilities.ResponseKey is not null))
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", cp, $"XML codec '{codec.Id}' is builtin and has no key capability", [codec.Id]);
        }

        var model = _index.Types[codec.TypeId];
        foreach (var (cap, direction, name) in new[] { (codec.Capabilities.Request, WireDirection.ServerRead, "request"), (codec.Capabilities.Response, WireDirection.ServerWrite, "response") })
        {
            if (cap is null || !_index.Wires.TryGetValue(cap.Wire.WireId, out var wire))
            {
                continue;
            }

            var path = JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), name), "wire");
            if (!xml)
            {
                if (IsXmlWire(wire.Shape))
                {
                    Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"codec '{codec.Id}' is not bound to the XmlSerializer but converts through XML wire '{wire.Id}'", [codec.Id, wire.Id]);
                }

                continue;
            }

            var implementation = wire.Shape switch
            {
                XmlTextWire => "xml-text",
                XmlElementWire => "xml-element",
                XmlItemsWire => "xml-items",
                _ => null,
            };
            if (implementation is null)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}' converts through '{wire.Id}', which is '{wire.Shape.Kind}', not an XML wire", [codec.Id, wire.Id]);
                continue;
            }

            if (cap.Implementation is not BuiltinImpl { Id: var implementationId } || implementationId != Builtins.CodecImpl(implementation, direction == WireDirection.ServerRead ? "encode" : "decode"))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), name), "implementation"), $"XML codec '{codec.Id}' converts an {wire.Shape.Kind} wire with the builtin {implementation} {(direction == WireDirection.ServerRead ? "encoder" : "decoder")}", [codec.Id]);
            }

            if (cap.NullBehavior != NullBehavior.Reject)
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), name), "nullBehavior"), $"XML codec '{codec.Id}': null is written by the member or the root (xsi:nil), never by the codec", [codec.Id]);
            }

            switch (model.Shape, wire.Shape)
            {
                case (PrimitiveShape primitive, XmlTextWire text):
                    if (Builtins.IsXmlGrammar(text.GrammarId) && (!XmlGrammarScalars.TryGetValue(GrammarName(text.GrammarId), out var scalars) || !scalars.Any(s => Builtins.Scalar(s) == primitive.PrimitiveId)))
                    {
                        Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': grammar '{text.GrammarId}' does not carry '{primitive.PrimitiveId}'", [codec.Id, wire.Id]);
                    }

                    break;
                case (EnumShape en, XmlTextWire text):
                    if (text.GrammarId != Builtins.Grammar(en.Flags ? "xml-flags" : "xml-enum") || en.AllowUndefinedInteger)
                    {
                        Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': an enum is written with the {(en.Flags ? "xml-flags" : "xml-enum")} grammar and only its defined values (XmlSerializer refuses others)", [codec.Id, wire.Id]);
                    }
                    else if (text.Names is { } names && !names.Select(n => n.Value).ToHashSet(StringComparer.Ordinal).SetEquals(en.Members.Select(m => m.Value)))
                    {
                        Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': wire '{wire.Id}' names other values than enum '{model.Id}' defines", [codec.Id, wire.Id]);
                    }

                    break;
                case (ObjectShape obj, XmlElementWire element):
                    CheckXmlMembers(codec, model, obj, wire, element, path);
                    break;
                case (ArrayShape array, XmlItemsWire items):
                    if (_index.Codecs.TryGetValue(array.Element.CodecId, out var elementCodec))
                    {
                        var elementCap = direction == WireDirection.ServerRead ? elementCodec.Capabilities.Request : elementCodec.Capabilities.Response;
                        if (!IsXmlCodec(elementCodec) || (elementCap is not null && elementCap.Wire.WireId != items.Item.Wire.WireId))
                        {
                            Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': the items of wire '{wire.Id}' are converted by XML codec '{array.Element.CodecId}' through its own wire", [codec.Id, wire.Id]);
                        }
                    }

                    if (array.Element.SemanticNullable != (items.Item.Nillable == true))
                    {
                        Error(TisiliaCodes.NullabilityMismatch, "SV55", path, $"XML codec '{codec.Id}': an item is null exactly when it is written with xsi:nil (wire '{wire.Id}')", [codec.Id, wire.Id]);
                    }

                    break;
                default:
                    Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': a {model.Shape.Kind} model is not carried by an {wire.Shape.Kind} wire", [codec.Id, wire.Id]);
                    break;
            }
        }
    }

    /// <summary>The members of a class's XML content and the properties of its domain object correspond one to one.</summary>
    private void CheckXmlMembers(Codec codec, Model model, ObjectShape obj, Wire wire, XmlElementWire element, string path)
    {
        if (obj.Extension is not NoExtension)
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': an XML class has no extension data", [codec.Id]);
        }

        var members = new Dictionary<string, (XmlMember Member, string Kind)>(StringComparer.Ordinal);
        foreach (var (member, kind) in element.Attributes.Select(m => (m, "attribute")).Concat(element.Elements.Select(m => (m, "element"))).Concat(element.Text is null ? [] : [(element.Text, "text")]))
        {
            members.TryAdd(member.Property, (member, kind));
            if (!obj.Properties.Any(p => p.Name == member.Property))
            {
                Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"XML codec '{codec.Id}': member '{member.Property}' of wire '{wire.Id}' has no domain property", [codec.Id, wire.Id]);
            }
        }

        foreach (var prop in obj.Properties)
        {
            if (!members.TryGetValue(prop.Name, out var entry))
            {
                Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"XML codec '{codec.Id}': domain property '{prop.Name}' has no member on wire '{wire.Id}'", [codec.Id, wire.Id]);
                continue;
            }

            var (member, kind) = entry;
            var repeated = member.Repeated == true;
            var valueUse = prop.Use;
            if (repeated)
            {
                if (!_index.Types.TryGetValue(prop.Use.TypeId, out var collection) || collection.Shape is not ArrayShape array || prop.Use.SemanticNullable)
                {
                    Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': repeated member '{prop.Name}' carries a collection that is never null (no element is no value)", [codec.Id, wire.Id]);
                    continue;
                }

                valueUse = array.Element;
            }

            // what an absent member reads as: an optional property is left out; a required one needs null, the default or no values
            var absentValue = member.Default is not null || repeated || (prop.Use.SemanticNullable && member.Nillable != true && kind != "text");
            if ((prop.Presence == Presence.Optional && member.Presence == Presence.Required)
                || (prop.Presence == Presence.Required && member.Presence == Presence.Optional && (wire.Direction == WireDirection.ServerRead || !absentValue)))
            {
                Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"XML codec '{codec.Id}': property '{prop.Name}' is {Enum(prop.Presence)} in the domain but {Enum(member.Presence)} on wire '{wire.Id}', with no value for an absent member", [codec.Id, wire.Id]);
            }

            if (_index.Codecs.TryGetValue(prop.Use.CodecId, out var propertyCodec) && !IsXmlCodec(propertyCodec))
            {
                Error(TisiliaCodes.XmlWireInvalid, "SV55", path, $"XML codec '{codec.Id}': property '{prop.Name}' uses codec '{propertyCodec.Id}', which is not an XML codec", [codec.Id, propertyCodec.Id]);
            }

            if (_index.Codecs.TryGetValue(valueUse.CodecId, out var valueCodec))
            {
                var valueCap = wire.Direction == WireDirection.ServerRead ? valueCodec.Capabilities.Request : valueCodec.Capabilities.Response;
                if (valueCap is not null && valueCap.Wire.WireId != member.Wire.WireId)
                {
                    Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"XML codec '{codec.Id}': member '{prop.Name}' references wire '{member.Wire.WireId}' but codec '{valueCodec.Id}' converts through '{valueCap.Wire.WireId}'", [codec.Id, wire.Id]);
                }
            }

            // null is xsi:nil; on the server-write side also an absent member the server leaves out for null, or empty character content
            var nullable = valueUse.SemanticNullable;
            if (member.Nillable == true && !nullable)
            {
                Error(TisiliaCodes.NullabilityMismatch, "SV55", path, $"XML codec '{codec.Id}': member '{prop.Name}' may be written with xsi:nil but its value is not nullable", [codec.Id, wire.Id]);
            }
            else if (nullable && member.Nillable != true)
            {
                var stringText = kind == "text" && _index.Wires.TryGetValue(member.Wire.WireId, out var textWire) && textWire.Shape is XmlTextWire { GrammarId: var grammar } && grammar == Builtins.Grammar("xml-string");
                var readsNull = wire.Direction == WireDirection.ServerWrite && !repeated
                    && (stringText || (kind != "text" && member.Presence == Presence.Optional && prop.Presence == Presence.Required && member.Default is null));
                if (!readsNull)
                {
                    Error(TisiliaCodes.NullabilityMismatch, "SV55", path, $"XML codec '{codec.Id}': property '{prop.Name}' is nullable, but wire '{wire.Id}' has no XML form of null for it (xsi:nil, or a member the server leaves out for null)", [codec.Id, wire.Id]);
                }
            }
        }
    }

    private void CheckXmlRoot(XmlElementName root, string path, string opId)
    {
        if (!IsNcName(root.Name))
        {
            Error(TisiliaCodes.XmlWireInvalid, "SV55", JsonPointer.Append(path, "name"), $"operation '{opId}': root element '{root.Name}' is not an XML local name (NCName)", [opId]);
        }
    }

    /// <summary>An XML body uses an XML codec with the capability of its direction, at a root element named by the body.</summary>
    private void CheckXmlBodyUse(TypeUse use, string path, string opId, bool request)
    {
        if (!CheckTypeUse(use, path))
        {
            return;
        }

        var codec = _index.Codecs[use.CodecId];
        if (!IsXmlCodec(codec) || (request ? codec.Capabilities.Request : codec.Capabilities.Response) is null)
        {
            Error(TisiliaCodes.TextBodyRule, "SV29", JsonPointer.Append(path, "codecId"), $"operation '{opId}': an XML {(request ? "request" : "response")} body uses an XML codec with the {(request ? "request" : "response")} capability, not '{codec.Id}'", [opId, codec.Id]);
        }

        if (request && use.SemanticNullable)
        {
            Error(TisiliaCodes.NullabilityMismatch, "SV29", JsonPointer.Append(path, "semanticNullable"), $"operation '{opId}': an XML request body is never null; an optional body is left out", [opId]);
        }
    }
}
