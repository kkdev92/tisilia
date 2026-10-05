using System.Text.RegularExpressions;

namespace Tisilia.Generator.Building;

/// <summary>
/// How member documentation travels in a contract. Documentation entries target ids (SV03) and object or enum
/// members have none, so a type's description ends with a member list — the heading <see cref="MembersHeading"/>, then one line per
/// member: <c>- `name` — text</c>. It reads as Markdown anywhere; the generated client and the Explorer read it back per member.
/// </summary>
public static partial class DocumentationText
{
    /// <summary>The line that starts the member list.</summary>
    public const string MembersHeading = "**Members**";

    /// <summary>
    /// What the description of a deprecated operation, parameter or type, and the text of a deprecated member, starts with (<c>[Obsolete]</c>;
    /// OpenAPI's <c>deprecated</c>): the generated client marks the declaration <c>@deprecated</c> and the Explorer strikes it through.
    /// </summary>
    public const string DeprecatedMark = "**Deprecated.**";

    /// <summary>Whether a description or a member's text says its target is deprecated: it starts with <see cref="DeprecatedMark"/>.</summary>
    public static bool IsDeprecated(string? text) => text is not null && text.TrimStart().StartsWith(DeprecatedMark, StringComparison.Ordinal);

    /// <summary>One member's line.</summary>
    public static string MemberLine(string name, string text) => "- `" + name + "` — " + text;

    /// <summary>
    /// A type's description split into its own text and the member list (by member name), when it ends with one: the last heading line
    /// followed by member lines only. Anything else after the heading (a description that uses the heading for text of its own) keeps the
    /// whole description as text, so nothing is lost.
    /// </summary>
    public static (string Text, IReadOnlyDictionary<string, string> Members) Split(string description)
    {
        var lines = description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Array.FindLastIndex(lines, l => l.Trim() == MembersHeading);
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in heading < 0 ? [] : lines.Skip(heading + 1).Where(l => l.Trim().Length > 0))
        {
            if (MemberPattern().Match(line) is not { Success: true } m)
            {
                members.Clear();
                break;
            }

            members.TryAdd(m.Groups["name"].Value, m.Groups["text"].Value.Trim());
        }

        return members.Count == 0 ? (description.Trim(), members) : (string.Join("\n", lines.Take(heading)).Trim(), members);
    }

    [GeneratedRegex("^- `(?<name>[^`]+)` — (?<text>.*)$")]
    private static partial Regex MemberPattern();
}
