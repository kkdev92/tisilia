using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tisilia.AspNetCore.SourceGenerator;

/// <summary>
/// Writes what the generator read into the compilation, as one internal class the exporter reads by reflection:
/// <c>Tisilia.Generated.TisiliaResponseInference</c> with the layout version (<c>Format</c>), the types it refers to
/// (<c>Types</c>, by <c>typeof</c>) and the operations as JSON (<c>Operations</c>) that refers to them by index. Nothing refers
/// to the class from code, so a friend assembly with a class of the same name does not conflict with it.
/// </summary>
internal static class Emitter
{
    /// <summary>The layout of the generated class; the exporter refuses another.</summary>
    public const int Format = 1;

    public const string ClassName = "Tisilia.Generated.TisiliaResponseInference";

    public static void Emit(SourceProductionContext context, ImmutableArray<OperationModel?> chained, ImmutableArray<OperationModel?> attributed, string? projectDirectory)
    {
        var operations = chained.Concat(attributed).OfType<OperationModel>().Distinct()
            .OrderBy(o => o.Id, StringComparer.Ordinal)
            .ThenBy(o => o.At.Path, StringComparer.Ordinal).ThenBy(o => o.At.Line).ThenBy(o => o.At.Column)
            .ToList();
        if (operations.Count == 0)
        {
            return;
        }

        context.AddSource("TisiliaResponseInference.g.cs", SourceText.From(Source(operations, projectDirectory), Encoding.UTF8));
    }

    public static string Source(IReadOnlyList<OperationModel> operations, string? projectDirectory)
    {
        var types = new List<string>();
        var json = new StringBuilder();
        json.Append('[');
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            json.Append(i == 0 ? "" : ",").Append('{');
            Property(json, "id", operation.Id, first: true);
            Property(json, "at", Spot(operation.At, projectDirectory));
            Property(json, "flavor", operation.Flavor);
            json.Append(",\"handler\":{");
            Property(json, "kind", operation.Handler.Kind, first: true);
            Index(json, "type", operation.Handler.ContainingType, types);
            Property(json, "name", operation.Handler.Name);
            json.Append(",\"parameters\":[");
            for (var p = 0; p < operation.Handler.Parameters.Length; p++)
            {
                var parameter = operation.Handler.Parameters[p];
                json.Append(p == 0 ? "" : ",").Append('{');
                Property(json, "name", parameter.Name, first: true);
                Index(json, "type", parameter.Type, types);
                json.Append('}');
            }

            json.Append(']');
            Index(json, "returns", operation.Handler.Returns, types);
            json.Append('}');
            Property(json, "failure", operation.Failure);
            Property(json, "failureAt", operation.FailureAt is { } failureAt ? Spot(failureAt, projectDirectory) : null);
            json.Append(",\"responses\":[");
            for (var r = 0; r < operation.Responses.Length; r++)
            {
                var response = operation.Responses[r];
                json.Append(r == 0 ? "" : ",").Append('{');
                Property(json, "kind", response.Kind, first: true);
                if (response.Status is { } status)
                {
                    json.Append(",\"status\":").Append(status.ToString(CultureInfo.InvariantCulture));
                }

                Index(json, "result", response.Result, types);
                Index(json, "body", response.Body, types);
                Property(json, "media", response.Media);
                json.Append('}');
            }

            json.Append("],\"writes\":[");
            for (var w = 0; w < operation.Writes.Length; w++)
            {
                var write = operation.Writes[w];
                json.Append(w == 0 ? "" : ",").Append("{\"status\":").Append(write.Status?.ToString(CultureInfo.InvariantCulture) ?? "null");
                Property(json, "helper", write.Helper);
                json.Append('}');
            }

            json.Append("],\"helpers\":[");
            for (var h = 0; h < operation.Helpers.Length; h++)
            {
                var helper = operation.Helpers[h];
                json.Append(h == 0 ? "" : ",").Append('{');
                Property(json, "name", helper.Name, first: true);
                json.Append(",\"parameters\":[");
                for (var p = 0; p < helper.Parameters.Length; p++)
                {
                    json.Append(p == 0 ? "" : ",");
                    var type = helper.Parameters[p];
                    json.Append(type == "?" ? "null" : TypeIndex(type, types).ToString(CultureInfo.InvariantCulture));
                }

                json.Append("]}");
            }

            json.Append("]}");
        }

        json.Append(']');

        var source = new StringBuilder();
        source.Append("// <auto-generated/>\n");
        source.Append("// The responses Tisilia read from the handlers of this project's operations, for `tisilia export`.\n");
        source.Append("#pragma warning disable\n\n");
        source.Append("namespace Tisilia.Generated\n{\n");
        source.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"Tisilia.AspNetCore.SourceGenerator\", \"")
            .Append(typeof(Emitter).Assembly.GetName().Version?.ToString() ?? "0.0.0.0").Append("\")]\n");
        source.Append("    internal static class TisiliaResponseInference\n    {\n");
        source.Append("        internal const int Format = ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append(";\n\n");
        source.Append("        internal static readonly global::System.Type[] Types = new global::System.Type[]\n        {\n");
        foreach (var type in types)
        {
            source.Append("            typeof(").Append(type).Append("),\n");
        }

        source.Append("        };\n\n");
        source.Append("        internal const string Operations = @\"").Append(json.ToString().Replace("\"", "\"\"")).Append("\";\n");
        source.Append("    }\n}\n");
        return source.ToString();
    }

    /// <summary>The file relative to the project, with forward slashes, then line and column: no machine path in the assembly.</summary>
    private static string Spot(SourceSpot spot, string? projectDirectory)
    {
        var path = spot.Path.Replace('\\', '/');
        var root = projectDirectory?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(root))
        {
            root = root!.EndsWith("/", StringComparison.Ordinal) ? root : root + "/";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length);
            }
            else
            {
                path = path.Substring(path.LastIndexOf('/') + 1);
            }
        }
        else
        {
            path = path.Substring(path.LastIndexOf('/') + 1);
        }

        return path + "(" + spot.Line.ToString(CultureInfo.InvariantCulture) + "," + spot.Column.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static void Index(StringBuilder json, string name, string? type, List<string> types)
    {
        json.Append(",\"").Append(name).Append("\":");
        json.Append(type is null ? "null" : TypeIndex(type, types).ToString(CultureInfo.InvariantCulture));
    }

    private static int TypeIndex(string type, List<string> types)
    {
        var index = types.IndexOf(type);
        if (index < 0)
        {
            types.Add(type);
            index = types.Count - 1;
        }

        return index;
    }

    private static void Property(StringBuilder json, string name, string? value, bool first = false)
    {
        json.Append(first ? "\"" : ",\"").Append(name).Append("\":");
        if (value is null)
        {
            json.Append("null");
            return;
        }

        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case < ' ':
                    json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }
}
