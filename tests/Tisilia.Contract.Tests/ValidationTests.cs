using System.Text.Json.Nodes;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;
using Xunit.Abstractions;

namespace Tisilia.Contract.Tests;

public class ValidationTests(ITestOutputHelper output)
{
    private static (LoadedContract? Loaded, DiagnosticBag Diagnostics) Load(JsonObject root)
    {
        var bag = new DiagnosticBag();
        var loaded = ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag);
        return (loaded, bag);
    }

    private (ContractIndex? Index, DiagnosticBag Diagnostics) ValidateAll(JsonObject root, bool verifyHashes = true)
    {
        var (loaded, bag) = Load(root);
        if (loaded is null)
        {
            return (null, bag);
        }

        var index = SemanticValidator.Validate(loaded, bag, verifyHashes);
        foreach (var d in bag.Items)
        {
            output.WriteLine(d.ToString());
        }

        return (index, bag);
    }

    [Fact]
    public void Sample_contract_is_structurally_and_semantically_valid()
    {
        var (index, bag) = ValidateAll(SampleContracts.UsersApiJson());
        Assert.Empty(bag.Items);
        Assert.NotNull(index);
        Assert.Equal(2, index!.Operations.Count);
    }

    [Fact]
    public void Unknown_field_is_rejected_by_schema()
    {
        var root = SampleContracts.UsersApiJson();
        root["extra"] = 1;
        var (_, bag) = Load(root);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.SchemaViolation);
    }

    [Fact]
    public void Old_draft_version_is_rejected_before_schema()
    {
        var root = SampleContracts.UsersApiJson();
        root["version"] = "0.2";
        var (loaded, bag) = Load(root);
        Assert.Null(loaded);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.FormatOrVersion);
    }

    [Fact]
    public void Non_safe_control_numbers_are_rejected()
    {
        var root = SampleContracts.UsersApiJson();
        root["operations"]![0]!["responses"]![0]!["status"] = JsonNode.Parse("200.0");
        var (_, bag) = Load(root);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.UnsafeControlNumber);
    }

    [Fact]
    public void Duplicate_json_names_are_rejected()
    {
        var text = SampleContracts.UsersApiJson().ToJsonString(TisiliaJson.Options);
        text = text.Replace("\"apiId\":", "\"apiId\":\"x\",\"apiId\":", StringComparison.Ordinal);
        var bag = new DiagnosticBag();
        Assert.Null(ContractLoader.Load(text, bag));
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.InvalidJson);
    }

    [Theory]
    [InlineData("SV02 duplicate id", "/types/1/id", "std.guid", TisiliaCodes.DuplicateId)]
    [InlineData("SV02 reserved prefix", "/operations/0/id", "tisilia.users.get", TisiliaCodes.ReservedId)]
    [InlineData("SV03 unresolved type", "/operations/0/parameters/0/use/typeId", "missing.type", TisiliaCodes.UnresolvedReference)]
    [InlineData("SV03 unresolved builtin", "/operations/0/security/authPolicyId", "tisilia.auth.future@0.4", TisiliaCodes.UnresolvedReference)]
    [InlineData("SV04 codec type mismatch", "/operations/0/parameters/0/use/codecId", "std.int64.codec.r", TisiliaCodes.CodecTypeMismatch)]
    [InlineData("SV17 max depth", "/profiles/0/options/maxDepthEffective", "32", TisiliaCodes.MaxDepth)]
    [InlineData("SV20 preserve references", "/profiles/0/options/referenceHandling", "Preserve", TisiliaCodes.ReferencePreserve)]
    [InlineData("SV26 GET with body", "/operations/1/method", "GET", TisiliaCodes.BodyOnGetOrHead)]
    [InlineData("SV27 204 with body", "/operations/0/responses/0/status", "204", TisiliaCodes.BodylessStatusMismatch)]
    [InlineData("SV30 route mismatch", "/operations/0/route", "/users/{userId:guid}", TisiliaCodes.ParameterRouteMismatch)]
    [InlineData("SV30 catch-all", "/operations/0/route", "/users/{*id}", TisiliaCodes.ParameterRouteMismatch)]
    [InlineData("SV32 unsafe route", "/operations/0/route", "/users/{id:guid}?x=1", TisiliaCodes.UnsafeRouteOrHeader)]
    [InlineData("SV33 redirect status", "/operations/0/responses/1/status", "302", TisiliaCodes.RedirectStatus)]
    [InlineData("SV49 enum member out of range", "/types/0/shape", "{\"kind\":\"enum\",\"underlyingPrimitiveId\":\"tisilia.int8@0.3\",\"flags\":false,\"allowUndefinedInteger\":true,\"members\":[{\"name\":\"A\",\"value\":\"300\"}]}", TisiliaCodes.EnumInvalid)]
    public void Negative_probes_report_the_expected_code(string label, string pointer, string replacementJson, string expectedCode)
    {
        output.WriteLine(label);
        var root = SampleContracts.UsersApiJson();
        SetPointer(root, pointer, replacementJson);
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Code == expectedCode);
    }

    [Theory]
    // a parameter's regex constraint may hold '?', '#', "://" and escaped braces (the ASP.NET Core routing documentation's example)
    [InlineData(@"/users/{id:regex(^\d{{3}}-\d{{2}}-\d{{4}}$)}")]
    [InlineData(@"/users/{id:regex(^\d+(\.\d+)?$)}")]
    [InlineData(@"/users/{id:regex(^https?://x#y$)}")]
    [InlineData("/users/{{literal}}/{id:guid}")]
    public void Route_constraints_are_not_literal_text(string route)
    {
        var root = SampleContracts.UsersApiJson();
        RouteTestContracts.SetRoute(root["operations"]![0]!, route);
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.DoesNotContain(bag.Items, d => d.Rule is "SV30" or "SV32");
    }

    [Theory]
    [InlineData("/users/{id:guid}/a?b", "SV32")]
    [InlineData("/users/{id:guid}#x", "SV32")]
    [InlineData("/users/{id:guid}/http://x", "SV01")]
    public void Unsafe_literal_route_text_is_rejected_for_that_reason(string route, string rule)
    {
        var root = SampleContracts.UsersApiJson();
        RouteTestContracts.SetRoute(root["operations"]![0]!, route);
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Rule == rule);
        // an optional parameter's '?' is not a query: the error names the parameter, not an unsafe route
        Assert.DoesNotContain(bag.Items, d => d.Message.Contains("display route differs", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoreNullValues_with_non_never_condition_is_rejected()
    {
        var root = SampleContracts.UsersApiJson();
        root["profiles"]![0]!["options"]!["ignoreNullValues"] = true;
        root["profiles"]![0]!["options"]!["defaultIgnoreCondition"] = "WhenWritingNull";
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.IgnoreCondition && d.Rule == "SV18");
    }

    [Fact]
    public void Same_status_and_media_is_ambiguous_but_different_media_is_not()
    {
        var root = SampleContracts.UsersApiJson();
        root["operations"]![0]!["responses"]![1]!["status"] = 200;
        var (_, bagDifferentMedia) = ValidateAll(root, verifyHashes: false);
        Assert.DoesNotContain(bagDifferentMedia.Items, d => d.Code == TisiliaCodes.AmbiguousResponseCase);
        root["operations"]![0]!["responses"]![1]!["body"]!["mediaType"] = "application/json";
        var (_, bagSameMedia) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bagSameMedia.Items, d => d.Code == TisiliaCodes.AmbiguousResponseCase);
        // bodyless + body at the same status is also ambiguous
        root["operations"]![0]!["responses"]![1]!["body"] = new JsonObject { ["kind"] = "none" };
        root["operations"]![0]!["responses"]![1]!["resultAdapterId"] = root["operations"]![0]!["responses"]![0]!["resultAdapterId"]!.GetValue<string>();
        var (_, bagBodyless) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bagBodyless.Items, d => d.Code == TisiliaCodes.AmbiguousResponseCase);
    }

    [Fact]
    public void Hash_mismatch_is_reported_after_semantic_change()
    {
        var root = SampleContracts.UsersApiJson();
        root["operations"]![0]!["tags"] = new JsonArray("changed");
        var (_, bag) = ValidateAll(root);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.HashMismatch && d.Path == "/semanticHash");
    }

    [Fact]
    public void Missing_response_capability_is_reported()
    {
        // Use the request-only Money codec in a response position.
        var root = SampleContracts.UsersApiJson();
        root["operations"]![0]!["responses"]![0]!["body"]!["use"] = new JsonObject { ["typeId"] = "demo.MoneyRequest", ["codecId"] = "demo.MoneyRequest.codec", ["semanticNullable"] = false };
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.MissingCapability && d.Rule == "SV05");
    }

    [Fact]
    public void Nullable_response_wire_requires_nullable_public_type()
    {
        var root = SampleContracts.UsersApiJson();
        // UserResponse.nickname uses the non-nullable string codec; point the wire at the or-null wire without changing the public type.
        var wires = root["wires"]!.AsArray();
        var write = wires.First(w => w!["id"]!.GetValue<string>() == "demo.UserResponse.write")!;
        var prop = write["shape"]!["properties"]!.AsArray().First(p => p!["name"]!.GetValue<string>() == "nickname")!;
        prop["wire"]!["wireId"] = "std.string.write.or-null";
        var (_, bag) = ValidateAll(root, verifyHashes: false);
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.NullabilityMismatch);
    }

    [Fact]
    public void Non_productive_recursion_is_rejected()
    {
        var b = SampleContracts.UsersApi();
        // Node { next: Node (required, non-null) } can never terminate.
        b.AddType(new Model
        {
            Id = "demo.Loop",
            TsName = "Loop",
            ClrIdentity = "Demo.Loop",
            Shape = new ObjectShape
            {
                Properties = [new DomainProperty { Name = "next", Use = new TypeUse { TypeId = "demo.Loop", CodecId = "demo.Loop.codec", SemanticNullable = false }, Presence = Presence.Required }],
                Extension = new NoExtension(),
            },
        });
        b.AddWire(new Wire
        {
            Id = "demo.Loop.write",
            Direction = WireDirection.ServerWrite,
            Shape = new ObjectWire
            {
                Properties = [new WireProperty { Name = "next", Wire = new WireRef { WireId = "demo.Loop.write", Direction = WireDirection.ServerWrite }, Presence = Presence.Required }],
                Additional = new IgnoreAdditional(),
                DuplicatePolicyId = Builtins.DuplicatesReject,
                NameMatchingId = Builtins.NamesOrdinal,
            },
        });
        b.AddEquivalence(new Equivalence
        {
            Id = "demo.Loop.response",
            Version = "0.3.0",
            DomainTypeId = "demo.Loop",
            Scope = EquivalenceScope.Response,
            Grade = Grade.G2,
            DomainRuleId = Builtins.DomainRule("object"),
            DotnetOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
            TypescriptOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
            NormalizationId = Builtins.NormalizeIdentity,
            Preserved = ["structure"],
            NotPreserved = [],
        });
        b.AddCodec(new Codec
        {
            Id = "demo.Loop.codec",
            TypeId = "demo.Loop",
            Origin = CodecOrigin.Builtin,
            BindingId = Builtins.BindingFor("object"),
            ValidateDomain = new BuiltinImpl { Id = Builtins.CodecImpl("object", "validate") },
            Capabilities = new Capabilities
            {
                Response = new ValueCapability
                {
                    Wire = new WireRef { WireId = "demo.Loop.write", Direction = WireDirection.ServerWrite },
                    Implementation = new BuiltinImpl { Id = Builtins.CodecImpl("object", "decode") },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = "demo.Loop.response",
                    DomainRuleId = Builtins.DomainRule("object"),
                },
            },
            Dependencies = ["demo.Loop.codec"],
            ProfileIds = [],
        });
        var (_, bag) = ValidateAll(b.BuildJson());
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.NonProductiveRecursion && d.RelatedIds.Contains("demo.Loop"));
        Assert.Contains(bag.Items, d => d.Code == TisiliaCodes.NonProductiveRecursion && d.RelatedIds.Contains("demo.Loop.write"));
    }

    [Fact]
    public void Finite_recursion_through_optional_member_is_accepted()
    {
        var b = SampleContracts.UsersApi();
        var self = new TypeUse { TypeId = "demo.Tree", CodecId = "demo.Tree.codec", SemanticNullable = false };
        var children = b.ArrayOf("demo.TreeList", "TreeList", "System.Collections.Generic.List<Demo.Tree>", self);
        // The array is created before the Tree type exists; register the tree afterwards with the array as a child.
        b.ObjectOf("demo.Tree", "Tree", "Demo.Tree",
        [
            new Generator.Building.PropertySpec("name", b.Scalar("string"), Presence.Required, Presence.Required, Presence.Required),
            new Generator.Building.PropertySpec("children", children, Presence.Required, Presence.Required, Presence.Required),
        ], Builtins.NamesOrdinal, Builtins.DuplicatesReject);
        var (index, bag) = ValidateAll(b.BuildJson());
        Assert.DoesNotContain(bag.Items, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(index);
    }

    [Theory]
    [InlineData("text/plain", false)]
    [InlineData("text/html", false)]
    [InlineData("text/csv; charset=utf-8", false)]
    [InlineData("text/event-stream", true)] // SSE: an unbounded stream, not supported
    [InlineData("text/json", true)] // a JSON body, not text
    [InlineData("text/plain; charset=iso-8859-1", true)] // text bodies are UTF-8
    [InlineData("application/xml", true)]
    public void Text_bodies_are_utf8_text_types_other_than_sse_and_json(string mediaType, bool rejected)
    {
        var b = SampleContracts.UsersApi();
        b.AddOperation(new Operation
        {
            Id = "users.page",
            Method = HttpMethodKind.GET,
            Route = "/users/page",
            Tags = ["users"],
            Parameters = [],
            RequestBody = new NoRequestBody(),
            Responses = [new Response { Id = "users.page.ok", Status = 200, Body = new TextResponseBody { MediaType = mediaType, Use = b.Scalar("string") }, ResultAdapterId = b.StandardResultAdapter(ResultAdapterKind.Text), Hydration = Hydration.ServerOnly, ExposedHeaders = [] }],
            PipelineBindingIds = [],
            Security = new Security { AuthPolicyId = Builtins.AuthAnonymous, RequestExecution = RequestExecution.BrowserAllowed, Redaction = new Redaction { Default = "mask", Rules = [] }, RequestHeaderAllowlist = [], CsrfPolicyId = Builtins.CsrfNone },
        });
        var (_, bag) = ValidateAll(b.BuildJson(), verifyHashes: false);
        Assert.Equal(rejected, bag.Items.Any(d => d.Rule == "SV29" && d.Code == TisiliaCodes.MediaTypeInvalid));
        Assert.Equal(rejected, bag.HasErrors);
    }

    [Theory]
    [InlineData("application/json", "application/json", null)]
    [InlineData("Application/JSON; Charset=\"UTF-8\"", "application/json", "utf-8")]
    [InlineData("text/plain;", "text/plain", null)] // RFC 9110 §5.6.6: parameters = *( OWS ";" OWS [ parameter ] )
    [InlineData("text/plain; ; charset=utf-8", "text/plain", "utf-8")]
    [InlineData("application/problem+json;charset=utf-8", "application/problem+json", "utf-8")]
    [InlineData("application/json; profile=\"a;b\"; charset=utf-8", "application/json", "utf-8")] // a quoted-string may hold ";"
    [InlineData("text/plain; title=\"say \\\"hi\\\"\"; charset=UTF-8", "text/plain", "utf-8")] // and quoted-pairs
    [InlineData("text/plain ;; charset=utf-8 ; ", "text/plain", "utf-8")]
    [InlineData("text/plain; charset=utf-8; charset=latin1", "text/plain", "utf-8")] // the first occurrence wins
    public void Media_types_parse_as_rfc_9110_defines_them(string value, string essence, string? charset)
    {
        var media = HttpRules.ParseMediaType(value);
        Assert.NotNull(media);
        Assert.Equal(essence, media.Essence);
        Assert.Equal(charset, media.Charset);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("/json")]
    [InlineData("text/")]
    [InlineData("te xt/plain")]
    [InlineData("text/plain; charset")]
    [InlineData("text/plain; charset = utf-8")] // RFC 9110 §5.6.6: no whitespace around "="
    [InlineData("text/plain; charset= utf-8")]
    [InlineData("text / plain")]
    [InlineData("text/plain; title=\"open")] // an unterminated quoted-string
    [InlineData("text/plain; =utf-8")]
    [InlineData("text/plain; charset=")]
    [InlineData("text/plain; charset=utf 8")]
    [InlineData("text/plain charset=utf-8")]
    public void Malformed_media_types_do_not_parse(string value) => Assert.Null(HttpRules.ParseMediaType(value));

    private static void SetPointer(JsonObject root, string pointer, string replacementJson)
    {
        var segments = pointer.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current is JsonArray arr ? arr[int.Parse(segments[i], System.Globalization.CultureInfo.InvariantCulture)]! : current[segments[i]]!;
        }

        JsonNode? replacement;
        try
        {
            replacement = JsonNode.Parse(replacementJson);
        }
        catch (System.Text.Json.JsonException)
        {
            replacement = System.Text.Json.Nodes.JsonValue.Create(replacementJson);
        }

        var last = segments[^1];
        if (current is JsonArray array)
        {
            array[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = replacement;
        }
        else
        {
            current[last] = replacement;
        }
    }
}
