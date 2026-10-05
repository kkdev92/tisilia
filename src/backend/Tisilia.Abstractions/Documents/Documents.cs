using System.Text.Json.Serialization;
using Tisilia.Contract;

namespace Tisilia.Documents;

public enum ModuleMode
{
    [JsonStringEnumMemberName("bundler")] Bundler,
    [JsonStringEnumMemberName("nodenext")] NodeNext,
}

public enum CoveragePolicy
{
    [JsonStringEnumMemberName("development")] Development,
    [JsonStringEnumMemberName("qualified-only")] QualifiedOnly,
}

public sealed record ConfigTarget
{
    public required int TypescriptMinimumMajor { get; init; }
    public required string EcmaScript { get; init; }
    public required ModuleMode ModuleMode { get; init; }
}

public sealed record NuxtConfig
{
    public required bool Enabled { get; init; }
    public required string Hydration { get; init; }
    public required bool SharedCache { get; init; }
}

/// <summary><c>tisilia.config</c> 0.3.</summary>
public sealed record ConfigDocument
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string ApiId { get; init; }
    public required string Contract { get; init; }
    public required string Output { get; init; }
    public required ConfigTarget Target { get; init; }
    public required string Selection { get; init; }
    public required CoveragePolicy CoveragePolicy { get; init; }
    public required IReadOnlyList<string> Modules { get; init; }
    public required IReadOnlyList<string> PortableProjects { get; init; }
    public required Limits Limits { get; init; }
    public required NuxtConfig Nuxt { get; init; }
}

public enum ClosureCapability
{
    [JsonStringEnumMemberName("request")] Request,
    [JsonStringEnumMemberName("response")] Response,
    [JsonStringEnumMemberName("requestKey")] RequestKey,
    [JsonStringEnumMemberName("responseKey")] ResponseKey,
    [JsonStringEnumMemberName("requestInput")] RequestInput,
    [JsonStringEnumMemberName("binder")] Binder,
    [JsonStringEnumMemberName("result")] Result,
    [JsonStringEnumMemberName("hydration")] Hydration,
    [JsonStringEnumMemberName("round-trip")] RoundTrip,
}

public enum RegistryName
{
    [JsonStringEnumMemberName("types")] Types,
    [JsonStringEnumMemberName("wires")] Wires,
    [JsonStringEnumMemberName("codecs")] Codecs,
    [JsonStringEnumMemberName("bindings")] Bindings,
    [JsonStringEnumMemberName("equivalences")] Equivalences,
    [JsonStringEnumMemberName("projections")] Projections,
    [JsonStringEnumMemberName("comparers")] Comparers,
    [JsonStringEnumMemberName("binders")] Binders,
    [JsonStringEnumMemberName("resultAdapters")] ResultAdapters,
    [JsonStringEnumMemberName("profiles")] Profiles,
    [JsonStringEnumMemberName("modules")] Modules,
    [JsonStringEnumMemberName("operations")] Operations,
}

public sealed record RegistryRef
{
    public required RegistryName Registry { get; init; }
    public required string Id { get; init; }
}

public sealed record ProfileFingerprintEntry
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
}

public sealed record BindingSettingsEntry
{
    public required string Id { get; init; }
    public required string SettingsDigest { get; init; }
}

/// <summary><c>tisilia.closure-record</c> 0.3: exact hash input of the qualification closure.</summary>
public sealed record ClosureRecord
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string SemanticHash { get; init; }
    public required IReadOnlyList<string> OperationIds { get; init; }
    public required IReadOnlyList<ClosureCapability> Capabilities { get; init; }
    public required IReadOnlyList<string> EquivalenceIds { get; init; }
    public required IReadOnlyList<RegistryRef> RegistryRefs { get; init; }
    public required IReadOnlyList<ProfileFingerprintEntry> ProfileFingerprints { get; init; }
    public required IReadOnlyList<BindingSettingsEntry> BindingSettings { get; init; }
    public required IReadOnlyList<IdentifiedArtifact> ModuleArtifacts { get; init; }
    public required IReadOnlyList<Artifact> ApplicationArtifacts { get; init; }
    public required string Abi { get; init; }
    public required RuntimeMatrix Matrix { get; init; }
    public required IReadOnlyList<NameValue> Context { get; init; }
    public required Limits Limits { get; init; }
}

/// <summary><c>tisilia.codec-manifest</c> 0.3: a reusable codec module with its declarations.</summary>
public sealed record CodecManifest
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required Module Module { get; init; }
    public required IReadOnlyList<Binding> Bindings { get; init; }
    public required IReadOnlyList<Codec> Codecs { get; init; }
    public required IReadOnlyList<Model> Types { get; init; }
    public required IReadOnlyList<Wire> Wires { get; init; }
    public required IReadOnlyList<Equivalence> Equivalences { get; init; }
    public required IReadOnlyList<Projection> Projections { get; init; }
    public required IReadOnlyList<Contract.Comparer> Comparers { get; init; }
    public required IReadOnlyList<Binder> Binders { get; init; }
    public required IReadOnlyList<ResultAdapter> ResultAdapters { get; init; }
}

public enum Verdict
{
    [JsonStringEnumMemberName("passed")] Passed,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed record EvidenceScope
{
    public required IReadOnlyList<string> OperationIds { get; init; }
    public required IReadOnlyList<ClosureCapability> Capabilities { get; init; }
    public required IReadOnlyList<string> EquivalenceIds { get; init; }
}

public sealed record EvidenceSuite
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public required string Seed { get; init; }
    public required string RunnerDigest { get; init; }
    public required string Protocol { get; init; }
}

public sealed record EvidenceCounts
{
    public required long Passed { get; init; }
    public required long Failed { get; init; }
    public required long Skipped { get; init; }
}

/// <summary><c>tisilia.conformance-evidence</c> 0.3.</summary>
public sealed record ConformanceEvidence
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string Id { get; init; }
    public required string IssuerId { get; init; }
    public required string IssuedAt { get; init; }
    public required Verdict Verdict { get; init; }
    public required string SemanticHash { get; init; }
    public required string ClosureDigest { get; init; }
    public required string Abi { get; init; }
    public required EvidenceScope Scope { get; init; }
    public required RuntimeMatrix Matrix { get; init; }
    public required IReadOnlyList<IdentifiedArtifact> ModuleArtifacts { get; init; }
    public required IReadOnlyList<Artifact> ApplicationArtifacts { get; init; }
    public required IReadOnlyList<NameValue> Context { get; init; }
    public required EvidenceSuite Suite { get; init; }
    public required EvidenceCounts Counts { get; init; }
    public required IReadOnlyList<string> RequiredTests { get; init; }
    public required Limits Limits { get; init; }
    public required string Claim { get; init; }
}

public sealed record GeneratedFile
{
    public required string Path { get; init; }
    public required string Digest { get; init; }
}

public enum NameMappingKind
{
    [JsonStringEnumMemberName("type")] Type,
    [JsonStringEnumMemberName("operation")] Operation,
    [JsonStringEnumMemberName("codec")] Codec,
    [JsonStringEnumMemberName("projection")] Projection,
}

public sealed record NameMapping
{
    public required NameMappingKind Kind { get; init; }
    public required string Id { get; init; }
    public required string GeneratedName { get; init; }
    public required string Path { get; init; }
}

public enum CoverageStatus
{
    [JsonStringEnumMemberName("unqualified")] Unqualified,
    [JsonStringEnumMemberName("qualified")] Qualified,
}

public sealed record CoverageEntry
{
    public required string OperationId { get; init; }
    public required CoverageStatus Status { get; init; }
    public required IReadOnlyList<string> EvidenceIds { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
}

/// <summary><c>tisilia.generation-manifest</c> 0.3: ownership of generated files and coverage.</summary>
public sealed record GenerationManifest
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string ApiId { get; init; }
    public required string SemanticHash { get; init; }
    public required string ContractArtifactDigest { get; init; }
    public required string GeneratorVersion { get; init; }
    public required string Abi { get; init; }
    public required ModuleMode TargetMode { get; init; }
    public required IReadOnlyList<GeneratedFile> Files { get; init; }
    public required IReadOnlyList<NameMapping> NameMappings { get; init; }
    public required IReadOnlyList<IdentifiedArtifact> ModuleArtifacts { get; init; }
    public required IReadOnlyList<CoverageEntry> Coverage { get; init; }
}

/// <summary><c>tisilia.portable-definition</c> 0.3.</summary>
public sealed record PortableDefinition
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string Id { get; init; }
    public required string DefinitionVersion { get; init; }
    public required string ClrType { get; init; }
    public PortableDirection? Request { get; init; }
    public PortableDirection? Response { get; init; }
}

public sealed record PortableImport
{
    public required string Path { get; init; }
    public required string Digest { get; init; }
}

public sealed record PortableOutput
{
    public required string Csharp { get; init; }
    public required string Typescript { get; init; }
}

/// <summary><c>tisilia.portable-project</c> 0.3.</summary>
public sealed record PortableProject
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string ProjectId { get; init; }
    public required string BuiltinSet { get; init; }
    public required IReadOnlyList<string> Definitions { get; init; }
    public required IReadOnlyList<PortableImport> Imports { get; init; }
    public required IReadOnlyList<Model> Models { get; init; }
    public required IReadOnlyList<Projection> Projections { get; init; }
    public required IReadOnlyList<Equivalence> Equivalences { get; init; }
    public required IReadOnlyList<Binding> Bindings { get; init; }
    public required IReadOnlyList<Module> Modules { get; init; }
    public required PortableOutput Output { get; init; }
}

public enum BodyKind
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("json")] Json,
}

/// <summary><c>tisilia.request-identity-record</c> 0.3: exact hash input of a Nuxt request identity.</summary>
public sealed record RequestIdentityRecord
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string OperationId { get; init; }
    public required HttpMethodKind Method { get; init; }
    public required string EncodedPath { get; init; }
    public required IReadOnlyList<NameValue> QueryEntries { get; init; }
    public required IReadOnlyList<NameValue> SelectedHeaderEntries { get; init; }
    public required BodyKind BodyKind { get; init; }
    public required string BodyText { get; init; }
    public required string SemanticHash { get; init; }
    public required string ScopeNonce { get; init; }
}

public enum EnvelopeKind
{
    [JsonStringEnumMemberName("json")] Json,
    [JsonStringEnumMemberName("bodyless")] Bodyless,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("failure")] Failure,
}

public enum FailureCode
{
    [JsonStringEnumMemberName("transport")] Transport,
    [JsonStringEnumMemberName("codec")] Codec,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("contract-mismatch")] ContractMismatch,
    [JsonStringEnumMemberName("limit")] Limit,
    [JsonStringEnumMemberName("unexpected-response")] UnexpectedResponse,
    [JsonStringEnumMemberName("server-only")] ServerOnly,
}

/// <summary><c>tisilia.hydration-envelope</c> 0.3. Discriminated by <c>kind</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JsonEnvelope), "json")]
[JsonDerivedType(typeof(BodylessEnvelope), "bodyless")]
[JsonDerivedType(typeof(TextEnvelope), "text")]
[JsonDerivedType(typeof(FailureEnvelope), "failure")]
public abstract record HydrationEnvelope
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string SemanticHash { get; init; }
    public required string OperationId { get; init; }
    public required string RequestIdentity { get; init; }
    public required string ScopeNonce { get; init; }

    [JsonIgnore]
    public abstract EnvelopeKind Kind { get; }
}

public sealed record JsonEnvelope : HydrationEnvelope
{
    public required string ResponseCaseId { get; init; }
    public required int Status { get; init; }
    public required string MediaType { get; init; }
    public required string BodyText { get; init; }
    public required IReadOnlyList<NameValue> Headers { get; init; }
    [JsonIgnore]
    public override EnvelopeKind Kind => EnvelopeKind.Json;
}

public sealed record BodylessEnvelope : HydrationEnvelope
{
    public required string ResponseCaseId { get; init; }
    public required int Status { get; init; }
    public required IReadOnlyList<NameValue> Headers { get; init; }
    [JsonIgnore]
    public override EnvelopeKind Kind => EnvelopeKind.Bodyless;
}

public sealed record TextEnvelope : HydrationEnvelope
{
    public required string ResponseCaseId { get; init; }
    public required int Status { get; init; }
    public required string MediaType { get; init; }
    public required string BodyText { get; init; }
    public required IReadOnlyList<NameValue> Headers { get; init; }
    [JsonIgnore]
    public override EnvelopeKind Kind => EnvelopeKind.Text;
}

public sealed record FailureEnvelope : HydrationEnvelope
{
    public required FailureCode Code { get; init; }
    public required string SafeMessageId { get; init; }
    [JsonIgnore]
    public override EnvelopeKind Kind => EnvelopeKind.Failure;
}

public enum RunnerAction
{
    [JsonStringEnumMemberName("dotnet-read")] DotnetRead,
    [JsonStringEnumMemberName("dotnet-write")] DotnetWrite,
    [JsonStringEnumMemberName("ts-encode-request")] TsEncodeRequest,
    [JsonStringEnumMemberName("ts-decode-response")] TsDecodeResponse,
    [JsonStringEnumMemberName("dotnet-read-key")] DotnetReadKey,
    [JsonStringEnumMemberName("dotnet-write-key")] DotnetWriteKey,
    [JsonStringEnumMemberName("ts-encode-key")] TsEncodeKey,
    [JsonStringEnumMemberName("ts-decode-key")] TsDecodeKey,
    [JsonStringEnumMemberName("validate-domain")] ValidateDomain,
    [JsonStringEnumMemberName("parse-request-input")] ParseRequestInput,
    [JsonStringEnumMemberName("compare")] Compare,
    /// <summary>Apply a projection (adapterId) to a domain AST at a path (inputs: domain, path) on the TypeScript side (behaviors).</summary>
    [JsonStringEnumMemberName("ts-project")] TsProject,
    /// <summary>The same projection applied by the registered .NET implementation; the suite requires both to agree.</summary>
    [JsonStringEnumMemberName("dotnet-project")] DotnetProject,
}

public enum RunnerFailureCode
{
    [JsonStringEnumMemberName("invalid-input")] InvalidInput,
    [JsonStringEnumMemberName("contract")] Contract,
    [JsonStringEnumMemberName("codec")] Codec,
    [JsonStringEnumMemberName("limit")] Limit,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("internal")] Internal,
}

/// <summary><c>tisilia.runner-message</c> 0.3, one record per JSON Lines row.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunnerRequest), "request")]
[JsonDerivedType(typeof(RunnerSuccess), "success")]
[JsonDerivedType(typeof(RunnerFailure), "failure")]
public abstract record RunnerMessage
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string SessionId { get; init; }
    public required string RequestId { get; init; }
}

public sealed record RunnerRequest : RunnerMessage
{
    public required RunnerAction Action { get; init; }
    public required string AdapterId { get; init; }
    public required string ProfileId { get; init; }
    public required IReadOnlyList<NameValue> Context { get; init; }
    public required IReadOnlyList<JsonValue> Inputs { get; init; }
}

public sealed record RunnerSuccess : RunnerMessage
{
    public required IReadOnlyList<JsonValue> Outputs { get; init; }
}

public sealed record RunnerFailure : RunnerMessage
{
    public required RunnerFailureCode Code { get; init; }
    public required string SafeMessageId { get; init; }
    public required string Path { get; init; }
}
