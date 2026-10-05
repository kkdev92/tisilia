using System.Text.RegularExpressions;
using Tisilia.Contract;

namespace Tisilia.Generator.Validation;

/// <summary>Deterministic display and validation of resolved routes. Parsing is for the explicit ContractBuilder API only.</summary>
public static partial class RoutePlans
{
    public static string Display(RoutePlan plan) => "/" + string.Join("/", plan.Segments.Select(s => string.Concat(s.Parts.Select(p => p switch
    {
        RouteLiteral l => Escape(l.Value),
        RouteSeparator s => Escape(s.Value),
        RouteParameter v => "{" + (v.CatchAll == CatchAllKind.PreserveSlashes ? "**" : v.CatchAll == CatchAllKind.EncodeSlashes ? "*" : "")
            + v.Name + string.Concat(v.Policies.Select(p => ":" + Escape(p))) + (v.HasDefault ? "=" + Escape(v.DefaultValue ?? "") : "") + (v.Optional ? "?" : "") + "}",
        _ => throw new ArgumentException("Unknown route part"),
    }))));

    private static string Escape(string s) => s.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Variables();

    public static RoutePlan Parse(string route, IReadOnlyList<Parameter> parameters)
    {
        var escaped = route.TrimStart('/').Replace("{{", "\u0001", StringComparison.Ordinal).Replace("}}", "\u0002", StringComparison.Ordinal);
        var segments = new List<RouteSegment>();
        var parts = new List<RoutePart>();
        void Literal(string text)
        {
            var pieces = text.Split('/');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) { segments.Add(new RouteSegment { Parts = parts.ToArray() }); parts.Clear(); }
                if (pieces[i].Length > 0) { parts.Add(new RouteLiteral { Value = Unescape(pieces[i]) }); }
            }
        }
        var offset = 0;
        foreach (Match match in Variables().Matches(escaped))
        {
            Literal(escaped[offset..match.Index]);
            var text = Unescape(match.Groups[1].Value);
            var v = HttpRules.ParseRouteVariables("{" + Escape(text) + "}").Single();
            var parameter = parameters.SingleOrDefault(p => p.Location == ParameterLocation.Path && p.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase));
            if (v.Optional && parts.LastOrDefault() is RouteLiteral { Value: var literal } && literal.EndsWith('.'))
            {
                parts.RemoveAt(parts.Count - 1);
                if (literal.Length > 1) { parts.Add(new RouteLiteral { Value = literal[..^1] }); }
                parts.Add(new RouteSeparator { Value = "." });
            }
            parts.Add(new RouteParameter
            {
                ParameterId = parameter?.Id ?? "unresolved." + v.Name,
                Name = v.Name,
                Optional = v.Optional,
                HasDefault = v.HasDefault,
                DefaultValue = v.DefaultValue,
                CatchAll = !v.CatchAll ? CatchAllKind.None : text.StartsWith("**", StringComparison.Ordinal) ? CatchAllKind.PreserveSlashes : CatchAllKind.EncodeSlashes,
                Policies = v.Constraint is null ? [] : [v.Constraint],
            });
            offset = match.Index + match.Length;
        }
        Literal(escaped[offset..]);
        if (parts.Count > 0) { segments.Add(new RouteSegment { Parts = parts.ToArray() }); }
        return new RoutePlan { Segments = segments };
    }

    private static string Unescape(string s) => s.Replace('\u0001', '{').Replace('\u0002', '}');

    public static IEnumerable<string> Errors(RoutePlan plan, IReadOnlyList<Parameter> parameters)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Segments.Count; i++)
        {
            var parts = plan.Segments[i].Parts;
            if (parts.Count == 0) { yield return "empty route segment"; }
            for (var j = 0; j < parts.Count; j++)
            {
                if (parts[j] is RouteParameter p)
                {
                    var parameter = parameters.SingleOrDefault(x => x.Id == p.ParameterId && x.Location == ParameterLocation.Path);
                    if (parameter is null || !parameter.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) { yield return $"unresolved path parameter '{p.ParameterId}'"; }
                    if (!used.Add(p.ParameterId)) { yield return $"duplicate route parameter '{p.ParameterId}'"; }
                    if (p.Name.Length == 0 || p.Name.IndexOfAny(['{', '}', '/', '?', '*']) >= 0) { yield return "invalid route parameter name"; }
                    if (j > 0 && parts[j - 1] is RouteParameter) { yield return "adjacent route parameters"; }
                    if (p.CatchAll != CatchAllKind.None && (parts.Count != 1 || i != plan.Segments.Count - 1 || p.Optional)) { yield return "catch-all must be a final simple segment without '?'"; }
                    if (p.Optional && (p.HasDefault || (parts.Count > 1 && (j != parts.Count - 1 || j == 0 || parts[j - 1] is not RouteSeparator { Value: "." })))) { yield return "invalid optional parameter / separator / default combination"; }
                    if (!p.HasDefault && p.DefaultValue is not null) { yield return "defaultValue without hasDefault"; }
                    if (parameter?.Presence == Presence.Optional && !(p.Optional || p.HasDefault || p.CatchAll != CatchAllKind.None)) { yield return "required route parameter cannot be omitted"; }
                }
                else
                {
                    var text = parts[j] switch { RouteLiteral l => l.Value, RouteSeparator s => s.Value, _ => "" };
                    if (text.Length == 0 || text.IndexOfAny(['/', '\\', '?', '#']) >= 0 || HttpRules.ContainsControlCharacters(text)) { yield return "unsafe route literal"; }
                    if (parts[j] is RouteSeparator && (text != "." || j == 0 || j != parts.Count - 2 || parts[j + 1] is not RouteParameter { Optional: true })) { yield return "invalid optional separator"; }
                }
            }
        }
        foreach (var parameter in parameters.Where(p => p.Location == ParameterLocation.Path && !used.Contains(p.Id))) { yield return $"path parameter '{parameter.Id}' is absent from route plan"; }
    }
}
