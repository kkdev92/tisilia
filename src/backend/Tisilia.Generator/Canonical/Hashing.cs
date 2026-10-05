using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tisilia.Generator.Canonical;

/// <summary>Hash recipes. All digests are <c>sha256:</c> + 64 lowercase hex characters.</summary>
public static class TisiliaHash
{
    /// <summary>Top-level registries sorted by id before hashing.</summary>
    public static readonly string[] SortedRegistries =
    [
        "profiles", "types", "wires", "codecs", "bindings", "equivalences", "projections", "comparers", "binders", "resultAdapters", "operations", "modules",
    ];

    public static string FormatDigest(ReadOnlySpan<byte> hash) => "sha256:" + Convert.ToHexStringLower(hash);

    public static string Sha256OfBytes(ReadOnlySpan<byte> bytes) => FormatDigest(SHA256.HashData(bytes));

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return FormatDigest(SHA256.HashData(stream));
    }

    private static string PrefixedHash(string prefix, byte[] jcs)
    {
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var buffer = new byte[prefixBytes.Length + jcs.Length];
        prefixBytes.CopyTo(buffer, 0);
        jcs.CopyTo(buffer, prefixBytes.Length);
        return Sha256OfBytes(buffer);
    }

    /// <summary>Semantic projection of a contract: drop top-level <c>semanticHash</c> and <c>documentation</c>, sort registries by id, keep all other order.</summary>
    public static JsonObject SemanticProjection(JsonObject contract)
    {
        var clone = (JsonObject)contract.DeepClone();
        clone.Remove("semanticHash");
        clone.Remove("documentation");
        foreach (var registry in SortedRegistries)
        {
            if (clone[registry] is JsonArray array)
            {
                var items = array.Select(n => n).ToList();
                foreach (var item in items)
                {
                    array.Remove(item);
                }

                var sorted = items.OrderBy(n => n?["id"]?.GetValue<string>() ?? "", Utf16CodeUnitComparer.Instance).ToList();
                foreach (var item in sorted)
                {
                    array.Add(item);
                }
            }
        }

        return clone;
    }

    /// <summary><c>sha256(UTF8("TISILIA-CONTRACT/0.3\n") || JCS(projection))</c>.</summary>
    public static string SemanticHash(JsonObject contract)
        => PrefixedHash(TisiliaJson.ContractHashPrefix, Jcs.Serialize(SemanticProjection(contract)));

    /// <summary>Profile fingerprint: remove only <c>fingerprint</c>, keep arrays, prefix <c>TISILIA-PROFILE/0.3\n</c>.</summary>
    public static string ProfileFingerprint(JsonObject profile)
    {
        var clone = (JsonObject)profile.DeepClone();
        clone.Remove("fingerprint");
        return PrefixedHash(TisiliaJson.ProfileHashPrefix, Jcs.Serialize(clone));
    }

    /// <summary>JCS of the closure record with prefix <c>TISILIA-CLOSURE/0.3\n</c>.</summary>
    public static string ClosureDigest(JsonObject closureRecord)
        => PrefixedHash(TisiliaJson.ClosureHashPrefix, Jcs.Serialize(closureRecord));

    /// <summary>Browser-computable request identity <c>rid:sha256:&lt;hex&gt;</c>.</summary>
    public static string RequestIdentity(JsonObject requestIdentityRecord)
    {
        var digest = PrefixedHash(TisiliaJson.RequestHashPrefix, Jcs.Serialize(requestIdentityRecord));
        return "rid:" + digest;
    }

    /// <summary>Server-side request identity with a scope-private 256-bit key, <c>rid:hmac-sha256:&lt;hex&gt;</c>.</summary>
    public static string RequestIdentityHmac(JsonObject requestIdentityRecord, ReadOnlySpan<byte> scopeKey)
    {
        if (scopeKey.Length < 32)
        {
            throw new ArgumentException("scope key must be at least 256 bits", nameof(scopeKey));
        }

        var prefixBytes = Encoding.UTF8.GetBytes(TisiliaJson.RequestHashPrefix);
        var jcs = Jcs.Serialize(requestIdentityRecord);
        var buffer = new byte[prefixBytes.Length + jcs.Length];
        prefixBytes.CopyTo(buffer, 0);
        jcs.CopyTo(buffer, prefixBytes.Length);
        var mac = HMACSHA256.HashData(scopeKey, buffer);
        return "rid:hmac-sha256:" + Convert.ToHexStringLower(mac);
    }
}
