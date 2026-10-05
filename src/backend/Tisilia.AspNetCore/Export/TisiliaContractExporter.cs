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
        var provider = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        var minimalJson = services.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var mvcJson = services.GetService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()?.Value.JsonSerializerOptions;
        var profiles = new Dictionary<JsonSerializerOptions, ProfileContext>(ReferenceEqualityComparer.Instance);
        var mappers = new Dictionary<string, ClrTypeMapper>(StringComparer.Ordinal);
        var adapters = new Conformance.RunnerAdapterTable();
        var documentation = new ApiDocumentation();
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
            mappers[ctx.Profile.Id] = new ClrTypeMapper(builder, ctx, opts, bag, adapters);
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
            var operation = BuildOperation(description, metadata, isMvc, Profile, Mapper, opts.DateTimes.Default, matches[0], services.GetRequiredService<ParameterPolicyFactory>(), builder, bag, opPath, problemDetails, mapClientErrors, nullAsNoContent, documentation);
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

    private static Operation? BuildOperation(ApiDescription description, TisiliaOperationAttribute metadata, bool isMvc, Func<ProfileContext> profile, Func<ClrTypeMapper> mapper, Bindings.DateTimeWire? dateTimeWire, RouteEndpoint endpoint, ParameterPolicyFactory policyFactory, ContractBuilder builder, DiagnosticBag bag, string opPath, bool problemDetails, bool mapClientErrors, bool nullAsNoContent, ApiDocumentation documentation)
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
        RequestBody requestBody = new NoRequestBody();
        var np = NumberProfile.Strict;
        var apiController = isMvc && description.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any();
        var pIndex = 0;
        foreach (var p in description.ParameterDescriptions)
        {
            var pPath = opPath + "/parameters/" + pIndex;
            var source = p.Source;
            if (source == BindingSource.Body)
            {
                if (requestBody is not NoRequestBody)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': more than one body parameter", [opId]);
                    continue;
                }

                var use = mapper().Map(p.Type, WireDirection.ServerRead, pPath, isRoot: true);
                if (use is null)
                {
                    continue;
                }

                var mediaType = description.SupportedRequestFormats.Select(f => f.MediaType).FirstOrDefault(m => HttpRules.ParseMediaType(m) is { } mt && HttpRules.JsonMediaEssences.Contains(mt.Essence)) ?? "application/json";
                requestBody = new JsonRequestBody
                {
                    MediaType = HttpRules.ParseMediaType(mediaType)!.Essence,
                    ProfileId = profile().Profile.Id,
                    Use = use,
                    Presence = p.IsRequired || !IsNullableType(p.Type) ? Presence.Required : Presence.Optional,
                };
                bodyParameter = (p.ParameterDescriptor as Microsoft.AspNetCore.Mvc.Infrastructure.IParameterInfoParameterDescriptor)?.ParameterInfo;
                continue;
            }

            if (source == BindingSource.Services || source == BindingSource.Special || source == BindingSource.FormFile || source == BindingSource.Form)
            {
                if (source == BindingSource.FormFile || source == BindingSource.Form)
                {
                    bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, $"operation '{opId}': form/multipart parameter '{p.Name}' is not supported (only JSON bodies and route, query and header parameters are)", [opId]);
                }

                continue; // DI and HttpContext parameters are not part of the HTTP contract
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
            var (elementType, repeated) = UnwrapCollection(p.Type);
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
            { Bindings.DateTimeWire.Utc => "datetime-utc", Bindings.DateTimeWire.Unspecified => "datetime-unspecified", Bindings.DateTimeWire.Local => "datetime-local-wire", _ => null } : ClrTypeMapper.ScalarNameOf(scalarType);
            if (scalarType == typeof(DateTime) && scalar is null or "datetime-local-wire")
            {
                // minimal APIs and MVC parse DateTime parameters with DateTimeStyles.AdjustToUniversal (aspnetcore v10.0.0
                // ParameterBindingMethodCache, DateTimeModelBinderProvider): "Z" stays Utc and a zone-less value stays Unspecified,
                // but an offset is converted to UTC — a Local wire cannot travel in a route, query or header value
                bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", pPath, scalar is null
                    ? $"operation '{opId}': DateTime parameter '{p.Name}' has no declared wire; declare TisiliaOptions.DateTimes.Default (Utc or Unspecified)"
                    : $"operation '{opId}': DateTime parameter '{p.Name}' is declared Local, but ASP.NET Core binds DateTime parameters with DateTimeStyles.AdjustToUniversal (an offset becomes UTC); use Utc or Unspecified, or DateTimeOffset", [opId]);
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
        var handlerResult = handler is null ? null : UnwrapAwaitable(handler.ReturnType);
        var declaresResponses = endpointMetadata.OfType<IProducesResponseTypeMetadata>().Any() || endpointMetadata.OfType<IApiResponseMetadataProvider>().Any();
        // EndpointMetadataApiDescriptionProvider treats an IResult return type without response metadata as void and still lists a
        // 200 response (aspnetcore v10.0.0): the result decides at run time what it writes, so that 200 describes nothing
        if (responseTypes.Count == 0 || (!isMvc && handlerResult is not null && typeof(IResult).IsAssignableFrom(handlerResult) && !declaresResponses))
        {
            bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV34", opPath + "/responses", $"operation '{opId}' declares no response types{(handlerResult is null ? "" : $" (it returns {FriendlyName(handlerResult)})")}; IActionResult/IResult without produces metadata cannot be exported", [opId],
                "return TypedResults (Ok<T>, Created<T>, Results<…>) or ActionResult<T>, or add [ProducesResponseType]/Produces<T>() metadata");
            return null;
        }

        // TypedResults.Json and MVC's JsonResult serialize with the JsonSerializerOptions handed to them at run time, which metadata cannot
        // show (explicit options are checked per result); the contract would describe the endpoint's options instead
        if (OpaqueJsonResults(handlerResult) is { Count: > 0 } opaque)
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
            if (isVoid && status is >= 200 and < 300 && !HttpRules.IsBodylessStatus(status) && method != HttpMethodKind.HEAD
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
                // TypedResults.ServerSentEvents describes 200 text/event-stream SseItem<T> (aspnetcore v10.0.0 ServerSentEventsResult)
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} is a server-sent event stream (text/event-stream); server-sent events are not supported", [opId],
                    "serve the stream outside the Tisilia operations (it is not exported), or poll a JSON operation");
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
            var textFormats = parsedFormats.Where(m => m.Type == "text" && !HttpRules.JsonMediaEssences.Contains(m.Essence)).ToList();
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
            var mvcProblem = isMvc && typeof(ProblemDetails).IsAssignableFrom(bodyType);
            if (formats.Count > 0 && jsonFormats.Count == 0 && !mvcProblem)
            {
                // Produces<byte[]>(200, "application/octet-stream"), [Produces("application/xml")], a text type for a non-string body: the
                // server writes no JSON, and the client would never select a JSON case for the declared media type
                bag.Error(TisiliaCodes.PipelineOrResultClosure, "SV29", rPath, $"operation '{opId}': status {status} declares {string.Join(", ", formats)} for a {FriendlyName(bodyType!)} body; only JSON ({string.Join(", ", HttpRules.JsonMediaEssences.Order(StringComparer.Ordinal))}) and text bodies of a string are supported — file transfer, XML and other media types are not", [opId],
                    "declare a JSON media type (Produces<T>() defaults to application/json), or serve the file outside the Tisilia operations");
                continue;
            }

            // a null return value: minimal APIs write the JSON null (RequestDelegateFactory → WriteAsJsonAsync), MVC writes 204 while
            // HttpNoContentOutputFormatter (TreatNullValueAsNoContent) comes before the JSON formatter, else the JSON null (aspnetcore v10.0.0)
            var returnsValue = status == 200 && returnedValue is not null && (Nullable.GetUnderlyingType(returnedValue) ?? returnedValue) == (Nullable.GetUnderlyingType(bodyType!) ?? bodyType);
            var nullIsNoContent = isMvc && nullAsNoContent && !responseTypes.Any(x => x.StatusCode == 204) && (returnsValue ? returnedValueMayBeNull : status == 200 && !bodyType!.IsValueType);
            var mapped = nullIsNoContent ? Nullable.GetUnderlyingType(bodyType!) ?? bodyType!
                : returnsValue && returnedValueMayBeNull && bodyType!.IsValueType && Nullable.GetUnderlyingType(bodyType) is null ? typeof(Nullable<>).MakeGenericType(bodyType) : bodyType!;
            var use = mapper().Map(mapped, WireDirection.ServerWrite, rPath, isRoot: true, nullableRoot: returnsValue && returnedValueMayBeNull && !nullIsNoContent);
            if (use is null)
            {
                continue;
            }

            // Only declared formats are trusted. Observed on ASP.NET Core 10: TypedResults.NotFound(problemDetails) writes
            // application/json, not application/problem+json — in minimal APIs the CLR type never decides the media type.
            var media = mvcProblem ? "application/problem+json" : jsonFormats.FirstOrDefault()?.Essence ?? "application/json";
            var adapterKind = isMvc ? ResultAdapterKind.MvcObject : (description.ActionDescriptor.EndpointMetadata.OfType<IProducesResponseTypeMetadata>().Any() ? ResultAdapterKind.MinimalResult : ResultAdapterKind.MinimalJson);
            responses.Add(new Response
            {
                Id = caseId,
                Status = status,
                Body = new JsonResponseBody { MediaType = media, ProfileId = profile().Profile.Id, Use = use },
                ResultAdapterId = builder.StandardResultAdapter(adapterKind, [profile().Profile.Id]),
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
                    ResultAdapterId = builder.StandardResultAdapter(ResultAdapterKind.MvcObject, [profile().Profile.Id]),
                    Hydration = Hydration.BrowserSafe,
                    ExposedHeaders = [],
                });
            }
        }

        var pipeline = new List<string>();
        if (problemDetails)
        {
            pipeline.Add("tisilia.pipeline.problem-details@0.3");
        }

        var authorize = endpointMetadata.OfType<IAuthorizeData>().Any();
        var anonymous = endpointMetadata.OfType<IAllowAnonymous>().Any();
        if (authorize && !anonymous)
        {
            pipeline.Add("tisilia.pipeline.authentication@0.3");
            pipeline.Add("tisilia.pipeline.authorization@0.3");
        }

        if (isMvc && endpointMetadata.OfType<ApiControllerAttribute>().Any())
        {
            pipeline.Add("tisilia.pipeline.api-controller-validation@0.3");
        }

        if (endpointMetadata.Any(m => m.GetType().FullName == "Microsoft.AspNetCore.Http.Validation.ValidationEndpointFilterMetadata" || m.GetType().Name.Contains("Validation", StringComparison.Ordinal) && m.GetType().Namespace?.StartsWith("Microsoft.AspNetCore.Http.Validation", StringComparison.Ordinal) == true))
        {
            pipeline.Add("tisilia.pipeline.minimal-validation@0.3");
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
                CsrfPolicyId = endpointMetadata.OfType<IAntiforgeryMetadata>().Any(m => m.RequiresValidation) ? Builtins.CsrfAntiforgery : Builtins.CsrfNone,
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
