using System.Text;

namespace Tisilia.Generator.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// One structured diagnostic: a stable <c>TISxxxx</c> code, a JSON Pointer (RFC 6901)
/// into the offending document, related identifiers and a fix hint. Messages never embed secret values.
/// </summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    /// <summary>RFC 6901 JSON Pointer into the document, e.g. <c>/operations/3/responses/0/status</c>.</summary>
    public string Path { get; init; } = "";
    public string? File { get; init; }
    public IReadOnlyList<string> RelatedIds { get; init; } = [];
    public string? Rule { get; init; }
    public string? Fix { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Severity.ToString().ToLowerInvariant()).Append(' ').Append(Code);
        if (Rule is not null)
        {
            sb.Append(" [").Append(Rule).Append(']');
        }

        sb.Append(": ").Append(Message);
        if (File is not null)
        {
            sb.Append(" (file ").Append(File).Append(')');
        }

        if (Path.Length > 0)
        {
            sb.Append(" at ").Append(Path);
        }

        if (RelatedIds.Count > 0)
        {
            sb.Append(" ids=[").Append(string.Join(", ", RelatedIds)).Append(']');
        }

        if (Fix is not null)
        {
            sb.Append(" fix: ").Append(Fix);
        }

        return sb.ToString();
    }
}

/// <summary>Accumulates diagnostics. Errors are never downgraded.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public int ErrorCount => _items.Count(d => d.Severity == DiagnosticSeverity.Error);

    public string? File { get; init; }

    public void Add(Diagnostic diagnostic)
    {
        _items.Add(diagnostic.File is null && File is not null ? diagnostic with { File = File } : diagnostic);
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            Add(d);
        }
    }

    public void Error(string code, string rule, string path, string message, IReadOnlyList<string>? relatedIds = null, string? fix = null)
        => Add(new Diagnostic { Code = code, Severity = DiagnosticSeverity.Error, Rule = rule, Path = path, Message = message, RelatedIds = relatedIds ?? [], Fix = fix });

    public void Warning(string code, string rule, string path, string message, IReadOnlyList<string>? relatedIds = null, string? fix = null)
        => Add(new Diagnostic { Code = code, Severity = DiagnosticSeverity.Warning, Rule = rule, Path = path, Message = message, RelatedIds = relatedIds ?? [], Fix = fix });
}

/// <summary>RFC 6901 JSON Pointer helpers.</summary>
public static class JsonPointer
{
    public static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static string Append(string pointer, string token) => pointer + "/" + Escape(token);

    public static string Append(string pointer, int index) => pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Diagnostic codes, grouped by range.</summary>
public static class TisiliaCodes
{
    // TIS1000–1099: format, version, configuration
    public const string FormatOrVersion = "TIS1001";
    public const string SchemaViolation = "TIS1002";
    public const string UnsafeControlNumber = "TIS1003";
    public const string InvalidLimits = "TIS1004";
    public const string InvalidJson = "TIS1005";
    public const string DuplicateId = "TIS1010";
    public const string ReservedId = "TIS1011";
    public const string ConfigInvalid = "TIS1020";

    // TIS1100–1199: operation / binder / result
    public const string BodyOnGetOrHead = "TIS1101";
    public const string BodylessStatusMismatch = "TIS1102";
    public const string AmbiguousResponseCase = "TIS1103";
    public const string TextBodyRule = "TIS1104";
    public const string ParameterRouteMismatch = "TIS1105";
    public const string BinderNullPolicy = "TIS1106";
    public const string RedirectStatus = "TIS1107";
    public const string PipelineOrResultClosure = "TIS1108";
    public const string MediaTypeInvalid = "TIS1109";

    // TIS1200–1299: profile / dynamic behavior
    public const string ProfileResolution = "TIS1201";
    public const string MaxDepth = "TIS1202";
    public const string IgnoreCondition = "TIS1203";
    public const string BehaviorEffect = "TIS1204";
    public const string ScopeClosure = "TIS1205";
    public const string ReferencePreserve = "TIS1206";

    // TIS1300–1399: custom codec / capability
    public const string CodecTypeMismatch = "TIS1301";
    public const string MissingCapability = "TIS1302";
    public const string UndeclaredDependency = "TIS1303";
    public const string ContextualModelMix = "TIS1304";
    public const string EquivalenceMismatch = "TIS1305";
    public const string ProjectionMismatch = "TIS1306";
    public const string ModuleExportMismatch = "TIS1307";
    public const string BindingScopeClaim = "TIS1308";
    public const string ComparerRule = "TIS1309";
    public const string PortableProject = "TIS1310";
    public const string PortableTokenUnion = "TIS1311";
    public const string PortableResponseUnion = "TIS1312";
    public const string AuxiliaryBinding = "TIS1313";
    public const string ObjectCodecMismatch = "TIS1314";

    // TIS1400–1499: type use / wire / range / recursion
    public const string UnresolvedReference = "TIS1401";
    public const string DirectionMismatch = "TIS1402";
    public const string NullabilityMismatch = "TIS1403";
    public const string MapKeyRule = "TIS1404";
    public const string NonProductiveRecursion = "TIS1405";
    public const string DuplicatePropertyName = "TIS1406";
    public const string ExtensionDataMismatch = "TIS1407";
    public const string TokenUnionInvalid = "TIS1408";
    public const string TaggedUnionInvalid = "TIS1409";
    public const string EnumInvalid = "TIS1410";
    public const string LiteralInvalid = "TIS1411";
    public const string StringLengthInvalid = "TIS1412";
    public const string XmlWireInvalid = "TIS1413";

    // TIS1500–1599: generation / names / owned files
    public const string OutputPath = "TIS1501";
    public const string TargetMode = "TIS1502";
    public const string GenerationManifest = "TIS1503";
    public const string NameMapping = "TIS1504";
    public const string NameCollision = "TIS1505";
    public const string OwnershipConflict = "TIS1506";

    // TIS1600–1699: evidence / qualification
    public const string HashMismatch = "TIS1601";
    public const string ModuleArtifact = "TIS1602";
    public const string EvidenceInvalid = "TIS1603";
    public const string CoverageNotQualified = "TIS1604";
    public const string ClosureRecord = "TIS1605";
    public const string RunnerProtocol = "TIS1606";

    // TIS1700–1799: publication / auth / path / origin
    public const string UnsafeRouteOrHeader = "TIS1701";
    public const string HydrationRule = "TIS1702";
    public const string RedactionRule = "TIS1703";
    public const string SecretExposure = "TIS1704";

    // TIS1800–1899: runtime decode / limit / mismatch
    public const string EnvelopeMismatch = "TIS1801";
    public const string RequestIdentity = "TIS1802";
}
