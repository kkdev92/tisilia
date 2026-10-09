using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

/// <summary>A contract that passed JSON parsing, SV01 control-number checks and structural schema validation.</summary>
public sealed record LoadedContract(JsonObject Root, ContractDocument Document, string? File);

/// <summary>The first validation stages: JSON syntax, JSON Schema, then typed materialization.</summary>
public static class ContractLoader
{
    public static LoadedContract? Load(string text, DiagnosticBag diagnostics, string? file = null)
    {
        var node = TisiliaSchemas.ParseStrict(text, diagnostics);
        if (node is null || diagnostics.HasErrors)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            diagnostics.Error(TisiliaCodes.SchemaViolation, "SV01", "", "contract root must be an object");
            return null;
        }

        // Format/version first so that an old draft is reported as such rather than as a pile of schema errors.
        var format = root["format"]?.GetValue<string>();
        var version = root["version"]?.GetValue<string>();
        if (format != TisiliaJson.Formats.Contract || version != TisiliaJson.ContractVersion)
        {
            diagnostics.Error(TisiliaCodes.FormatOrVersion, "SV01", "/format",
                $"expected format '{TisiliaJson.Formats.Contract}' version '{TisiliaJson.ContractVersion}' but found '{format}' / '{version}'",
                fix: "re-export with matching Tisilia tooling and regenerate using its runtime; other contract versions are not supported. Documents are not silently converted");
            return null;
        }

        if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Contract, root, diagnostics))
        {
            return null;
        }

        ContractDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ContractDocument>(root, TisiliaJson.Options);
        }
        catch (JsonException e)
        {
            diagnostics.Error(TisiliaCodes.SchemaViolation, "SV01", e.Path ?? "", "contract could not be materialized: " + e.Message);
            return null;
        }

        if (document is null)
        {
            diagnostics.Error(TisiliaCodes.SchemaViolation, "SV01", "", "contract could not be materialized");
            return null;
        }

        return new LoadedContract(root, document, file);
    }

    public static LoadedContract? LoadFile(string path, DiagnosticBag diagnostics)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            diagnostics.Add(new Diagnostic { Code = TisiliaCodes.ConfigInvalid, Severity = DiagnosticSeverity.Error, Message = "cannot read contract: " + e.Message, File = path });
            return null;
        }

        return Load(text, diagnostics, path);
    }
}
