using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tisilia.Contract;

public enum HttpMethodKind
{
    GET,
    HEAD,
    POST,
    PUT,
    PATCH,
    DELETE,
    OPTIONS,
}

public sealed record Parameter
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ParameterLocation Location { get; init; }
    public required string BinderId { get; init; }
    public required TypeUse Use { get; init; }
    public required Presence Presence { get; init; }
    public JsonValue? ServerDefault { get; init; }
    public bool HasServerDefault { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoRequestBody), "none")]
[JsonDerivedType(typeof(JsonRequestBody), "json")]
public abstract record RequestBody
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record NoRequestBody : RequestBody
{
    [JsonIgnore]
    public override string Kind => "none";
}

public sealed record JsonRequestBody : RequestBody
{
    public required string MediaType { get; init; }
    public required string ProfileId { get; init; }
    public required TypeUse Use { get; init; }
    public required Presence Presence { get; init; }
    [JsonIgnore]
    public override string Kind => "json";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoResponseBody), "none")]
[JsonDerivedType(typeof(JsonResponseBody), "json")]
[JsonDerivedType(typeof(TextResponseBody), "text")]
[JsonDerivedType(typeof(BinaryResponseBody), "binary")]
public abstract record ResponseBody
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record NoResponseBody : ResponseBody
{
    [JsonIgnore]
    public override string Kind => "none";
}

public sealed record JsonResponseBody : ResponseBody
{
    public required string MediaType { get; init; }
    public required string ProfileId { get; init; }
    public required TypeUse Use { get; init; }
    [JsonIgnore]
    public override string Kind => "json";
}

public sealed record TextResponseBody : ResponseBody
{
    public required string MediaType { get; init; }
    public required TypeUse Use { get; init; }
    [JsonIgnore]
    public override string Kind => "text";
}

public sealed record BinaryResponseBody : ResponseBody
{
    public required string MediaType { get; init; }
    [JsonIgnore]
    public override string Kind => "binary";
}

public enum Hydration
{
    [JsonStringEnumMemberName("server-only")] ServerOnly,
    [JsonStringEnumMemberName("browser-safe")] BrowserSafe,
}

public sealed record Response
{
    public required string Id { get; init; }
    public required int Status { get; init; }
    public required ResponseBody Body { get; init; }
    public required string ResultAdapterId { get; init; }
    public required Hydration Hydration { get; init; }
    public required IReadOnlyList<string> ExposedHeaders { get; init; }
}

/// <summary>One typed segment of a body-path selector: <c>{"property":"x"}</c>, <c>{"index":0}</c> or <c>{"each":true}</c>.</summary>
[JsonConverter(typeof(SelectorSegmentConverter))]
public abstract record SelectorSegment;

public sealed record PropertySegment(string Property) : SelectorSegment;

public sealed record IndexSegment(long Index) : SelectorSegment;

public sealed record EachSegment : SelectorSegment;

public sealed class SelectorSegmentConverter : JsonConverter<SelectorSegment>
{
    public override SelectorSegment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("selector segment must be an object");
        }

        var count = 0;
        SelectorSegment? result = null;
        foreach (var property in root.EnumerateObject())
        {
            count++;
            switch (property.Name)
            {
                case "property" when property.Value.ValueKind == JsonValueKind.String:
                    result = new PropertySegment(property.Value.GetString()!);
                    break;
                case "index" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var index) && index >= 0:
                    result = new IndexSegment(index);
                    break;
                case "each" when property.Value.ValueKind == JsonValueKind.True:
                    result = new EachSegment();
                    break;
                default:
                    throw new JsonException($"unknown selector segment member '{property.Name}'");
            }
        }

        if (count != 1 || result is null)
        {
            throw new JsonException("selector segment must have exactly one member");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, SelectorSegment value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case PropertySegment p:
                writer.WriteString("property", p.Property);
                break;
            case IndexSegment i:
                writer.WriteNumber("index", i.Index);
                break;
            case EachSegment:
                writer.WriteBoolean("each", true);
                break;
            default:
                throw new JsonException("unknown selector segment");
        }

        writer.WriteEndObject();
    }
}

public enum SelectorKind
{
    [JsonStringEnumMemberName("header")] Header,
    [JsonStringEnumMemberName("query")] Query,
    [JsonStringEnumMemberName("path")] Path,
    [JsonStringEnumMemberName("body-path")] BodyPath,
}

/// <summary>Redaction selector: a named header/query/path value or a typed body path.</summary>
[JsonConverter(typeof(SelectorConverter))]
public abstract record Selector
{
    public abstract SelectorKind Kind { get; }
}

public sealed record NamedSelector(SelectorKind Kind, string Name) : Selector
{
    public override SelectorKind Kind { get; } = Kind;
}

public sealed record BodyPathSelector(IReadOnlyList<SelectorSegment> Segments) : Selector
{
    public override SelectorKind Kind => SelectorKind.BodyPath;
}

public sealed class SelectorConverter : JsonConverter<Selector>
{
    public override Selector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("selector must be an object with a string 'kind'");
        }

        var kind = kindElement.GetString();
        var members = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        switch (kind)
        {
            case "header":
            case "query":
            case "path":
                if (!members.SetEquals(["kind", "name"]) || root.GetProperty("name").ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("named selector requires exactly 'kind' and 'name'");
                }

                var selectorKind = kind switch
                {
                    "header" => SelectorKind.Header,
                    "query" => SelectorKind.Query,
                    _ => SelectorKind.Path,
                };
                return new NamedSelector(selectorKind, root.GetProperty("name").GetString()!);
            case "body-path":
                if (!members.SetEquals(["kind", "segments"]))
                {
                    throw new JsonException("body-path selector requires exactly 'kind' and 'segments'");
                }

                var segments = JsonSerializer.Deserialize<List<SelectorSegment>>(root.GetProperty("segments").GetRawText(), options)
                               ?? throw new JsonException("segments must be an array");
                return new BodyPathSelector(segments);
            default:
                throw new JsonException($"unknown selector kind '{kind}'");
        }
    }

    public override void Write(Utf8JsonWriter writer, Selector value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case NamedSelector named:
                writer.WriteString("kind", named.Kind switch
                {
                    SelectorKind.Header => "header",
                    SelectorKind.Query => "query",
                    SelectorKind.Path => "path",
                    _ => throw new JsonException("invalid named selector kind"),
                });
                writer.WriteString("name", named.Name);
                break;
            case BodyPathSelector body:
                writer.WriteString("kind", "body-path");
                writer.WritePropertyName("segments");
                JsonSerializer.Serialize(writer, body.Segments, options);
                break;
            default:
                throw new JsonException("unknown selector");
        }

        writer.WriteEndObject();
    }
}

public enum RedactionDirection
{
    [JsonStringEnumMemberName("request")] Request,
    [JsonStringEnumMemberName("response")] Response,
}

public enum RedactionAction
{
    [JsonStringEnumMemberName("mask")] Mask,
    [JsonStringEnumMemberName("omit")] Omit,
    [JsonStringEnumMemberName("show")] Show,
}

public sealed record RedactionRule
{
    public required RedactionDirection Direction { get; init; }
    public required Selector Selector { get; init; }
    public required RedactionAction Action { get; init; }
}

public sealed record Redaction
{
    public required string Default { get; init; }
    public required IReadOnlyList<RedactionRule> Rules { get; init; }
}

public enum RequestExecution
{
    [JsonStringEnumMemberName("browser-allowed")] BrowserAllowed,
    [JsonStringEnumMemberName("server-only")] ServerOnly,
}

public sealed record Security
{
    public required string AuthPolicyId { get; init; }
    public required RequestExecution RequestExecution { get; init; }
    public required Redaction Redaction { get; init; }
    public required IReadOnlyList<string> RequestHeaderAllowlist { get; init; }
    public required string CsrfPolicyId { get; init; }
}

public sealed record Operation
{
    public required string Id { get; init; }
    public required HttpMethodKind Method { get; init; }
    public required string Route { get; init; }
    public RoutePlan? RoutePlan { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<Parameter> Parameters { get; init; }
    public required RequestBody RequestBody { get; init; }
    public required IReadOnlyList<Response> Responses { get; init; }
    public required IReadOnlyList<string> PipelineBindingIds { get; init; }
    public required Security Security { get; init; }
}

public enum ExportRole
{
    [JsonStringEnumMemberName("codec")] Codec,
    [JsonStringEnumMemberName("key-codec")] KeyCodec,
    [JsonStringEnumMemberName("request-input")] RequestInput,
    [JsonStringEnumMemberName("validator")] Validator,
    [JsonStringEnumMemberName("oracle")] Oracle,
    [JsonStringEnumMemberName("projection")] Projection,
    [JsonStringEnumMemberName("binder")] Binder,
    [JsonStringEnumMemberName("result")] Result,
    [JsonStringEnumMemberName("behavior")] Behavior,
    [JsonStringEnumMemberName("domain-rule")] DomainRule,
    [JsonStringEnumMemberName("normalization")] Normalization,
    [JsonStringEnumMemberName("comparer")] Comparer,
    [JsonStringEnumMemberName("grammar")] Grammar,
    [JsonStringEnumMemberName("name-matching")] NameMatching,
    [JsonStringEnumMemberName("duplicate-policy")] DuplicatePolicy,
    [JsonStringEnumMemberName("editor")] Editor,
    [JsonStringEnumMemberName("naming-policy")] NamingPolicy,
    [JsonStringEnumMemberName("encoder")] Encoder,
    [JsonStringEnumMemberName("resolver")] Resolver,
    [JsonStringEnumMemberName("server-acceptance")] ServerAcceptance,
    [JsonStringEnumMemberName("pipeline")] Pipeline,
    [JsonStringEnumMemberName("auth-policy")] AuthPolicy,
    [JsonStringEnumMemberName("csrf-policy")] CsrfPolicy,
}

public sealed record ModuleExport
{
    public required string Name { get; init; }
    public required ExportRole Role { get; init; }
    public required IReadOnlyList<ArtifactTarget> Targets { get; init; }
}

public sealed record Module
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Abi { get; init; }
    public required IReadOnlyList<Artifact> Artifacts { get; init; }
    public required IReadOnlyList<ModuleExport> Exports { get; init; }
    public required IReadOnlyList<string> DependencyIds { get; init; }
    public required string License { get; init; }
    public required IReadOnlyList<string> NoticeFiles { get; init; }
}

public sealed record DocumentationEntry
{
    public required string TargetId { get; init; }
    public required string Summary { get; init; }
    public required string Description { get; init; }
}

/// <summary>Root of <c>tisilia.contract</c> 0.1 (<c>contract.schema.json</c>).</summary>
public sealed record ContractDocument
{
    public required string Format { get; init; }
    public required string Version { get; init; }
    public required string ApiId { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
    public required IReadOnlyList<Model> Types { get; init; }
    public required IReadOnlyList<Wire> Wires { get; init; }
    public required IReadOnlyList<Codec> Codecs { get; init; }
    public required IReadOnlyList<Binding> Bindings { get; init; }
    public required IReadOnlyList<Equivalence> Equivalences { get; init; }
    public required IReadOnlyList<Projection> Projections { get; init; }
    public required IReadOnlyList<Comparer> Comparers { get; init; }
    public required IReadOnlyList<Binder> Binders { get; init; }
    public required IReadOnlyList<ResultAdapter> ResultAdapters { get; init; }
    public required IReadOnlyList<Operation> Operations { get; init; }
    public required IReadOnlyList<Module> Modules { get; init; }
    public required IReadOnlyList<DocumentationEntry> Documentation { get; init; }
    public required string SemanticHash { get; init; }
}
