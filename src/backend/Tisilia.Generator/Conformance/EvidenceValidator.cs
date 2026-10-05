using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Conformance;

/// <summary>
/// SV45/SV46/SV53: an evidence record qualifies operations only when its scope, semantic hash, recomputed closure
/// digest, verdict, counts, required tests and issuer all check out against the contract being generated. Declared
/// status never qualifies anything; a mismatch produces an unqualified entry with a reason code, not a warning.
/// </summary>
public static class EvidenceValidator
{
    public sealed record Checked(string Path, ConformanceEvidence? Evidence, IReadOnlyList<string> ReasonCodes)
    {
        public bool Valid => Evidence is not null && ReasonCodes.Count == 0;
    }

    public const string ReasonNoEvidence = "no-evidence";
    public const string ReasonUnreadable = "evidence-unreadable";
    public const string ReasonHashMismatch = "evidence-hash-mismatch";
    public const string ReasonClosureMismatch = "evidence-closure-mismatch";
    public const string ReasonScopeUnknown = "evidence-scope-unknown";
    public const string ReasonFailed = "evidence-failed";
    public const string ReasonSkipped = "evidence-skipped";
    public const string ReasonNoPasses = "evidence-no-passes";
    public const string ReasonUntrustedIssuer = "untrusted-issuer";
    public const string ReasonMissingRequiredTests = "missing-required-tests";
    public const string ReasonProtocol = "evidence-protocol-mismatch";
    public const string ReasonArtifactMismatch = "evidence-artifact-mismatch";

    /// <summary>
    /// Checks one evidence file against the contract. When <paramref name="artifactRoot"/> (the config directory) and
    /// <paramref name="outputRoot"/> (the generation output) are given, the evidence's node/browser application
    /// artifacts must still exist under the output with the recorded digests: evidence is bound to the executed client build.
    /// </summary>
    public static Checked Check(string path, ContractIndex index, IReadOnlySet<string> trustedIssuers, DiagnosticBag bag, string? artifactRoot = null, string? outputRoot = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            bag.Error(TisiliaCodes.EvidenceInvalid, "SV45", "", $"evidence file not found: {full}");
            return new Checked(full, null, [ReasonUnreadable]);
        }

        var local = new DiagnosticBag { File = full };
        var node = TisiliaSchemas.ParseStrict(File.ReadAllText(full), local);
        if (node is null || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.ConformanceEvidence, node, local))
        {
            bag.AddRange(local.Items);
            return new Checked(full, null, [ReasonUnreadable]);
        }

        ConformanceEvidence evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<ConformanceEvidence>(node, TisiliaJson.Options)!;
        }
        catch (JsonException e)
        {
            bag.Error(TisiliaCodes.EvidenceInvalid, "SV45", "", $"evidence '{full}' cannot be read: {e.Message}");
            return new Checked(full, null, [ReasonUnreadable]);
        }

        var reasons = new List<string>();
        void Reason(string code, string message)
        {
            reasons.Add(code);
            bag.Add(new Diagnostic { Code = TisiliaCodes.EvidenceInvalid, Severity = DiagnosticSeverity.Warning, Rule = "SV45", Path = "", File = full, Message = $"evidence '{evidence.Id}': {message}", RelatedIds = [evidence.Id] });
        }

        if (evidence.Abi != TisiliaJson.DraftVersion || evidence.Suite.Protocol != RunnerProtocol.ProtocolVersion || evidence.Claim != "observed-conformance")
        {
            Reason(ReasonProtocol, "abi/protocol/claim are not the 0.3 observed-conformance values");
        }

        if (!string.Equals(evidence.SemanticHash, index.Document.SemanticHash, StringComparison.Ordinal))
        {
            Reason(ReasonHashMismatch, $"semanticHash {evidence.SemanticHash} is not the contract's {index.Document.SemanticHash}; the evidence belongs to a different contract");
        }

        var unknownOps = evidence.Scope.OperationIds.Where(id => !index.Operations.ContainsKey(id)).ToList();
        if (unknownOps.Count > 0)
        {
            Reason(ReasonScopeUnknown, "scope names unknown operations: " + string.Join(", ", unknownOps));
        }

        if (!trustedIssuers.Contains(evidence.IssuerId))
        {
            Reason(ReasonUntrustedIssuer, $"issuer '{evidence.IssuerId}' is not in the trusted issuer list (pass --trusted-issuer)");
        }

        if (evidence.Verdict != Verdict.Passed)
        {
            Reason(ReasonFailed, "verdict is not 'passed'");
        }

        if (evidence.Counts.Failed > 0)
        {
            Reason(ReasonFailed, $"{evidence.Counts.Failed} failed case(s)");
        }

        if (evidence.Counts.Skipped > 0)
        {
            Reason(ReasonSkipped, $"{evidence.Counts.Skipped} skipped case(s); skips are never hidden as passes");
        }

        if (evidence.Counts.Passed == 0)
        {
            Reason(ReasonNoPasses, "no passed case");
        }

        if (artifactRoot is not null)
        {
            // Evidence is bound to the executed client build; a rebuilt client is a new artifact
            foreach (var artifact in evidence.ApplicationArtifacts.Where(a => a.Target != ArtifactTarget.Dotnet))
            {
                var fullArtifact = Path.GetFullPath(Path.Combine(artifactRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (outputRoot is not null && Path.GetRelativePath(Path.GetFullPath(outputRoot), fullArtifact).StartsWith("..", StringComparison.Ordinal))
                {
                    Reason(ReasonArtifactMismatch, $"application artifact '{artifact.Path}' is outside the generation output {outputRoot}; the evidence observed a different client build");
                    break;
                }

                if (!File.Exists(fullArtifact))
                {
                    Reason(ReasonArtifactMismatch, $"application artifact '{artifact.Path}' is not present under {artifactRoot}; the evidence observed a client build that no longer exists");
                    break;
                }

                var bytes = File.ReadAllBytes(fullArtifact);
                var actual = TisiliaHash.Sha256OfBytes(bytes);
                if (!string.Equals(actual, artifact.Digest, StringComparison.Ordinal))
                {
                    Reason(ReasonArtifactMismatch, $"application artifact '{artifact.Path}' has digest {actual} but the evidence observed {artifact.Digest}; " + (LineEndings.ArtifactHint(bytes, artifact.Digest, fullArtifact) ?? "rebuild the client and re-run conformance"));
                    break;
                }
            }
        }

        if (unknownOps.Count == 0 && string.Equals(evidence.SemanticHash, index.Document.SemanticHash, StringComparison.Ordinal))
        {
            var closure = ContractClosure.Compute(index, evidence.Scope.OperationIds);
            var record = closure.ToRecord(evidence.SemanticHash, evidence.ModuleArtifacts, evidence.ApplicationArtifacts, evidence.Matrix, evidence.Context, evidence.Limits);
            var recordNode = JsonSerializer.SerializeToNode(record, TisiliaJson.Options)!.AsObject();
            var digest = TisiliaHash.ClosureDigest(recordNode);
            if (!string.Equals(digest, evidence.ClosureDigest, StringComparison.Ordinal))
            {
                Reason(ReasonClosureMismatch, $"closureDigest {evidence.ClosureDigest} does not match the recomputed {digest} (SV53): artifacts, environment, context, limits or the reached definitions changed");
            }

            if (!record.Capabilities.SequenceEqual(evidence.Scope.Capabilities) || !record.EquivalenceIds.SequenceEqual(evidence.Scope.EquivalenceIds, StringComparer.Ordinal) || !record.OperationIds.SequenceEqual(evidence.Scope.OperationIds, StringComparer.Ordinal))
            {
                Reason(ReasonClosureMismatch, "scope capabilities/equivalences/operations are not the sorted closure of the scope's operations");
            }

            if (!record.ModuleArtifacts.SequenceEqual(evidence.ModuleArtifacts) || !record.ApplicationArtifacts.SequenceEqual(evidence.ApplicationArtifacts) || !record.Context.SequenceEqual(evidence.Context))
            {
                Reason(ReasonClosureMismatch, "moduleArtifacts/applicationArtifacts/context are not in the closure-record order or include unreached modules");
            }

            var required = SuiteBuilder.RequiredCategories(index, closure);
            var missing = required.Where(r => !evidence.RequiredTests.Contains(r, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                Reason(ReasonMissingRequiredTests, $"requiredTests lacks {missing.Count} categor{(missing.Count == 1 ? "y" : "ies")} the closure needs: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}");
            }
        }

        return new Checked(full, evidence, reasons.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Coverage per operation from checked evidences (SV46): qualified only through valid evidence covering the operation.</summary>
    public static IReadOnlyList<CoverageEntry> Coverage(ContractIndex index, IReadOnlyList<Checked> evidences)
    {
        var entries = new List<CoverageEntry>();
        foreach (var id in index.Operations.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!ContractClosure.Compute(index, [id]).RegistryRefs.Any(r => r.Registry == RegistryName.Codecs))
            {
                entries.Add(new CoverageEntry { OperationId = id, Status = CoverageStatus.Unqualified, EvidenceIds = [], ReasonCodes = ["codec-not-applicable", "http-unobserved"] });
                continue;
            }
            var covering = evidences.Where(e => e.Evidence is not null && e.Evidence.Scope.OperationIds.Contains(id, StringComparer.Ordinal)).ToList();
            var valid = covering.Where(e => e.Valid).ToList();
            if (valid.Count > 0)
            {
                entries.Add(new CoverageEntry
                {
                    OperationId = id,
                    Status = CoverageStatus.Qualified,
                    EvidenceIds = valid.Select(e => e.Evidence!.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    ReasonCodes = [],
                });
                continue;
            }

            var reasons = covering.SelectMany(e => e.ReasonCodes).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            entries.Add(new CoverageEntry
            {
                OperationId = id,
                Status = CoverageStatus.Unqualified,
                EvidenceIds = [],
                ReasonCodes = reasons.Count == 0 ? [ReasonNoEvidence] : reasons,
            });
        }

        return entries;
    }
}
