using System.Text;
using System.Text.Json.Nodes;
using Tisilia.Generator.Canonical;
using Xunit;

namespace Tisilia.Contract.Tests;

public class HashingTests
{
    [Fact]
    public void Semantic_hash_ignores_documentation_and_registry_order()
    {
        var a = SampleContracts.UsersApiJson();
        var b = (JsonObject)a.DeepClone();
        // reorder types and change documentation: hash must be stable
        var types = b["types"]!.AsArray();
        var items = types.ToList();
        foreach (var t in items)
        {
            types.Remove(t);
        }

        foreach (var t in items.AsEnumerable().Reverse())
        {
            types.Add(t);
        }

        b["documentation"] = new JsonArray(new JsonObject { ["targetId"] = "users.get", ["summary"] = "changed", ["description"] = "x" });
        Assert.Equal(TisiliaHash.SemanticHash(a), TisiliaHash.SemanticHash(b));
    }

    [Fact]
    public void Semantic_hash_changes_when_meaning_changes()
    {
        var a = SampleContracts.UsersApiJson();
        var b = (JsonObject)a.DeepClone();
        b["operations"]![0]!["route"] = "/users/{id:guid}/v2";
        Assert.NotEqual(TisiliaHash.SemanticHash(a), TisiliaHash.SemanticHash(b));
    }

    [Fact]
    public void Prefix_is_real_lf_not_escaped()
    {
        // The prefix ends with an actual 0x0A byte, never backslash + 'n'.
        var prefix = Encoding.UTF8.GetBytes(TisiliaJson.ContractHashPrefix);
        Assert.Equal(0x0A, prefix[^1]);
        Assert.DoesNotContain((byte)'\\', prefix);
    }

    [Fact]
    public void Semantic_hash_matches_reference_computation()
    {
        var root = SampleContracts.UsersApiJson();
        var projection = TisiliaHash.SemanticProjection(root);
        var bytes = Encoding.UTF8.GetBytes(TisiliaJson.ContractHashPrefix).Concat(Jcs.Serialize(projection)).ToArray();
        var expected = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.Equal(expected, root["semanticHash"]!.GetValue<string>());
        Assert.Null(projection["semanticHash"]);
        Assert.Null(projection["documentation"]);
    }

    [Fact]
    public void Request_identity_uses_prefix_and_hmac_variant_is_distinguishable()
    {
        var record = new JsonObject
        {
            ["format"] = "tisilia.request-identity-record",
            ["version"] = "0.1",
            ["operationId"] = "users.get",
            ["method"] = "GET",
            ["encodedPath"] = "/users/550e8400-e29b-41d4-a716-446655440000",
            ["queryEntries"] = new JsonArray(),
            ["selectedHeaderEntries"] = new JsonArray(),
            ["bodyKind"] = "none",
            ["bodyText"] = "",
            ["semanticHash"] = "sha256:" + new string('a', 64),
            ["scopeNonce"] = "0123456789abcdef0123456789abcdef",
        };
        var rid = TisiliaHash.RequestIdentity(record);
        Assert.StartsWith("rid:sha256:", rid, StringComparison.Ordinal);
        Assert.Equal(11 + 64, rid.Length);
        var key = new byte[32];
        key[0] = 1;
        var hmac = TisiliaHash.RequestIdentityHmac(record, key);
        Assert.StartsWith("rid:hmac-sha256:", hmac, StringComparison.Ordinal);
        Assert.NotEqual(rid[11..], hmac[16..]);
        Assert.Throws<ArgumentException>(() => TisiliaHash.RequestIdentityHmac(record, new byte[16]));
    }
}
