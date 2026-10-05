using System.Text.Json;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Tool;

/// <summary>Human output on stdout, or machine-readable JSON only on stdout with logs on stderr.</summary>
public static class Output
{
    /// <param name="summary">Human output for the result (one or a few lines); without it the result is printed as JSON. `--format json` always prints the result.</param>
    /// <param name="status">The human status line when the command failed without an error diagnostic (check: OUT OF DATE); otherwise OK or FAILED.</param>
    public static void Report(DiagnosticBag bag, bool json, object? result, string? summary = null, string? status = null)
    {
        if (json)
        {
            var payload = new
            {
                ok = !bag.HasErrors,
                diagnostics = bag.Items.Select(d => new
                {
                    code = d.Code,
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    rule = d.Rule,
                    message = d.Message,
                    path = d.Path,
                    file = d.File,
                    relatedIds = d.RelatedIds,
                    fix = d.Fix,
                }),
                result,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, TisiliaJson.IndentedOptions));
            return;
        }

        foreach (var d in bag.Items)
        {
            Console.Out.WriteLine(d.ToString());
        }

        if (summary is not null)
        {
            Console.Out.WriteLine(summary);
        }
        else if (result is not null)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, TisiliaJson.IndentedOptions));
        }

        Console.Out.WriteLine(bag.HasErrors ? $"FAILED: {bag.ErrorCount} error(s)" : status ?? "OK");
    }
}
