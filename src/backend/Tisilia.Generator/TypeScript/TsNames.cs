using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.TypeScript;

/// <summary>Naming rules: stable ids stay in the contract, generated names are checked for reserved words, collisions (including case-only differences) and Windows reserved file names.</summary>
public static class TsNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "as", "implements", "interface", "let", "package", "private", "protected", "public", "static", "yield", "any", "boolean", "constructor", "declare", "get", "module", "require", "number", "set", "string", "symbol", "type", "from", "of", "async", "await", "namespace", "readonly", "unknown", "never", "object", "bigint", "undefined", "NaN", "Infinity", "globalThis", "eval", "arguments",
    };

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Global types the generated models file refers to by name (extension data, bytes): a model of that name would capture those
    /// references. Runtime types (Guid, Duration, …) are no such names: the models file imports them under an alias when a model shares
    /// a name with one, and every other generated file refers to models through the <c>models</c> namespace.
    /// </summary>
    public static readonly HashSet<string> ModelReservedNames = new(StringComparer.Ordinal)
    {
        "ReadonlyMap", "Uint8Array",
    };

    /// <summary>
    /// An ECMAScript IdentifierName (ECMA-262 §12.7) of BMP characters: ID_Start letters (Unicode categories Lu, Ll, Lt, Lm, Lo, Nl), `$` or
    /// `_` first, then ID_Continue (adding Mn, Mc, Nd, Pc). C# identifiers use the same categories, so a type or member named in Japanese
    /// (商品, 名前) keeps its name; surrogate pairs are left to quoting.
    /// </summary>
    public static bool IsIdentifier(string name) => name.Length > 0 && IsIdentifierStart(name[0]) && name.All(IsIdentifierPart);

    public static bool IsIdentifierStart(char c) => c is '_' or '$' || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.UppercaseLetter or System.Globalization.UnicodeCategory.LowercaseLetter or System.Globalization.UnicodeCategory.TitlecaseLetter
        or System.Globalization.UnicodeCategory.ModifierLetter or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.LetterNumber;

    public static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.DecimalDigitNumber
        or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    public static bool IsReserved(string name) => Reserved.Contains(name);

    public static bool IsWindowsReservedFileName(string name) => WindowsReserved.Contains(System.IO.Path.GetFileNameWithoutExtension(name));

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string CamelCase(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Operation id → method name: <c>users.get</c> → <c>usersGet</c>, non-identifier characters separate words.</summary>
    public static string OperationMethodName(string operationId)
    {
        var parts = operationId.Split(['.', '-', '_', ':', '/', '@'], StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(parts.Select((p, i) => i == 0 ? CamelCase(p) : Capitalize(p)));
        if (!IsIdentifier(name) || IsReserved(name))
        {
            name = "op" + Capitalize(name);
        }

        return name;
    }

    public static string TypeName(string operationId, string suffix) => Capitalize(OperationMethodName(operationId)) + suffix;

    /// <summary>Property name as it appears in generated interfaces: quoted when not an identifier.</summary>
    public static string PropertyKey(string name) => IsIdentifier(name) && !IsReserved(name) ? name : Quote(name);

    /// <summary>
    /// A key of an object literal the generated code builds (enum member consts): <c>__proto__</c> — bare or quoted — sets the prototype
    /// of a literal instead of creating a property (ECMA-262 §B.3.1), so it is written as a computed key.
    /// </summary>
    public static string ObjectLiteralKey(string name) => name == "__proto__" ? "[" + Quote(name) + "]" : PropertyKey(name);

    /// <summary>
    /// Contract text inside a generated <c>/** … */</c> comment: "*/" would end the comment early, and routing accepts it in a route
    /// (a literal <c>glob*</c> segment followed by '/', a regex constraint <c>^x*/y$</c>).
    /// </summary>
    public static string CommentText(string s) => s.Replace("*/", "*\\/", StringComparison.Ordinal);

    public static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case (char)0x2028: sb.Append("\\u2028"); break;
                case (char)0x2029: sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>Validates a model's tsName and registers it; collisions (case-insensitive) and reserved names are errors (SV52).</summary>
    public static bool Claim(Dictionary<string, string> claimed, Model model, DiagnosticBag bag)
    {
        var name = model.TsName;
        if (!IsIdentifier(name) || IsReserved(name) || ModelReservedNames.Contains(name))
        {
            bag.Error(TisiliaCodes.NameCollision, "SV52", "/types", $"type '{model.Id}': tsName '{name}' is reserved or not an identifier", [model.Id], "set a different tsName in the export (e.g. via a distinct CLR type name)");
            return false;
        }

        var key = name.ToUpperInvariant();
        if (claimed.TryGetValue(key, out var other))
        {
            bag.Error(TisiliaCodes.NameCollision, "SV52", "/types", $"type '{model.Id}': tsName '{name}' collides with '{other}' (names differing only by case are also collisions)", [model.Id, other]);
            return false;
        }

        claimed[key] = model.Id;
        return true;
    }
}
