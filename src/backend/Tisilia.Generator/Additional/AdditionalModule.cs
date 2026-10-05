using System.Globalization;
using System.Numerics;
using Tisilia.Contract;
using Tisilia.Generator.Conformance;

namespace Tisilia.Generator.Additional;

/// <summary>
/// The additional codec module shipped with Tisilia (types outside the builtin scalar set are used through certified
/// additional codecs, never rounded to string/number). It is the TypeScript side of the codecs for Int128, UInt128, BigInteger,
/// Half, Uri, Version, IPAddress and Rune; the ASP.NET Core registrations (<c>Tisilia.AspNetCore.Codecs.AdditionalCodecs</c>)
/// declare them, <c>tisilia codec install-additional</c> installs the files next to the application, and generated clients import
/// that installed copy. The module is a paired module: a hand-written codec per type, qualified by
/// conformance like any other module.
/// </summary>
public static class AdditionalModule
{
    public const string ModuleId = "tisilia-additional";
    public const string Version = "0.3.0";
    public const string License = "MIT";

    /// <summary>Install folder relative to the application's content root (where the contract is exported).</summary>
    public const string InstallDirectory = "modules/tisilia-additional";

    public const string ScriptFile = "tisilia-additional.js";
    public const string TypesFile = "tisilia-additional.d.ts";

    /// <summary>The artifact path of the browser/node module in the contract (relative to the contract directory).</summary>
    public const string ScriptPath = InstallDirectory + "/" + ScriptFile;

    /// <summary>Model, brand and binding ids of the module start with this prefix (never the reserved <c>tisilia.</c>, SV02).</summary>
    public const string IdPrefix = "tisilia-additional.";

    /// <summary>Grammar binding ids: <c>tisilia-additional.grammar.&lt;name&gt;</c>, implemented by the module's grammar exports.</summary>
    public const string GrammarPrefix = IdPrefix + "grammar.";

    public static string Grammar(string name) => GrammarPrefix + name;

    /// <summary>The model id (and brand id) of an additional type: <c>tisilia-additional.Int128</c>.</summary>
    public static string ModelId(string typeName) => IdPrefix + typeName;

    /// <summary>The bytes of the module script (the artifact whose digest the contract records).</summary>
    public static byte[] Script() => Resource(ScriptFile);

    /// <summary>The bytes of the module's TypeScript declarations (installed beside the script).</summary>
    public static byte[] Types() => Resource(TypesFile);

    private static byte[] Resource(string file)
    {
        using var stream = typeof(AdditionalModule).Assembly.GetManifestResourceStream("Tisilia.Additional." + file)
            ?? throw new InvalidOperationException($"embedded resource 'Tisilia.Additional.{file}' is missing from {typeof(AdditionalModule).Assembly.GetName().Name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public enum InstallStatus
    {
        Written,
        Unchanged,
        Replaced,
        Conflict,
    }

    public sealed record InstalledFile(string Path, InstallStatus Status);

    /// <summary>
    /// Installs the module files under <paramref name="contentRoot"/>/<see cref="InstallDirectory"/>, with a .gitattributes that
    /// pins their bytes unless one is there already. An existing file with other content is a conflict unless <paramref name="force"/>
    /// is set (nothing is written then); identical files are left alone, and a CRLF checkout of them is replaced without --force.
    /// </summary>
    public static IReadOnlyList<InstalledFile> Install(string contentRoot, bool force)
    {
        var directory = System.IO.Path.Combine(contentRoot, InstallDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var files = new[] { (Name: ScriptFile, Bytes: Script()), (Name: TypesFile, Bytes: Types()) };
        var plan = new List<(string Path, byte[] Bytes, InstallStatus Status)>();
        foreach (var (name, bytes) in files)
        {
            var path = System.IO.Path.Combine(directory, name);
            var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var status = current is null ? InstallStatus.Written
                : current.AsSpan().SequenceEqual(bytes) ? InstallStatus.Unchanged
                : force || Canonical.LineEndings.SameText(current, bytes) ? InstallStatus.Replaced : InstallStatus.Conflict;
            plan.Add((path, bytes, status));
        }

        // git must not convert the installed files' line endings (Git for Windows checks text out with CRLF by default)
        var attributes = System.IO.Path.Combine(directory, ".gitattributes");
        plan.Add((attributes, System.Text.Encoding.UTF8.GetBytes(Canonical.LineEndings.PinningGitAttributes("tisilia codec install-additional")), File.Exists(attributes) ? InstallStatus.Unchanged : InstallStatus.Written));

        if (plan.Any(p => p.Status == InstallStatus.Conflict))
        {
            return plan.Select(p => new InstalledFile(p.Path, p.Status)).ToList();
        }

        System.IO.Directory.CreateDirectory(directory);
        foreach (var (path, bytes, status) in plan)
        {
            if (status is InstallStatus.Written or InstallStatus.Replaced)
            {
                File.WriteAllBytes(path, bytes);
            }
        }

        return plan.Select(p => new InstalledFile(p.Path, p.Status)).ToList();
    }

    /// <summary>The fix for a missing or stale installed module (diagnostic hints of generate and conformance).</summary>
    public static string InstallHint(string contentRoot) => $"tisilia codec install-additional --project \"{contentRoot}\"";

    // ------------------------------------------------------------------ conformance samples

    /// <summary>
    /// Domain samples of the module's types for the standard suite: generated values of the brand's base scalar (any
    /// string, any double) are outside these domains, so the suite asks the module's own sample source. The first samples are the
    /// edges of each domain; later ones are drawn from <paramref name="next"/> (an exclusive upper bound → value).
    /// </summary>
    public static JsonValue? Sample(string brandId, int index, Func<int, int> next, IReadOnlyDictionary<string, string>? context = null)
    {
        if (!brandId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return brandId[IdPrefix.Length..] switch
        {
            "Int128" => DomainAst.String(Pick(index, ["170141183460469231731687303715884105727", "-170141183460469231731687303715884105728", "0", "-1", "9223372036854775808", "-9223372036854775809"], () => RandomInteger(next, 39, true, Int128.MinValue, Int128.MaxValue))),
            "UInt128" => DomainAst.String(Pick(index, ["340282366920938463463374607431768211455", "0", "18446744073709551616", "1"], () => RandomInteger(next, 39, false, UInt128.MinValue, UInt128.MaxValue))),
            "BigInteger" => DomainAst.String(Pick(index, ["-" + new string('9', 60), "0", "1" + new string('0', 100), "-1", "123456789012345678901234567890"], () => RandomInteger(next, 300, true, null, null))),
            "Half" => HalfSample(index, next),
            "Uri" => DomainAst.String(Pick(index, ["https://example.com/a/b?q=1#frag", "relative/path?x=y", "", "urn:isbn:0451450523", "http://[::1]:8080/", "mailto:someone@example.com", "/abs/%20encoded", "../up", "http://user@host:65535/p;a=b?q#f", "file:///C:/data/file.txt"], null, next)),
            "Version" => DomainAst.String(Pick(index, ["1.0", "2147483647.2147483647.2147483647.2147483647", "0.0", "1.2.3", "10.20.30.40"], () => RandomVersion(next))),
            "IPAddress" => DomainAst.String(Pick(index, ["2001:db8::1", "192.168.0.1", "::", "0.0.0.0", "::ffff:10.0.0.1", "fe80::1%3", "1:2:3:4:5:6:7:8", "255.255.255.255", "::1"], () => string.Join('.', Enumerable.Range(0, 4).Select(_ => next(256).ToString(CultureInfo.InvariantCulture))))),
            "Rune" => DomainAst.String(Pick(index, ["😀", "a", "\0", "\uFFFF", "あ", char.ConvertFromUtf32(0x10FFFF)], () => RandomScalar(next))),
            "IPNetwork" => DomainAst.String(Pick(index, ["10.0.0.0/8", "2001:db8::/32", "0.0.0.0/0", "::/0", "192.168.1.0/24", "fe80::%2/64", "::ffff:10.0.0.0/104", "203.0.113.7/32", "2001:db8:85a3::8a2e:370:7334/128"], () => RandomIPv4Network(next))),
            "Index" => DomainAst.String(Pick(index, ["^1", "0", "2147483647", "^0", "^2147483647", "42"], () => RandomIndex(next))),
            "Range" => DomainAst.String(Pick(index, ["1..^2", "0..^0", "0..5", "^3..^1", "2147483647..^2147483647"], () => RandomIndex(next) + ".." + RandomIndex(next))),
            "JsonScalar" => JsonScalarSample(index, next),
            // environment-bound ids: the ones the exporting server recorded in the binding context
            "TimeZoneId" => ContextSample(context, "zones", index, next),
            "CultureName" => ContextSample(context, "cultures", index, next),
            _ => null,
        };
    }

    private static string Pick(int index, string[] fixedSamples, Func<string>? random, Func<int, int>? next = null)
        => index < fixedSamples.Length ? fixedSamples[index] : random is not null ? random() : fixedSamples[next!(fixedSamples.Length)];

    private static JsonValue? ContextSample(IReadOnlyDictionary<string, string>? context, string entry, int index, Func<int, int> next)
    {
        if (context is null || !context.TryGetValue(entry, out var json) || System.Text.Json.JsonSerializer.Deserialize<string[]>(json, TisiliaJson.Plain) is not { Length: > 0 } ids)
        {
            return null;
        }

        return DomainAst.String(ids[index < ids.Length && index < 4 ? index : next(ids.Length)]);
    }

    private static string RandomIndex(Func<int, int> next) => (next(2) == 0 ? "^" : "") + (next(3) == 0 ? next(int.MaxValue) : next(100)).ToString(CultureInfo.InvariantCulture);

    /// <summary>An IPv4 network with a random prefix and its host bits cleared (the canonical text IPNetwork.ToString writes).</summary>
    private static string RandomIPv4Network(Func<int, int> next)
    {
        var prefix = next(33);
        var address = ((uint)next(0x10000) << 16) | (uint)next(0x10000);
        address = prefix == 0 ? 0 : address & (uint.MaxValue << (32 - prefix));
        return string.Create(CultureInfo.InvariantCulture, $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}/{prefix}");
    }

    /// <summary>JSON scalars as a JsonValue node holds them: strings, booleans and numbers with their lexemes ("1.50", "-0", "1E+400").</summary>
    private static JsonValue JsonScalarSample(int index, Func<int, int> next)
    {
        JsonValue[] samples =
        [
            DomainAst.Number("1.50"), DomainAst.String("x"), DomainAst.Boolean(true), DomainAst.Number("-0"), DomainAst.Number("123456789012345678901234567890"),
            DomainAst.String("\u2028 \"quoted\" \\ 😀"), DomainAst.Number("1E+400"), DomainAst.Boolean(false), DomainAst.Number("-12.5e-3"), DomainAst.String(""),
        ];
        return samples[index < samples.Length ? index : next(samples.Length)];
    }

    private static string RandomInteger(Func<int, int> next, int maxDigits, bool signed, BigInteger? min, BigInteger? max)
    {
        while (true)
        {
            var digits = 1 + next(maxDigits);
            var text = new System.Text.StringBuilder();
            text.Append((char)('1' + next(9)));
            for (var i = 1; i < digits; i++)
            {
                text.Append((char)('0' + next(10)));
            }

            var value = BigInteger.Parse(text.ToString(), CultureInfo.InvariantCulture);
            if (signed && next(2) == 0)
            {
                value = -value;
            }

            if ((min is null || value >= min) && (max is null || value <= max))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static string RandomVersion(Func<int, int> next)
    {
        var parts = 2 + next(3);
        return string.Join('.', Enumerable.Range(0, parts).Select(_ => (next(3) == 0 ? next(int.MaxValue) : next(100)).ToString(CultureInfo.InvariantCulture)));
    }

    private static string RandomScalar(Func<int, int> next)
    {
        while (true)
        {
            var cp = next(0x110000);
            if (cp is < 0xD800 or > 0xDFFF)
            {
                return char.ConvertFromUtf32(cp);
            }
        }
    }

    /// <summary>Half samples as the float64 projection of the value (the domain AST of a brand over float64).</summary>
    private static JsonValue HalfSample(int index, Func<int, int> next)
    {
        ushort[] edges = [0x7BFF, 0x0001, 0x8000, 0x3C00, 0xC100, 0x0400, 0x2E66, 0xFBFF, 0x03FF];
        ushort bits;
        if (index < edges.Length)
        {
            bits = edges[index];
        }
        else
        {
            do
            {
                bits = (ushort)next(0x10000);
            }
            while ((bits & 0x7C00) == 0x7C00); // NaN and ±Infinity are outside the finite domain of the strict handling
        }

        return DomainAst.Float64((double)BitConverter.UInt16BitsToHalf(bits));
    }

    /// <summary>Values of the brand's domain that every validator must refuse (domain-validation cases besides wrong AST kinds).</summary>
    public static IEnumerable<(JsonValue Domain, string Reason)> InvalidSamples(string brandId)
    {
        if (!brandId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            yield break;
        }

        switch (brandId[IdPrefix.Length..])
        {
            case "Int128":
                yield return (DomainAst.String("170141183460469231731687303715884105728"), "range");
                yield return (DomainAst.String("01"), "leading-zero");
                yield return (DomainAst.String("-0"), "negative-zero");
                break;
            case "UInt128":
                yield return (DomainAst.String("-1"), "negative");
                yield return (DomainAst.String("340282366920938463463374607431768211456"), "range");
                break;
            case "BigInteger":
                yield return (DomainAst.String("1.5"), "fraction");
                yield return (DomainAst.String("1" + new string('0', 4096)), "length");
                break;
            case "Half":
                yield return (DomainAst.Number("0.1"), "not-binary16");
                yield return (DomainAst.Number("65505"), "between-halves");
                yield return (DomainAst.Number("70000"), "range");
                break;
            case "Version":
                yield return (DomainAst.String("01.2"), "leading-zero");
                yield return (DomainAst.String("1"), "one-component");
                yield return (DomainAst.String("2147483648.0"), "range");
                break;
            case "IPAddress":
                yield return (DomainAst.String("::FFFF:1.2.3.4"), "not-canonical");
                yield return (DomainAst.String("0:0:0:0:0:0:0:1"), "not-compressed");
                yield return (DomainAst.String("01.2.3.4"), "leading-zero");
                break;
            case "Rune":
                yield return (DomainAst.String("ab"), "two-scalars");
                yield return (DomainAst.String(""), "empty");
                break;
            case "IPNetwork":
                yield return (DomainAst.String("10.0.0.1/8"), "host-bits");
                yield return (DomainAst.String("10.0.0.0/08"), "leading-zero");
                yield return (DomainAst.String("10.0.0.0"), "no-prefix");
                yield return (DomainAst.String("10.0.0.0/33"), "prefix-range");
                break;
            case "Index":
                yield return (DomainAst.String("-1"), "negative");
                yield return (DomainAst.String("^2147483648"), "range");
                yield return (DomainAst.String("01"), "leading-zero");
                break;
            case "Range":
                yield return (DomainAst.String("..5"), "open-start");
                yield return (DomainAst.String("1..2..3"), "three-parts");
                break;
            case "JsonScalar":
                yield return (DomainAst.Object(), "json-object");
                yield return (DomainAst.Array([]), "json-array");
                break;
        }
    }
}
