using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

/// <summary>Kinds of normative control documents.</summary>
public enum DocumentKind
{
    Contract,
    Config,
    ClosureRecord,
    CodecManifest,
    ConformanceEvidence,
    GenerationManifest,
    HydrationEnvelope,
    PortableDefinition,
    PortableProject,
    RequestIdentityRecord,
    RunnerMessage,
    /// <summary>The input of <c>tisilia explorer build</c>.</summary>
    ExplorerRegistry,
}

/// <summary>
/// The JSON Schemas (draft 2020-12) of Tisilia's documents, embedded in this assembly and evaluated by
/// <see cref="SchemaEvaluator"/>. The reserved <c>schemas.tisilia.invalid</c> identifiers are never fetched: every reference
/// resolves against the embedded copies.
/// </summary>
public sealed class TisiliaSchemas
{
    private static readonly Lazy<TisiliaSchemas> LazyInstance = new(() => new TisiliaSchemas());

    private readonly Dictionary<DocumentKind, Uri> _schemas = new();
    private readonly SchemaEvaluator _evaluator;

    private TisiliaSchemas()
    {
        var assembly = typeof(TisiliaSchemas).Assembly;
        var documents = new List<(Uri, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Tisilia.Schemas.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var fileName = name.Substring("Tisilia.Schemas.".Length);
            var text = reader.ReadToEnd();
            var id = new Uri(JsonNode.Parse(text)!["$id"]!.GetValue<string>());
            documents.Add((id, text));
            if (KindFromFileName(fileName) is { } kind)
            {
                _schemas[kind] = id;
            }
        }

        _evaluator = SchemaEvaluator.Load(documents);
        if (_schemas.Count != 12)
        {
            throw new InvalidOperationException("embedded schema set is incomplete: " + _schemas.Count);
        }
    }

    public static TisiliaSchemas Instance => LazyInstance.Value;

    public static string FormatOf(DocumentKind kind) => kind switch
    {
        DocumentKind.Contract => TisiliaJson.Formats.Contract,
        DocumentKind.Config => TisiliaJson.Formats.Config,
        DocumentKind.ClosureRecord => TisiliaJson.Formats.ClosureRecord,
        DocumentKind.CodecManifest => TisiliaJson.Formats.CodecManifest,
        DocumentKind.ConformanceEvidence => TisiliaJson.Formats.ConformanceEvidence,
        DocumentKind.GenerationManifest => TisiliaJson.Formats.GenerationManifest,
        DocumentKind.HydrationEnvelope => TisiliaJson.Formats.HydrationEnvelope,
        DocumentKind.PortableDefinition => TisiliaJson.Formats.PortableDefinition,
        DocumentKind.PortableProject => TisiliaJson.Formats.PortableProject,
        DocumentKind.RequestIdentityRecord => TisiliaJson.Formats.RequestIdentityRecord,
        DocumentKind.RunnerMessage => TisiliaJson.Formats.RunnerMessage,
        DocumentKind.ExplorerRegistry => TisiliaJson.Formats.ExplorerRegistry,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DocumentKind? KindFromFileName(string fileName) => fileName switch
    {
        "contract.schema.json" => DocumentKind.Contract,
        "config.schema.json" => DocumentKind.Config,
        "closure-record.schema.json" => DocumentKind.ClosureRecord,
        "codec-manifest.schema.json" => DocumentKind.CodecManifest,
        "conformance-evidence.schema.json" => DocumentKind.ConformanceEvidence,
        "generation-manifest.schema.json" => DocumentKind.GenerationManifest,
        "hydration-envelope.schema.json" => DocumentKind.HydrationEnvelope,
        "portable-definition.schema.json" => DocumentKind.PortableDefinition,
        "portable-project.schema.json" => DocumentKind.PortableProject,
        "request-identity-record.schema.json" => DocumentKind.RequestIdentityRecord,
        "runner-message.schema.json" => DocumentKind.RunnerMessage,
        "explorer-registry.schema.json" => DocumentKind.ExplorerRegistry,
        _ => null,
    };

    /// <summary>
    /// Parses text with strict RFC 8259 reading (no comments, no trailing commas, duplicate names rejected) and checks
    /// SV01's control-number rule: every JSON number must be an integer within ±(2^53−1).
    /// </summary>
    public static JsonNode? ParseStrict(string text, DiagnosticBag diagnostics)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 256,
            });
        }
        catch (JsonException e)
        {
            diagnostics.Error(TisiliaCodes.InvalidJson, "SV01", e.Path ?? "", "document is not valid JSON: " + e.Message);
            return null;
        }

        if (node is null)
        {
            diagnostics.Error(TisiliaCodes.InvalidJson, "SV01", "", "document is JSON null");
            return null;
        }

        CheckControlNumbers(node, "", diagnostics);
        return node;
    }

    private static void CheckControlNumbers(JsonNode? node, string pointer, DiagnosticBag diagnostics)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    CheckControlNumbers(child, JsonPointer.Append(pointer, name), diagnostics);
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    CheckControlNumbers(arr[i], JsonPointer.Append(pointer, i), diagnostics);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                var raw = value.ToJsonString();
                if (!IsSafeInteger(raw))
                {
                    diagnostics.Error(TisiliaCodes.UnsafeControlNumber, "SV01", pointer,
                        $"control number '{raw}' must be an integer within ±(2^53-1); business numbers belong in jsonValue.text strings");
                }

                break;
        }
    }

    public static bool IsSafeInteger(string lexeme)
    {
        if (lexeme.Length == 0)
        {
            return false;
        }

        var start = lexeme[0] == '-' ? 1 : 0;
        if (lexeme.Length == start)
        {
            return false;
        }

        for (var i = start; i < lexeme.Length; i++)
        {
            if (lexeme[i] is < '0' or > '9')
            {
                return false;
            }
        }

        if (lexeme.Length - start > 1 && lexeme[start] == '0')
        {
            return false;
        }

        return long.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && Math.Abs(v) <= 9007199254740991L;
    }

    /// <summary>Structural validation against the bundled schema for <paramref name="kind"/>. Adds one TIS1002 per violation.</summary>
    public bool ValidateStructure(DocumentKind kind, JsonNode node, DiagnosticBag diagnostics)
    {
        var element = JsonSerializer.SerializeToElement(node, TisiliaJson.Plain);
        var errors = _evaluator.Evaluate(_schemas[kind], element);
        foreach (var error in errors)
        {
            diagnostics.Error(TisiliaCodes.SchemaViolation, "SV01", error.InstanceLocation, $"schema violation ({error.Keyword}): {error.Message}");
        }

        return errors.Count == 0;
    }
}
