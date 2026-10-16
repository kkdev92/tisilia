using System.Text.RegularExpressions;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// The forms the contract schema requires of what a paired registration names (common.schema.json: ids, export names, TypeScript
/// names, versions). Export checks a registration when it reaches it, so a name the schema refuses is reported with the
/// registration's own field instead of the contract path it would end up at.
/// </summary>
internal static partial class PairedRegistrationRules
{
    private const string IdForm = "an id (a letter, then letters, digits and _ . : @ / -; at most 160 characters)";
    private const string NameForm = "a JavaScript identifier (letters, digits, _ and $; not starting with a digit)";

    /// <summary>The first field the schema would refuse, as a sentence that names it; null when every field has its form.</summary>
    public static string? Problem(PairedCodecRegistration registration)
    {
        foreach (var (field, value) in Ids(registration))
        {
            if (!IdPattern().IsMatch(value))
            {
                return $"{field} '{value}' is not {IdForm}";
            }
        }

        foreach (var (field, value) in Names(registration))
        {
            if (!NamePattern().IsMatch(value))
            {
                return $"{field} '{value}' is not {NameForm}";
            }
        }

        return VersionPattern().IsMatch(registration.ModuleVersion) ? null : $"ModuleVersion '{registration.ModuleVersion}' is not a semantic version (1.0.0, 1.0.0-alpha)";
    }

    private static IEnumerable<(string Field, string Value)> Ids(PairedCodecRegistration r)
    {
        yield return (nameof(r.ModelId), r.ModelId);
        yield return (nameof(r.ModuleId), r.ModuleId);
        foreach (var grammar in r.Grammars)
        {
            yield return ($"{nameof(r.Grammars)} id", grammar.Id);
        }

        if (r.ParameterGrammarId is { } parameterGrammar)
        {
            yield return (nameof(r.ParameterGrammarId), parameterGrammar);
        }

        if (r.Key is { } key)
        {
            yield return ($"{nameof(r.Key)}.{nameof(key.GrammarId)}", key.GrammarId);
            if (key.ResponseGrammarId is { } responseGrammar)
            {
                yield return ($"{nameof(r.Key)}.{nameof(key.ResponseGrammarId)}", responseGrammar);
            }
        }
    }

    private static IEnumerable<(string Field, string Value)> Names(PairedCodecRegistration r)
    {
        yield return (nameof(r.TsName), r.TsName);
        yield return (nameof(r.DotnetConverterExport), r.DotnetConverterExport);
        yield return (nameof(r.DomainRuleExport), r.DomainRuleExport);
        if (!r.BuiltinOracles)
        {
            // a registration with builtin oracles names no oracle export (the portable generator leaves both names empty)
            yield return (nameof(r.DotnetOracleExport), r.DotnetOracleExport);
            yield return (nameof(r.TypescriptOracleExport), r.TypescriptOracleExport);
        }
        foreach (var (direction, value) in new[] { (nameof(r.Request), r.Request), (nameof(r.Response), r.Response) })
        {
            if (value is null)
            {
                continue;
            }

            yield return ($"{direction}.{nameof(value.CodecExport)}", value.CodecExport);
            yield return ($"{direction}.{nameof(value.ValidateExport)}", value.ValidateExport);
            if (value.RequestInputExport is { } input)
            {
                yield return ($"{direction}.{nameof(value.RequestInputExport)}", input);
            }
        }

        if (r.Key is { } key)
        {
            yield return ($"{nameof(r.Key)}.{nameof(key.EncodeKeyExport)}", key.EncodeKeyExport);
            yield return ($"{nameof(r.Key)}.{nameof(key.DecodeKeyExport)}", key.DecodeKeyExport);
        }

        foreach (var grammar in r.Grammars)
        {
            yield return ($"{nameof(r.Grammars)} export of '{grammar.Id}'", grammar.ExportName);
        }
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_.:@/\-]{0,159}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[A-Za-z_$][A-Za-z0-9_$]*\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z")]
    private static partial Regex VersionPattern();
}
