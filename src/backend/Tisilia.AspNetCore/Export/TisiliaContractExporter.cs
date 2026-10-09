using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.AspNetCore.Export;

public sealed record ExportResult(DiagnosticBag Diagnostics, string? Text, JsonObject? Root, int OperationCount, ContractIndex? Index = null, Conformance.RunnerAdapterTable? Adapters = null);

/// <summary>
/// Builds the <c>tisilia.contract</c> from resolved ASP.NET Core metadata (ApiExplorer descriptions for both MVC and
/// minimal APIs) and the effective System.Text.Json options. Only explicitly registered
/// operations are exported; anything that cannot be described exactly is a diagnostic, never a guess.
/// </summary>
public sealed partial class TisiliaContractExporter(IServiceProvider services, IOptions<TisiliaOptions> options)
{
    private readonly object _gate = new();
    private ExportResult? _cached;

    public ExportResult Export()
    {
        lock (_gate)
        {
            return _cached ??= ExportCore();
        }
    }

    /// <summary>Recomputes (tests and watch mode).</summary>
    public ExportResult ExportFresh()
    {
        lock (_gate)
        {
            _cached = ExportCore();
            return _cached;
        }
    }

    private ExportResult ExportCore(IReadOnlySet<string>? selection = null)
    {
        var bag = new DiagnosticBag();
        var opts = options.Value;
        var builder = new ContractBuilder(opts.ApiId);
        if (opts.DateTimes.ServerTimeZone is { } serverTimeZone)
        {
            builder.BindServerTimeZone(ServerTimeZoneTable.Of(serverTimeZone).ToBinding());
        }

        var provider = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        var minimalJson = services.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var mvcJson = services.GetService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()?.Value.JsonSerializerOptions;
        var profiles = new Dictionary<JsonSerializerOptions, ProfileContext>(ReferenceEqualityComparer.Instance);
        var mappers = new Dictionary<string, ClrTypeMapper>(StringComparer.Ordinal);
        var adapters = new Conformance.RunnerAdapterTable();
        var documentation = new ApiDocumentation();
        var xml = new XmlTypeMapper(builder, opts);
        var problemDetails = services.GetService<Microsoft.AspNetCore.Http.IProblemDetailsService>() is not null;
        // [ApiController] maps status code results of 400 and above to ProblemDetails unless ApiBehaviorOptions.SuppressMapClientErrors
        var mapClientErrors = services.GetService<IOptions<ApiBehaviorOptions>>()?.Value.SuppressMapClientErrors != true;
        var nullAsNoContent = NullResultIsNoContent(services.GetService<IOptions<MvcOptions>>()?.Value);

        ProfileContext ProfileFor(JsonSerializerOptions source, string suffix)
        {
            if (profiles.TryGetValue(source, out var existing))
            {
                return existing;
            }

            var ctx = ProfileContext.Create(builder, opts.ApiId + ".profile." + suffix, source, opts.Resolvers, bag);
            profiles[source] = ctx;
            // models are named by CLR type and direction: options that describe types otherwise than the minimal API's (MVC with its own
            // naming policy, converters …) or an endpoint's own options get their own model scope, so neither profile's description
            // replaces the other's
            var ownScope = suffix.StartsWith("endpoint.", StringComparison.Ordinal) || !ReferenceEquals(source, minimalJson) && !ProfileContext.DescribeTypesAlike(source, minimalJson);
            mappers[ctx.Profile.Id] = new ClrTypeMapper(builder, ctx, opts, bag, adapters, ownScope ? ctx.Profile.Id : null);
            return ctx;
        }

        List<ApiDescription> descriptions;
        try
        {
            descriptions = provider.ApiDescriptionGroups.Items.SelectMany(g => g.Items).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // ApiExplorer resolves System.Text.Json metadata of every endpoint (e.g. two members with one JSON name): the application itself
            // cannot serve such an endpoint, and the export reports it instead of failing with an unhandled exception
            bag.Error(TisiliaCodes.ConfigInvalid, "SV01", "/operations", $"ASP.NET Core could not describe the endpoints ({e.GetType().Name}: {e.Message})", [],
                "fix the type the message names; the application fails the same way when the endpoint is called");
            return new ExportResult(bag, null, null, 0);
        }
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var endpoint in endpoints.Where(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>() is { } m && (selection is null || selection.Contains(m.OperationId))))
        {
            var selected = endpoint.Metadata.GetMetadata<TisiliaOperationAttribute>()!;
            if (!descriptions.Any(d => d.ActionDescriptor.EndpointMetadata.OfType<TisiliaOperationAttribute>().Any(m => m.OperationId == selected.OperationId)))
            {
                bag.Error(TisiliaCodes.ConfigInvalid, "SV01", "/operations", "selected endpoint is absent from ApiExplorer; analysis is incomplete", [selected.OperationId]);
            }
        }
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var description in descriptions)
        {
            var metadata = description.ActionDescriptor.EndpointMetadata.OfType<TisiliaOperationAttribute>().FirstOrDefault();
            if (metadata is null || (selection is not null && !selection.Contains(metadata.OperationId)))
            {
                continue;
            }

            var opId = metadata.OperationId;
            var opPath = "/operations/" + count;
            if (!seenIds.Add(opId))
            {
                bag.Error(TisiliaCodes.DuplicateId, "SV02", opPath, $"operation id '{opId}' is registered on more than one endpoint", [opId]);
                continue;
            }

            var isMvc = description.ActionDescriptor is ControllerActionDescriptor;
            var jsonOptions = isMvc ? (mvcJson ?? minimalJson) : minimalJson;
            ProfileContext Profile() => ProfileFor(jsonOptions, isMvc ? "mvc" : "minimal");
            ClrTypeMapper Mapper() => mappers[Profile().Profile.Id];
            var matches = endpoints.Where(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>()?.OperationId == opId
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(description.HttpMethod!, StringComparer.OrdinalIgnoreCase) == true
                && (description.ActionDescriptor is not ControllerActionDescriptor controller || e.Metadata.GetMetadata<ControllerActionDescriptor>()?.Id == controller.Id)).ToArray();
            if (matches.Length != 1)
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath, "selected endpoint cannot be uniquely matched to its ApiDescription; analysis is incomplete", [opId]);
                continue;
            }
            var responseDeclaration = matches[0].Metadata.GetMetadata<TisiliaJsonOptionsMetadata>();
            if (isMvc && responseDeclaration is not null && services.GetService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionResultExecutor<JsonResult>>()?.GetType().FullName != "Microsoft.AspNetCore.Mvc.Infrastructure.SystemTextJsonResultExecutor")
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath, "declared MVC JsonResult options require the standard System.Text.Json result executor", [opId]);
                continue;
            }
            ProfileContext ResponseProfile(int status) => responseDeclaration is null || status != responseDeclaration.StatusCode ? Profile() : ProfileFor(responseDeclaration.Options, "endpoint." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(opId)))[..24]);
            ClrTypeMapper ResponseMapper(int status) => mappers[ResponseProfile(status).Profile.Id];
            // ServerSentEventsResult<T> serializes event data with the minimal API JsonOptions from DI, also when an MVC action
            // returns it, and whatever an endpoint declares for TypedResults.Json (aspnetcore v10.0.0)
            ProfileContext EventProfile() => ProfileFor(minimalJson, "minimal");
            ClrTypeMapper EventMapper() => mappers[EventProfile().Profile.Id];
            var conflicts = builder.ProfileConflicts.Count;
            var operation = BuildOperation(description, metadata, isMvc, Profile, Mapper, ResponseProfile, ResponseMapper, EventProfile, EventMapper, opts.DateTimes.Default, matches[0], services.GetRequiredService<ParameterPolicyFactory>(), services.GetService<Microsoft.AspNetCore.Mvc.ModelBinding.IModelMetadataProvider>(), opts.CustomBinding, builder, bag, opPath, problemDetails, mapClientErrors, nullAsNoContent, documentation, services, xml);
            foreach (var conflict in builder.ProfileConflicts.Skip(conflicts))
            {
                var id = conflict.Id.StartsWith("type:", StringComparison.Ordinal) || conflict.Id.StartsWith("wire:", StringComparison.Ordinal) ? conflict.Id[5..] : conflict.Id;
                var described = builder.GetType(id)?.ClrIdentity ?? id;
                bag.Error(TisiliaCodes.ProfileResolution, "SV16", opPath, $"operation '{opId}': the JSON options of profiles '{conflict.FirstProfileId}' and '{conflict.SecondProfileId}' describe '{described}' differently under one model id ('{id}'), so one description would be wrong for the other's endpoints", [opId, id],
                    "give the minimal API and MVC JSON options the same converters with the same settings, or serve the type through one of them; options that differ in naming, number handling, ignore conditions, converter types or resolvers already get their own models");
            }
            if (operation is not null)
            {
                builder.AddOperation(operation);
                count++;
            }
        }

        if (count == 0 && !bag.HasErrors)
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV01", "/operations", "no endpoint is registered with WithTisiliaOperation/[TisiliaOperation]; Tisilia only exports explicit operations");
        }

        // documentation: what the endpoints, types and XML comments say, then the application's own entries over it
        foreach (var mapper in mappers.Values)
        {
            documentation.Types(mapper.Documented);
        }

        documentation.Types(xml.Documented);

        foreach (var (targetId, doc) in opts.Documentation)
        {
            documentation.Override(targetId, doc.Summary, doc.Description);
        }

        foreach (var (targetId, summary, description) in documentation.Entries)
        {
            builder.AddDocumentation(targetId, summary, description);
        }

        foreach (var profile in profiles.Values)
        {
            profile.Finish();
        }

        if (bag.HasErrors)
        {
            return new ExportResult(bag, null, null, count);
        }

        var root = builder.BuildJson();
        ReportUnstableArtifacts(root, bag);
        var text = root.ToJsonString(TisiliaJson.IndentedOptions) + "\n";
        // The exporter validates its own output: a contract that fails semantic validation is never written (fail closed).
        var loaded = ContractLoader.Load(text, bag);
        ContractIndex? index = null;
        if (loaded is not null)
        {
            index = SemanticValidator.Validate(loaded, bag);
        }

        return bag.HasErrors ? new ExportResult(bag, null, null, count) : new ExportResult(bag, text, root, count, index, adapters);
    }

    private static Operation? BuildOperation(ApiDescription description, TisiliaOperationAttribute metadata, bool isMvc, Func<ProfileContext> profile, Func<ClrTypeMapper> mapper, Func<int, ProfileContext> responseProfile, Func<int, ClrTypeMapper> responseMapper, Func<ProfileContext> eventProfile, Func<ClrTypeMapper> eventMapper, Bindings.DateTimeWire? dateTimeWire, RouteEndpoint endpoint, ParameterPolicyFactory policyFactory, Microsoft.AspNetCore.Mvc.ModelBinding.IModelMetadataProvider? modelMetadata, Bindings.CustomBindingCollection customBinding, ContractBuilder builder, DiagnosticBag bag, string opPath, bool problemDetails, bool mapClientErrors, bool nullAsNoContent, ApiDocumentation documentation, IServiceProvider services, XmlTypeMapper xml)
    {
        // what the documentation is read from, once the operation is known to be exported
        var documentedParameters = new List<(string Id, ApiParameterDescription Description, Type ValueType)>();
        var documentedResponses = new List<(string Id, int Status, string? Description, bool ReturnedValue)>();
        System.Reflection.ParameterInfo? bodyParameter = null;
        var opId = metadata.OperationId;
        if (!Enum.TryParse<HttpMethodKind>(description.HttpMethod, ignoreCase: false, out var method))
        {
            bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath + "/method", $"operation '{opId}': HTTP method '{description.HttpMethod}' is not supported", [opId]);
            return null;
        }

        // MVC's ApiExplorer writes every route parameter as "{name}" (DefaultApiDescriptionProvider.GetRelativePath drops constraints,
        // '?', defaults and the catch-all '*'), which hid catch-all and optional parameters from SV30; the attribute route template
        // keeps them, as a minimal API's RoutePattern.RawText (its RelativePath) does
        var template = isMvc ? description.ActionDescriptor.AttributeRouteInfo?.Template : null;
        var route = "/" + (template ?? description.RelativePath ?? "").TrimStart('/');
        var parameters = new List<Parameter>();
        var formFields = new List<FormField>();
        var everyFile = new List<FormField>();
        RequestBody requestBody = new NoRequestBody();
        var np = NumberProfile.Strict;
        var apiController = isMvc && description.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any();
        var pIndex = 0;
        var mvcFormModels = new HashSet<Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor>();
        foreach (var p in description.ParameterDescriptions)
        {
            var pPath = opPath + "/parameters/" + pIndex;
            var source = p.Source;
            var type = ParameterClrType(p, isMvc);
            if (source == BindingSource.Body)
            {
                if (requestBody is not NoRequestBody)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': more than one body parameter", [opId]);
                    continue;
                }

                if (type == typeof(Stream) || type == typeof(System.IO.Pipelines.PipeReader))
                {
                    var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
                    var rawMedia = accepts?.ContentTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
                    if (isMvc || rawMedia.Length != 1 || !HttpRules.IsRawRequestMediaType(rawMedia[0]))
                    {
                        bag.Error(TisiliaCodes.MediaTypeInvalid, "SV29", pPath, $"operation '{opId}': raw uploads require a minimal API and one concrete, non-form media type", [opId],
                            "declare Accepts<Stream> or Accepts<PipeReader> with the actual media type; multipart and form binding require a separate adapter");
                        continue;
                    }
                    requestBody = new BinaryRequestBody { MediaType = HttpRules.ParseMediaType(rawMedia[0])!.Essence, Presence = accepts!.IsOptional ? Presence.Optional : Presence.Required };
                    continue;
                }

                // The media type the client sends the JSON body with. ApiExplorer's request formats are, for MVC, the media types an input
                // formatter reads within the declared ones ([Consumes]); for a minimal API, the declared ones (Accepts<T>, application/json
                // when inferred), of which the request delegate reads application/json and +json types only (HasJsonContentType answers
                // text/json with 415). Routing answers a content type outside the declared ones with 415 (AcceptsMatcherPolicy), so a
                // wildcard declared with Accepts<T> admits application/json; [Consumes] refuses wildcards (aspnetcore v10.0.0).
                static bool IsJson(string media) => HttpRules.ParseMediaType(media) is { } parsed && HttpRules.JsonMediaEssences.Contains(parsed.Essence);
                static string? MinimalApiJson(string declared) => HttpRules.ParseMediaType(declared) switch
                {
                    { Type: "*", Subtype: "*" } or { Type: "application", Subtype: "*" } => "application/json",
                    { } media when HttpRules.JsonMediaEssences.Contains(media.Essence) && (media.Essence == "application/json" || media.Subtype.EndsWith("+json", StringComparison.Ordinal)) => media.Essence,
                    _ => null,
                };
                var requestFormats = description.SupportedRequestFormats.Select(f => f.MediaType).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var mediaType = isMvc ? requestFormats.Where(IsJson).Select(m => HttpRules.ParseMediaType(m)!.Essence).FirstOrDefault()
                    : requestFormats.Length == 0 ? "application/json" : requestFormats.Select(MinimalApiJson).FirstOrDefault(m => m is not null);
                // MVC: an input formatter reads the body in a declared media type that is not JSON (XmlSerializerInputFormatter, a custom
                // formatter). The client sends the bytes it is given; the contract does not describe their shape.
                // (XmlSerializerInputFormatter lists application/xml, text/xml and the application/*+xml pattern: the first concrete one is sent)
                var readable = requestFormats.Select(HttpRules.ParseMediaType).FirstOrDefault(m => m is not null && !m.Type.Contains('*') && !m.Subtype.Contains('*') && HttpRules.IsRawRequestMediaType(m.Essence));
                if (mediaType is null && isMvc && readable is not null)
                {
                    var presence = p.IsRequired || !IsNullableType(type) ? Presence.Required : Presence.Optional;
                    // XmlSerializerInputFormatter reads the body with XmlSerializer: the contract describes it with XmlSerializer's own mapping
                    var xmlReason = XmlInput(description.SupportedRequestFormats, readable.Essence, type, xml, out var xmlBody, out var maxDepth);
                    if (xmlBody is not null)
                    {
                        requestBody = new XmlRequestBody { MediaType = readable.Essence, Root = xmlBody.Root, Use = xmlBody.Use, Presence = presence, MaxDepth = maxDepth };
                        bodyParameter = (p.ParameterDescriptor as Microsoft.AspNetCore.Mvc.Infrastructure.IParameterInfoParameterDescriptor)?.ParameterInfo;
                        continue;
                    }

                    bag.Warning(TisiliaCodes.MediaTypeInvalid, "SV29", pPath, $"operation '{opId}': an input formatter reads the {FriendlyName(type)} body as {string.Join(", ", requestFormats)}{(xmlReason is null ? "" : $" ({xmlReason})")}; the contract does not describe that representation, so the client sends the bytes it is given as {readable.Essence}", [opId],
                        xmlReason is null ? "accept application/json for this body to have the client encode the value" : "accept application/json for this body, or keep its type to what XmlSerializer writes without xsi:type, choices or xs:any, to have the client encode the value");
                    requestBody = new BinaryRequestBody { MediaType = readable.Essence, Presence = presence };
                    continue;
                }

                if (mediaType is null)
                {
                    var accepted = requestFormats.Length > 0 ? requestFormats : endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.ToArray() ?? [];
                    bag.Error(TisiliaCodes.MediaTypeInvalid, "SV29", pPath, accepted.Length > 0
                            ? $"operation '{opId}': the request body accepts {string.Join(", ", accepted)}; {(isMvc ? "no input formatter reads it as JSON" : "a minimal API reads only application/json and +json types as JSON")}. Only JSON request bodies, raw uploads and forms are supported"
                            : $"operation '{opId}': no input formatter reads the request body as JSON. Only JSON request bodies, raw uploads and forms are supported", [opId],
                        "accept application/json for this body, or keep the operation out of the Tisilia operations");
                    continue;
                }

                var use = mapper().Map(type, WireDirection.ServerRead, pPath, isRoot: true);
                if (use is null)
                {
                    continue;
                }

                requestBody = new JsonRequestBody
                {
                    MediaType = mediaType,
                    ProfileId = profile().Profile.Id,
                    Use = use,
                    Presence = p.IsRequired || !IsNullableType(type) ? Presence.Required : Presence.Optional,
                };
                bodyParameter = (p.ParameterDescriptor as Microsoft.AspNetCore.Mvc.Infrastructure.IParameterInfoParameterDescriptor)?.ParameterInfo;
                continue;
            }

            if (source == BindingSource.Services || source == BindingSource.Special || source == BindingSource.FormFile || source == BindingSource.Form)
            {
                if (source == BindingSource.FormFile || source == BindingSource.Form)
                {
                    // MVC's ApiExplorer spreads a complex form model into one description per leaf property (DefaultApiDescriptionProvider,
                    // aspnetcore v10.0.0): the model is described once, from the first of them
                    var spread = isMvc && p.ModelMetadata?.MetadataKind == Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.ModelMetadataKind.Property && p.ParameterDescriptor is not null;
                    if (!spread || mvcFormModels.Add(p.ParameterDescriptor!))
                    {
                        AddFormFields(p, isMvc, apiController, endpoint.Metadata.GetOrderedMetadata<IParameterBindingMetadata>(), mapper, modelMetadata, builder, bag, opId, pPath, formFields, everyFile);
                    }
                }

                continue; // DI and HttpContext parameters are not part of the HTTP contract
            }

            // an MVC model binder ([ModelBinder]) reads the request in its own code: the parameter is what its declaration says
            if (isMvc && (p.BindingInfo?.BinderType ?? p.ModelMetadata?.BinderType) is { } binderType)
            {
                AddDeclaredReads(customBinding.ForModelBinder(binderType), $"binds through the model binder {FriendlyName(binderType)}", p.BindingInfo?.BinderModelName ?? p.Name,
                    $"options.CustomBinding.ModelBinder<{FriendlyName(binderType)}>(reads => reads.Query<string>(RequestReads.ModelName))", pPath);
                continue;
            }

            ParameterLocation location;
            if (source == BindingSource.Path)
            {
                location = ParameterLocation.Path;
            }
            else if (source == BindingSource.Query || source == BindingSource.ModelBinding)
            {
                location = ParameterLocation.Query;
            }
            else if (source == BindingSource.Header)
            {
                location = ParameterLocation.Header;
            }
            else
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': parameter '{p.Name}' has binding source '{source?.Id ?? "unknown"}' which requires an explicit binder adapter", [opId],
                    "use [FromRoute]/[FromQuery]/[FromHeader]/[FromBody] or register a binder binding");
                continue;
            }

            // ParameterInfo.DefaultValue (what minimal APIs report) is DBNull.Value when the parameter declares no default — not a default of
            // its own, and distinct from an explicit null default; MVC reports null then (ProcessParameterDefaultValue)
            var declaredDefault = p.DefaultValue is DBNull or System.Reflection.Missing ? null : p.DefaultValue;
            var (elementType, repeated) = UnwrapCollection(type);
            // MVC's ApiExplorer sets IsRequired only for [BindRequired] and route values (DefaultApiDescriptionProvider.ProcessIsRequired,
            // v10.0.0); under [ApiController] a validation-required parameter ([Required], or the implicit one of a non-nullable reference
            // type) that is missing is an automatic 400 (ModelStateInvalidFilter), so the client must always send it. A missing repeated
            // parameter binds as an empty collection, which satisfies [Required] (observed on .NET 10: no ids → [] and 200)
            var isRequired = p.IsRequired || (apiController && !repeated && p.ModelMetadata?.IsRequired == true);
            // A path value cannot be null; undefined omission depends on its resolved route and binder. A query/header is nullable when not
            // required (its nullable annotation / default value); items of a repeated parameter are never null
            var routePart = endpoint.RoutePattern.Parameters.SingleOrDefault(v => v.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            // MVC marks all non-nullable value types IsRequired even without a Required validator. An omitted int then binds to 0;
            // only [BindRequired] / an actual [Required] validator prevents omission. Minimal API requirements remain p.IsRequired.
            var validationRequiresValue = p.ModelMetadata?.IsRequired == true && (!elementType.IsValueType
                || Nullable.GetUnderlyingType(elementType) is not null
                || p.ModelMetadata.ValidatorMetadata.OfType<System.ComponentModel.DataAnnotations.RequiredAttribute>().Any());
            var pathBindingRequired = p.IsRequired || (apiController && validationRequiresValue);
            var pathRequired = routePart is null || !(endpoint.RoutePattern.Defaults.ContainsKey(routePart.Name) || ((routePart.IsOptional || routePart.IsCatchAll) && !pathBindingRequired));
            var nullable = location != ParameterLocation.Path && (Nullable.GetUnderlyingType(elementType) is not null
                || (!elementType.IsValueType && !repeated && !isRequired));
            var parameterInfo = (p.ParameterDescriptor as Microsoft.AspNetCore.Mvc.Infrastructure.IParameterInfoParameterDescriptor)?.ParameterInfo;
            var hasServerDefault = parameterInfo?.HasDefaultValue == true;
            var scalarType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            var scalar = scalarType == typeof(DateTime) ? dateTimeWire switch
            { Bindings.DateTimeWire.Utc => "datetime-utc", Bindings.DateTimeWire.Unspecified => "datetime-unspecified", Bindings.DateTimeWire.Local => "datetime-local-wire", _ => "datetime" } : ClrTypeMapper.ScalarNameOf(scalarType);
            if (scalarType == typeof(DateTime) && scalar == "datetime-local-wire")
            {
                // minimal APIs and MVC parse DateTime parameters with DateTimeStyles.AdjustToUniversal (aspnetcore v10.0.0
                // ParameterBindingMethodCache, DateTimeModelBinderProvider): "Z" stays Utc and a zone-less value stays Unspecified,
                // but an offset is converted to UTC — a Local wire cannot travel in a route, query or header value
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath,
                    $"operation '{opId}': DateTime parameter '{p.Name}' is declared Local, but ASP.NET Core binds DateTime parameters with DateTimeStyles.AdjustToUniversal (an offset becomes UTC); use Mixed, Utc, Unspecified, or DateTimeOffset", [opId]);
                continue;
            }

            if (scalarType.IsEnum)
            {
                // Enum.TryParse (minimal APIs) or MVC's enum binder: the client writes a defined member's C# name, else the integer; MVC binds
                // only defined values (EnumTypeModelBinder.CheckModel), so its parameters refuse the others before sending
                if (location == ParameterLocation.Path && repeated)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': path parameter '{p.Name}' cannot be a collection", [opId]);
                    continue;
                }

                var enumUse = mapper().Map(scalarType, WireDirection.ServerRead, pPath, isRoot: false);
                if (enumUse is null)
                {
                    continue;
                }

                var enumBinder = builder.EnumBinder(enumUse, definedOnly: isMvc, location, repeated ? Cardinality.Repeated : Cardinality.Single, nullable ? NullPolicy.Omit : NullPolicy.Reject);
                var enumRequired = location == ParameterLocation.Path ? pathRequired : (isRequired && !nullable && !repeated && declaredDefault is null);
                parameters.Add(new Parameter
                {
                    Id = ParameterId(opId, p.Name),
                    Name = p.Name,
                    HasServerDefault = hasServerDefault,
                    Location = location,
                    BinderId = enumBinder,
                    Use = nullable ? builder.Nullable(enumUse) : enumUse,
                    Presence = enumRequired ? Presence.Required : Presence.Optional,
                    // the domain value of an enum is its integer
                    ServerDefault = declaredDefault is null ? (hasServerDefault ? new JsonNullValue() : null) : new Contract.JsonNumberValue { Text = Convert.ToString(Convert.ChangeType(declaredDefault, Enum.GetUnderlyingType(scalarType), System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)! },
                });
                documentedParameters.Add((parameters[^1].Id, p, scalarType));
                pIndex++;
                continue;
            }

            if (scalar is null && scalarType != typeof(DateTime) && mapper().PairedParameter(scalarType, pPath) is { } paired)
            {
                // a module type (an additional codec): the client writes its request codec's canonical text, which the server's TryParse reads
                if (location == ParameterLocation.Path && repeated)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': path parameter '{p.Name}' cannot be a collection", [opId]);
                    continue;
                }

                var pairedBinder = builder.CodecBinder(paired.Use, paired.Registration.ParameterGrammarId!, location, repeated ? Cardinality.Repeated : Cardinality.Single, nullable ? NullPolicy.Omit : NullPolicy.Reject);
                var pairedRequired = location == ParameterLocation.Path ? pathRequired : (isRequired && !nullable && !repeated && declaredDefault is null);
                parameters.Add(new Parameter
                {
                    Id = ParameterId(opId, p.Name),
                    Name = p.Name,
                    HasServerDefault = hasServerDefault,
                    Location = location,
                    BinderId = pairedBinder,
                    Use = nullable ? builder.Nullable(paired.Use) : paired.Use,
                    Presence = pairedRequired ? Presence.Required : Presence.Optional,
                    ServerDefault = declaredDefault is null ? (hasServerDefault ? new JsonNullValue() : null) : paired.Registration.Project?.Invoke(declaredDefault),
                });
                documentedParameters.Add((parameters[^1].Id, p, scalarType));
                pIndex++;
                continue;
            }

            if (scalar is null && ReadsOwnText(scalarType, isMvc, repeated ? p.ModelMetadata?.ElementMetadata : p.ModelMetadata))
            {
                // The server reads the text with the type's own parser (a minimal API through its TryParse, IParsable<T> included; MVC
                // through its TypeConverter or TryParse): the client sends it as a string the contract does not check.
                if (location == ParameterLocation.Path && repeated)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': path parameter '{p.Name}' cannot be a collection", [opId]);
                    continue;
                }

                bag.Warning(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': parameter '{p.Name}' is sent as text that the server parses with {FriendlyName(scalarType)}'s own parser; the contract does not describe which texts it accepts, so the client sends any string and the server answers the texts it refuses", [opId],
                    "take a builtin scalar or an additional codec type to have the client check the value");
                var parsedRequired = location == ParameterLocation.Path ? pathRequired : (isRequired && !nullable && !repeated && declaredDefault is null);
                parameters.Add(new Parameter
                {
                    Id = ParameterId(opId, p.Name),
                    Name = p.Name,
                    HasServerDefault = hasServerDefault,
                    Location = location,
                    BinderId = builder.ServerParsedBinder("string", location, repeated ? Cardinality.Repeated : Cardinality.Single, nullable ? NullPolicy.Omit : NullPolicy.Reject),
                    Use = builder.Scalar("string", NumberProfile.Strict, nullable),
                    Presence = parsedRequired ? Presence.Required : Presence.Optional,
                    // a default of the type itself has no text the contract could hold
                    ServerDefault = hasServerDefault ? new JsonNullValue() : null,
                });
                documentedParameters.Add((parameters[^1].Id, p, scalarType));
                pIndex++;
                continue;
            }

            if (scalar is null || scalar is "bytes" or "json-value" or "datetime-local-wire")
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': parameter '{p.Name}' of type '{elementType}' has no standard HTTP string binder; TryParse/BindAsync types need a registered binder binding", [opId]);
                continue;
            }

            if (location == ParameterLocation.Path && repeated)
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': path parameter '{p.Name}' cannot be a collection", [opId]);
                continue;
            }

            var scalarUse = builder.Scalar(scalar, np, nullable);
            var binderId = builder.StandardBinder(scalar, location, repeated ? Cardinality.Repeated : Cardinality.Single, nullable ? NullPolicy.Omit : NullPolicy.Reject);
            // a repeated parameter that is absent binds as an empty collection (observed on .NET 10: GET /http/query?q=x → tags [] and 200)
            var required = location == ParameterLocation.Path ? pathRequired : (isRequired && !nullable && !repeated && declaredDefault is null);
            parameters.Add(new Parameter
            {
                Id = ParameterId(opId, p.Name),
                Name = p.Name,
                HasServerDefault = hasServerDefault,
                Location = location,
                BinderId = binderId,
                Use = scalarUse,
                Presence = required ? Presence.Required : Presence.Optional,
                ServerDefault = declaredDefault is null ? (hasServerDefault ? new JsonNullValue() : null) : ToJsonValue(declaredDefault),
            });
            documentedParameters.Add((parameters[^1].Id, p, scalarType));
            pIndex++;
        }

        var responses = new List<Response>();
        var responseTypes = ResponseMetadata(description, bag, opPath);
        var endpointMetadata = description.ActionDescriptor.EndpointMetadata;
        var handler = HandlerMethod(description);
        // EndpointMetadataApiDescriptionProvider files a BindAsync parameter under Services and leaves it out of the description
        // (aspnetcore v10.0.0). The request delegate records how it binds each parameter and [AsParameters] member, so an explicit
        // [FromBody], [FromQuery] … still wins over the type's BindAsync here as it does at run time. BindAsync code may read the body,
        // query or headers: nothing the contract can say.
        foreach (var bound in endpoint.Metadata.GetOrderedMetadata<IParameterBindingMetadata>().Where(p => p.HasBindAsync))
        {
            var boundType = Nullable.GetUnderlyingType(bound.ParameterInfo.ParameterType) ?? bound.ParameterInfo.ParameterType;
            AddDeclaredReads(customBinding.ForBindAsync(boundType), $"binds through {FriendlyName(boundType)}.BindAsync", bound.Name, $"options.CustomBinding.BindAsync<{FriendlyName(boundType)}>(reads => reads.Query<int>(\"page\"))", opPath + "/parameters");
        }

        // A type's BindAsync or an MVC model binder reads the request in the application's own code, which the contract can describe only
        // as declared (TisiliaOptions.CustomBinding): each declared value is a parameter whose canonical text that code parses.
        void AddDeclaredReads(Bindings.RequestReads? reads, string binding, string modelName, string declaration, string at)
        {
            if (reads is null)
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", at, $"operation '{opId}': parameter '{modelName}' {binding}, which reads the request in code the contract cannot describe without a declaration", [opId],
                    $"declare what it reads — {declaration}, or reads.NotFromRequest() for a value of claims or features — or bind request data with [FromRoute], [FromQuery], [FromHeader], [FromBody] or [FromForm]");
                return;
            }
            foreach (var read in reads.Values)
            {
                var name = read.Name.Replace(Bindings.RequestReads.ModelName, modelName, StringComparison.Ordinal);
                var (readElement, readRepeated) = UnwrapCollection(read.Type);
                var readType = Nullable.GetUnderlyingType(readElement) ?? readElement;
                var readScalar = readType == typeof(DateTime) ? "datetime" : ClrTypeMapper.ScalarNameOf(readType);
                if (readScalar is null or "bytes" or "json-value" or "datetime-local-wire")
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", at, $"operation '{opId}': the declared read '{name}' of parameter '{modelName}' is a {FriendlyName(read.Type)}; declare a builtin scalar, or an array or list of one", [opId]);
                    continue;
                }
                if (read.Location == ParameterLocation.Path && (readRepeated || endpoint.RoutePattern.GetParameter(name) is null))
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", at, $"operation '{opId}': the declared route value '{name}' of parameter '{modelName}' is not a single parameter of the route '{endpoint.RoutePattern.RawText}'", [opId]);
                    continue;
                }
                if (parameters.Any(other => string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", at, $"operation '{opId}': the declared read '{name}' of parameter '{modelName}' is also another parameter of the operation", [opId]);
                    continue;
                }
                var optionalRead = read.Location != ParameterLocation.Path && !readRepeated && (read.Optional || Nullable.GetUnderlyingType(readElement) is not null);
                parameters.Add(new Parameter
                {
                    Id = ParameterId(opId, name),
                    Name = name,
                    HasServerDefault = false,
                    Location = read.Location,
                    BinderId = builder.ServerParsedBinder(readScalar, read.Location, readRepeated ? Cardinality.Repeated : Cardinality.Single, optionalRead ? NullPolicy.Omit : NullPolicy.Reject),
                    Use = builder.Scalar(readScalar, NumberProfile.Strict, optionalRead),
                    Presence = optionalRead || readRepeated ? Presence.Optional : Presence.Required,
                });
            }
        }
        var handlerResult = handler is null ? null : UnwrapAwaitable(handler.ReturnType);
        var jsonDeclaration = endpoint.Metadata.GetMetadata<TisiliaJsonOptionsMetadata>();
        if (jsonDeclaration is not null)
        {
            var matchingReturn = isMvc ? handlerResult == typeof(JsonResult)
                : handlerResult is { IsGenericType: true } && handlerResult.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<>)
                    && handlerResult.GetGenericArguments()[0] == jsonDeclaration.ValueType;
            if (endpoint.Metadata.OfType<TisiliaJsonOptionsMetadata>().Count() != 1 || !matchingReturn)
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", "WithTisiliaJsonOptions<T> requires one declaration and a handler returning JsonHttpResult<T>, or JsonResult in MVC (optionally Task/ValueTask wrapped)", [opId]);
                return null;
            }
            if (isMvc)
            {
                // MVC ApiExplorer predates endpoint conventions. Read the explicit convention from the resolved endpoint.
                if (responseTypes.Any(r => r.StatusCode == jsonDeclaration.StatusCode && r.Type is not null && r.Type != typeof(void) && r.Type != jsonDeclaration.ValueType))
                {
                    bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", "MVC JSON declaration conflicts with the produced response type", [opId]);
                    return null;
                }
                responseTypes.RemoveAll(r => r.StatusCode == jsonDeclaration.StatusCode);
                var declared = new ApiResponseType { StatusCode = jsonDeclaration.StatusCode, Type = jsonDeclaration.ValueType };
                declared.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = jsonDeclaration.ContentType });
                responseTypes.Add(declared);
            }
        }
        var declaresResponses = endpointMetadata.OfType<IProducesResponseTypeMetadata>().Any() || endpointMetadata.OfType<IApiResponseMetadataProvider>().Any();
        // EndpointMetadataApiDescriptionProvider treats an IResult return type without response metadata as void and still lists a
        // 200 response (aspnetcore v10.0.0): the result decides at run time what it writes, so that 200 describes nothing
        var inferred = false;
        if (responseTypes.Count == 0 || (!isMvc && handlerResult is not null && typeof(IResult).IsAssignableFrom(handlerResult) && !declaresResponses))
        {
            // the responses Tisilia's source generator read from the handler's return paths, which are what the typed declaration
            // would have said; a path it could not read leaves the operation undescribed
            var inference = handlerResult == typeof(IResult) || handlerResult == typeof(IActionResult) || handlerResult == typeof(ActionResult)
                ? ResponseInference.Read(opId, handler, description, services)
                : null;
            if (inference?.Responses is not { } read)
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", $"operation '{opId}' declares no response types{(handlerResult is null ? "" : $" (it returns {FriendlyName(handlerResult)})")}; IActionResult/IResult without produces metadata cannot be exported{(inference?.Failure is { } why ? $", and Tisilia could not read them from the source: {why}" : "")}", [opId],
                    "return TypedResults (Ok<T>, Created<T>, Results<…>) or ActionResult<T>, or add [ProducesResponseType]/Produces<T>() metadata; Tisilia reads the responses itself from handlers whose every path returns a Results, TypedResults or ControllerBase helper");
                return null;
            }

            responseTypes = read;
            inferred = true;
        }

        // TypedResults.Json and MVC's JsonResult serialize with the JsonSerializerOptions handed to them at run time, which metadata cannot
        // show (explicit options are checked per result); the contract would describe the endpoint's options instead
        if (OpaqueJsonResults(handlerResult) is { Count: > 0 } opaque && endpoint.Metadata.GetMetadata<TisiliaJsonOptionsMetadata>() is null)
        {
            bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", $"operation '{opId}' returns {string.Join(", ", opaque.Select(FriendlyName))}, which serializes with JsonSerializerOptions chosen at run time; endpoint metadata cannot show them", [opId],
                "return TypedResults.Ok<T>/Created<T>/… or the value itself (written with the endpoint's JSON options), or ActionResult<T> in MVC");
            return null;
        }

        // a handler that receives HttpContext/HttpResponse and returns nothing can write any body itself: the contract declares what the
        // metadata says (no body), which only holds if the handler writes nothing
        if (!isMvc && handlerResult == typeof(void) && responseTypes.All(r => r.Type is null || r.Type == typeof(void)) && handler!.GetParameters().Any(p => p.ParameterType == typeof(HttpContext) || p.ParameterType == typeof(HttpResponse)))
        {
            bag.Warning(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", $"operation '{opId}' returns no result but receives {(handler.GetParameters().Any(p => p.ParameterType == typeof(HttpResponse)) ? "HttpResponse" : "HttpContext")}: a body it writes itself is not described; the contract declares {string.Join(", ", responseTypes.Select(r => r.StatusCode == 0 ? 200 : r.StatusCode))} without a body", [opId],
                "return a typed result (TypedResults.Ok<T>, Results<…>) instead of writing to the response");
        }

        // a Results<…> member that adds no endpoint metadata (UnauthorizedHttpResult, ProblemHttpResult, …) has no response case unless
        // the endpoint declares its status: the client would report it as unexpected-response
        if (UndescribedResults(description, responseTypes.Select(r => r.StatusCode == 0 ? 200 : r.StatusCode).ToHashSet()) is { Count: > 0 } undescribed)
        {
            bag.Warning(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", $"operation '{opId}' can return {string.Join(", ", undescribed.Select(FriendlyName))}, which add no response metadata: the statuses they write are not in the contract and the client reports them as unexpected-response", [opId],
                "declare them with .Produces(status) / .ProducesProblem(status) or [ProducesResponseType]");
        }

        // the value a handler returns (T, Task<T>, ActionResult<T>) and whether its nullable annotation lets it be null
        var returnedValue = handlerResult is null ? null : ReturnedValueType(handlerResult);
        var returnedValueMayBeNull = returnedValue is not null && ReturnValueMayBeNull(handler!);

        // MVC's ApiExplorer lists the 200 of an action's return type only when no status is declared at all (ApiResponseTypeProvider,
        // aspnetcore v10.0.0): declaring only an error ([ProducesResponseType<ValidationProblemDetails>(400)]) dropped the 200 the action
        // still writes. It is restored unless a success status is declared, with the media type ApiExplorer would have given it. (Minimal
        // APIs keep it: RequestDelegateFactory adds 200 metadata for a value-returning handler.)
        if (returnedValue is not null && !responseTypes.Any(r => r.StatusCode is 0 or (>= 200 and < 300)))
        {
            var implicitOk = new ApiResponseType { StatusCode = 200, Type = returnedValue };
            if (!(isMvc && returnedValue == typeof(string)))
            {
                implicitOk.ApiResponseFormats.Add(new ApiResponseFormat { MediaType = returnedValue == typeof(string) ? "text/plain" : "application/json" });
            }

            responseTypes.Insert(0, implicitOk);
        }

        if (formFields.Count > 0)
        {
            if (everyFile.Count > 0 && FormField.Descendants(formFields).Count(f => f.Kind == "file") > 1)
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath + "/requestBody", $"'{everyFile[0].Name}' (IFormFileCollection) receives every file of the request, so it cannot be combined with another file field", [opId],
                    "make it the operation's only file field, or name the files: IReadOnlyList<IFormFile> on a model, or a controller action, whose IFormFileCollection receives the files of its own name");
            }
            if (requestBody is not NoRequestBody)
            {
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath + "/requestBody", "form fields cannot share a request with a JSON or raw body", [opId]);
            }
            var multipart = FormField.Descendants(formFields).Any(f => f.Kind == "file");
            var mediaTypes = description.SupportedRequestFormats.Select(f => HttpRules.ParseMediaType(f.MediaType)?.Essence).ToArray();
            var media = !multipart && mediaTypes.Contains("application/x-www-form-urlencoded", StringComparer.Ordinal) ? "application/x-www-form-urlencoded" : "multipart/form-data";
            requestBody = new FormRequestBody { MediaType = media, Fields = formFields, Presence = Presence.Required };
        }

        IReadOnlyList<ResponseInference.HelperWrite>? helperWrites = null;
        var rIndex = 0;
        foreach (var r in responseTypes)
        {
            var rPath = opPath + "/responses/" + rIndex++;
            var status = r.StatusCode == 0 ? 200 : r.StatusCode;
            var caseId = opId + "." + CaseSuffix(status);
            var bodyType = r.Type;
            if (isMvc && apiController && mapClientErrors && status >= 400 && (bodyType is null || bodyType == typeof(void)) && !HttpRules.IsBodylessStatus(status))
            {
                // under [ApiController] ClientErrorResultFilter turns every status code result of 400 and above (NotFound(), StatusCode(500))
                // into a ProblemDetails (ProblemDetailsClientErrorFactory, aspnetcore v10.0.0); ApiExplorer types a bare
                // [ProducesResponseType(404)] that way already, but not an explicit typeof(void) or a 5xx status
                bodyType = typeof(ProblemDetails);
            }

            var isVoid = bodyType is null || bodyType == typeof(void);
            // an inferred response without a body was read from a helper that writes none (Ok(), NoContent(), StatusCode(202))
            if (isVoid && !inferred && status is >= 200 and < 300 && !HttpRules.IsBodylessStatus(status) && method != HttpMethodKind.HEAD
                && (handlerResult == typeof(IResult) || handlerResult == typeof(IActionResult)))
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", rPath, $"operation '{opId}': status {status} has no body type on an opaque result; a bare Produces(status) cannot establish an empty or file body", [opId],
                    "declare the actual body using Produces<T> / ProducesResponseType; a finite file uses FileContentResult/FileStreamResult plus concrete media; known bodyless results may use TypedResults.NoContent");
                continue;
            }
            var formats = r.ApiResponseFormats.Select(f => f.MediaType).Where(m => !string.IsNullOrEmpty(m)).ToList();
            if (isVoid && IsFileMarker(handlerResult))
            {
                formats = formats.Concat(DeclaredVoidContentTypes(endpointMetadata, status)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            var parsedFormats = formats.Select(HttpRules.ParseMediaType).OfType<HttpRules.MediaType>().ToList();
            if (parsedFormats.Any(m => m.Essence == "text/event-stream") || (bodyType is { IsGenericType: true } sse && sse.GetGenericTypeDefinition().FullName == "System.Net.ServerSentEvents.SseItem`1"))
            {
                // ServerSentEventsResult<T> writes strings directly and other data as JSON, except byte[] (raw UTF-8).
                // Metadata must identify the event data type; a text/event-stream content type alone cannot do so.
                if (bodyType is not { IsGenericType: true } eventType || eventType.GetGenericTypeDefinition() != typeof(System.Net.ServerSentEvents.SseItem<>)
                    || eventType.GetGenericArguments()[0] is var declared && declared != typeof(byte[]) && declared != typeof(object) && declared.IsAssignableFrom(typeof(byte[]))
                    || parsedFormats.Count != 1 || parsedFormats[0].Essence != "text/event-stream")
                {
                    bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': SSE needs explicit SseItem<T> metadata with a string or JSON data type", [opId],
                        "use TypedResults.ServerSentEvents with a concrete event data type; raw byte[] and object events have no single declared data encoding");
                    continue;
                }
                var dataType = eventType.GetGenericArguments()[0];
                // byte[] data goes out as its bytes, and object data as JSON unless the value is a byte[] (FormatSseItem, aspnetcore
                // v10.0.0): the client receives either as the event's text, which the contract does not describe
                var opaqueEvents = dataType == typeof(byte[]) || dataType == typeof(object);
                if (opaqueEvents)
                {
                    bag.Warning(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': the server writes {FriendlyName(dataType)} event data {(dataType == typeof(byte[]) ? "as its bytes" : "as JSON, or as its bytes for a byte[] value")}; the client receives each event's data as text, whose shape the contract does not describe (CR and CRLF become LF, and data that is not UTF-8 fails the subscription)", [opId],
                        "declare the event data type (SseItem<T> with a string or a JSON type) to have the client decode it");
                }
                var textEvents = dataType == typeof(string) || opaqueEvents;
                var eventUse = textEvents ? builder.Scalar("string") : eventMapper().Map(dataType, WireDirection.ServerWrite, rPath + "/body/use", isRoot: true, nullableRoot: !dataType.IsValueType);
                if (eventUse is null) { continue; }
                responses.Add(new Response
                {
                    Id = caseId,
                    Status = status,
                    Body = new SseResponseBody
                    {
                        MediaType = "text/event-stream",
                        DataFormat = textEvents ? "text" : "json",
                        ProfileId = textEvents ? null : eventProfile().Profile.Id,
                        Use = eventUse,
                        Resume = endpointMetadata.OfType<TisiliaEventResumeAttribute>().Any() ? "last-event-id" : null,
                    },
                    ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Sse, textEvents ? [] : [eventProfile().Profile.Id]),
                    Hydration = Hydration.ServerOnly,
                    ExposedHeaders = [],
                });
                documentedResponses.Add((caseId, status, r.Description, false));
                continue;
            }

            var binary = IsFileMarker(bodyType) || (isVoid && IsFileMarker(handlerResult));
            if (binary && !HttpRules.IsBodylessStatus(status) && method != HttpMethodKind.HEAD)
            {
                if (parsedFormats.Count == 0 || parsedFormats.Count != formats.Count || parsedFormats.Any(m => m.Type.Contains('*') || m.Subtype.Contains('*')))
                {
                    bag.Error(TisiliaCodes.MediaTypeInvalid, "SV29", rPath, $"operation '{opId}': file case requires explicit status and concrete media", [opId], "add .Produces<FileContentResult>(200, contentType: the actual media) or [ProducesResponseType(typeof(FileContentResult), 200, actualMedia)]");
                    continue;
                }
                foreach (var format in parsedFormats.DistinctBy(m => m.Essence))
                {
                    // Media tokens contain punctuation outside the identifier alphabet; replacing it loses distinctions
                    // such as application/a+b versus application/a.b. Hash the complete selector into a bounded stable id.
                    var binaryId = "binary." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(caseId + "\n" + format.Essence)));
                    responses.Add(new Response
                    {
                        Id = binaryId,
                        Status = status,
                        Body = new BinaryResponseBody { MediaType = format.Essence },
                        ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Binary),
                        Hydration = Hydration.ServerOnly,
                        ExposedHeaders = status == 206 ? ["content-disposition", "content-range"] : ["content-disposition"]
                    });
                    documentedResponses.Add((binaryId, status, r.Description, false));
                }
                continue;
            }

            if (!isVoid && !binary && NonDataBodyType(bodyType!) is { } nonData)
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} declares a {FriendlyName(bodyType!)} body ({nonData}); its wire representation is not declared", [opId],
                    "declare the actual JSON data type, or use a FileContentResult/FileStreamResult marker with explicit status and concrete media for a finite file");
                continue;
            }

            // ApiExplorer drops the content types of a response without a type (CalculateResponseFormatForType returns early for void), so
            // .Produces(200, contentType: "application/pdf") reaches the description as a bare 200: the metadata still names them
            var voidContentTypes = isVoid && !HttpRules.IsBodylessStatus(status) && method != HttpMethodKind.HEAD ? formats.Concat(DeclaredVoidContentTypes(endpointMetadata, status)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
            if (voidContentTypes.Count > 0)
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} declares the content type {string.Join(", ", voidContentTypes)} but no body type", [opId],
                    "declare the body type with Produces<T> / ProducesResponseType; a finite file uses FileContentResult/FileStreamResult and concrete media");
                continue;
            }

            if (isVoid || HttpRules.IsBodylessStatus(status) || method == HttpMethodKind.HEAD)
            {
                responses.Add(new Response
                {
                    Id = caseId,
                    Status = status,
                    Body = new NoResponseBody(),
                    ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Bodyless),
                    Hydration = Hydration.BrowserSafe,
                    ExposedHeaders = [],
                });
                documentedResponses.Add((caseId, status, r.Description, false));
                continue;
            }

            var jsonFormats = parsedFormats.Where(m => HttpRules.JsonMediaEssences.Contains(m.Essence)).ToList();
            // MVC's StringOutputFormatter writes text/plain only: text/xml goes to the XML formatter, which writes a string as <string>…</string>
            var textFormats = parsedFormats.Where(m => m.Type == "text" && !HttpRules.JsonMediaEssences.Contains(m.Essence) && !(isMvc && HttpRules.IsXmlMediaType(m.Essence))).ToList();
            if (bodyType == typeof(string) && ((isMvc && (formats.Count == 0 || textFormats.Count > 0)) || (!isMvc && textFormats.Count > 0 && jsonFormats.Count == 0)))
            {
                // MVC StringOutputFormatter: string return types are text/plain (observed); a Minimal API handler returning
                // string is written directly as text/plain too (learn.microsoft.com "Responses in Minimal API apps": string → text/plain),
                // while TypedResults.Ok<string> stays JSON (its metadata names application/json). A declared text type (Produces<string>(200,
                // "text/html") for TypedResults.Content) is the media type the case is selected by.
                responses.Add(new Response
                {
                    Id = caseId,
                    Status = status,
                    Body = new TextResponseBody { MediaType = textFormats.FirstOrDefault()?.Essence ?? "text/plain", Use = builder.Scalar("string") },
                    ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Text),
                    Hydration = Hydration.ServerOnly,
                    ExposedHeaders = [],
                });
                documentedResponses.Add((caseId, status, r.Description, status == 200 && returnedValue == typeof(string)));
                continue;
            }

            // MVC writes every ProblemDetails value as application/problem+json, whatever the Accept header or the declared formats say
            // (ObjectResultExecutor.InferContentTypes adds application/problem+json and application/problem+xml, aspnetcore v10.0.0)
            var mvcProblem = isMvc && jsonDeclaration?.StatusCode != status && typeof(ProblemDetails).IsAssignableFrom(bodyType);
            // a null return value: minimal APIs write the JSON null (RequestDelegateFactory → WriteAsJsonAsync), MVC writes 204 while
            // HttpNoContentOutputFormatter (TreatNullValueAsNoContent) comes before the JSON formatter, else the JSON null (aspnetcore v10.0.0)
            var returnsValue = status == 200 && returnedValue is not null && (Nullable.GetUnderlyingType(returnedValue) ?? returnedValue) == (Nullable.GetUnderlyingType(bodyType!) ?? bodyType);
            var nullIsNoContent = isMvc && jsonDeclaration?.StatusCode != status && nullAsNoContent && !responseTypes.Any(x => x.StatusCode == 204) && (returnsValue ? returnedValueMayBeNull : status == 200 && !bodyType!.IsValueType);
            // An MVC output formatter writes the body in a declared media type that is not JSON (XmlSerializerOutputFormatter, a custom
            // formatter), and a minimal API string goes out as the declared media type it was given (TypedResults.Content): the client
            // receives the bytes, whose shape the contract does not describe. A minimal API writes any other body as JSON, whatever the
            // metadata declares.
            if (formats.Count > 0 && jsonFormats.Count == 0 && !mvcProblem && (isMvc || bodyType == typeof(string))
                && parsedFormats.Count == formats.Count && parsedFormats.All(m => !m.Type.Contains('*') && !m.Subtype.Contains('*') && m.Essence != "text/event-stream"))
            {
                // MVC's XmlSerializerOutputFormatter writes the body with XmlSerializer: the contract describes such a case with XmlSerializer's
                // own mapping; any other formatter's bytes are what the client receives
                var xmlCases = new List<(HttpRules.MediaType Format, XmlBody Body)>();
                var opaqueFormats = new List<HttpRules.MediaType>();
                string? xmlReason = null;
                foreach (var format in parsedFormats.DistinctBy(m => m.Essence))
                {
                    XmlBody? xmlBody = null;
                    var reason = isMvc ? XmlOutput(r, format.Essence, bodyType!, xml, out xmlBody) : null;
                    if (xmlBody is not null)
                    {
                        xmlCases.Add((format, xmlBody));
                    }
                    else
                    {
                        opaqueFormats.Add(format);
                        xmlReason ??= reason;
                    }
                }

                foreach (var (format, xmlBody) in xmlCases)
                {
                    var xmlId = xmlCases.Count == 1 && opaqueFormats.Count == 0 ? caseId : "xml." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(caseId + "\n" + format.Essence)));
                    // XmlSerializer writes a null root as xsi:nil, when HttpNoContentOutputFormatter does not answer it with 204 first
                    var nullableRoot = returnsValue && returnedValueMayBeNull && !nullIsNoContent && xmlBody.RootNillable;
                    responses.Add(new Response
                    {
                        Id = xmlId,
                        Status = status,
                        Body = new XmlResponseBody { MediaType = format.Essence, Root = xmlBody.Root, Use = xmlBody.Use with { SemanticNullable = nullableRoot } },
                        ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Xml),
                        Hydration = Hydration.ServerOnly,
                        ExposedHeaders = [],
                    });
                    documentedResponses.Add((xmlId, status, r.Description, returnsValue));
                }

                if (xmlCases.Count > 0 && nullIsNoContent)
                {
                    responses.Add(new Response
                    {
                        Id = opId + ".no-content",
                        Status = 204,
                        Body = new NoResponseBody(),
                        ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Xml),
                        // no body: hydrated in the browser like the 204 of a JSON operation
                        Hydration = Hydration.BrowserSafe,
                        ExposedHeaders = [],
                    });
                }

                if (opaqueFormats.Count == 0)
                {
                    continue;
                }

                bag.Warning(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} writes the {FriendlyName(bodyType!)} body as {string.Join(", ", opaqueFormats.Select(m => m.Essence))}{(xmlReason is null ? "" : $" ({xmlReason})")}; the contract does not describe that representation, so the client receives its bytes", [opId],
                    xmlReason is null ? "declare a JSON media type (Produces<T>() defaults to application/json) to have the client decode the value" : "declare a JSON media type, or keep the type to what XmlSerializer writes without xsi:type, choices or xs:any, to have the client decode the value");
                foreach (var format in opaqueFormats)
                {
                    var opaqueId = "binary." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(caseId + "\n" + format.Essence)));
                    responses.Add(new Response
                    {
                        Id = opaqueId,
                        Status = status,
                        Body = new BinaryResponseBody { MediaType = format.Essence },
                        ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.Binary),
                        Hydration = Hydration.ServerOnly,
                        ExposedHeaders = [],
                    });
                    documentedResponses.Add((opaqueId, status, r.Description, false));
                }
                continue;
            }

            if (formats.Count > 0 && jsonFormats.Count == 0 && !mvcProblem)
            {
                // Produces<byte[]>(200, "application/octet-stream"), [Produces("application/xml")], a text type for a non-string body: the
                // server writes no JSON, and the client would never select a JSON case for the declared media type
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} declares {string.Join(", ", formats)} for a {FriendlyName(bodyType!)} body; only JSON ({string.Join(", ", HttpRules.JsonMediaEssences.Order(StringComparer.Ordinal))}) and text bodies of a string are supported — file transfer, XML and other media types are not", [opId],
                    "declare a JSON media type (Produces<T>() defaults to application/json), or serve the file outside the Tisilia operations");
                continue;
            }

            // MVC writes a value passed to Ok(value), Created…(value), StatusCode(status, value) … with the value's own type, whatever the
            // action declares (ObjectResult has no DeclaredType: ObjectResultExecutor, aspnetcore v10.0.0): a derived value of a polymorphic
            // type goes out without the discriminator its union requires, where returning the value itself from ActionResult<T> keeps it.
            // The source shows which statuses such helpers write.
            if (isMvc && PolymorphicJson(responseProfile(status).Options, bodyType!)
                && (helperWrites ??= ResponseInference.HelperWrites(opId, handler) ?? []).FirstOrDefault(w => w.Status is null || w.Status == status) is { } write)
            {
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", rPath, $"operation '{opId}': status {status} is written by {write.Helper}(value), which MVC writes with the value's own type, so a value of a type derived from {FriendlyName(bodyType!)} has no discriminator", [opId],
                    "return the value itself from an ActionResult<T> action (return value;), which MVC writes with the declared type");
                continue;
            }

            var mapped = nullIsNoContent ? Nullable.GetUnderlyingType(bodyType!) ?? bodyType!
                : returnsValue && returnedValueMayBeNull && bodyType!.IsValueType && Nullable.GetUnderlyingType(bodyType) is null ? typeof(Nullable<>).MakeGenericType(bodyType) : bodyType!;
            var use = responseMapper(status).Map(mapped, WireDirection.ServerWrite, rPath, isRoot: true, nullableRoot: returnsValue && returnedValueMayBeNull && !nullIsNoContent);
            if (use is null)
            {
                continue;
            }

            // Only declared formats are trusted. Observed on ASP.NET Core 10: TypedResults.NotFound(problemDetails) writes
            // application/json, not application/problem+json — in minimal APIs the CLR type never decides the media type.
            var media = mvcProblem ? "application/problem+json" : jsonFormats.FirstOrDefault()?.Essence ?? "application/json";
            var adapterKind = isMvc ? ResultAdapterKind.MvcObject : (inferred || description.ActionDescriptor.EndpointMetadata.OfType<IProducesResponseTypeMetadata>().Any() ? ResultAdapterKind.MinimalResult : ResultAdapterKind.MinimalJson);
            responses.Add(new Response
            {
                Id = caseId,
                Status = status,
                Body = new JsonResponseBody { MediaType = media, ProfileId = responseProfile(status).Profile.Id, Use = use },
                ResultAdapterId = builder.StandardResultAdapter(adapterKind, [responseProfile(status).Profile.Id]),
                Hydration = status < 400 ? Hydration.BrowserSafe : Hydration.ServerOnly,
                ExposedHeaders = [],
            });
            documentedResponses.Add((caseId, status, r.Description, returnsValue));

            // MVC HttpNoContentOutputFormatter: a null result becomes 204 (observed) — a returned value only when its annotation
            // lets it be null, an untyped one (IActionResult with [ProducesResponseType(typeof(T), 200)]) whenever it is a reference
            if (nullIsNoContent)
            {
                responses.Add(new Response
                {
                    Id = opId + ".no-content",
                    Status = 204,
                    Body = new NoResponseBody(),
                    ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.MvcObject, [responseProfile(status).Profile.Id]),
                    Hydration = Hydration.BrowserSafe,
                    ExposedHeaders = [],
                });
            }
        }

        var pipeline = new List<string>();
        if (problemDetails)
        {
            pipeline.Add("tisilia.pipeline.problem-details@0.1");
        }

        var authorize = endpointMetadata.OfType<IAuthorizeData>().Any();
        var anonymous = endpointMetadata.OfType<IAllowAnonymous>().Any();
        if (authorize && !anonymous)
        {
            pipeline.Add("tisilia.pipeline.authentication@0.1");
            pipeline.Add("tisilia.pipeline.authorization@0.1");
        }

        if (isMvc && endpointMetadata.OfType<ApiControllerAttribute>().Any())
        {
            pipeline.Add("tisilia.pipeline.api-controller-validation@0.1");
        }

        if (endpointMetadata.Any(m => m.GetType().FullName == "Microsoft.AspNetCore.Http.Validation.ValidationEndpointFilterMetadata" || m.GetType().Name.Contains("Validation", StringComparison.Ordinal) && m.GetType().Namespace?.StartsWith("Microsoft.AspNetCore.Http.Validation", StringComparison.Ordinal) == true))
        {
            pipeline.Add("tisilia.pipeline.minimal-validation@0.1");
        }

        documentation.Operation(opId, endpointMetadata, handler, bodyParameter);
        foreach (var (id, parameter, valueType) in documentedParameters)
        {
            documentation.Parameter(id, parameter, valueType);
        }

        foreach (var (id, status, text, returned) in documentedResponses)
        {
            documentation.Response(id, status, text, handler, returned);
        }

        if (endpointMetadata.OfType<TisiliaEventResumeAttribute>().Any() && !responses.Any(r => r.Body is SseResponseBody))
        {
            bag.Error(TisiliaCodes.MediaTypeInvalid, "SV29", opPath + "/responses", $"operation '{opId}' declares that its server-sent events resume from Last-Event-ID, but it writes no server-sent events", [opId],
                "remove TisiliaEventResume, or return TypedResults.ServerSentEvents(...) (SseItem<T> events with ids)");
        }

        RoutePlan routePlan;
        try { routePlan = ResolvedRoutes.Build(endpoint.RoutePattern, parameters, policyFactory); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath + "/routePlan", $"operation '{opId}': {e.Message}", [opId]);
            return null;
        }
        return new Operation
        {
            Id = opId,
            Method = method,
            Route = RoutePlans.Display(routePlan),
            RoutePlan = routePlan,
            Tags = metadata.Tags ?? [],
            Parameters = parameters,
            RequestBody = requestBody,
            Responses = responses,
            PipelineBindingIds = pipeline,
            Security = new Security
            {
                AuthPolicyId = authorize && !anonymous ? Builtins.AuthAuthenticated : Builtins.AuthAnonymous,
                RequestExecution = RequestExecution.BrowserAllowed,
                Redaction = new Redaction { Default = "mask", Rules = [] },
                RequestHeaderAllowlist = [],
                CsrfPolicyId = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true ? Builtins.CsrfAntiforgery : Builtins.CsrfNone,
            },
        };
    }

    private static string ParameterId(string operationId, string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '@')
        ? operationId + "." + name
        : operationId + ".parameter-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    private static string CaseSuffix(int status) => status switch
    {
        200 => "ok",
        201 => "created",
        202 => "accepted",
        204 => "no-content",
        400 => "bad-request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not-found",
        409 => "conflict",
        422 => "unprocessable",
        500 => "server-error",
        _ => "status-" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static bool IsNullableType(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// The XML body XmlSerializerInputFormatter reads in <paramref name="essence"/> (the first input formatter of that media type is the one
    /// MVC reads the body with), or the reason it is not described. Only the formatter itself, with its own wrapper providers, reads with
    /// <c>new XmlSerializer(type)</c>: a derived formatter can read otherwise.
    /// </summary>
    private static string? XmlInput(IList<ApiRequestFormat> formats, string essence, Type type, XmlTypeMapper xml, out XmlBody? body, out int maxDepth)
    {
        body = null;
        maxDepth = 0;
        var formatter = formats.FirstOrDefault(f => HttpRules.ParseMediaType(f.MediaType)?.Essence == essence)?.Formatter;
        if (formatter is null || !HttpRules.IsXmlMediaType(essence))
        {
            return null;
        }

        if (formatter.GetType() != typeof(Microsoft.AspNetCore.Mvc.Formatters.XmlSerializerInputFormatter))
        {
            return $"{formatter.GetType().Name} reads it, not XmlSerializerInputFormatter";
        }

        var input = (Microsoft.AspNetCore.Mvc.Formatters.XmlSerializerInputFormatter)formatter;
        if (!DefaultXmlWrappers(input.WrapperProviderFactories, output: false) || XmlWrapped(type))
        {
            return $"MVC reads {FriendlyName(type)} through a wrapper type";
        }

        body = xml.Map(type, WireDirection.ServerRead, out var reason);
        maxDepth = input.MaxDepth;
        return reason;
    }

    /// <summary>The XML body XmlSerializerOutputFormatter writes in <paramref name="essence"/>, or the reason it is not described.</summary>
    private static string? XmlOutput(ApiResponseType response, string essence, Type type, XmlTypeMapper xml, out XmlBody? body)
    {
        body = null;
        var formatter = response.ApiResponseFormats.FirstOrDefault(f => HttpRules.ParseMediaType(f.MediaType)?.Essence == essence)?.Formatter;
        if (formatter is null || !HttpRules.IsXmlMediaType(essence))
        {
            return null;
        }

        if (formatter.GetType() != typeof(Microsoft.AspNetCore.Mvc.Formatters.XmlSerializerOutputFormatter))
        {
            return $"{formatter.GetType().Name} writes it, not XmlSerializerOutputFormatter";
        }

        var output = (Microsoft.AspNetCore.Mvc.Formatters.XmlSerializerOutputFormatter)formatter;
        if (!DefaultXmlWrappers(output.WrapperProviderFactories, output: true) || XmlWrapped(type))
        {
            return $"MVC writes {FriendlyName(type)} through a wrapper type";
        }

        // an interface collection is written through DelegatingEnumerable<T, T>, whose items are T (EnumerableWrapperProviderFactory)
        var serialized = type;
        if (type.IsInterface && type.IsGenericType && output.WrapperProviderFactories.Any(f => f is Microsoft.AspNetCore.Mvc.Formatters.Xml.EnumerableWrapperProviderFactory)
            && (type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))) is { } enumerable)
        {
            var element = enumerable.GenericTypeArguments[0];
            if (XmlWrapped(element))
            {
                return $"MVC writes the items of {FriendlyName(type)} through a wrapper type";
            }

            serialized = typeof(Microsoft.AspNetCore.Mvc.Formatters.Xml.DelegatingEnumerable<,>).MakeGenericType(element, element);
        }

        body = xml.Map(serialized, WireDirection.ServerWrite, out var reason);
        return reason;
    }

    /// <summary>SerializableError, ProblemDetails and ValidationProblemDetails are read and written through IXmlSerializable wrappers.</summary>
    private static bool XmlWrapped(Type type) => type == typeof(SerializableError) || type == typeof(ProblemDetails) || type == typeof(ValidationProblemDetails);

    /// <summary>The wrapper providers AddXmlSerializerFormatters gives the formatters (and the formatter's constructor its own): no other.</summary>
    private static bool DefaultXmlWrappers(IList<Microsoft.AspNetCore.Mvc.Formatters.Xml.IWrapperProviderFactory> factories, bool output)
        => factories.Select(f => f.GetType()).Distinct().Count() == factories.Count && factories.All(f => f.GetType() == typeof(Microsoft.AspNetCore.Mvc.Formatters.Xml.SerializableErrorWrapperProviderFactory)
            || f.GetType().FullName == "Microsoft.AspNetCore.Mvc.Formatters.Xml.ProblemDetailsWrapperProviderFactory"
            || (output && f.GetType() == typeof(Microsoft.AspNetCore.Mvc.Formatters.Xml.EnumerableWrapperProviderFactory)));

    /// <summary>
    /// Members of a <c>Results&lt;…&gt;</c> return type that describe no response: UnauthorizedHttpResult implements no
    /// IEndpointMetadataProvider, and ProblemHttpResult's only adds DisableCookieRedirectMetadata (aspnetcore v10.0.0). ApiExplorer
    /// cannot know what they write. They are reported unless the endpoint declares a status that none of the describing members
    /// produces (then the application has declared them, e.g. with ProducesProblem).
    /// </summary>
    private static List<Type> UndescribedResults(ApiDescription description, HashSet<int> declaredStatuses)
    {
        var method = HandlerMethod(description);
        var type = method is null ? null : UnwrapAwaitable(method.ReturnType);
        if (method is null || !IsResultsUnion(type))
        {
            return [];
        }

        var described = new HashSet<int>();
        var undescribed = new List<Type>();
        foreach (var member in type!.GetGenericArguments())
        {
            if (!typeof(IEndpointMetadataProvider).IsAssignableFrom(member))
            {
                undescribed.Add(member);
                continue;
            }

            // the statuses the member describes: what its static PopulateMetadata adds to an endpoint
            var populate = member.GetMethod("PopulateMetadata", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?? member.GetMethod(typeof(IEndpointMetadataProvider).FullName + ".PopulateMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (populate is null)
            {
                return [];
            }

            var endpoint = new Microsoft.AspNetCore.Routing.RouteEndpointBuilder(null, Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/"), 0);
            populate.Invoke(null, [method, endpoint]);
            var statuses = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Select(m => m.StatusCode).ToList();
            if (statuses.Count == 0)
            {
                undescribed.Add(member);
            }

            described.UnionWith(statuses);
        }

        return declaredStatuses.All(described.Contains) ? undescribed : [];
    }

    /// <summary>
    /// Content types the metadata declares for <paramref name="status"/> on a response without a type: <c>.Produces(200, contentType: …)</c>
    /// (IProducesResponseTypeMetadata) and <c>[ProducesResponseType(typeof(void), 200, "…")]</c>. ApiExplorer drops them from the description.
    /// </summary>
    private static List<string> DeclaredVoidContentTypes(IList<object> metadata, int status)
    {
        var types = new List<string>();
        foreach (var produces in metadata.OfType<IProducesResponseTypeMetadata>().Where(m => m.StatusCode == status && (m.Type is null || m.Type == typeof(void))))
        {
            types.AddRange(produces.ContentTypes);
        }

        foreach (var attribute in metadata.OfType<ProducesResponseTypeAttribute>().Where(a => a.StatusCode == status && a.Type == typeof(void)))
        {
            var collection = new Microsoft.AspNetCore.Mvc.Formatters.MediaTypeCollection();
            ((IApiResponseMetadataProvider)attribute).SetContentTypes(collection);
            types.AddRange(collection);
        }

        return types;
    }

    /// <summary>
    /// The contract hashes the application's module artifacts byte for byte, so bytes that depend on the commit or the checkout change
    /// the semanticHash without a code change — committed contracts and generated clients go stale and evidence lapses. The causes are
    /// reported once per file: the .NET SDK puts the git commit into an assembly's InformationalVersion (SourceRevisionId) and into the
    /// Source Link map of its PDB, whose content the assembly's PDB id and checksum hash (.NET 8 and later), and Git for Windows checks
    /// a script out with CRLF unless .gitattributes pins it (core.autocrlf=true by default).
    /// Tisilia's own module (tisilia-additional) is versioned by its package and installed with its own .gitattributes.
    /// </summary>
    private void ReportUnstableArtifacts(JsonObject root, DiagnosticBag bag)
    {
        var contentRoot = services.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
        var artifacts = (root["modules"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(m => m["id"]?.GetValue<string>() != Generator.Additional.AdditionalModule.ModuleId)
            .SelectMany(m => (m["artifacts"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(a => (Module: m["id"]!.GetValue<string>(), Dotnet: a["target"]?.GetValue<string>() == "dotnet", Path: a["path"]!.GetValue<string>())))
            .GroupBy(a => (a.Dotnet, a.Path));
        foreach (var artifact in artifacts.OrderBy(g => g.Key.Path, StringComparer.Ordinal))
        {
            var file = Path.Combine(contentRoot, artifact.Key.Path);
            if (!File.Exists(file))
            {
                continue;
            }

            var modules = artifact.Select(a => a.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (!artifact.Key.Dotnet)
            {
                if (File.ReadAllBytes(file).AsSpan().IndexOf("\r\n"u8) >= 0)
                {
                    bag.Warning(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"{artifact.Key.Path} (modules {string.Join(", ", modules)}) has CRLF line endings: when git converted them on checkout (core.autocrlf=true, the Git for Windows default), its digest and the contract's semanticHash differ from a checkout elsewhere, where generate then rejects the module", modules,
                        $"put a .gitattributes with `{Path.GetFileName(file)} -text` in {Path.GetDirectoryName(Path.GetFullPath(file))}, delete the file, restore it with `git checkout` and export again");
                }

                continue;
            }

            var informationalVersion = Generator.Canonical.SourceRevision.InformationalVersion(file);
            var sourceLinkCommit = Generator.Canonical.SourceRevision.SourceLinkCommit(file);
            if (informationalVersion is null && sourceLinkCommit is null)
            {
                continue;
            }

            var where = new[] { informationalVersion is null ? null : $"InformationalVersion {informationalVersion}", sourceLinkCommit is null ? null : $"Source Link of its PDB, commit {sourceLinkCommit}" }.OfType<string>();
            var settings = new[] { informationalVersion is null ? null : "<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", sourceLinkCommit is null ? null : "<EnableSourceLink>false</EnableSourceLink>" }.OfType<string>();
            bag.Warning(TisiliaCodes.ModuleArtifact, "SV44", "/modules", $"{artifact.Key.Path} (modules {string.Join(", ", modules)}) embeds the source revision ({string.Join("; ", where)}): its digest and the contract's semanticHash change with every commit, so a committed contract and its generated client go stale without a code change", modules,
                $"set {string.Join(" and ", settings)} in the project that builds it (a released package keeps them: its assembly never changes)");
        }
    }

    /// <summary>
    /// Whether MVC writes a null object result as 204: the first output formatter that accepts a null value is HttpNoContentOutputFormatter
    /// with TreatNullValueAsNoContent (its default, first in MvcCoreMvcOptionsSetup) rather than the JSON formatter (aspnetcore v10.0.0).
    /// </summary>
    private static bool NullResultIsNoContent(MvcOptions? mvc)
    {
        foreach (var formatter in mvc?.OutputFormatters ?? [])
        {
            if (formatter is Microsoft.AspNetCore.Mvc.Formatters.HttpNoContentOutputFormatter { TreatNullValueAsNoContent: true })
            {
                return true;
            }

            if (formatter is Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonOutputFormatter)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The value type a handler returns: T of T, Task&lt;T&gt;, ValueTask&lt;T&gt; and ActionResult&lt;T&gt;; null for results (IResult, IActionResult) and void.</summary>
    private static Type? ReturnedValueType(Type result)
    {
        if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            result = result.GetGenericArguments()[0];
        }

        return result == typeof(void) || typeof(IResult).IsAssignableFrom(result) || typeof(IActionResult).IsAssignableFrom(result) || typeof(Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult).IsAssignableFrom(result) ? null : result;
    }

    /// <summary>
    /// Whether a handler's returned value may be null: a Nullable&lt;T&gt;, or a reference whose nullable annotation is nullable or oblivious
    /// (#nullable disable) — the awaited value of Task&lt;T&gt;/ValueTask&lt;T&gt;, the value of ActionResult&lt;T&gt;. A lambda whose return type
    /// is inferred carries no annotation at all (the C# compiler emits none, observed with the .NET 10 SDK): its value counts as not null,
    /// and a lambda that returns null declares it (<c>Item? (…) =&gt; …</c>).
    /// </summary>
    private static bool ReturnValueMayBeNull(System.Reflection.MethodInfo handler)
    {
        System.Reflection.NullabilityInfo info;
        try
        {
            info = new System.Reflection.NullabilityInfoContext().Create(handler.ReturnParameter);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return true;
        }

        while (info.Type.IsGenericType && info.GenericTypeArguments.Length == 1
            && (info.Type.GetGenericTypeDefinition() == typeof(Task<>) || info.Type.GetGenericTypeDefinition() == typeof(ValueTask<>) || info.Type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
        {
            info = info.GenericTypeArguments[0];
        }

        if (info.Type.IsValueType)
        {
            return Nullable.GetUnderlyingType(info.Type) is not null;
        }

        return info.ReadState == System.Reflection.NullabilityState.Nullable
            || (info.ReadState == System.Reflection.NullabilityState.Unknown && !handler.Name.Contains(">b__", StringComparison.Ordinal));
    }

    /// <summary>The action method of a controller, or the handler a minimal API endpoint was created from (RequestDelegateFactory adds it to the metadata).</summary>
    private static System.Reflection.MethodInfo? HandlerMethod(ApiDescription description) =>
        description.ActionDescriptor is ControllerActionDescriptor controller ? controller.MethodInfo : description.ActionDescriptor.EndpointMetadata.OfType<System.Reflection.MethodInfo>().FirstOrDefault();

    /// <summary>The result type a handler produces: <c>void</c> for void, Task and ValueTask; T for Task&lt;T&gt; and ValueTask&lt;T&gt;.</summary>
    private static Type UnwrapAwaitable(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask))
        {
            return typeof(void);
        }

        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0] : type;
    }

    private static bool IsResultsUnion(Type? type) => type is { IsGenericType: true } && type.GetGenericTypeDefinition().FullName?.StartsWith("Microsoft.AspNetCore.Http.HttpResults.Results`", StringComparison.Ordinal) == true;

    /// <summary>Whether System.Text.Json writes the type through a polymorphic contract, with a discriminator for its derived types.</summary>
    private static bool PolymorphicJson(JsonSerializerOptions options, Type type)
    {
        try
        {
            return options.GetTypeInfo(type).PolymorphismOptions is not null;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // a type System.Text.Json cannot describe: the mapper reports it
            return false;
        }
    }

    /// <summary>JsonHttpResult&lt;T&gt; (TypedResults.Json) and MVC's JsonResult in a handler's result type, directly or as a member of Results&lt;…&gt;.</summary>
    private static List<Type> OpaqueJsonResults(Type? result)
    {
        if (result is null)
        {
            return [];
        }

        var candidates = IsResultsUnion(result) ? result.GetGenericArguments() : [result];
        return candidates.Where(t => (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<>)) || typeof(JsonResult).IsAssignableFrom(t)).ToList();
    }

    /// <summary>
    /// Why a declared response type is not a data body: a stream, a pipe, or a result object ([ProducesResponseType(typeof(FileContentResult))]
    /// names what the action returns, not what it writes). Null for data types.
    /// </summary>
    private static string? NonDataBodyType(Type type)
    {
        if (typeof(Stream).IsAssignableFrom(type) || type == typeof(System.IO.Pipelines.PipeReader))
        {
            return "a stream";
        }

        if (typeof(IActionResult).IsAssignableFrom(type) || typeof(IResult).IsAssignableFrom(type))
        {
            return "a result object, not the data it writes";
        }

        return null;
    }

    /// <summary>
    /// The CLR type a parameter binds to. MVC's ApiExplorer reports string for a parseable or convertible type other than a primitive,
    /// an enum, decimal, the date and time types, Guid and Uri — Int128, Version, IPAddress, a type with its own TryParse
    /// (DefaultApiDescriptionProvider.GetModelType → EndpointModelMetadata.GetDisplayType, aspnetcore v10.0.0) — and keeps the type in
    /// the model metadata. A minimal API reports the parameter's own type, and the display type in its model metadata.
    /// </summary>
    private static Type ParameterClrType(ApiParameterDescription p, bool mvc) => mvc && p.ModelMetadata is { } metadata ? metadata.ModelType : p.Type;

    /// <summary>
    /// Whether the server reads a value of this type from one text with the type's own parser: a minimal API when the type has a TryParse
    /// (IParsable&lt;T&gt;, TryParse(string, IFormatProvider, out T) or TryParse(string, out T), as ParameterBindingMethodCache finds them),
    /// MVC when its model metadata is not complex (a TypeConverter from string or a TryParse; aspnetcore v10.0.0).
    /// </summary>
    private static bool ReadsOwnText(Type type, bool mvc, Microsoft.AspNetCore.Mvc.ModelBinding.ModelMetadata? metadata) => mvc
        ? metadata is { IsComplexType: false }
        : type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>) && i.GetGenericArguments()[0] == type)
            || new[] { new[] { typeof(string), typeof(IFormatProvider), type.MakeByRefType() }, new[] { typeof(string), type.MakeByRefType() } }
                .Any(signature => type.GetMethod("TryParse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy, signature)?.ReturnType == typeof(bool));

    /// <summary>C#-like type names for diagnostics (<c>Results&lt;Ok&lt;Item&gt;, NotFound&gt;</c> instead of <c>Results`2</c>).</summary>
    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return (tick < 0 ? name : name[..tick]) + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
    }

    private static (Type ElementType, bool Repeated) UnwrapCollection(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return (type, false);
        }

        if (type.IsArray)
        {
            return (type.GetElementType()!, true);
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(List<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(ICollection<>))
            {
                return (type.GetGenericArguments()[0], true);
            }
        }

        return (type, false);
    }

    private static Contract.JsonValue? ToJsonValue(object value)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(value, TisiliaJson.Plain);
            return node is null ? new JsonNullValue() : FromNode(node);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static Contract.JsonValue FromNode(JsonNode node) => node switch
    {
        JsonObject o => new JsonObjectValue { Entries = o.Select(kv => new JsonObjectEntry { Name = kv.Key, Value = kv.Value is null ? new JsonNullValue() : FromNode(kv.Value) }).ToList() },
        JsonArray a => new JsonArrayValue { Items = a.Select(i => i is null ? new JsonNullValue() : FromNode(i)).ToList() },
        System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.String => new JsonStringValue { Value = v.GetValue<string>() },
        System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.Number => new JsonNumberValue { Text = v.ToJsonString() },
        System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.True => new JsonBooleanValue { Value = true },
        System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.False => new JsonBooleanValue { Value = false },
        _ => new JsonNullValue(),
    };
}
