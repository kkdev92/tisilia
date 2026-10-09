using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.AspNetCore.Export;

public sealed record DoctorOperation(string OperationId, IReadOnlyList<string> Methods, string Route, string Readiness,
    IReadOnlyList<Diagnostic> Diagnostics, string MetadataVerification = "observed", string HttpVerification = "unobserved");
public sealed record DoctorCause(string ReasonCode, string Message, string? Fix, IReadOnlyList<string> OperationIds, IReadOnlyList<string> Paths);
public sealed record DoctorReport(bool AnalysisComplete, int SelectedCount, int AnalyzedCount, int UnanalyzedCount,
    IReadOnlyList<DoctorOperation> Operations, IReadOnlyList<DoctorCause> Causes)
{
    public string Format => "tisilia.doctor-report";
    public string Version => TisiliaJson.DraftVersion;
    public string Overall => !AnalysisComplete ? "analysis-incomplete" : Operations.Any(o => o.Readiness != "supported") || BindingProbes.Any(p => p.Status != "matches") ? "blockers" : "supported";
    public string ExecutionScope => BindingProbesRun ? "application-startup-and-metadata; declared custom bindings called with a recording request; no handler probing; allow flags are not a sandbox"
        : "application-startup-and-metadata; no handler probing; allow flag is not a sandbox";
    /// <summary>Whether the declared custom bindings were called (<c>--allow-execute-binders</c>).</summary>
    public bool BindingProbesRun { get; init; }
    public IReadOnlyList<DoctorBindingProbe> BindingProbes { get; init; } = [];
    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string Architecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string TimeZone => TimeZoneInfo.Local.Id;
    public string Culture => System.Globalization.CultureInfo.CurrentCulture.Name;
    public string? DateTimeDefault { get; init; }
    public IReadOnlyList<string> DateTimeMembers { get; init; } = [];
    /// <summary>The declared server time zone (<c>TisiliaOptions.DateTimes.ServerTimeZone</c>), or null.</summary>
    public string? DateTimeServerTimeZone { get; init; }
    /// <summary>Whether this process's own zone has the declared zone's offsets from 1900 to 2199 (null without a declaration).</summary>
    public bool? DateTimeServerTimeZoneIsLocal { get; init; }
    public string DateTimeScope => "JSON: mixed Kind supported by default; optional Kind declarations and converter bindings; route/query/header: mixed Kind supported, offsets bind as UTC; fixed Local is unsupported";
    public int ExitCode => !AnalysisComplete ? 6 : Overall == "blockers" ? 3 : 0;
}

public sealed partial class TisiliaContractExporter
{
    /// <summary>
    /// Separate staging for each operation; diagnostic results are never a partial contract. With <paramref name="probeBindings"/>, the
    /// declared custom bindings are also called with a recording request (application code), and a read outside a declaration is a blocker.
    /// </summary>
    public DoctorReport Diagnose(bool probeBindings = false)
    {
        var report = DiagnoseMetadata();
        if (!probeBindings) { return report; }
        var probes = ProbeCustomBindingsAsync().GetAwaiter().GetResult();
        var causes = probes.Where(p => p.Status != "matches").Select(p => new DoctorCause(p.Status == "failed" ? "binding-probe-failed" : "binding-declaration-mismatch",
            p.Status == "failed" ? $"parameter '{p.Parameter}' ({p.Binding}): {p.Failure} when called with a recording request"
                : $"parameter '{p.Parameter}' ({p.Binding}) reads {string.Join(", ", p.UndeclaredReads)}, which its declaration does not name",
            "declare every request value the binding reads in TisiliaOptions.CustomBinding", [p.OperationId], ["/operations"]));
        return report with { Causes = [.. report.Causes, .. causes], BindingProbesRun = true, BindingProbes = probes };
    }

    private DoctorReport DiagnoseMetadata()
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
                .GroupBy(x => (x.Diagnostic.Code, x.Diagnostic.Rule, Message: x.Diagnostic.Message))
                .Select(g => new DoctorCause(g.Key.Code + "/" + g.Key.Rule, g.Key.Message, g.First().Diagnostic.Fix,
                    g.Select(x => x.OperationId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), g.Select(x => x.Diagnostic.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
            var serverTimeZone = options.Value.DateTimes.ServerTimeZone;
            return new DoctorReport(complete, groups.Length, analyzed, groups.Length - analyzed, operations, causes)
            {
                DateTimeDefault = options.Value.DateTimes.Default?.ToString(),
                DateTimeMembers = options.Value.DateTimes.DescribeMembers(),
                DateTimeServerTimeZone = serverTimeZone?.Id,
                DateTimeServerTimeZoneIsLocal = serverTimeZone is null ? null : ServerTimeZoneTable.Of(serverTimeZone).HasSameOffsets(ServerTimeZoneTable.Of(TimeZoneInfo.Local)),
            };
        }
    }

    private static DoctorOperation Identity(IGrouping<string, RouteEndpoint> group, string readiness, IReadOnlyList<Diagnostic> diagnostics) => new(group.Key,
        group.SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        string.Join(" | ", group.Select(e => e.RoutePattern.RawText ?? "<resolved route>").Distinct(StringComparer.Ordinal)), readiness, diagnostics);

    private static Diagnostic SafeDiagnostic(Diagnostic d) => d with { File = null, Message = Scrub(d.Message), Fix = d.Fix is null ? null : Scrub(d.Fix) };
    private static string Scrub(string value)
    {
        var text = Regex.Replace(value, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\s'\""<>]+", "[local-path]");
        text = Regex.Replace(text, @"(?i)(?:https?://)[^\s'\""<>]+", "[url]");
        text = Regex.Replace(text, @"(?<![\w/])/(?:home|Users|private|tmp|etc|var)/[^\s'\""<>]+", "[local-path]");
        return Regex.Replace(text, @"(?i)(password|pwd|token|secret|connectionstring|api[-_]?key)\s*[:=]\s*[^\s;,]+", "$1=[redacted]");
    }
}
