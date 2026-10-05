using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Tisilia.Generator.Building;

namespace Tisilia.AspNetCore.Export;

/// <summary>A model the export described: the CLR type behind it and, for an object, its members as the contract names them; for an enum, its fields.</summary>
public sealed record DocumentedType(string TypeId, Type ClrType, IReadOnlyList<DocumentedMember> Members);

/// <summary>
/// A member as the contract names it, its value type, where its documentation may sit (the property or field, and a record's constructor
/// parameter), and the serializer options that write it, which a [DefaultValue] or [AllowedValues] value is shown in.
/// </summary>
public sealed record DocumentedMember(string Name, Type ValueType, MemberInfo? Member, ICustomAttributeProvider? Parameter, JsonSerializerOptions? Json = null);

/// <summary>
/// The contract's documentation entries (display text, outside the semantic hash), gathered from what an ASP.NET Core
/// application already writes for OpenAPI: endpoint summaries and descriptions (<c>WithSummary</c>/<c>WithDescription</c>,
/// <c>[EndpointSummary]</c>/<c>[EndpointDescription]</c>), <c>[Description]</c> on parameters, types and members, response
/// descriptions (<c>[ProducesResponseType(Description = …)]</c>, <c>Produces(…)</c> metadata), XML documentation comments
/// (<c>GenerateDocumentationFile</c>), validation attributes (<c>[Range]</c>, <c>[StringLength]</c>, <c>[RegularExpression]</c> …),
/// <c>[DefaultValue]</c> and <c>[Obsolete]</c>. An attribute wins over a comment. Entries target ids of the contract only
/// (SV03); members and enum members have none, so they are listed in their type's description (<see cref="DocumentationText"/>).
/// </summary>
public sealed class ApiDocumentation
{
    private readonly XmlDocumentation _xml = new();
    private readonly Dictionary<string, (string Summary, string Description)> _entries = new(StringComparer.Ordinal);

    /// <summary>The entries, ordered by target id (the contract is canonical: the same application writes the same text).</summary>
    public IEnumerable<(string TargetId, string Summary, string Description)> Entries =>
        _entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.Summary, e.Value.Description));

    /// <summary>An operation: its summary and description, a deprecation, and what the body parameter says about the request body.</summary>
    public void Operation(string operationId, IEnumerable<object> metadata, MethodInfo? handler, ParameterInfo? bodyParameter)
    {
        var endpoint = metadata.ToList();
        var xml = handler is null ? null : _xml.Of(handler);
        var summary = endpoint.OfType<IEndpointSummaryMetadata>().LastOrDefault()?.Summary ?? XmlDocumentation.Section(xml, "summary");
        var parts = new List<string?>
        {
            endpoint.OfType<ObsoleteAttribute>().LastOrDefault() is { } obsolete ? Deprecated(obsolete) : null,
            endpoint.OfType<IEndpointDescriptionMetadata>().LastOrDefault()?.Description ?? XmlDocumentation.Section(xml, "remarks"),
        };
        if (bodyParameter is not null && (DescriptionOf(bodyParameter) ?? XmlDocumentation.Param(xml, bodyParameter.Name)) is { } body)
        {
            parts.Add("**Request body**: " + body);
        }

        Add(operationId, summary, Join(parts, "\n\n"));
    }

    /// <summary>A route, query or header parameter: its description, and the validation attributes on it as notes.</summary>
    public void Parameter(string parameterId, ApiParameterDescription parameter, Type valueType)
    {
        // MVC lists each property of a complex [FromQuery] model as a parameter of its own (its metadata names the container); minimal
        // APIs hand each property of an [AsParameters] type over as a ParameterInfo whose Member is the property
        PropertyInfo? property = null;
        if (parameter.ModelMetadata is { MetadataKind: ModelMetadataKind.Property, ContainerType: { } container, PropertyName: { } name })
        {
            property = container.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        }

        // the descriptor's ParameterInfo stands for this parameter unless it is MVC's container model; for an [AsParameters] member its
        // attributes are the property's and the constructor parameter's together (ApiExplorer's metadata names the property as well)
        var described = (parameter.ParameterDescriptor as IParameterInfoParameterDescriptor)?.ParameterInfo;
        var info = property is null || (described?.Member is PropertyInfo same && same.Name == property.Name && same.DeclaringType == property.DeclaringType) ? described : null;
        property ??= info?.Member as PropertyInfo;
        // MVC's model property of a positional record: attributes without a property: target sit on the constructor parameter
        var constructorParameter = property is not null && info is null ? ConstructorParameter(property) : null;
        var text = DescriptionOf(info) ?? DescriptionOf(property) ?? DescriptionOf(constructorParameter)
            ?? (property is not null ? XmlDocumentation.Section(_xml.Of(property), "summary") : null)
            ?? (info?.Member is MethodInfo method ? XmlDocumentation.Param(_xml.Of(method), info.Name) : null);
        // [Obsolete] cannot sit on a parameter, but on the property an [AsParameters] member or an MVC model property binds
        var obsolete = new ICustomAttributeProvider?[] { info, property }.SelectMany(p => p?.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: true) ?? []).OfType<ObsoleteAttribute>().FirstOrDefault();
        Add(parameterId, text, Join([obsolete is null ? null : Deprecated(obsolete), .. Notes([info, property, constructorParameter], valueType, withDefault: false)], " "));
    }

    private static ParameterInfo? ConstructorParameter(PropertyInfo property) =>
        property.DeclaringType?.GetConstructors().SelectMany(c => c.GetParameters())
            .FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase) && p.ParameterType == property.PropertyType);

    /// <summary>A response case: the description its metadata gives, else the XML comment for its status (or the returns comment for a value's 2xx).</summary>
    public void Response(string caseId, int status, string? description, MethodInfo? handler, bool returnedValue)
    {
        var xml = handler is null ? null : _xml.Of(handler);
        var text = description ?? XmlDocumentation.Response(xml, status) ?? (returnedValue ? XmlDocumentation.Section(xml, "returns") : null);
        Add(caseId, text, "");
    }

    /// <summary>Models: the type's description, its remarks and deprecation, and a line per documented member.</summary>
    public void Types(IEnumerable<DocumentedType> types)
    {
        foreach (var type in types)
        {
            if (_entries.ContainsKey(type.TypeId))
            {
                continue;
            }

            var xml = _xml.Of(type.ClrType);
            var lines = new List<string>();
            foreach (var member in type.Members)
            {
                var text = DescriptionOf(member.Member) ?? DescriptionOf(member.Parameter) ?? MemberComment(type.ClrType, member, xml);
                var notes = Notes([member.Member, member.Parameter], member.ValueType, withDefault: true, value => WireLiteral(value, member.ValueType, member.Json));
                var deprecated = member.Member?.GetCustomAttribute<ObsoleteAttribute>() is { } obsolete ? Deprecated(obsolete) : null;
                var line = Join([deprecated, text, .. notes], " ");
                if (line is not null && Listable(member.Name))
                {
                    lines.Add(DocumentationText.MemberLine(member.Name, OneLine(line)));
                }
            }

            var description = Join(
            [
                type.ClrType.GetCustomAttribute<ObsoleteAttribute>() is { } typeObsolete ? Deprecated(typeObsolete) : null,
                XmlDocumentation.Section(xml, "remarks"),
                lines.Count > 0 ? DocumentationText.MembersHeading + "\n\n" + string.Join("\n", lines) : null,
            ], "\n\n");
            Add(type.TypeId, DescriptionOf(type.ClrType) ?? XmlDocumentation.Section(xml, "summary"), description);
        }
    }

    /// <summary>An entry the application wrote itself (<see cref="TisiliaOptions.Documentation"/>): it replaces what was gathered for the target.</summary>
    public void Override(string targetId, string summary, string description) => _entries[targetId] = (summary.Trim(), description.Trim());

    private void Add(string targetId, string? summary, string? description)
    {
        summary = summary?.Trim() ?? "";
        description = description?.Trim() ?? "";
        if (summary.Length > 0 || description.Length > 0)
        {
            _entries.TryAdd(targetId, (summary, description));
        }
    }

    /// <summary>A member's XML comment: the property's or field's own, else (a record's positional property) the type's param comment.</summary>
    private string? MemberComment(Type owner, DocumentedMember member, XElement? typeXml)
    {
        if (member.Member is { } m && XmlDocumentation.Section(_xml.Of(m), "summary") is { } own)
        {
            return own;
        }

        return member.Parameter is ParameterInfo parameter ? XmlDocumentation.Param(typeXml ?? _xml.Of(owner), parameter.Name) : null;
    }

    internal static string? DescriptionOf(ICustomAttributeProvider? provider) =>
        provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).OfType<DescriptionAttribute>().Select(d => d.Description?.Trim()).FirstOrDefault(d => !string.IsNullOrEmpty(d));

    private static string Deprecated(ObsoleteAttribute obsolete) => DocumentationText.DeprecatedMark + (string.IsNullOrWhiteSpace(obsolete.Message) ? "" : " " + OneLine(obsolete.Message));

    /// <summary>
    /// The validation attributes (and a member's [DefaultValue]) as short sentences, in the order they are declared. Values are shown by
    /// <paramref name="literal"/> (a member's: as it is written in JSON), else by their C# name — what a route, query or header value is
    /// bound from (Enum.TryParse, not the JSON options).
    /// </summary>
    internal static List<string> Notes(IEnumerable<ICustomAttributeProvider?> providers, Type valueType, bool withDefault, Func<object?, string>? literal = null)
    {
        var show = literal ?? Literal;
        var text = IsText(valueType);
        string Count(int n) => n.ToString(CultureInfo.InvariantCulture) + " " + (text ? (n == 1 ? "character" : "characters") : (n == 1 ? "item" : "items"));
        var notes = new List<string>();
        var seen = new HashSet<Type>();
        foreach (var attribute in providers.Where(p => p is not null).SelectMany(p => p!.GetCustomAttributes(inherit: true)))
        {
            if (!seen.Add(attribute.GetType()))
            {
                continue;
            }

            string? note = attribute switch
            {
                RangeAttribute r => $"{Literal(r.Minimum)} {(r.MinimumIsExclusive ? "<" : "≤")} value {(r.MaximumIsExclusive ? "<" : "≤")} {Literal(r.Maximum)}.",
                StringLengthAttribute s => s.MinimumLength > 0 ? $"Length {s.MinimumLength}–{s.MaximumLength} characters." : $"At most {Count(s.MaximumLength)}.",
                LengthAttribute l => $"Length {l.MinimumLength}–{l.MaximumLength} {(text ? "characters" : "items")}.",
                MinLengthAttribute min => $"At least {Count(min.Length)}.",
                MaxLengthAttribute max when max.Length > 0 => $"At most {Count(max.Length)}.",
                RegularExpressionAttribute regex => $"Pattern: {Code(regex.Pattern)}.",
                AllowedValuesAttribute allowed => $"Allowed: {string.Join(", ", allowed.Values.Select(v => Code(show(v))))}.",
                DeniedValuesAttribute denied => $"Not allowed: {string.Join(", ", denied.Values.Select(v => Code(show(v))))}.",
                EmailAddressAttribute => "Format: email address.",
                UrlAttribute => "Format: URL.",
                PhoneAttribute => "Format: phone number.",
                CreditCardAttribute => "Format: credit card number.",
                Base64StringAttribute => "Format: base64.",
                DefaultValueAttribute d when withDefault => $"Default: {Code(show(d.Value))}.",
                _ => null,
            };
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    private static bool IsText(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is var t && (t == typeof(string) || t == typeof(char[]));

    private static string Literal(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => s,
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// A member's value as System.Text.Json writes the member — an enum by its wire name, under the naming policy — the way OpenAPI writes
    /// [DefaultValue]; a string without its quotes. Without the options, or for a value they cannot write, its C# literal.
    /// </summary>
    internal static string WireLiteral(object? value, Type memberType, JsonSerializerOptions? options)
    {
        if (options is null || value is null)
        {
            return Literal(value);
        }

        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;
        try
        {
            // [DefaultValue(1)] on an enum member means the member whose value is 1
            if (target.IsEnum && value is not Enum && value is not string)
            {
                value = Enum.ToObject(target, value);
            }

            var json = JsonSerializer.SerializeToElement(value, target.IsInstanceOfType(value) ? target : value.GetType(), options);
            return json.ValueKind == JsonValueKind.String ? json.GetString() is { Length: > 0 } s ? s : "\"\"" : json.GetRawText();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or JsonException)
        {
            return Literal(value);
        }
    }

    /// <summary>Inline code: backticks around the text, doubled (with spaces) when the text holds one.</summary>
    internal static string Code(string text) => text.Contains('`', StringComparison.Ordinal) ? "`` " + text + " ``" : "`" + text + "`";

    private static string? Join(IEnumerable<string?> parts, string separator)
    {
        var kept = parts.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s*\n\s*", " ").Trim();

    // a member line is `- \`name\` — text`: a name with a backtick or a line break could not be read back from it
    private static bool Listable(string name) => name.Length > 0 && !name.Contains('`', StringComparison.Ordinal) && !name.Contains('\n', StringComparison.Ordinal) && !name.Contains('\r', StringComparison.Ordinal);
}

/// <summary>
/// XML documentation files written by the compiler next to each assembly (<c>GenerateDocumentationFile</c>), read once per assembly and
/// turned into Markdown: <c>&lt;c&gt;</c> → inline code, <c>&lt;code&gt;</c> → a fenced block, <c>&lt;para&gt;</c> → a paragraph,
/// <c>&lt;list&gt;</c> → a list, <c>&lt;see&gt;</c> → the referenced name or link, <c>&lt;inheritdoc/&gt;</c> → the base member's comment.
/// </summary>
internal sealed class XmlDocumentation
{
    private readonly Dictionary<Assembly, Dictionary<string, XElement>> _files = [];

    public XElement? Of(MemberInfo member)
    {
        var element = File(member.Module.Assembly).GetValueOrDefault(DocumentationId.Of(member));
        return element?.Element("inheritdoc") is { } inherit ? Inherited(member, inherit) ?? element : element;
    }

    /// <summary>The comment &lt;inheritdoc/&gt; stands for: the cref's, or the base type's / overridden member's / interface member's.</summary>
    private XElement? Inherited(MemberInfo member, XElement inherit, int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }

        if (inherit.Attribute("cref")?.Value is { } cref)
        {
            return _files.Values.Select(f => f.GetValueOrDefault(cref)).FirstOrDefault(e => e is not null);
        }

        IEnumerable<MemberInfo> candidates = member switch
        {
            Type type => new[] { type.BaseType }.Concat(type.GetInterfaces()).OfType<Type>().Where(t => t != typeof(object)),
            MethodInfo method => new[] { method.GetBaseDefinition() }.Where(b => b != method).Concat(InterfaceMembers(method)),
            PropertyInfo property => new[] { property.DeclaringType?.BaseType?.GetProperty(property.Name) }.OfType<PropertyInfo>()
                .Concat(property.DeclaringType?.GetInterfaces().Select(i => i.GetProperty(property.Name)).OfType<PropertyInfo>() ?? []),
            _ => [],
        };
        foreach (var candidate in candidates)
        {
            if (File(candidate.Module.Assembly).GetValueOrDefault(DocumentationId.Of(candidate)) is { } found)
            {
                return found.Element("inheritdoc") is { } again ? Inherited(candidate, again, depth + 1) : found;
            }
        }

        return null;
    }

    private static IEnumerable<MemberInfo> InterfaceMembers(MethodInfo method)
    {
        if (method.DeclaringType is not { IsInterface: false } type)
        {
            yield break;
        }

        foreach (var i in type.GetInterfaces())
        {
            var map = type.GetInterfaceMap(i);
            for (var k = 0; k < map.TargetMethods.Length; k++)
            {
                if (map.TargetMethods[k] == method)
                {
                    yield return map.InterfaceMethods[k];
                }
            }
        }
    }

    private Dictionary<string, XElement> File(Assembly assembly)
    {
        if (_files.TryGetValue(assembly, out var members))
        {
            return members;
        }

        members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        try
        {
            var location = assembly.IsDynamic ? "" : assembly.Location;
            var path = location.Length == 0 ? null : Path.ChangeExtension(location, ".xml");
            if (path is not null && System.IO.File.Exists(path))
            {
                var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                foreach (var member in document.Root?.Element("members")?.Elements("member") ?? [])
                {
                    if (member.Attribute("name")?.Value is { } name)
                    {
                        members[name] = member;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // an unreadable file documents nothing; the export goes on without it
        }

        _files[assembly] = members;
        return members;
    }

    public static string? Section(XElement? member, string name) => Markdown(member?.Element(name));

    public static string? Param(XElement? member, string? name) =>
        name is null ? null : Markdown(member?.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name));

    public static string? Response(XElement? member, int status) =>
        Markdown(member?.Elements("response").FirstOrDefault(r => r.Attribute("code")?.Value == status.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The element's content as Markdown, or null when it says nothing.</summary>
    public static string? Markdown(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var text = new StringBuilder();
        Append(element.Nodes(), text);
        var normalized = Normalize(text.ToString());
        return normalized.Length == 0 ? null : normalized;
    }

    private static void Append(IEnumerable<XNode> nodes, StringBuilder text)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText t:
                    text.Append(Regex.Replace(t.Value, @"\s+", " "));
                    break;
                case XElement e:
                    Element(e, text);
                    break;
            }
        }
    }

    private static void Element(XElement e, StringBuilder text)
    {
        switch (e.Name.LocalName)
        {
            case "para":
                text.Append("\n\n");
                Append(e.Nodes(), text);
                text.Append("\n\n");
                break;
            case "br":
                text.Append('\n');
                break;
            case "c":
                text.Append(ApiDocumentation.Code(Regex.Replace(e.Value, @"\s+", " ").Trim()));
                break;
            case "code":
                text.Append("\n\n```\n").Append(Dedent(e.Value)).Append("\n```\n\n");
                break;
            case "b" or "strong":
                text.Append("**");
                Append(e.Nodes(), text);
                text.Append("**");
                break;
            case "i" or "em":
                text.Append('*');
                Append(e.Nodes(), text);
                text.Append('*');
                break;
            case "a":
                Link(e.Value, e.Attribute("href")?.Value, text);
                break;
            case "see" or "seealso":
                if (e.Attribute("langword")?.Value is { } word)
                {
                    text.Append(ApiDocumentation.Code(word));
                }
                else if (e.Attribute("href")?.Value is { } href)
                {
                    Link(e.Value, href, text);
                }
                else if (e.Value.Trim() is { Length: > 0 } label)
                {
                    text.Append(label);
                }
                else if (e.Attribute("cref")?.Value is { } cref)
                {
                    text.Append(ApiDocumentation.Code(CrefName(cref)));
                }

                break;
            case "paramref" or "typeparamref":
                text.Append(ApiDocumentation.Code(e.Attribute("name")?.Value ?? ""));
                break;
            case "list":
                List(e, text);
                break;
            case "inheritdoc":
                break;
            default:
                Append(e.Nodes(), text);
                break;
        }
    }

    private static void Link(string label, string? href, StringBuilder text)
    {
        label = Regex.Replace(label, @"\s+", " ").Trim();
        if (href is not null && (href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            text.Append('[').Append(label.Length > 0 ? label : href).Append("](").Append(href).Append(')');
        }
        else
        {
            text.Append(label.Length > 0 ? label : href);
        }
    }

    private static void List(XElement list, StringBuilder text)
    {
        var numbered = list.Attribute("type")?.Value == "number";
        text.Append("\n\n");
        var n = 0;
        foreach (var item in list.Elements("item"))
        {
            var term = Inline(item.Element("term"));
            var description = Inline(item.Element("description")) ?? (item.Element("term") is null ? Inline(item) : null);
            var line = term is not null && description is not null ? "**" + term + "** — " + description : term ?? description;
            if (line is not null)
            {
                text.Append(numbered ? (++n).ToString(CultureInfo.InvariantCulture) + ". " : "- ").Append(line).Append('\n');
            }
        }

        text.Append('\n');
    }

    private static string? Inline(XElement? element) => Markdown(element) is { } md ? Regex.Replace(md, @"\s*\n\s*", " ") : null;

    /// <summary>A cref as a short name: T:Ns.Type → Type, M:Ns.Type.Method(System.Int32) → Type.Method, generic arity dropped.</summary>
    private static string CrefName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        name = Regex.Replace(paren < 0 ? name : name[..paren], "`+[0-9]+", "");
        var parts = name.Split('.');
        return cref.StartsWith("T:", StringComparison.Ordinal) || parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
    }

    private static string Dedent(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.TrimEnd()));
    }

    /// <summary>Blank lines collapsed to one, spaces around line breaks dropped — except inside fenced code, which keeps its indentation.</summary>
    private static string Normalize(string text)
    {
        var output = new List<string>();
        var fenced = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Trim() == "```")
            {
                fenced = !fenced;
                output.Add("```");
                continue;
            }

            var line = fenced ? raw.TrimEnd() : raw.Trim();
            if (!fenced && line.Length == 0 && (output.Count == 0 || output[^1].Length == 0))
            {
                continue;
            }

            output.Add(line);
        }

        return string.Join("\n", output).Trim();
    }
}

/// <summary>
/// Documentation ids as the C# compiler writes them into the XML file (C# standard §D.4.2): T:/P:/F:/M:/E: and the fully qualified
/// name — enclosing types joined with '.', a generic type's own arity after a backtick, a generic method's after two, parameters as
/// their types (constructed generics in braces, type parameters as `n and ``n, arrays as [] or [0:,0:], by-reference with @).
/// </summary>
internal static class DocumentationId
{
    public static string Of(MemberInfo member) => member switch
    {
        Type type => "T:" + TypeName(type),
        PropertyInfo property => "P:" + TypeName(property.DeclaringType!) + "." + Escape(property.Name) + Parameters(property.GetIndexParameters()),
        FieldInfo field => "F:" + TypeName(field.DeclaringType!) + "." + Escape(field.Name),
        ConstructorInfo constructor => "M:" + TypeName(constructor.DeclaringType!) + "." + (constructor.IsStatic ? "#cctor" : "#ctor") + Parameters(constructor.GetParameters()),
        MethodInfo method => "M:" + TypeName(method.DeclaringType!) + "." + Escape(method.Name) + (method.IsGenericMethod ? "``" + method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture) : "")
            + Parameters(method.GetParameters()) + (method.Name is "op_Implicit" or "op_Explicit" ? "~" + ParameterType(method.ReturnType) : ""),
        EventInfo e => "E:" + TypeName(e.DeclaringType!) + "." + Escape(e.Name),
        _ => "",
    };

    /// <summary>A type as the T: id names its definition: namespace, then each enclosing type, each with its own arity.</summary>
    public static string TypeName(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            type = type.GetGenericTypeDefinition();
        }

        var outer = type.DeclaringType is { } declaring ? TypeName(declaring) + "." : type.Namespace is { Length: > 0 } ns ? ns + "." : "";
        return outer + Escape(type.Name);
    }

    private static string Parameters(ParameterInfo[] parameters) =>
        parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(p => ParameterType(p.ParameterType))) + ")";

    public static string ParameterType(Type type)
    {
        if (type.IsByRef)
        {
            return ParameterType(type.GetElementType()!) + "@";
        }

        if (type.IsPointer)
        {
            return ParameterType(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            var element = ParameterType(type.GetElementType()!);
            return type.IsSZArray ? element + "[]" : element + "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]";
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is not null ? "``" : "`") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        }

        if (!type.IsGenericType)
        {
            return TypeName(type);
        }

        // a constructed type: each enclosing level takes its own number of arguments, in braces after its name
        var arguments = type.GetGenericArguments();
        var chain = new List<Type>();
        for (var level = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition(); level is not null; level = level.DeclaringType)
        {
            chain.Insert(0, level);
        }

        var text = new StringBuilder();
        var used = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            var name = chain[i].Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            var own = tick < 0 ? 0 : int.Parse(name[(tick + 1)..], CultureInfo.InvariantCulture);
            if (i == 0)
            {
                text.Append(chain[i].Namespace is { Length: > 0 } ns ? ns + "." : "");
            }
            else
            {
                text.Append('.');
            }

            text.Append(Escape(tick < 0 ? name : name[..tick]));
            if (own > 0)
            {
                text.Append('{').Append(string.Join(",", arguments.Skip(used).Take(own).Select(ParameterType))).Append('}');
                used += own;
            }
        }

        return text.ToString();
    }

    // a name with periods (an explicit interface implementation) writes them as '#'
    private static string Escape(string name) => name.Replace('.', '#');
}
