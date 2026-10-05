using System.Globalization;
using System.Text;
using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Portable;

/// <summary>Naming and scalar tables shared by the C# and TypeScript portable generators.</summary>
public static class PortableModel
{
    public const string TsArtifactSuffix = ".portable.js";

    /// <summary>C# namespace of a project: dotted id segments in PascalCase (<c>demo.portable</c> → <c>Demo.Portable</c>).</summary>
    public static string Namespace(string projectId) => string.Join(".", projectId.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Pascal));

    public static string Pascal(string raw)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        var s = sb.ToString();
        return s.Length == 0 || char.IsAsciiDigit(s[0]) ? "P" + s : s;
    }

    public static string Camel(string raw)
    {
        var p = Pascal(raw);
        return char.ToLowerInvariant(p[0]) + p[1..];
    }

    private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    public static string CsIdentifier(string raw)
    {
        var p = Pascal(raw);
        return CsKeywords.Contains(p) ? "@" + p : p;
    }

    public static string ScalarName(string primitiveId) => primitiveId.StartsWith(Builtins.Prefix, StringComparison.Ordinal) && primitiveId.EndsWith("@0.3", StringComparison.Ordinal) ? primitiveId[Builtins.Prefix.Length..^4] : primitiveId;

    public static string? CsScalarType(string scalar) => scalar switch
    {
        "string" => "string",
        "boolean" => "bool",
        "char" => "char",
        "guid" => "System.Guid",
        "bytes" => "byte[]",
        "json-value" => "System.Text.Json.JsonElement",
        "int8" => "sbyte",
        "uint8" => "byte",
        "int16" => "short",
        "uint16" => "ushort",
        "int32" => "int",
        "uint32" => "uint",
        "int64" => "long",
        "uint64" => "ulong",
        "decimal" => "decimal",
        "float32" => "float",
        "float64" => "double",
        "date-only" => "System.DateOnly",
        "time-only" => "System.TimeOnly",
        "datetime-utc" or "datetime-unspecified" or "datetime-local-wire" => "System.DateTime",
        "datetime-offset" => "System.DateTimeOffset",
        "duration" => "System.TimeSpan",
        _ => null,
    };

    public static bool IsCsValueType(string scalar) => scalar is not ("string" or "bytes");

    /// <summary>Every builtin scalar: the self-contained TypeScript module carries exact parsers/writers for all of them (ported from the runtime primitives).</summary>
    public static readonly string[] TsSupportedScalars = Builtins.ScalarNames;

    /// <summary>TypeScript domain type of a scalar; object-shaped domains (decimal, dates, durations, JSON values) are the runtime's kind-tagged records, declared in the module's .d.ts under a <c>Portable</c> prefix.</summary>
    public static string TsScalarType(string scalar) => scalar switch
    {
        "string" or "guid" or "char" => "string",
        "boolean" => "boolean",
        "bytes" => "Uint8Array",
        "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "float32" or "float64" => "number",
        "int64" or "uint64" => "bigint",
        "decimal" => "PortableDecimal",
        "json-value" => "PortableJsonValue",
        "date-only" => "PortableDateOnly",
        "time-only" => "PortableTimeOnly",
        "datetime-utc" => "PortableDateTimeUtc",
        "datetime-unspecified" => "PortableDateTimeUnspecified",
        "datetime-local-wire" => "PortableDateTimeLocalWire",
        "datetime-offset" => "PortableDateTimeOffset",
        "duration" => "PortableDuration",
        _ => "unknown",
    };

    /// <summary>The union model a variant object model belongs to and its tag, or null when the model is not a variant.</summary>
    public static (Model Union, string Tag)? VariantOf(LoadedPortableProject project, string modelId)
    {
        foreach (var model in project.Models.Values)
        {
            if (model.Shape is UnionShape union && union.Variants.FirstOrDefault(v => v.Use.TypeId == modelId) is { } variant)
            {
                return (model, variant.Tag);
            }
        }

        return null;
    }

    /// <summary>The discriminator member name of a union model: recorded from its tagged-union programs, else the first property of its first variant.</summary>
    public static string? DiscriminatorOf(LoadedPortableProject project, string unionModelId)
    {
        if (project.Discriminators.TryGetValue(unionModelId, out var recorded))
        {
            return recorded;
        }

        return project.Models.GetValueOrDefault(unionModelId)?.Shape is UnionShape u && u.Variants.Count > 0 && project.Models.GetValueOrDefault(u.Variants[0].Use.TypeId)?.Shape is ObjectShape os && os.Properties.Count > 0
            ? os.Properties[0].Name
            : null;
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>Resolves the model of a definition direction's domain, or null for std scalar domains.</summary>
    public static Model? DomainModel(LoadedPortableProject project, string domainTypeId) => project.Models.GetValueOrDefault(domainTypeId);

    /// <summary>The definition owning a model id (its request/response domain), searching the import closure.</summary>
    public static LoadedDefinition? DefinitionOfModel(LoadedPortableProject project, string modelId)
        => project.DefinitionsById.Values.FirstOrDefault(d => (d.Definition.Request?.DomainTypeId ?? d.Definition.Response?.DomainTypeId) == modelId || d.Definition.Response?.DomainTypeId == modelId);

    /// <summary>The definition a ref points at (request/response domain of that definition).</summary>
    public static LoadedDefinition? Ref(LoadedPortableProject project, string definitionId) => project.DefinitionsById.GetValueOrDefault(definitionId);

    public static void Unsupported(DiagnosticBag bag, string path, string message, string definitionId)
        => bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, message, [definitionId]);
}
