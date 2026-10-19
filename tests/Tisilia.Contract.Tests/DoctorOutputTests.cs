using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Tisilia.Tool;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// What doctor prints. The report is always written to its file; as JSON stdout carries it again unless <c>--output</c> named the file,
/// then a summary: a report of a few hundred operations runs to megabytes, which a terminal or a CI log does not need twice.
/// </summary>
public sealed class DoctorOutputTests
{
    private static readonly Diagnostic Unbound = new() { Code = "TIS1302", Rule = "SV05", Severity = DiagnosticSeverity.Error, Message = "type 'A' is serialized by custom converter 'C' which has no registered Tisilia binding" };
    private static readonly Diagnostic Undeclared = new() { Code = "TIS1108", Rule = "SV34", Severity = DiagnosticSeverity.Error, Message = "operation 'c' declares no response types" };

    // the report as the doctor host writes it to its file
    private static JsonObject Report() => JsonNode.Parse(JsonSerializer.Serialize(new DoctorReport(true, 4, 4, 0,
        [
            new DoctorOperation("a", ["GET"], "/a", "supported", []),
            new DoctorOperation("b", ["POST"], "/b", "unsupported-feature", [Unbound]),
            new DoctorOperation("c", ["PATCH"], "/c", "requires-declaration", [Undeclared]),
            new DoctorOperation("d", ["PUT"], "/d", "unsupported-feature", [Unbound]),
        ],
        [
            new DoctorCause("TIS1302/SV05", "type 'A' is serialized by custom converter 'C' which has no registered Tisilia binding", null, ["b", "d"], ["/a"]),
            new DoctorCause("TIS1302/SV05", "type 'B' is serialized by custom converter 'C' which has no registered Tisilia binding", null, ["d"], ["/b"]),
            new DoctorCause("TIS1108/SV34", "operation 'c' declares no response types", null, ["c"], ["/c"]),
        ])
    {
        UnboundConverters = [new DoctorUnboundConverter("C", false, ["A", "B"], ["b", "d"], "register each type with TisiliaOptions.Codecs.AddPaired (ConverterType = typeof(C))")],
        ConformanceGaps = [new DoctorConformanceGap("P", ["Project", "Oracle"], ["E"], ["a"], "give the paired registration of 'P' Project, Oracle: conformance runs need them")],
    }, TisiliaJson.IndentedOptions))!.AsObject();

    [Fact]
    public void With_an_output_file_stdout_carries_a_summary_of_the_report()
    {
        var stdout = new StringWriter();
        Commands.WriteDoctorReport(Report(), "/reports/doctor.json", json: true, summaryOnly: true, stdout);
        var summary = JsonNode.Parse(stdout.ToString())!.AsObject();

        Assert.Equal("tisilia.doctor-summary", summary["format"]!.GetValue<string>());
        Assert.Equal("/reports/doctor.json", summary["report"]!.GetValue<string>());
        Assert.Equal(("blockers", 4, 3), (summary["overall"]!.GetValue<string>(), summary["selectedCount"]!.GetValue<int>(), summary["exitCode"]!.GetValue<int>()));
        Assert.Equal("""{"unsupported-feature":2,"requires-declaration":1,"supported":1}""", summary["readiness"]!.ToJsonString());
        // each reason once: how many causes it has, and in how many operations
        Assert.Equal("""[{"reasonCode":"TIS1302/SV05","causes":2,"operations":2},{"reasonCode":"TIS1108/SV34","causes":1,"operations":1}]""", summary["causes"]!.ToJsonString());
        // each converter without a binding once: how many types it serializes so, and in how many operations
        Assert.Equal("""[{"converter":"C","factory":false,"types":2,"operations":2}]""", summary["unboundConverters"]!.ToJsonString());
        // each converter whose registrations lack delegates conformance runs need once, with those delegates
        Assert.Equal("""[{"converter":"P","missing":["Project","Oracle"],"types":1,"operations":1}]""", summary["conformanceGaps"]!.ToJsonString());
        Assert.DoesNotContain("operationIds", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_output_file_stdout_carries_the_report_and_text_always_lists_it()
    {
        var json = new StringWriter();
        Commands.WriteDoctorReport(Report(), "/project/tisilia.doctor.json", json: true, summaryOnly: false, json);
        Assert.Equal("tisilia.doctor-report", JsonNode.Parse(json.ToString())!["format"]!.GetValue<string>());

        // the text is not the file again: every cause and operation, with or without --output
        var text = new StringWriter();
        Commands.WriteDoctorReport(Report(), "/project/tisilia.doctor.json", json: false, summaryOnly: false, text);
        Assert.Contains("d: unsupported-feature /d", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("cause TIS1302/SV05: type 'B'", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("unbound converter C: 2 type(s) in 2 operation(s): A, B; Fix: register each type with", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("conformance delegates missing for P (Project, Oracle): 1 type(s) in 1 operation(s): E; Fix: give the paired registration of 'P'", text.ToString(), StringComparison.Ordinal);
    }
}
