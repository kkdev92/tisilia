using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Runner protocol, oracles, suite determinism and evidence validation (SV45/46/53/54).</summary>
public class ConformanceTests
{
    private static (ContractIndex Index, LoadedContract Loaded) Sample()
    {
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(SampleContracts.UsersApi().BuildText(), bag)!;
        var index = SemanticValidator.Validate(loaded, bag)!;
        Assert.Empty(bag.Items);
        return (index, loaded);
    }

    // ------------------------------------------------------------------ protocol (SV54)

    [Fact]
    public void Runner_messages_round_trip_as_single_lines_and_validate_against_the_schema()
    {
        var request = RunnerProtocol.Request("s.1", "r.1", RunnerAction.Compare, "std.int64.response", "p", [], [DomainAst.Number("1"), DomainAst.Number("1")]);
        var line = RunnerProtocol.Serialize(request);
        Assert.DoesNotContain('\n', line);
        var parsed = RunnerProtocol.Parse(line, RunnerProtocol.DefaultMaxRecordBytes, out var error);
        Assert.Null(error);
        var back = Assert.IsType<RunnerRequest>(parsed);
        Assert.Equal(RunnerAction.Compare, back.Action);
        Assert.Equal(2, back.Inputs.Count);

        var success = RunnerProtocol.Serialize(RunnerProtocol.Success("s.1", "r.1", DomainAst.String("line\nbreak")));
        Assert.DoesNotContain('\n', success);
        Assert.IsType<RunnerSuccess>(RunnerProtocol.Parse(success, RunnerProtocol.DefaultMaxRecordBytes, out _));

        var failure = RunnerProtocol.Failure("s.1", "r.1", RunnerFailureCode.Codec, "codec.type mismatch!", "/a/0");
        Assert.Equal("codec.type-mismatch-", failure.SafeMessageId);
        Assert.IsType<RunnerFailure>(RunnerProtocol.Parse(RunnerProtocol.Serialize(failure), RunnerProtocol.DefaultMaxRecordBytes, out _));
    }

    [Fact]
    public void Runner_protocol_rejects_wrong_arity_unknown_members_and_oversized_records()
    {
        var one = RunnerProtocol.Request("s.1", "r.1", RunnerAction.Compare, "eq", "p", [], [DomainAst.Number("1")]);
        Assert.Null(RunnerProtocol.Parse(RunnerProtocol.Serialize(one), RunnerProtocol.DefaultMaxRecordBytes, out var arity));
        Assert.Contains("takes 2 input(s)", arity);

        var two = RunnerProtocol.Request("s.1", "r.1", RunnerAction.DotnetRead, "c", "p", [], [DomainAst.Number("1"), DomainAst.Number("2")]);
        Assert.Null(RunnerProtocol.Parse(RunnerProtocol.Serialize(two), RunnerProtocol.DefaultMaxRecordBytes, out _));

        var extra = JsonNode.Parse(RunnerProtocol.Serialize(one with { Inputs = [DomainAst.Number("1"), DomainAst.Number("2")] }))!.AsObject();
        extra["extraMember"] = 1;
        Assert.Null(RunnerProtocol.Parse(extra.ToJsonString(), RunnerProtocol.DefaultMaxRecordBytes, out var unknown));
        Assert.NotNull(unknown);

        Assert.Null(RunnerProtocol.Parse(RunnerProtocol.Serialize(RunnerProtocol.Success("s.1", "r.1", DomainAst.Null)), 10, out var limit));
        Assert.Contains("per-record limit", limit);
        Assert.Null(RunnerProtocol.Parse("{\"format\":\"tisilia.runner-message\",\"format\":\"x\"}", RunnerProtocol.DefaultMaxRecordBytes, out _));
    }

    // ------------------------------------------------------------------ domain AST and oracles

    [Fact]
    public void Domain_ast_text_is_lossless_and_scalar_text_is_canonical()
    {
        var ast = DomainAst.ParseJsonText("{\"a\":1.50,\"a\":2e3,\"b\":[null,true,\"x\\u0000\"],\"c\":-0}");
        Assert.Equal("{\"a\":1.50,\"a\":2e3,\"b\":[null,true,\"x\\u0000\"],\"c\":-0}", DomainAst.ToJsonText(ast));
        Assert.Equal("123.4500", DomainAst.FormatDecimal(123.4500m));
        Assert.Equal("0.0", DomainAst.FormatDecimal(decimal.Negate(0.0m))); // scale kept, sign of zero dropped
        Assert.Equal("-0.5", DomainAst.FormatDecimal(-0.5m));
        Assert.Equal("79228162514264337593543950335", DomainAst.FormatDecimal(decimal.MaxValue));
        Assert.Equal("0.0000000000000000000000000001", DomainAst.FormatDecimal(0.0000000000000000000000000001m));
        Assert.Equal("1E-05", ((JsonNumberValue)DomainAst.Float64(0.00001)).Text);
        Assert.Equal("0.0001", ((JsonNumberValue)DomainAst.Float64(0.0001)).Text);
        Assert.Equal("1E+17", ((JsonNumberValue)DomainAst.Float64(1e17)).Text);
        Assert.Equal("-0", ((JsonNumberValue)DomainAst.Float64(-0d)).Text);
        Assert.Equal("NaN", ((JsonStringValue)DomainAst.Float64(double.NaN)).Value);
        Assert.Equal("0.1", ((JsonNumberValue)DomainAst.Float32(0.1f)).Text);
        Assert.Equal("2026-09-30T15:04:05.1234567+09:00", DomainAst.StjText(new DateTimeOffset(2026, 9, 30, 15, 4, 5, TimeSpan.FromHours(9)).AddTicks(1234567)));
    }

    [Fact]
    public void Builtin_oracles_distinguish_structural_numeric_and_exact_wire_equality()
    {
        var a = DomainAst.ParseJsonText("{\"x\":1.0,\"y\":[1,2]}");
        var b = DomainAst.ParseJsonText("{\"y\":[1,2],\"x\":1}");
        var c = DomainAst.ParseJsonText("{\"x\":1.0,\"y\":[1,2]}");
        Assert.False(AstOracles.Structural(a, b)); // 1.0 vs 1 differ structurally
        Assert.True(AstOracles.Numeric(a, b)); // order-insensitive, numbers by value
        Assert.True(AstOracles.Structural(a, c));
        Assert.True(AstOracles.ExactWire(a, c));
        Assert.False(AstOracles.ExactWire(a, DomainAst.ParseJsonText("{\"y\":[1,2],\"x\":1.0}")));
        Assert.True(AstOracles.Numeric(DomainAst.Number("-0"), DomainAst.Number("0")));
        Assert.True(AstOracles.Numeric(DomainAst.Number("1e2"), DomainAst.Number("100")));
        Assert.False(AstOracles.Numeric(DomainAst.Number("100"), DomainAst.Number("100.1")));
        Assert.False(AstOracles.Structural(DomainAst.Number("1"), DomainAst.String("1")));
        Assert.Null(AstOracles.Evaluate("tisilia.oracle.unknown@0.3", a, b));
    }

    // ------------------------------------------------------------------ suite

    [Fact]
    public void Suite_is_reproducible_from_the_seed_and_required_categories_are_seed_independent()
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, index.Operations.Keys);
        var one = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 42, CasesPerCodec = 6 });
        var same = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 42, CasesPerCodec = 6 });
        var other = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 43, CasesPerCodec = 6 });
        Assert.Equal(one.Digest(), same.Digest());
        Assert.NotEqual(one.Digest(), other.Digest());
        Assert.Equal(one.RequiredTests, other.RequiredTests);
        Assert.Equal(one.RequiredTests, SuiteBuilder.RequiredCategories(index, closure));
        Assert.Equal(one.Cases.Select(c => c.Id).Distinct().Count(), one.Cases.Count);
        Assert.Contains(one.Cases, c => c is ResponseRoundTripCase r && r.CodecId.StartsWith("std.int64", StringComparison.Ordinal));
        Assert.Contains(one.Cases, c => c is NegativeWireCase n && n.Target == RunnerTarget.Node);
        Assert.Contains(one.Cases, c => c is OracleDiscriminationCase);
        Assert.Contains(one.Cases, c => c.Category.StartsWith("response-round-trip:paired", StringComparison.Ordinal));
        Assert.All(one.Cases, c => Assert.Contains(c.Category, one.RequiredTests));

        // the suite record itself validates as JSON and survives a round trip (it is what the suite digest hashes)
        var text = JsonSerializer.Serialize(one, TisiliaJson.Options);
        var back = JsonSerializer.Deserialize<ConformanceSuite>(text, TisiliaJson.Options)!;
        Assert.Equal(one.Digest(), back.Digest());
    }

    [Fact]
    public async Task Filtering_drops_module_domain_rejections_but_keeps_builtin_cases_and_required_categories()
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, index.Operations.Keys);
        var suite = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 1, CasesPerCodec = 4 });
        var pairedBefore = suite.Cases.Count(c => c.Category.Contains(":paired", StringComparison.Ordinal));
        var builtinBefore = suite.Cases.Count(c => c is ResponseRoundTripCase r && r.CodecId.StartsWith("std.", StringComparison.Ordinal));
        var filtered = await SuiteBuilder.FilterAsync(index, suite, (codecId, _, _) => Task.FromResult(!codecId.StartsWith("demo.Money", StringComparison.Ordinal)));
        Assert.True(filtered.Cases.Count(c => c.Category.Contains(":paired", StringComparison.Ordinal)) < pairedBefore);
        Assert.Equal(builtinBefore, filtered.Cases.Count(c => c is ResponseRoundTripCase r && r.CodecId.StartsWith("std.", StringComparison.Ordinal)));
        Assert.Equal(suite.RequiredTests, filtered.RequiredTests);
    }

    // ------------------------------------------------------------------ evidence (SV45/SV46/SV53)

    internal static (JsonObject Node, ConformanceEvidence Evidence) SyntheticEvidence(ContractIndex index, ContractClosure closure, string issuer = "ci")
    {
        var suite = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 1, CasesPerCodec = 2 });
        var results = suite.Cases.Select(c => new CaseResult(c.Id, c.Category, CaseStatus.Passed, "")).ToList();
        var bag = new DiagnosticBag();
        var built = EvidenceBuilder.Build(index, closure, suite, results, new EvidenceInputs
        {
            IssuerId = issuer,
            Matrix = new RuntimeMatrix { Dotnet = "10.0.12", Aspnetcore = "10.0.12", Stj = "10.0.0", Typescript = "6.0.3", Node = "24.13.1", Os = "test", Architecture = "x64" },
            Context = [new NameValue { Name = "dotnet.timeZone", Value = "UTC" }],
            ModuleArtifacts = closure.DeclaredModuleArtifacts(),
            ApplicationArtifacts = [new Artifact { Target = ArtifactTarget.Dotnet, Path = "bin/App.dll", Digest = "sha256:" + new string('a', 64) }],
            Limits = Limits.Default,
            RunnerDigest = "sha256:" + new string('c', 64),
            IssuedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        }, bag);
        Assert.Empty(bag.Items);
        return (built!.Node, built.Evidence);
    }

    [Fact]
    public void Valid_evidence_from_a_trusted_issuer_qualifies_its_scope_only()
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, ["users.get"]);
        var (node, evidence) = SyntheticEvidence(index, closure);
        Assert.Equal(Verdict.Passed, evidence.Verdict);
        var path = Path.Combine(Path.GetTempPath(), "tisilia-evidence-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, node.ToJsonString(TisiliaJson.IndentedOptions));
        try
        {
            var bag = new DiagnosticBag();
            var ok = EvidenceValidator.Check(path, index, new HashSet<string> { "ci" }, bag);
            Assert.True(ok.Valid, string.Join("; ", ok.ReasonCodes));
            var coverage = EvidenceValidator.Coverage(index, [ok]);
            Assert.Equal(CoverageStatus.Qualified, coverage.Single(c => c.OperationId == "users.get").Status);
            Assert.Equal([evidence.Id], coverage.Single(c => c.OperationId == "users.get").EvidenceIds);
            Assert.Equal(CoverageStatus.Unqualified, coverage.Single(c => c.OperationId == "users.put").Status);
            Assert.Equal([EvidenceValidator.ReasonNoEvidence], coverage.Single(c => c.OperationId == "users.put").ReasonCodes);

            var untrusted = EvidenceValidator.Check(path, index, new HashSet<string>(), new DiagnosticBag());
            Assert.Equal([EvidenceValidator.ReasonUntrustedIssuer], untrusted.ReasonCodes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EV01_Original_03_synthetic_evidence_cannot_qualify_a_04_contract()
    {
        // Captured from SyntheticEvidence in commit 54e4794009bb8d8329773fa612e84c75c7ac5da7, without editing its fields.
        // It is a validation fixture, not a claim that a codec or HTTP runner observed passed cases.
        var path = Path.Combine(FixtureTests.RepoRoot(), "tests", "fixtures", "legacy-0.3-codec-synthetic.evidence.json");
        var (index, _) = Sample();
        var checkedEvidence = EvidenceValidator.Check(path, index, new HashSet<string> { "ci" }, new DiagnosticBag());
        Assert.False(checkedEvidence.Valid);
        Assert.Contains(EvidenceValidator.ReasonHashMismatch, checkedEvidence.ReasonCodes);
        Assert.All(EvidenceValidator.Coverage(index, [checkedEvidence]), c => Assert.NotEqual(CoverageStatus.Qualified, c.Status));
    }

    [Theory]
    [InlineData("verdict", "failed", EvidenceValidator.ReasonFailed)]
    [InlineData("semanticHash", "sha256:0000000000000000000000000000000000000000000000000000000000000000", EvidenceValidator.ReasonHashMismatch)]
    [InlineData("closureDigest", "sha256:0000000000000000000000000000000000000000000000000000000000000000", EvidenceValidator.ReasonClosureMismatch)]
    public void Tampered_evidence_is_rejected_with_a_reason_code(string field, string value, string reason)
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, ["users.get"]);
        var (node, _) = SyntheticEvidence(index, closure);
        node[field] = value;
        var path = Path.Combine(Path.GetTempPath(), "tisilia-evidence-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, node.ToJsonString(TisiliaJson.IndentedOptions));
        try
        {
            var checkedEvidence = EvidenceValidator.Check(path, index, new HashSet<string> { "ci" }, new DiagnosticBag());
            Assert.False(checkedEvidence.Valid);
            Assert.Contains(reason, checkedEvidence.ReasonCodes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Skipped_cases_never_qualify_and_counts_cannot_hide_them()
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, ["users.get"]);
        var (node, _) = SyntheticEvidence(index, closure);
        node["counts"]!["skipped"] = 1;
        var path = Path.Combine(Path.GetTempPath(), "tisilia-evidence-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, node.ToJsonString(TisiliaJson.IndentedOptions));
        try
        {
            var checkedEvidence = EvidenceValidator.Check(path, index, new HashSet<string> { "ci" }, new DiagnosticBag());
            Assert.Contains(EvidenceValidator.ReasonSkipped, checkedEvidence.ReasonCodes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evidence_verdict_is_failed_when_a_required_category_has_no_passed_case()
    {
        var (index, _) = Sample();
        var closure = ContractClosure.Compute(index, ["users.get"]);
        var suite = SuiteBuilder.Build(index, closure, new SuiteOptions { Seed = 1, CasesPerCodec = 2 });
        var firstCategory = suite.RequiredTests[0];
        var results = suite.Cases.Select(c => new CaseResult(c.Id, c.Category, c.Category == firstCategory ? CaseStatus.Failed : CaseStatus.Passed, "")).ToList();
        var bag = new DiagnosticBag();
        var built = EvidenceBuilder.Build(index, closure, suite, results, new EvidenceInputs
        {
            IssuerId = "ci",
            Matrix = new RuntimeMatrix { Dotnet = "10.0.12", Aspnetcore = "10.0.12", Stj = "10.0.0", Typescript = "6.0.3", Os = "test", Architecture = "x64" },
            Context = [],
            ModuleArtifacts = closure.DeclaredModuleArtifacts(),
            ApplicationArtifacts = [new Artifact { Target = ArtifactTarget.Node, Path = "dist/index.js", Digest = "sha256:" + new string('b', 64) }],
            Limits = Limits.Default,
            RunnerDigest = "sha256:" + new string('c', 64),
            IssuedAt = DateTimeOffset.UnixEpoch,
        }, bag)!;
        Assert.Equal(Verdict.Failed, built.Evidence.Verdict);
        Assert.True(built.Evidence.Counts.Failed > 0);
        Assert.Equal(TisiliaHash.ClosureDigest(JsonSerializer.SerializeToNode(built.Closure, TisiliaJson.Options)!.AsObject()), built.Evidence.ClosureDigest);
    }
}
