using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Validation;

namespace Tisilia.Contract.Tests;

/// <summary>An end-to-end example: GET/PUT /users/{id:guid} with an asymmetric paired Money converter.</summary>
public static class SampleContracts
{
    public const string ApiId = "sample-api";
    public const string ProfileId = "sample-api.profile.web";
    public const string MoneyModuleId = "demo.money";

    public static ContractBuilder UsersApi()
    {
        var b = new ContractBuilder(ApiId);
        var nameMatching = StandardProfile.NameMatchingBinding(ProfileId, caseInsensitive: true);
        b.AddBinding(nameMatching);
        var profile = StandardProfile.Web(ProfileId, "10.0.12", "10.0.0", nameMatching.Id);
        b.AddProfile(profile);
        var np = StandardProfile.NumberProfileFor(profile.Options);
        var dup = StandardProfile.DuplicatePolicyFor(profile.Options);

        var guid = b.Scalar("guid");
        var int64 = b.Scalar("int64", np);
        var str = b.Scalar("string");
        var strOrNull = b.NullableScalar("string");
        var dto = b.Scalar("datetime-offset");
        var dec = b.Scalar("decimal", np);
        var jsonValue = b.Scalar("json-value");

        // --- paired Money converter: request = decimal string, response = {amount,currency} object
        b.AddModule(new Module
        {
            Id = MoneyModuleId,
            Version = "1.0.0",
            Abi = "0.3",
            Artifacts =
            [
                new Artifact { Target = ArtifactTarget.Dotnet, Path = "modules/demo.money/Demo.Money.dll", Digest = Digest("demo.money.dll") },
                new Artifact { Target = ArtifactTarget.Browser, Path = "modules/demo.money/money.browser.js", Digest = Digest("demo.money.browser") },
                new Artifact { Target = ArtifactTarget.Node, Path = "modules/demo.money/money.node.js", Digest = Digest("demo.money.node") },
            ],
            Exports =
            [
                new ModuleExport { Name = "MoneyJsonConverter", Role = ExportRole.Codec, Targets = [ArtifactTarget.Dotnet] },
                new ModuleExport { Name = "moneyRequestEncode", Role = ExportRole.Codec, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] },
                new ModuleExport { Name = "moneyRequestValidate", Role = ExportRole.Validator, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] },
                new ModuleExport { Name = "moneyRequestInput", Role = ExportRole.RequestInput, Targets = [ArtifactTarget.Browser] },
                new ModuleExport { Name = "moneyResponseDecode", Role = ExportRole.Codec, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] },
                new ModuleExport { Name = "moneyResponseValidate", Role = ExportRole.Validator, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] },
                new ModuleExport { Name = "moneyDomainRule", Role = ExportRole.DomainRule, Targets = [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node] },
                new ModuleExport { Name = "moneyOracleDotnet", Role = ExportRole.Oracle, Targets = [ArtifactTarget.Dotnet] },
                new ModuleExport { Name = "moneyOracleTs", Role = ExportRole.Oracle, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] },
            ],
            DependencyIds = [],
            License = "MIT",
            NoticeFiles = ["modules/demo.money/NOTICE"],
        });
        b.AddBinding(new Binding
        {
            Id = "demo.money.converter",
            Kind = BindingKind.Converter,
            Version = "1.0.0",
            Implementation = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "MoneyJsonConverter" },
            SettingsDigest = Digest("money-converter-settings:currency=JPY;scale=4"),
            DependencyIds = [],
            Context = [new BindingContextEntry { Name = "currency", Value = "JPY", Confidential = false }],
        });
        b.AddBinding(new Binding
        {
            Id = "demo.money.domain-rule",
            Kind = BindingKind.DomainRule,
            Version = "1.0.0",
            Implementation = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyDomainRule" },
            SettingsDigest = Digest("money-domain-rule"),
            DependencyIds = [],
            Context = [],
        });

        var moneyRequest = b.ObjectOf("demo.MoneyRequest", "MoneyRequest", "Demo.Money", [new PropertySpec("amount", dec, Presence.Required, Presence.Required, Presence.Required)], nameMatching.Id, dup, emitRead: false, emitWrite: false);
        var moneyResponse = b.ObjectOf("demo.MoneyResponse", "MoneyResponse", "Demo.Money",
            [new PropertySpec("amount", dec, Presence.Required, Presence.Required, Presence.Required), new PropertySpec("currency", str, Presence.Required, Presence.Required, Presence.Required)],
            nameMatching.Id, dup, emitRead: false, emitWrite: false);
        // replace the builtin object codecs with the paired ones
        b.AddWire(new Wire { Id = "demo.money.request.wire", Direction = WireDirection.ServerRead, Shape = new StringWire { GrammarId = Builtins.Grammar("decimal-string") } });
        b.AddWire(new Wire { Id = "demo.money.response.amount", Direction = WireDirection.ServerWrite, Shape = new StringWire { GrammarId = Builtins.Grammar("decimal-string") } });
        b.AddWire(new Wire
        {
            Id = "demo.money.response.wire",
            Direction = WireDirection.ServerWrite,
            Shape = new ObjectWire
            {
                Properties =
                [
                    new WireProperty { Name = "amount", Wire = new WireRef { WireId = "demo.money.response.amount", Direction = WireDirection.ServerWrite }, Presence = Presence.Required },
                    new WireProperty { Name = "currency", Wire = new WireRef { WireId = "std.string.write", Direction = WireDirection.ServerWrite }, Presence = Presence.Required },
                ],
                Additional = new RejectAdditional(),
                DuplicatePolicyId = Builtins.DuplicatesReject,
                NameMatchingId = Builtins.NamesOrdinal,
            },
        });
        foreach (var (id, scope) in new[] { ("demo.money.request.eq", EquivalenceScope.Request), ("demo.money.response.eq", EquivalenceScope.Response) })
        {
            b.AddEquivalence(new Equivalence
            {
                Id = id,
                Version = "1.0.0",
                DomainTypeId = scope == EquivalenceScope.Request ? moneyRequest.TypeId : moneyResponse.TypeId,
                Scope = scope,
                Grade = Grade.G2,
                DomainRuleId = "demo.money.domain-rule",
                DotnetOracle = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyOracleDotnet" },
                TypescriptOracle = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyOracleTs" },
                NormalizationId = Builtins.NormalizeIdentity,
                Preserved = ["amount", "currency"],
                NotPreserved = ["scale-beyond-4"],
            });
        }

        b.AddCodec(new Codec
        {
            Id = moneyRequest.CodecId,
            TypeId = moneyRequest.TypeId,
            Origin = CodecOrigin.Paired,
            BindingId = "demo.money.converter",
            ValidateDomain = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyRequestValidate" },
            Capabilities = new Capabilities
            {
                Request = new ValueCapability
                {
                    Wire = new WireRef { WireId = "demo.money.request.wire", Direction = WireDirection.ServerRead },
                    Implementation = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyRequestEncode" },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = "demo.money.request.eq",
                    DomainRuleId = "demo.money.domain-rule",
                },
                RequestInput = new InputCapability { Implementation = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyRequestInput" }, InputKind = InputKind.Text, EditorId = Builtins.EditorText },
            },
            Dependencies = [dec.CodecId],
            ProfileIds = [ProfileId],
        });
        b.AddCodec(new Codec
        {
            Id = moneyResponse.CodecId,
            TypeId = moneyResponse.TypeId,
            Origin = CodecOrigin.Paired,
            BindingId = "demo.money.converter",
            ValidateDomain = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyResponseValidate" },
            Capabilities = new Capabilities
            {
                Response = new ValueCapability
                {
                    Wire = new WireRef { WireId = "demo.money.response.wire", Direction = WireDirection.ServerWrite },
                    Implementation = new ModuleImpl { ModuleId = MoneyModuleId, ExportName = "moneyResponseDecode" },
                    NullBehavior = NullBehavior.Reject,
                    EquivalenceId = "demo.money.response.eq",
                    DomainRuleId = "demo.money.domain-rule",
                },
            },
            Dependencies = [dec.CodecId, str.CodecId],
            ProfileIds = [ProfileId],
        });

        // --- DTOs
        var putRequest = b.ObjectOf("demo.UserPutRequest", "UserPutRequest", "Demo.UserPutRequest",
        [
            new PropertySpec("revision", int64, Presence.Required, Presence.Required, Presence.Required),
            new PropertySpec("nickname", strOrNull, Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("balance", moneyRequest, Presence.Required, Presence.Required, Presence.Required),
        ], nameMatching.Id, dup, emitWrite: false);
        var userResponse = b.ObjectOf("demo.UserResponse", "UserResponse", "Demo.UserResponse",
        [
            new PropertySpec("id", guid, Presence.Required, Presence.Required, Presence.Required),
            new PropertySpec("revision", int64, Presence.Required, Presence.Required, Presence.Required),
            new PropertySpec("nickname", str, Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("balance", moneyResponse, Presence.Required, Presence.Required, Presence.Required),
            new PropertySpec("createdAt", dto, Presence.Required, Presence.Required, Presence.Required),
        ], nameMatching.Id, dup, emitRead: false);
        var problem = b.ObjectOf("demo.ProblemResponse", "ProblemResponse", "Microsoft.AspNetCore.Mvc.ProblemDetails",
        [
            new PropertySpec("type", strOrNull, Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("title", strOrNull, Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("status", b.Nullable(b.Scalar("int32", np)), Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("detail", strOrNull, Presence.Optional, Presence.Optional, Presence.Optional),
            new PropertySpec("instance", strOrNull, Presence.Optional, Presence.Optional, Presence.Optional),
        ], nameMatching.Id, dup,
            writeAdditional: new CaptureAdditional { Wire = new WireRef { WireId = "std.json-value.write", Direction = WireDirection.ServerWrite } },
            extension: new CaptureExtension { Value = jsonValue, Collision = "reject" },
            emitRead: false);

        var idBinder = b.StandardBinder("guid", ParameterLocation.Path);
        var okAdapter = b.StandardResultAdapter(ResultAdapterKind.MinimalResult, [ProfileId]);
        var problemAdapter = b.StandardResultAdapter(ResultAdapterKind.MinimalResult, [ProfileId]);
        Security Security() => new()
        {
            AuthPolicyId = Builtins.AuthAnonymous,
            RequestExecution = RequestExecution.BrowserAllowed,
            Redaction = new Redaction
            {
                Default = "mask",
                Rules =
                [
                    new RedactionRule { Direction = RedactionDirection.Response, Selector = new BodyPathSelector([new PropertySegment("id")]), Action = RedactionAction.Show },
                    new RedactionRule { Direction = RedactionDirection.Response, Selector = new BodyPathSelector([new PropertySegment("revision")]), Action = RedactionAction.Show },
                ],
            },
            RequestHeaderAllowlist = ["accept-language"],
            CsrfPolicyId = Builtins.CsrfNone,
        };
        b.AddOperation(new Operation
        {
            Id = "users.get",
            Method = HttpMethodKind.GET,
            Route = "/users/{id:guid}",
            Tags = ["users"],
            Parameters = [new Parameter { Id = "users.get.id", Name = "id", Location = ParameterLocation.Path, BinderId = idBinder, Use = guid, Presence = Presence.Required }],
            RequestBody = new NoRequestBody(),
            Responses =
            [
                new Response { Id = "users.get.ok", Status = 200, Body = new JsonResponseBody { MediaType = "application/json", ProfileId = ProfileId, Use = userResponse }, ResultAdapterId = okAdapter, Hydration = Hydration.BrowserSafe, ExposedHeaders = ["etag"] },
                new Response { Id = "users.get.not-found", Status = 404, Body = new JsonResponseBody { MediaType = "application/problem+json", ProfileId = ProfileId, Use = problem }, ResultAdapterId = problemAdapter, Hydration = Hydration.ServerOnly, ExposedHeaders = [] },
            ],
            PipelineBindingIds = ["tisilia.pipeline.problem-details@0.3"],
            Security = Security(),
        });
        b.AddOperation(new Operation
        {
            Id = "users.put",
            Method = HttpMethodKind.PUT,
            Route = "/users/{id:guid}",
            Tags = ["users"],
            Parameters = [new Parameter { Id = "users.put.id", Name = "id", Location = ParameterLocation.Path, BinderId = idBinder, Use = guid, Presence = Presence.Required }],
            RequestBody = new JsonRequestBody { MediaType = "application/json", ProfileId = ProfileId, Use = putRequest, Presence = Presence.Required },
            Responses =
            [
                new Response { Id = "users.put.ok", Status = 200, Body = new JsonResponseBody { MediaType = "application/json", ProfileId = ProfileId, Use = userResponse }, ResultAdapterId = okAdapter, Hydration = Hydration.BrowserSafe, ExposedHeaders = [] },
                new Response { Id = "users.put.not-found", Status = 404, Body = new JsonResponseBody { MediaType = "application/problem+json", ProfileId = ProfileId, Use = problem }, ResultAdapterId = problemAdapter, Hydration = Hydration.ServerOnly, ExposedHeaders = [] },
            ],
            PipelineBindingIds = ["tisilia.pipeline.problem-details@0.3", "tisilia.pipeline.minimal-validation@0.3"],
            Security = Security(),
        });
        b.AddDocumentation("users.get", "Get a user by id");
        return b;
    }

    public static JsonObject UsersApiJson() => UsersApi().BuildJson();

    private static string Digest(string seed) => TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(seed));
}
