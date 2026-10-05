using System.Text.Json.Nodes;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

public class SchemaTests
{
    [Fact]
    public void All_bundled_schemas_load_and_resolve_common_definitions()
    {
        var schemas = TisiliaSchemas.Instance;
        var bag = new DiagnosticBag();
        var envelope = JsonNode.Parse("""
            {"format":"tisilia.hydration-envelope","version":"0.1","semanticHash":"sha256:0000000000000000000000000000000000000000000000000000000000000000",
             "operationId":"users.get","requestIdentity":"rid:sha256:0000000000000000000000000000000000000000000000000000000000000000",
             "scopeNonce":"0123456789abcdef","kind":"bodyless","responseCaseId":"users.delete.ok","status":204,"headers":[]}
            """)!;
        Assert.True(schemas.ValidateStructure(DocumentKind.HydrationEnvelope, envelope, bag), string.Join("\n", bag.Items));
    }

    [Fact]
    public void Envelope_with_body_on_bodyless_kind_is_rejected()
    {
        var bag = new DiagnosticBag();
        var envelope = JsonNode.Parse("""
            {"format":"tisilia.hydration-envelope","version":"0.1","semanticHash":"sha256:0000000000000000000000000000000000000000000000000000000000000000",
             "operationId":"users.get","requestIdentity":"rid:sha256:0000000000000000000000000000000000000000000000000000000000000000",
             "scopeNonce":"0123456789abcdef","kind":"bodyless","responseCaseId":"x","status":204,"headers":[],"bodyText":"null"}
            """)!;
        Assert.False(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.HydrationEnvelope, envelope, bag));
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.SchemaViolation);
    }

    [Fact]
    public void Runner_request_and_failure_records_validate()
    {
        var bag = new DiagnosticBag();
        var request = JsonNode.Parse("""
            {"format":"tisilia.runner-message","version":"0.1","sessionId":"s1","requestId":"r1","kind":"request","action":"ts-encode-request",
             "adapterId":"demo.money.request","profileId":"sample-api.profile.web","context":[],"inputs":[{"kind":"object","entries":[{"name":"amount","value":{"kind":"number","text":"123.4500"}}]}]}
            """)!;
        Assert.True(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.RunnerMessage, request, bag), string.Join("\n", bag.Items));
        var failure = JsonNode.Parse("""
            {"format":"tisilia.runner-message","version":"0.1","sessionId":"s1","requestId":"r1","kind":"failure","code":"codec","safeMessageId":"codec.invalid-amount","path":"/amount"}
            """)!;
        Assert.True(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.RunnerMessage, failure, bag), string.Join("\n", bag.Items));
    }

    [Fact]
    public void Config_document_validates_and_rejects_unknown_target()
    {
        var bag = new DiagnosticBag();
        var config = JsonNode.Parse("""
            {"format":"tisilia.config","version":"0.1","apiId":"sample-api","contract":"artifacts/tisilia.contract.json","output":"src/Web/app/generated/tisilia",
             "target":{"typescriptMinimumMajor":6,"ecmaScript":"ES2022","moduleMode":"bundler"},"selection":"explicit","coveragePolicy":"development","modules":[],"portableProjects":[],
             "limits":{"maxBodyBytes":16777216,"maxDepth":64,"maxTokens":1000000,"maxNumberCharacters":4096,"timeoutMs":30000,"maxDiagnosticBytes":262144},
             "nuxt":{"enabled":true,"hydration":"browser-safe-only","sharedCache":false}}
            """)!;
        Assert.True(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Config, config, bag), string.Join("\n", bag.Items));
        config["target"]!["typescriptMinimumMajor"] = 5;
        Assert.False(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Config, config, new DiagnosticBag()));
    }

    [Fact]
    public void Safe_path_pattern_rejects_traversal_and_absolute_paths()
    {
        foreach (var bad in new[] { "../x", "/abs", "C:\\x", "a/../b", "a\\b" })
        {
            var bag = new DiagnosticBag();
            var config = JsonNode.Parse("""
                {"format":"tisilia.config","version":"0.1","apiId":"sample-api","contract":"c.json","output":"out",
                 "target":{"typescriptMinimumMajor":6,"ecmaScript":"ES2022","moduleMode":"bundler"},"selection":"explicit","coveragePolicy":"development","modules":[],"portableProjects":[],
                 "limits":{"maxBodyBytes":16777216,"maxDepth":64,"maxTokens":1000000,"maxNumberCharacters":4096,"timeoutMs":30000,"maxDiagnosticBytes":262144},
                 "nuxt":{"enabled":false,"hydration":"browser-safe-only","sharedCache":false}}
                """)!;
            config["output"] = bad;
            Assert.False(TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Config, config, bag), bad);
        }
    }
}
