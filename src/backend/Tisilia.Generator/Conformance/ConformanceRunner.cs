using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using JsonValue = Tisilia.Contract.JsonValue;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Conformance;

public enum CaseStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record CaseResult(string Id, string Category, CaseStatus Status, string Message);

/// <summary>Executes suite cases against the two runners. A protocol failure of a runner fails every remaining case.</summary>
public sealed class ConformanceExecutor(RunnerClient dotnet, RunnerClient node, ContractIndex index, TimeSpan timeout, TextWriter log)
{
    private RunnerProtocolException? _broken;

    public async Task<IReadOnlyList<CaseResult>> ExecuteAllAsync(ConformanceSuite suite, CancellationToken cancellationToken)
    {
        var results = new List<CaseResult>(suite.Cases.Count);
        var n = 0;
        foreach (var c in suite.Cases)
        {
            n++;
            CaseResult result;
            if (_broken is not null)
            {
                result = new CaseResult(c.Id, c.Category, CaseStatus.Failed, "runner unavailable: " + _broken.Message);
            }
            else
            {
                try
                {
                    result = await ExecuteAsync(c, cancellationToken).ConfigureAwait(false);
                }
                catch (RunnerProtocolException e)
                {
                    _broken = e;
                    result = new CaseResult(c.Id, c.Category, CaseStatus.Failed, "runner protocol failure: " + e.Message);
                }
            }

            results.Add(result);
            if (result.Status != CaseStatus.Passed)
            {
                log.WriteLine($"[{result.Status.ToString().ToLowerInvariant()}] {result.Id}: {result.Message}");
            }
            else if (n % 50 == 0)
            {
                log.WriteLine($"… {n}/{suite.Cases.Count} cases");
            }
        }

        return results;
    }

    public Task<CaseResult> ExecuteAsync(ConformanceCase c, CancellationToken ct) => c switch
    {
        ResponseRoundTripCase r => ResponseAsync(r, ct),
        RequestRoundTripCase r => RequestAsync(r, ct),
        KeyRoundTripCase k => KeyAsync(k, ct),
        NegativeWireCase nw => NegativeAsync(nw, ct),
        DomainValidationCase dv => ValidationAsync(dv, ct),
        OracleDiscriminationCase od => DiscriminationAsync(od, ct),
        _ => Task.FromResult(new CaseResult(c.Id, c.Category, CaseStatus.Failed, "unknown case kind")),
    };

    private IReadOnlyList<NameValue> ContextFor(string codecId)
    {
        // a builtin codec's binding context (the server's time zone of the DateTime codecs) is in the generated registry already
        if (index.Codecs.TryGetValue(codecId, out var codec) && codec.Origin != CodecOrigin.Builtin && index.Bindings.TryGetValue(codec.BindingId, out var binding))
        {
            return binding.Context.Where(e => !e.Confidential).Select(e => new NameValue { Name = e.Name, Value = e.Value }).ToList();
        }

        return [];
    }

    private async Task<(JsonValue? Output, RunnerFailure? Failure)> StepAsync(RunnerClient runner, RunnerAction action, string adapterId, string profileId, IReadOnlyList<NameValue> context, IReadOnlyList<JsonValue> inputs, CancellationToken ct)
    {
        var message = await runner.SendAsync(action, adapterId, profileId, context, inputs, timeout, ct).ConfigureAwait(false);
        return message switch
        {
            RunnerSuccess s => (s.Outputs[0], null),
            RunnerFailure f => (null, f),
            _ => throw new RunnerProtocolException(runner.Name, "unexpected record kind"),
        };
    }

    private static string Fail(RunnerFailure f) => $"{f.Code.ToString().ToLowerInvariant()} ({f.SafeMessageId}) at '{f.Path}'";

    private async Task<(bool? Equal, string Detail)> CompareAsync(RunnerClient runner, string equivalenceId, string profileId, JsonValue a, JsonValue b, CancellationToken ct)
    {
        var (output, failure) = await StepAsync(runner, RunnerAction.Compare, equivalenceId, profileId, [], [a, b], ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return (null, $"{runner.Name} compare failed: {Fail(failure)}");
        }

        return output is JsonBooleanValue bv ? (bv.Value, "") : (null, $"{runner.Name} compare returned a non-boolean AST");
    }

    private async Task<CaseResult> CompareBothAsync(ConformanceCase c, string equivalenceId, JsonValue expected, JsonValue actual, bool expectEqual, string prefix, CancellationToken ct)
    {
        var (nodeEq, nodeDetail) = await CompareAsync(node, equivalenceId, c.ProfileId, expected, actual, ct).ConfigureAwait(false);
        var (dotnetEq, dotnetDetail) = await CompareAsync(dotnet, equivalenceId, c.ProfileId, expected, actual, ct).ConfigureAwait(false);
        if (nodeEq is null || dotnetEq is null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, prefix + string.Join("; ", new[] { nodeDetail, dotnetDetail }.Where(s => s.Length > 0)));
        }

        if (nodeEq != dotnetEq)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"{prefix}oracle disagreement: node={nodeEq}, dotnet={dotnetEq} for {DomainAst.Describe(expected)} vs {DomainAst.Describe(actual)}");
        }

        if (nodeEq.Value != expectEqual)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, expectEqual
                ? $"{prefix}mismatch: expected {DomainAst.Describe(expected)} but observed {DomainAst.Describe(actual)}"
                : $"{prefix}oracle cannot discriminate {DomainAst.Describe(expected)} from {DomainAst.Describe(actual)}");
        }

        return new CaseResult(c.Id, c.Category, CaseStatus.Passed, "");
    }

    private async Task<CaseResult> ResponseAsync(ResponseRoundTripCase c, CancellationToken ct)
    {
        var context = ContextFor(c.CodecId);
        var (wire, wf) = await StepAsync(dotnet, RunnerAction.DotnetWrite, c.CodecId, c.ProfileId, context, [c.Domain], ct).ConfigureAwait(false);
        if (wf is not null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"dotnet-write rejected {DomainAst.Describe(c.Domain)}: {Fail(wf)}");
        }

        var (decoded, df) = await StepAsync(node, RunnerAction.TsDecodeResponse, c.CodecId, c.ProfileId, context, [wire!], ct).ConfigureAwait(false);
        if (df is not null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"ts-decode-response rejected the server wire {DomainAst.Describe(wire!)}: {Fail(df)}");
        }

        var (expected, projectionFailure) = await ProjectExpectedAsync(c, c.Expected, c.Projections, ct).ConfigureAwait(false);
        if (projectionFailure is not null)
        {
            return projectionFailure;
        }

        return await CompareBothAsync(c, c.EquivalenceId, expected, decoded!, expectEqual: true, $"wire {DomainAst.Describe(wire!)}: ", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalized behaviors: what the server constructs/writes is the projection of the generated value. Both
    /// implementations of each projection must agree before the server's value is compared with the result.
    /// </summary>
    private async Task<(JsonValue Expected, CaseResult? Failure)> ProjectExpectedAsync(ConformanceCase c, JsonValue expected, IReadOnlyList<ProjectionStep>? projections, CancellationToken ct)
    {
        foreach (var step in projections ?? [])
        {
            var inputs = new[] { expected, DomainAst.String(step.Path) };
            var (byNode, nf) = await StepAsync(node, RunnerAction.TsProject, step.ProjectionId, c.ProfileId, [], inputs, ct).ConfigureAwait(false);
            if (nf is not null)
            {
                return (expected, new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"ts-project '{step.ProjectionId}' at '{step.Path}' failed: {Fail(nf)}"));
            }

            var (byDotnet, df) = await StepAsync(dotnet, RunnerAction.DotnetProject, step.ProjectionId, c.ProfileId, [], inputs, ct).ConfigureAwait(false);
            if (df is not null)
            {
                return (expected, new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"dotnet-project '{step.ProjectionId}' at '{step.Path}' failed: {Fail(df)}"));
            }

            if (!AstOracles.Structural(byNode!, byDotnet!))
            {
                return (expected, new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"projection '{step.ProjectionId}' differs between the runners: node {DomainAst.Describe(byNode!)} vs dotnet {DomainAst.Describe(byDotnet!)}"));
            }

            expected = byNode!;
        }

        return (expected, null);
    }

    private async Task<CaseResult> RequestAsync(RequestRoundTripCase c, CancellationToken ct)
    {
        var context = ContextFor(c.CodecId);
        var (wire, ef) = await StepAsync(node, RunnerAction.TsEncodeRequest, c.CodecId, c.ProfileId, context, [c.Domain], ct).ConfigureAwait(false);
        if (ef is not null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"ts-encode-request rejected {DomainAst.Describe(c.Domain)}: {Fail(ef)}");
        }

        var (read, rf) = await StepAsync(dotnet, RunnerAction.DotnetRead, c.CodecId, c.ProfileId, context, [wire!], ct).ConfigureAwait(false);
        if (rf is not null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"dotnet-read rejected the client wire {DomainAst.Describe(wire!)}: {Fail(rf)}");
        }

        var (expected, projectionFailure) = await ProjectExpectedAsync(c, c.Expected, c.Projections, ct).ConfigureAwait(false);
        if (projectionFailure is not null)
        {
            return projectionFailure;
        }

        return await CompareBothAsync(c, c.EquivalenceId, expected, read!, expectEqual: true, $"wire {DomainAst.Describe(wire!)}: ", ct).ConfigureAwait(false);
    }

    private async Task<CaseResult> KeyAsync(KeyRoundTripCase c, CancellationToken ct)
    {
        var context = ContextFor(c.CodecId);
        JsonValue? encoded;
        JsonValue? decoded;
        RunnerFailure? failure;
        if (c.RequestDirection)
        {
            (encoded, failure) = await StepAsync(node, RunnerAction.TsEncodeKey, c.CodecId, c.ProfileId, context, [c.Key], ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"ts-encode-key rejected {DomainAst.Describe(c.Key)}: {Fail(failure)}");
            }

            if (encoded is not JsonStringValue)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, "ts-encode-key returned a non-string AST");
            }

            (decoded, failure) = await StepAsync(dotnet, RunnerAction.DotnetReadKey, c.CodecId, c.ProfileId, context, [encoded], ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"dotnet-read-key rejected {DomainAst.Describe(encoded)}: {Fail(failure)}");
            }
        }
        else
        {
            (encoded, failure) = await StepAsync(dotnet, RunnerAction.DotnetWriteKey, c.CodecId, c.ProfileId, context, [c.Key], ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"dotnet-write-key rejected {DomainAst.Describe(c.Key)}: {Fail(failure)}");
            }

            if (encoded is not JsonStringValue)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, "dotnet-write-key returned a non-string AST");
            }

            (decoded, failure) = await StepAsync(node, RunnerAction.TsDecodeKey, c.CodecId, c.ProfileId, context, [encoded], ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"ts-decode-key rejected {DomainAst.Describe(encoded)}: {Fail(failure)}");
            }
        }

        return await CompareBothAsync(c, c.EquivalenceId, c.Expected ?? c.Key, decoded!, expectEqual: true, $"key text {DomainAst.Describe(encoded)}: ", ct).ConfigureAwait(false);
    }

    private async Task<CaseResult> NegativeAsync(NegativeWireCase c, CancellationToken ct)
    {
        var runner = c.Target == RunnerTarget.Node ? node : dotnet;
        var action = c.Target == RunnerTarget.Node ? RunnerAction.TsDecodeResponse : RunnerAction.DotnetRead;
        var (output, failure) = await StepAsync(runner, action, c.CodecId, c.ProfileId, ContextFor(c.CodecId), [c.Wire], ct).ConfigureAwait(false);
        if (failure is null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"{action} accepted invalid wire {DomainAst.Describe(c.Wire)} ({c.Reason}) and produced {DomainAst.Describe(output!)}");
        }

        return failure.Code is RunnerFailureCode.Codec or RunnerFailureCode.InvalidInput
            ? new CaseResult(c.Id, c.Category, CaseStatus.Passed, "")
            : new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"{action} failed with '{failure.Code}' instead of a codec failure for {DomainAst.Describe(c.Wire)}: {Fail(failure)}");
    }

    private async Task<CaseResult> ValidationAsync(DomainValidationCase c, CancellationToken ct)
    {
        var (output, failure) = await StepAsync(node, RunnerAction.ValidateDomain, c.CodecId, c.ProfileId, ContextFor(c.CodecId), [c.Domain], ct).ConfigureAwait(false);
        if (c.ExpectValid)
        {
            if (failure is not null)
            {
                return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"validate-domain rejected a valid value {DomainAst.Describe(c.Domain)}: {Fail(failure)}");
            }

            return output is JsonBooleanValue { Value: true }
                ? new CaseResult(c.Id, c.Category, CaseStatus.Passed, "")
                : new CaseResult(c.Id, c.Category, CaseStatus.Failed, "validate-domain did not return boolean true");
        }

        if (failure is null)
        {
            return new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"validate-domain accepted invalid value {DomainAst.Describe(c.Domain)}");
        }

        return failure.Code is RunnerFailureCode.Codec or RunnerFailureCode.InvalidInput
            ? new CaseResult(c.Id, c.Category, CaseStatus.Passed, "")
            : new CaseResult(c.Id, c.Category, CaseStatus.Failed, $"validate-domain failed with '{failure.Code}' instead of a codec failure: {Fail(failure)}");
    }

    private Task<CaseResult> DiscriminationAsync(OracleDiscriminationCase c, CancellationToken ct)
        => CompareBothAsync(c, c.EquivalenceId, c.A, c.B, expectEqual: false, "", ct);
}

/// <summary>Everything the evidence needs besides the case results: runner reports, artifacts and limits.</summary>
public sealed record EvidenceInputs
{
    public required string IssuerId { get; init; }
    public required RuntimeMatrix Matrix { get; init; }
    public required IReadOnlyList<NameValue> Context { get; init; }
    public required IReadOnlyList<IdentifiedArtifact> ModuleArtifacts { get; init; }
    public required IReadOnlyList<Artifact> ApplicationArtifacts { get; init; }
    public required Limits Limits { get; init; }
    public required string RunnerDigest { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
}

/// <summary>Assembles and validates the <c>tisilia.conformance-evidence</c> record.</summary>
public static class EvidenceBuilder
{
    public sealed record Built(ConformanceEvidence Evidence, ClosureRecord Closure, JsonObject Node);

    public static Built? Build(ContractIndex index, ContractClosure closure, ConformanceSuite suite, IReadOnlyList<CaseResult> results, EvidenceInputs inputs, DiagnosticBag bag)
    {
        var closureRecord = closure.ToRecord(index.Document.SemanticHash, inputs.ModuleArtifacts, inputs.ApplicationArtifacts, inputs.Matrix, inputs.Context, inputs.Limits);
        var closureNode = JsonSerializer.SerializeToNode(closureRecord, TisiliaJson.Options)!.AsObject();
        if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.ClosureRecord, closureNode, bag))
        {
            return null;
        }

        var closureDigest = TisiliaHash.ClosureDigest(closureNode);
        var passed = results.Count(r => r.Status == CaseStatus.Passed);
        var failed = results.Count(r => r.Status == CaseStatus.Failed);
        var skipped = results.Count(r => r.Status == CaseStatus.Skipped);
        // every required category must have at least one passed case; a category with no passed case cannot be claimed
        var coveredCategories = results.Where(r => r.Status == CaseStatus.Passed).Select(r => r.Category).ToHashSet(StringComparer.Ordinal);
        var uncovered = suite.RequiredTests.Where(t => !coveredCategories.Contains(t)).ToList();
        var verdict = failed == 0 && skipped == 0 && passed > 0 && uncovered.Count == 0 ? Verdict.Passed : Verdict.Failed;
        var issuedAt = inputs.IssuedAt.ToUniversalTime();
        var evidence = new ConformanceEvidence
        {
            Format = TisiliaJson.Formats.ConformanceEvidence,
            Version = TisiliaJson.DraftVersion,
            Id = "ev." + RunnerProtocol.SafeId(index.Document.ApiId) + "." + issuedAt.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "." + closureDigest["sha256:".Length..][..12],
            IssuerId = inputs.IssuerId,
            IssuedAt = issuedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            Verdict = verdict,
            SemanticHash = index.Document.SemanticHash,
            ClosureDigest = closureDigest,
            Abi = TisiliaJson.DraftVersion,
            Scope = new EvidenceScope
            {
                OperationIds = closureRecord.OperationIds,
                Capabilities = closureRecord.Capabilities,
                EquivalenceIds = closureRecord.EquivalenceIds,
            },
            Matrix = inputs.Matrix,
            ModuleArtifacts = closureRecord.ModuleArtifacts,
            ApplicationArtifacts = closureRecord.ApplicationArtifacts,
            Context = closureRecord.Context,
            Suite = new EvidenceSuite
            {
                Id = suite.Id,
                Version = suite.Version,
                Digest = suite.Digest(),
                Seed = suite.Seed,
                RunnerDigest = inputs.RunnerDigest,
                Protocol = RunnerProtocol.ProtocolVersion,
            },
            Counts = new EvidenceCounts { Passed = passed, Failed = failed, Skipped = skipped },
            RequiredTests = suite.RequiredTests,
            Limits = inputs.Limits,
            Claim = "observed-conformance",
        };
        var node = JsonSerializer.SerializeToNode(evidence, TisiliaJson.Options)!.AsObject();
        if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.ConformanceEvidence, node, bag))
        {
            return null;
        }

        return new Built(evidence, closureRecord, node);
    }
}
