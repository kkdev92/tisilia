using System.Text.Json.Nodes;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Diff;
using Xunit;
using Xunit.Abstractions;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The per-direction compatibility diff between two contract versions. Every modified contract is
/// re-hashed so that the diff compares valid documents, never a hash mismatch.
/// </summary>
public class ContractDiffTests(ITestOutputHelper output)
{
    [Fact]
    public void Form_file_cardinality_changes_are_breaking()
    {
        var oldRoot = SampleContracts.UsersApiJson();
        oldRoot["operations"]![1]!["requestBody"] = new JsonObject
        {
            ["kind"] = "form",
            ["mediaType"] = "multipart/form-data",
            ["presence"] = "required",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "file", ["kind"] = "file", ["repeated"] = false, ["presence"] = "required" }),
        };
        var changed = oldRoot.DeepClone().AsObject();
        changed["operations"]![1]!["requestBody"]!["fields"]![0]!["repeated"] = true;
        Assert.Contains(Diff(oldRoot, changed).Breaking, d => d.Kind == "body-changed");
    }

    private static string WriteTemp(JsonObject root)
    {
        root["semanticHash"] = TisiliaHash.SemanticHash(root);
        var path = Path.Combine(Path.GetTempPath(), "tisilia-diff-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private DiffResult Diff(JsonObject oldRoot, JsonObject newRoot)
    {
        var oldPath = WriteTemp(oldRoot);
        var newPath = WriteTemp(newRoot);
        try
        {
            var bag = new DiagnosticBag();
            var result = ContractDiff.Compare(oldPath, newPath, bag);
            foreach (var d in bag.Items)
            {
                output.WriteLine(d.ToString());
            }

            Assert.False(bag.HasErrors);
            Assert.NotNull(result);
            foreach (var (kind, entries) in new[] { ("breaking", result!.Breaking), ("compatible", result.Compatible), ("review", result.ReviewRequired) })
            {
                foreach (var e in entries)
                {
                    output.WriteLine($"{kind}: {e.Kind} {e.Id} — {e.Message}");
                }
            }

            return result;
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }

    private static JsonObject Operation(JsonObject root, string id) => root["operations"]!.AsArray().First(o => o!["id"]!.GetValue<string>() == id)!.AsObject();

    [Fact]
    public void Identical_contracts_have_no_differences_and_the_same_hash()
    {
        var result = Diff(SampleContracts.UsersApiJson(), SampleContracts.UsersApiJson());
        Assert.Equal(result.OldHash, result.NewHash);
        Assert.Empty(result.Breaking);
        Assert.Empty(result.Compatible);
        Assert.Empty(result.ReviewRequired);
    }

    [Theory]
    [InlineData("application/pdf", "optional")]
    [InlineData("application/octet-stream", "required")]
    public void Raw_upload_media_and_required_presence_changes_are_breaking(string media, string presence)
    {
        var oldRoot = SampleContracts.UsersApiJson();
        var newRoot = SampleContracts.UsersApiJson();
        Operation(oldRoot, "users.put")["requestBody"] = new JsonObject { ["kind"] = "binary", ["mediaType"] = "application/octet-stream", ["presence"] = "optional" };
        Operation(newRoot, "users.put")["requestBody"] = new JsonObject { ["kind"] = "binary", ["mediaType"] = media, ["presence"] = presence };
        Assert.Contains(Diff(oldRoot, newRoot).Breaking, entry => entry.Kind == "body-changed");
    }

    [Fact]
    public void Changing_JSON_to_raw_bytes_is_breaking()
    {
        var oldRoot = SampleContracts.UsersApiJson();
        var newRoot = SampleContracts.UsersApiJson();
        Operation(newRoot, "users.put")["requestBody"] = new JsonObject { ["kind"] = "binary", ["mediaType"] = "application/json", ["presence"] = "required" };
        Assert.Contains(Diff(oldRoot, newRoot).Breaking, entry => entry.Kind == "body-changed");
    }

    [Theory]
    [InlineData("/users/{*id}", "/users/{**id}")]
    [InlineData("/users/{id}", "/users/{id?}")]
    [InlineData("/users/{id=11111111-1111-1111-1111-111111111111}", "/users/{id=22222222-2222-2222-2222-222222222222}")]
    public void RT25_Route_execution_changes_invalidate_semantic_identity_and_diff(string before, string after)
    {
        var oldRoot = SampleContracts.UsersApiJson(); var newRoot = SampleContracts.UsersApiJson();
        RouteTestContracts.SetRoute(Operation(oldRoot, "users.get"), before);
        RouteTestContracts.SetRoute(Operation(newRoot, "users.get"), after);
        var result = Diff(oldRoot, newRoot);
        Assert.NotEqual(result.OldHash, result.NewHash);
        Assert.Contains(result.Breaking, e => e.Kind == "route-changed" && e.Id == "users.get");
        // Every closure record includes semanticHash; old evidence cannot qualify the changed route.
        var oldLoaded = Tisilia.Generator.Validation.ContractLoader.Load(oldRoot.ToJsonString(), new DiagnosticBag())!;
        var newLoaded = Tisilia.Generator.Validation.ContractLoader.Load(newRoot.ToJsonString(), new DiagnosticBag())!;
        var oldIndex = Tisilia.Generator.Validation.SemanticValidator.Validate(oldLoaded, new DiagnosticBag())!;
        var newIndex = Tisilia.Generator.Validation.SemanticValidator.Validate(newLoaded, new DiagnosticBag())!;
        var closure = Tisilia.Generator.Closure.ContractClosure.Compute(oldIndex, ["users.get"]);
        var (evidence, _) = ConformanceTests.SyntheticEvidence(oldIndex, closure);
        var path = Path.Combine(Path.GetTempPath(), "tisilia-route-evidence-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, evidence.ToJsonString());
        try
        {
            var checkedEvidence = Tisilia.Generator.Conformance.EvidenceValidator.Check(path, newIndex, new HashSet<string> { "ci" }, new DiagnosticBag());
            Assert.False(checkedEvidence.Valid);
            Assert.Contains(Tisilia.Generator.Conformance.EvidenceValidator.ReasonHashMismatch, checkedEvidence.ReasonCodes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Removed_operation_and_changed_route_are_breaking()
    {
        var changed = SampleContracts.UsersApiJson();
        changed["operations"]!.AsArray().Remove(Operation(changed, "users.put"));
        RouteTestContracts.SetRoute(Operation(changed, "users.get"), "/v2/users/{id:guid}");
        var result = Diff(SampleContracts.UsersApiJson(), changed);
        Assert.NotEqual(result.OldHash, result.NewHash);
        Assert.Contains(result.Breaking, e => e.Kind == "operation-removed" && e.Id == "users.put");
        Assert.Contains(result.Breaking, e => e.Kind == "route-changed" && e.Id == "users.get");
        Assert.Empty(result.Compatible);
    }

    [Fact]
    public void Added_operation_is_compatible_and_old_clients_keep_working()
    {
        var changed = SampleContracts.UsersApiJson();
        var added = JsonNode.Parse(Operation(changed, "users.get").ToJsonString().Replace("users.get", "users.head", StringComparison.Ordinal))!.AsObject();
        RouteTestContracts.SetRoute(added, "/users/{id:guid}/head");
        changed["operations"]!.AsArray().Add(added);
        var result = Diff(SampleContracts.UsersApiJson(), changed);
        Assert.Contains(result.Compatible, e => e.Kind == "operation-added" && e.Id == "users.head");
        Assert.Empty(result.Breaking);
        Assert.Empty(result.ReviewRequired);
    }

    [Fact]
    public void Hydration_and_serializer_profile_changes_require_review_without_being_breaking()
    {
        var changed = SampleContracts.UsersApiJson();
        // the 200 case moves from browser-safe to server-only: the same wire, a different Nuxt payload policy
        Operation(changed, "users.get")["responses"]![0]!["hydration"] = "server-only";
        // a serializer option changes: the fingerprint changes with it, the TypeScript types do not
        var profile = changed["profiles"]![0]!.AsObject();
        profile["options"]!["allowTrailingCommas"] = true;
        profile["fingerprint"] = TisiliaHash.ProfileFingerprint(profile);
        var result = Diff(SampleContracts.UsersApiJson(), changed);
        Assert.Contains(result.ReviewRequired, e => e.Kind == "hydration-changed" && e.Id == "users.get");
        Assert.Contains(result.ReviewRequired, e => e.Kind == "profile-changed" && e.Id == SampleContracts.ProfileId);
        Assert.Empty(result.Breaking);
        Assert.Empty(result.Compatible);
    }

    [Fact]
    public void Removed_response_case_is_breaking_and_an_added_case_needs_review()
    {
        var changed = SampleContracts.UsersApiJson();
        var responses = Operation(changed, "users.put")["responses"]!.AsArray();
        var notFound = responses.First(r => r!["id"]!.GetValue<string>() == "users.put.not-found")!;
        responses.Remove(notFound);
        var conflict = JsonNode.Parse(notFound.ToJsonString().Replace("users.put.not-found", "users.put.conflict", StringComparison.Ordinal))!.AsObject();
        conflict["status"] = 409;
        responses.Add(conflict);
        var result = Diff(SampleContracts.UsersApiJson(), changed);
        Assert.Contains(result.Breaking, e => e.Kind == "response-removed" && e.Id == "users.put");
        Assert.Contains(result.ReviewRequired, e => e.Kind == "response-added" && e.Id == "users.put");
    }
}
