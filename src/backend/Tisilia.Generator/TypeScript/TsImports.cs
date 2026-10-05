using System.Text;
using System.Text.RegularExpressions;

namespace Tisilia.Generator.TypeScript;

/// <summary>
/// Drops the import specifiers a generated file does not use, so that generated code compiles under <c>noUnusedLocals</c>
/// (on in the create-vite TypeScript templates) and <c>verbatimModuleSyntax</c>. Only the generator's own import forms are
/// handled — one statement per line at the top of the file: <c>import [type] { a, b as c } from "x";</c> and
/// <c>import [type] * as ns from "x";</c>.
/// </summary>
public static partial class TsImports
{
    [GeneratedRegex("""^import (?<type>type )?\{ (?<names>[^}]*) \} from (?<from>"[^"]*");$""")]
    private static partial Regex NamedImport();

    [GeneratedRegex("""^import (?<type>type )?\* as (?<name>[A-Za-z_$][A-Za-z0-9_$]*) from (?<from>"[^"]*");$""")]
    private static partial Regex NamespaceImport();

    // the first identifier of a line followed by ":" is a property key (an interface member, an enum member, an object literal entry)
    [GeneratedRegex("""^\s*(?:readonly\s+)?[\p{L}\p{Nl}_$][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}_$]*\??\s*:""")]
    private static partial Regex LeadingKey();

    public static string Prune(string content)
    {
        var lines = content.Split('\n');
        var importLines = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("import ", StringComparison.Ordinal))
            {
                importLines.Add(i);
            }
        }

        if (importLines.Count == 0)
        {
            return content;
        }

        var used = UsedIdentifiers(string.Join('\n', lines.Where((_, i) => !importLines.Contains(i))));
        foreach (var i in importLines)
        {
            if (NamedImport().Match(lines[i]) is { Success: true } named)
            {
                var kept = named.Groups["names"].Value.Split(", ").Where(specifier => used.Contains(LocalName(specifier))).ToList();
                lines[i] = kept.Count == 0 ? null! : $"import {named.Groups["type"].Value}{{ {string.Join(", ", kept)} }} from {named.Groups["from"].Value};";
            }
            else if (NamespaceImport().Match(lines[i]) is { Success: true } ns && !used.Contains(ns.Groups["name"].Value))
            {
                lines[i] = null!;
            }
        }

        return string.Join('\n', lines.Where(l => l is not null));
    }

    private static string LocalName(string specifier)
    {
        var alias = specifier.IndexOf(" as ", StringComparison.Ordinal);
        return alias < 0 ? specifier : specifier[(alias + " as ".Length)..];
    }

    /// <summary>Identifiers the code refers to: outside comments and string literals, not a member after <c>.</c>, not a leading property key.</summary>
    public static HashSet<string> UsedIdentifiers(string code)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in CodeOnly(code).Split('\n'))
        {
            var start = LeadingKey().Match(line) is { Success: true } key ? key.Length : 0;
            for (var i = start; i < line.Length; i++)
            {
                if (!IsIdentifierStart(line[i]))
                {
                    continue;
                }

                var end = i + 1;
                while (end < line.Length && IsIdentifierPart(line[end]))
                {
                    end++;
                }

                var member = i > 0 && line[i - 1] == '.' && !(i > 2 && line[i - 2] == '.' && line[i - 3] == '.');
                if (!member && !(i > 0 && char.IsAsciiDigit(line[i - 1])))
                {
                    used.Add(line[i..end]);
                }

                i = end - 1;
            }
        }

        return used;
    }

    private static bool IsIdentifierStart(char c) => c is '_' or '$' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) => c is '_' or '$' || char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    /// <summary>The code with comments and string contents blanked (line breaks kept); template literal substitutions stay code.</summary>
    public static string CodeOnly(string text)
    {
        var sb = new StringBuilder(text.Length);
        var templates = new Stack<int>(); // brace depth at which each open template substitution returns to its template
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i += 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                i = SkipString(text, i, c, sb);
                continue;
            }

            if (c == '`' || (c == '}' && templates.Count > 0 && templates.Peek() == depth))
            {
                if (c == '}')
                {
                    templates.Pop();
                }

                // template text up to its end or the next substitution
                i++;
                while (i < text.Length && text[i] != '`' && !(text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{'))
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                if (i < text.Length && text[i] == '$')
                {
                    templates.Push(depth);
                    i += 2;
                }
                else
                {
                    i++;
                }

                sb.Append(' ');
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipString(string text, int i, char quote, StringBuilder sb)
    {
        i++;
        while (i < text.Length && text[i] != quote && text[i] != '\n')
        {
            if (text[i] == '\\')
            {
                i++;
            }

            i++;
        }

        sb.Append(' ');
        return i + 1;
    }
}
