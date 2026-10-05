using System.Text.RegularExpressions;

namespace Tisilia.Generator.Validation;

/// <summary>Route template, media type and header rules shared by validation, generation and the exporter.</summary>
public static partial class HttpRules
{
    /// <summary>Header names a browser fetch cannot set (WHATWG Fetch "forbidden request-header", checked 2026-09-30) plus the Tisilia credential headers.</summary>
    public static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-charset", "accept-encoding", "access-control-request-headers", "access-control-request-method", "connection",
        "content-length", "cookie", "cookie2", "date", "dnt", "expect", "host", "keep-alive", "origin", "referer", "set-cookie",
        "te", "trailer", "transfer-encoding", "upgrade", "via",
        // credential-bearing headers are supplied only by the credential provider
        "authorization", "proxy-authorization",
    };

    /// <summary>Hop-by-hop headers that an SSR forwarder must never copy (RFC 9110 §7.6.1).</summary>
    public static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "te", "trailer", "upgrade", "host", "content-length",
    };

    public static bool IsForbiddenRequestHeader(string name)
        => ForbiddenRequestHeaders.Contains(name)
           || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase)
           || name.Equals("x-http-method", StringComparison.OrdinalIgnoreCase)
           || name.Equals("x-http-method-override", StringComparison.OrdinalIgnoreCase)
           || name.Equals("x-method-override", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 9110 token: 1*tchar.</summary>
    public static bool IsHttpToken(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c > 0x7e || c < 0x21)
            {
                return false;
            }

            if ("\"(),/:;<=>?@[\\]{}".Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ContainsControlCharacters(string s)
    {
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return true;
            }
        }

        return false;
    }

    public sealed record RouteVariable(string Name, string? Constraint, bool Optional, bool CatchAll, bool HasDefault, string? DefaultValue = null);

    [GeneratedRegex(@"\{(?<body>[^{}]*)\}")]
    private static partial Regex RouteVariableRegex();

    // `{{` and `}}` are escaped braces, in literal text and inside a parameter (RoutePatternParser): set aside before matching
    private static string EscapeBraces(string route) => route.Replace("{{", "\u0001", StringComparison.Ordinal).Replace("}}", "\u0002", StringComparison.Ordinal);

    /// <summary>
    /// The literal text of a route template, each parameter replaced by a placeholder and <c>{{</c>/<c>}}</c> decoded. Only literal
    /// text must not hold '?' or '#': a parameter's regex constraint may (<c>{v:regex(^\d+(\.\d+)?$)}</c>).
    /// </summary>
    public static string RouteLiteralText(string route) =>
        RouteVariableRegex().Replace(EscapeBraces(route), "p").Replace('\u0001', '{').Replace('\u0002', '}');

    /// <summary>Extracts ASP.NET Core route template variables. Literal braces <c>{{</c>/<c>}}</c> are skipped.</summary>
    public static IReadOnlyList<RouteVariable> ParseRouteVariables(string route)
    {
        var unescaped = EscapeBraces(route);
        var result = new List<RouteVariable>();
        foreach (Match m in RouteVariableRegex().Matches(unescaped))
        {
            result.Add(ParseRouteParameter(m.Groups["body"].Value.Replace('\u0001', '{').Replace('\u0002', '}')));
        }

        return result;
    }

    /// <summary>
    /// One parameter, split as RouteParameterParser.ParseRouteParameter does (aspnetcore v10.0): a leading <c>*</c>/<c>**</c> is a
    /// catch-all, only a trailing <c>?</c> makes it optional, the name runs to the first ':' or '=', and a default follows only
    /// where the constraints end on '=' — a regex constraint may hold '=' or '?' (<c>{v:regex(^(?=\d)\w+=?$)}</c>).
    /// </summary>
    private static RouteVariable ParseRouteParameter(string parameter)
    {
        var start = 0;
        var end = parameter.Length - 1;
        var catchAll = false;
        if (parameter.StartsWith("**", StringComparison.Ordinal))
        {
            catchAll = true;
            start = 2;
        }
        else if (parameter.StartsWith('*'))
        {
            catchAll = true;
            start = 1;
        }

        var optional = end >= start && parameter[end] == '?';
        if (optional)
        {
            end--;
        }

        // a name may start with ':' or '=' ("=foo" is a name, not a default)
        var nameEnd = start;
        while (nameEnd <= end && !((parameter[nameEnd] == ':' || parameter[nameEnd] == '=') && nameEnd != start))
        {
            nameEnd++;
        }

        var name = parameter[start..Math.Min(nameEnd, end + 1)];
        // like ParseRouteParameter: from the name's last character when a delimiter follows it, past the end otherwise
        var constraintsEnd = SkipConstraints(parameter, nameEnd > end ? end + 1 : nameEnd - 1, end);
        var hasDefault = constraintsEnd <= end && parameter[constraintsEnd] == '=';
        var stop = Math.Min(constraintsEnd, end + 1);
        var constraint = nameEnd <= end && parameter[nameEnd] == ':' && stop > nameEnd + 1 ? parameter[(nameEnd + 1)..stop] : null;
        return new RouteVariable(name, constraint, optional, catchAll, hasDefault, hasDefault ? parameter[(constraintsEnd + 1)..(end + 1)] : null);
    }

    // RouteParameterParser.ParseConstraints reduced to where the constraints end: inside "(…)" a ')' ends the argument only as the last
    // character or before ':' or '='; a ':' or '=' inside parentheses that close later belongs to the argument
    private static int SkipConstraints(string text, int index, int end)
    {
        const int Start = 0, ParsingName = 1, InsideParenthesis = 2, End = 3;
        var state = Start;
        do
        {
            char? c = index > end ? null : text[index];
            switch (state)
            {
                case Start:
                    if (c is null)
                    {
                        state = End;
                    }
                    else if (c == ':')
                    {
                        state = ParsingName;
                    }
                    else if (c == '(')
                    {
                        state = InsideParenthesis;
                    }
                    else if (c == '=')
                    {
                        state = End;
                        index--;
                    }

                    break;
                case InsideParenthesis:
                    if (c is null)
                    {
                        state = End;
                    }
                    else if (c == ')')
                    {
                        char? next = index + 1 > end ? null : text[index + 1];
                        if (next is null || next == '=')
                        {
                            state = End;
                        }
                        else if (next == ':')
                        {
                            state = Start;
                        }
                    }
                    else if (c is ':' or '=')
                    {
                        var close = text.IndexOf(')', index + 1);
                        if (close == -1)
                        {
                            state = c == ':' ? ParsingName : End;
                            index -= c == '=' ? 1 : 0;
                        }
                        else
                        {
                            index = close;
                        }
                    }

                    break;
                case ParsingName:
                    if (c is null)
                    {
                        state = End;
                    }
                    else if (c == '(')
                    {
                        state = InsideParenthesis;
                    }
                    else if (c == '=')
                    {
                        state = End;
                        index--;
                    }

                    break;
            }

            index++;
        }
        while (state != End);

        return index;
    }

    public sealed record MediaType(string Type, string Subtype, string? Charset)
    {
        public string Essence => Type + "/" + Subtype;
    }

    /// <summary>
    /// Parses <c>type/subtype[; parameter=value]*</c>, lower-casing type and subtype (ASCII case-insensitive comparison).
    /// Returns null when the value is not a syntactically valid media type.
    /// </summary>
    /// <summary>
    /// RFC 9110 §8.3.1 <c>media-type = type "/" subtype parameters</c> with §5.6.6 <c>parameters = *( OWS ";" OWS [ parameter ] )</c> and
    /// <c>parameter = token "=" ( token / quoted-string )</c>: no whitespace around "=", a quoted-string may hold ";" and quoted-pairs
    /// (§5.6.4). Null for anything else; the first charset parameter wins. The runtime's <c>parseMediaType</c> is the same grammar.
    /// </summary>
    public static MediaType? ParseMediaType(string value)
    {
        var text = value.Trim(' ', '\t');
        var i = 0;
        var type = Token();
        if (type.Length == 0 || i >= text.Length || text[i] != '/')
        {
            return null;
        }

        i++;
        var subtype = Token();
        if (subtype.Length == 0)
        {
            return null;
        }

        string? charset = null;
        while (true)
        {
            Ows();
            if (i == text.Length)
            {
                break;
            }

            if (text[i] != ';')
            {
                return null;
            }

            i++;
            Ows();
            if (i == text.Length || text[i] == ';')
            {
                continue; // an empty parameter
            }

            var name = Token();
            if (name.Length == 0 || i >= text.Length || text[i] != '=')
            {
                return null;
            }

            i++;
            string parameterValue;
            if (i < text.Length && text[i] == '"')
            {
                i++;
                var quoted = new System.Text.StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                    {
                        return null; // unterminated quoted-string
                    }

                    var c = text[i];
                    if (c == '"')
                    {
                        i++;
                        break;
                    }

                    if (c == '\\')
                    {
                        if (i + 1 >= text.Length || !(text[i + 1] == '\t' || (text[i + 1] >= 0x20 && text[i + 1] <= 0x7e) || (text[i + 1] >= 0x80 && text[i + 1] <= 0xff)))
                        {
                            return null;
                        }

                        quoted.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    // qdtext = HTAB / SP / %x21 / %x23-5B / %x5D-7E / obs-text
                    if (!(c == '\t' || c == ' ' || c == 0x21 || (c >= 0x23 && c <= 0x5b) || (c >= 0x5d && c <= 0x7e) || (c >= 0x80 && c <= 0xff)))
                    {
                        return null;
                    }

                    quoted.Append(c);
                    i++;
                }

                parameterValue = quoted.ToString();
            }
            else
            {
                parameterValue = Token();
                if (parameterValue.Length == 0)
                {
                    return null;
                }
            }

            if (charset is null && name.Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                charset = parameterValue.ToLowerInvariant();
            }
        }

        return new MediaType(type.ToLowerInvariant(), subtype.ToLowerInvariant(), charset);

        string Token()
        {
            var begin = i;
            while (i < text.Length && IsHttpToken(text[i].ToString()))
            {
                i++;
            }

            return text[begin..i];
        }

        void Ows()
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            {
                i++;
            }
        }
    }

    /// <summary>JSON media types the runtime accepts: explicit list, never a generic <c>+json</c> suffix rule.</summary>
    public static readonly HashSet<string> JsonMediaEssences = new(StringComparer.Ordinal)
    {
        "application/json", "text/json", "application/problem+json",
    };

    public static bool IsRedirectStatus(int status) => status is 301 or 302 or 303 or 307 or 308;

    public static bool IsBodylessStatus(int status) => status is 204 or 205 or 304;
}
