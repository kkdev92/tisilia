using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.AspNetCore.Export;

public sealed record DoctorOperation(string OperationId, IReadOnlyList<string> Methods, string Route, string Readiness,
    IReadOnlyList<Diagnostic> Diagnostics, string MetadataVerification = "observed", string HttpVerification = "unobserved");
public sealed record DoctorCause(string ReasonCode, string Message, string? Fix, IReadOnlyList<string> OperationIds, IReadOnlyList<string> Paths);
public sealed record DoctorReport(bool AnalysisComplete, int SelectedCount, int AnalyzedCount, int UnanalyzedCount,
    IReadOnlyList<DoctorOperation> Operations, IReadOnlyList<DoctorCause> Causes)
{
    public string Format => "tisilia.doctor-report";
    public string Version => "1.0";
    public string Overall => !AnalysisComplete ? "analysis-incomplete" : Operations.Any(o => o.Readiness != "supported") ? "blockers" : "supported";
    public string ExecutionScope => "application-startup-and-metadata; no handler probing; allow flag is not a sandbox";
    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string Architecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string TimeZone => TimeZoneInfo.Local.Id;
    public string Culture => System.Globalization.CultureInfo.CurrentCulture.Name;
    public string? DateTimeDefault { get; init; }
    public IReadOnlyList<string> DateTimeMembers { get; init; } = [];
    public string DateTimeScope => "JSON: declared Kind and converter binding; route/query/header: HTTP binder semantics, Local unsupported; mixed Kind needs a separate codec";
    public int ExitCode => !AnalysisComplete ? 6 : Overall == "blockers" ? 3 : 0;
}

public sealed partial class TisiliaContractExporter
{
    /// <summary>Separate staging for each operation; diagnostic results are never a partial contract.</summary>
    public DoctorReport Diagnose()
    {
        lock (_gate)
        {
            var operations = new List<DoctorOperation>();
            RouteEndpoint[] endpoints;
            try { endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>() is not null).ToArray(); }
            catch (Exception) { return new DoctorReport(false, 0, 0, 0, [], [new DoctorCause("metadata-unavailable", "Endpoint metadata could not be enumerated; the selected set is unknown", "inspect application startup privately", [], [])]); }
            var groups = endpoints.GroupBy(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>()!.OperationId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var complete = true;
            var analyzed = 0;
            try { _ = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups; }
            catch (Exception)
            {
                return new DoctorReport(false, groups.Length, 0, groups.Length,
                    groups.Select(g => Identity(g, "analysis-incomplete", [new Diagnostic { Code = "TIS1020", Severity = DiagnosticSeverity.Error, Rule = "SV01", Path = "/operations", Message = "ApiExplorer enumeration failed; application exception text is withheld", Fix = "inspect startup / metadata configuration using isolated settings; no handler was probed" }])).ToArray(),
                    [new DoctorCause("metadata-unavailable", "ApiExplorer enumeration failed", "inspect metadata provider configuration privately", groups.Select(g => g.Key).ToArray(), ["/operations"])]);
            }
            foreach (var group in groups)
            {
                try
                {
                    var result = ExportCore(new HashSet<string>([group.Key], StringComparer.Ordinal));
                    var diagnostics = result.Diagnostics.Items.Select(SafeDiagnostic).ToArray();
                    var incomplete = diagnostics.Any(d => d.Message.Contains("analysis is incomplete", StringComparison.Ordinal));
                    complete &= !incomplete;
                    if (!incomplete) { analyzed++; }
                    var readiness = incomplete ? "analysis-incomplete" : !result.Diagnostics.HasErrors ? "supported"
                        : diagnostics.Any(d => d.Message.Contains("no declared wire", StringComparison.Ordinal) || d.Message.Contains("no response", StringComparison.Ordinal) || d.Message.Contains("no body type", StringComparison.Ordinal)) ? "requires-declaration"
                        : diagnostics.Any(d => d.Code == TisiliaCodes.SchemaViolation || d.Code == TisiliaCodes.DuplicateId) ? "invalid-contract" : "unsupported-feature";
                    operations.Add(Identity(group, readiness, diagnostics));
                }
                catch (Exception)
                {
                    complete = false;
                    operations.Add(Identity(group, "analysis-incomplete", [new Diagnostic { Code = "TIS1020", Severity = DiagnosticSeverity.Error, Message = "Operation analysis failed; application exception text is withheld", Fix = "inspect application metadata / resolver privately using isolated settings" }]));
                }
            }
            var causes = operations.SelectMany(o => o.Diagnostics.Select(d => (o.OperationId, Diagnostic: d)))
                .GroupBy(x => (x.Diagnostic.Code, x.Diagnostic.Rule, Message: CauseMessage(x.Diagnostic.Message)))
                .Select(g => new DoctorCause(g.Key.Code + "/" + g.Key.Rule, g.Key.Message, g.First().Diagnostic.Fix,
                    g.Select(x => x.OperationId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), g.Select(x => x.Diagnostic.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
            return new DoctorReport(complete, groups.Length, analyzed, groups.Length - analyzed, operations, causes)
            { DateTimeDefault = options.Value.DateTimes.Default?.ToString(), DateTimeMembers = options.Value.DateTimes.DescribeMembers() };
        }
    }

    private static DoctorOperation Identity(IGrouping<string, RouteEndpoint> group, string readiness, IReadOnlyList<Diagnostic> diagnostics) => new(group.Key,
        group.SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        string.Join(" | ", group.Select(e => e.RoutePattern.RawText ?? "<resolved route>").Distinct(StringComparer.Ordinal)), readiness, diagnostics);

    private static string CauseMessage(string message) => message.Contains("System.DateTime", StringComparison.Ordinal) && message.Contains("no declared wire", StringComparison.Ordinal)
        ? "DateTime wire is undeclared; choose a declaration matching existing Kind semantics (mixed Kind needs a separate codec)" : message;

    private static Diagnostic SafeDiagnostic(Diagnostic d) => d with { File = null, Message = Scrub(d.Message), Fix = d.Fix is null ? null : Scrub(d.Fix) };
    private static string Scrub(string value)
    {
        var text = Regex.Replace(value, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\s'\""<>]+", "[local-path]");
        text = Regex.Replace(text, @"(?i)(?:https?://)[^\s'\""<>]+", "[url]");
        text = Regex.Replace(text, @"(?<![\w/])/(?:home|Users|private|tmp|etc|var)/[^\s'\""<>]+", "[local-path]");
        return Regex.Replace(text, @"(?i)(password|pwd|token|secret|connectionstring|api[-_]?key)\s*[:=]\s*[^\s;,]+", "$1=[redacted]");
    }
}
