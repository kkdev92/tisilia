using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

public sealed partial class SemanticValidator
{
    // ---------------------------------------------------------------- binders (SV03/SV30/SV31)

    private void CheckBinders()
    {
        for (var i = 0; i < _doc.Binders.Count; i++)
        {
            var b = _doc.Binders[i];
            var bp = JsonPointer.Append("/binders", i);
            RequireBuiltinOrBinding(b.BindingId, JsonPointer.Append(bp, "bindingId"), "binder binding", [BuiltinKind.Binder], [BindingKind.Binder]);
            RequireType(b.TypeId, JsonPointer.Append(bp, "typeId"));
            RequireImpl(b.Implementation, JsonPointer.Append(bp, "implementation"), "binder", [BuiltinKind.Binder], [ExportRole.Binder], TsTargets);
            RequireBuiltinOrBinding(b.GrammarId, JsonPointer.Append(bp, "grammarId"), "binder grammar", [BuiltinKind.Grammar, BuiltinKind.KeyGrammar], [BindingKind.Grammar]);
            RequireBuiltinOrBinding(b.NormalizationId, JsonPointer.Append(bp, "normalizationId"), "normalization", [BuiltinKind.Normalization], [BindingKind.Normalization]);
            RequireBuiltinOrBinding(b.ServerAcceptanceId, JsonPointer.Append(bp, "serverAcceptanceId"), "server acceptance", [BuiltinKind.ServerAcceptance], [BindingKind.ServerAcceptance]);
            if (b.NullPolicy == NullPolicy.Literal && b.NullLiteral is null)
            {
                Error(TisiliaCodes.BinderNullPolicy, "SV31", JsonPointer.Append(bp, "nullLiteral"), $"binder '{b.Id}': nullPolicy 'literal' requires nullLiteral", [b.Id]);
            }

            if (b.NullPolicy != NullPolicy.Literal && b.NullLiteral is not null)
            {
                Error(TisiliaCodes.BinderNullPolicy, "SV31", JsonPointer.Append(bp, "nullLiteral"), $"binder '{b.Id}': nullLiteral is only allowed with nullPolicy 'literal'", [b.Id]);
            }

            var expectedEncoding = b.Location switch
            {
                ParameterLocation.Path => BinderEncoding.PathSegment,
                ParameterLocation.Query => BinderEncoding.QueryComponent,
                _ => BinderEncoding.HeaderText,
            };
            if (b.Encoding != expectedEncoding)
            {
                Error(TisiliaCodes.BinderNullPolicy, "SV30", JsonPointer.Append(bp, "encoding"), $"binder '{b.Id}': location '{Enum(b.Location)}' requires encoding '{Enum(expectedEncoding)}'", [b.Id]);
            }

            if (b.Location == ParameterLocation.Path && b.Cardinality == Cardinality.Repeated)
            {
                Error(TisiliaCodes.BinderNullPolicy, "SV30", JsonPointer.Append(bp, "cardinality"), $"binder '{b.Id}': a path segment cannot be repeated", [b.Id]);
            }

            if (b.Location == ParameterLocation.Path && b.NullPolicy == NullPolicy.Omit)
            {
                Error(TisiliaCodes.BinderNullPolicy, "SV31", JsonPointer.Append(bp, "nullPolicy"), $"binder '{b.Id}': a path segment cannot be omitted", [b.Id]);
            }

            if (b.NullLiteral is not null && HttpRules.ContainsControlCharacters(b.NullLiteral))
            {
                Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", JsonPointer.Append(bp, "nullLiteral"), $"binder '{b.Id}': nullLiteral contains control characters", [b.Id]);
            }
        }
    }

    // ---------------------------------------------------------------- result adapters (SV03/SV34)

    private void CheckResultAdapters()
    {
        for (var i = 0; i < _doc.ResultAdapters.Count; i++)
        {
            var r = _doc.ResultAdapters[i];
            var rp = JsonPointer.Append("/resultAdapters", i);
            RequireBuiltinOrBinding(r.BindingId, JsonPointer.Append(rp, "bindingId"), "result binding", [BuiltinKind.Result], [BindingKind.Result]);
            RequireImpl(r.Implementation, JsonPointer.Append(rp, "implementation"), "result adapter", [BuiltinKind.Result], [ExportRole.Result], DotnetTargets);
            for (var j = 0; j < r.ProfileIds.Count; j++)
            {
                RequireProfile(r.ProfileIds[j], JsonPointer.Append(JsonPointer.Append(rp, "profileIds"), j));
            }

            for (var j = 0; j < r.BehaviorIds.Count; j++)
            {
                if (!_index.Behaviors.ContainsKey(r.BehaviorIds[j]))
                {
                    Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(JsonPointer.Append(rp, "behaviorIds"), j), $"behavior '{r.BehaviorIds[j]}' is not defined in any profile", [r.Id, r.BehaviorIds[j]]);
                }
            }

            if (r.Kind == ResultAdapterKind.Custom)
            {
                if (r.Implementation is not ModuleImpl || !_index.Bindings.ContainsKey(r.BindingId))
                {
                    Error(TisiliaCodes.PipelineOrResultClosure, "SV34", JsonPointer.Append(rp, "kind"), $"custom result adapter '{r.Id}' requires a module implementation and an explicit result binding", [r.Id]);
                }
            }
            else if (r.Implementation is BuiltinImpl impl)
            {
                var expected = r.Kind switch
                {
                    ResultAdapterKind.MinimalJson => Builtins.ResultMinimalJson,
                    ResultAdapterKind.MinimalResult => Builtins.ResultMinimalResult,
                    ResultAdapterKind.MvcObject => Builtins.ResultMvcObject,
                    ResultAdapterKind.MvcJson => Builtins.ResultMvcJson,
                    ResultAdapterKind.Bodyless => Builtins.ResultBodyless,
                    ResultAdapterKind.Text => Builtins.ResultTextUtf8,
                    ResultAdapterKind.Binary => Builtins.ResultBinaryBuffered,
                    ResultAdapterKind.Sse => Builtins.ResultSse,
                    _ => null,
                };
                if (expected is not null && impl.Id != expected)
                {
                    Error(TisiliaCodes.PipelineOrResultClosure, "SV34", JsonPointer.Append(rp, "implementation"), $"result adapter '{r.Id}' of kind '{Enum(r.Kind)}' must use builtin '{expected}' or a module implementation", [r.Id]);
                }
            }
        }
    }

    // ---------------------------------------------------------------- operations (SV16/SV26–SV36)

    private void CheckOperations()
    {
        for (var i = 0; i < _doc.Operations.Count; i++)
        {
            var op = _doc.Operations[i];
            var opPath = JsonPointer.Append("/operations", i);
            CheckRoute(op, opPath);
            CheckParameters(op, opPath);
            CheckRequestBody(op, opPath);
            CheckResponses(op, opPath);
            CheckSecurity(op, opPath);
            for (var j = 0; j < op.PipelineBindingIds.Count; j++)
            {
                RequireBuiltinOrBinding(op.PipelineBindingIds[j], JsonPointer.Append(JsonPointer.Append(opPath, "pipelineBindingIds"), j), "pipeline", [BuiltinKind.Pipeline], [BindingKind.Pipeline]);
            }
        }
    }

    private void CheckRoute(Operation op, string opPath)
    {
        var rp = JsonPointer.Append(opPath, "route");
        if (op.RoutePlan is null)
        {
            Error(TisiliaCodes.ParameterRouteMismatch, "SV30", rp, "contract 0.1 requires a resolved routePlan; re-export with Tisilia 0.1.0-alpha", [op.Id]);
        }
        else
        {
            foreach (var error in RoutePlans.Errors(op.RoutePlan, op.Parameters))
            {
                Error(TisiliaCodes.ParameterRouteMismatch, "SV30", opPath + "/routePlan", error, [op.Id]);
            }
            if (RoutePlans.Display(op.RoutePlan) != op.Route)
            {
                Error(TisiliaCodes.ParameterRouteMismatch, "SV30", rp, "display route differs from resolved routePlan", [op.Id]);
            }
        }
        // '?', '#' and "://" are looked for in the literal text: a parameter's regex constraint may hold them
        var literal = HttpRules.RouteLiteralText(op.Route);
        if (!op.Route.StartsWith('/') || literal.Contains('?', StringComparison.Ordinal) || literal.Contains('#', StringComparison.Ordinal) || op.Route.Any(c => c <= ' ' || c == 0x7f))
        {
            Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", rp, $"operation '{op.Id}': route must be an absolute path template without query, fragment, whitespace or control characters", [op.Id]);
        }

        if (op.Route.StartsWith("//", StringComparison.Ordinal) || literal.Contains("://", StringComparison.Ordinal))
        {
            Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", rp, $"operation '{op.Id}': route must not look like an absolute URL", [op.Id]);
        }
    }

    private void CheckParameters(Operation op, string opPath)
    {
        var routeVars = HttpRules.ParseRouteVariables(op.Route);
        var rp = JsonPointer.Append(opPath, "route");
        var routeNames = routeVars.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(ParameterLocation, string)>();
        for (var j = 0; j < op.Parameters.Count; j++)
        {
            var p = op.Parameters[j];
            var pp = JsonPointer.Append(JsonPointer.Append(opPath, "parameters"), j);
            var key = (p.Location, p.Location == ParameterLocation.Header ? p.Name.ToLowerInvariant() : p.Name);
            if (!seen.Add(key))
            {
                Error(TisiliaCodes.ParameterRouteMismatch, "SV30", JsonPointer.Append(pp, "name"), $"operation '{op.Id}': parameter '{p.Name}' is declared twice for location '{Enum(p.Location)}'", [op.Id]);
            }

            CheckTypeUse(p.Use, JsonPointer.Append(pp, "use"));
            if (_index.Binders.TryGetValue(p.BinderId, out var binder))
            {
                if (binder.Location != p.Location)
                {
                    Error(TisiliaCodes.ParameterRouteMismatch, "SV30", JsonPointer.Append(pp, "binderId"), $"operation '{op.Id}': parameter '{p.Name}' is a '{Enum(p.Location)}' parameter but binder '{binder.Id}' binds '{Enum(binder.Location)}'", [op.Id, binder.Id]);
                }

                if (binder.TypeId != p.Use.TypeId)
                {
                    Error(TisiliaCodes.ParameterRouteMismatch, "SV30", JsonPointer.Append(pp, "binderId"), $"operation '{op.Id}': parameter '{p.Name}' has type '{p.Use.TypeId}' but binder '{binder.Id}' binds '{binder.TypeId}'", [op.Id, binder.Id]);
                }

                if (p.Use.SemanticNullable && binder.NullPolicy == NullPolicy.Reject)
                {
                    Error(TisiliaCodes.BinderNullPolicy, "SV31", JsonPointer.Append(JsonPointer.Append(pp, "use"), "semanticNullable"), $"operation '{op.Id}': parameter '{p.Name}' allows null but binder '{binder.Id}' rejects null", [op.Id, binder.Id]);
                }

                if (p.Use.SemanticNullable && binder.NullPolicy == NullPolicy.Omit && p.Presence == Presence.Required)
                {
                    Error(TisiliaCodes.BinderNullPolicy, "SV31", JsonPointer.Append(pp, "presence"), $"operation '{op.Id}': required parameter '{p.Name}' would be omitted for null by binder '{binder.Id}'; declare it optional or reject null", [op.Id, binder.Id]);
                }

            }
            else
            {
                Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(pp, "binderId"), $"binder '{p.BinderId}' is not defined in binders", [op.Id, p.BinderId]);
            }

            if (p.Location == ParameterLocation.Path)
            {
                pathParams.Add(p.Name);
                if (!routeNames.Contains(p.Name))
                {
                    Error(TisiliaCodes.ParameterRouteMismatch, "SV30", JsonPointer.Append(pp, "name"), $"operation '{op.Id}': path parameter '{p.Name}' does not appear in route '{op.Route}'", [op.Id]);
                }
            }

            if (p.Location == ParameterLocation.Header)
            {
                if (!HttpRules.IsHttpToken(p.Name))
                {
                    Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", JsonPointer.Append(pp, "name"), $"operation '{op.Id}': header parameter name '{p.Name}' is not a valid HTTP token", [op.Id]);
                }
                else if (HttpRules.IsForbiddenRequestHeader(p.Name))
                {
                    Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", JsonPointer.Append(pp, "name"), $"operation '{op.Id}': header '{p.Name}' cannot be set by a browser fetch or is credential-bearing; it cannot be a typed parameter", [op.Id]);
                }
            }
        }

        foreach (var name in routeNames)
        {
            if (!pathParams.Contains(name))
            {
                Error(TisiliaCodes.ParameterRouteMismatch, "SV30", rp, $"operation '{op.Id}': route variable '{name}' has no path parameter", [op.Id]);
            }
        }
    }

    private void CheckRequestBody(Operation op, string opPath)
    {
        var bp = JsonPointer.Append(opPath, "requestBody");
        if (op.RequestBody is NoRequestBody)
        {
            return;
        }

        if (op.Method is HttpMethodKind.GET or HttpMethodKind.HEAD)
        {
            Error(TisiliaCodes.BodyOnGetOrHead, "SV26", bp, $"operation '{op.Id}': {op.Method} requests cannot carry a body (Fetch throws TypeError; ASP.NET Core does not bind it implicitly)", [op.Id]);
        }

        if (op.RequestBody is BinaryRequestBody binary)
        {
            if (!HttpRules.IsRawRequestMediaType(binary.MediaType))
            {
                Error(TisiliaCodes.MediaTypeInvalid, "SV29", bp + "/mediaType", "raw uploads require a concrete media type without parameters; multipart, form and SSE are not raw upload formats", [op.Id]);
            }
            return;
        }
        if (op.RequestBody is FormRequestBody form)
        {
            if (form.MediaType is not ("multipart/form-data" or "application/x-www-form-urlencoded"))
            {
                Error(TisiliaCodes.MediaTypeInvalid, "SV29", bp + "/mediaType", "forms require multipart/form-data or application/x-www-form-urlencoded", [op.Id]);
            }
            if (form.Fields.Count(f => f.WireName == "") > 1)
            {
                Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "only one root collection can own the form's index space", [op.Id]);
            }
            var wirePaths = new List<string[]>();
            CheckFormFields(form.Fields, 0, "");
            void CheckFormFields(IReadOnlyList<FormField> fields, int depth, string prefix)
            {
                if (depth > 16) { Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "form fields exceed maximum nesting", [op.Id]); return; }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    var wireName = prefix + (field.WireName ?? field.Name) + (field.Indexed ? "[]" : "");
                    if (field.Kind != "object")
                    {
                        // [] is an emitted variable index; distinct literal indexes remain distinct form names.
                        var parts = System.Text.RegularExpressions.Regex.Split(wireName, @"(\[(?:0|[1-9][0-9]*)?\])");
                        if (wirePaths.Any(other => other.Length == parts.Length && parts.Select((part, i) =>
                            part.Equals(other[i], StringComparison.OrdinalIgnoreCase) || i % 2 == 1 && (part == "[]" || other[i] == "[]")).All(equal => equal)))
                        {
                            Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "form fields have overlapping wire names", [op.Id]);
                        }
                        wirePaths.Add(parts);
                    }
                    if (field.WireName is not null && (field.WireName != "" || depth != 0 || !field.Indexed))
                    {
                        Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "an empty wire name is reserved for indexed root collections", [op.Id]);
                    }
                    if (field.Kind == "object")
                    {
                        if (field.Use is not null || field.Fields is not { Count: > 0 } || field.Repeated != field.Indexed || field.HasServerDefault || field.EnumDefinedOnly || field.RejectBlank)
                        {
                            Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "object form fields require child fields and indexed repetition without scalar options", [op.Id]);
                        }
                        if (field.Fields is not null) { CheckFormFields(field.Fields, depth + 1, wireName + "."); }
                    }
                    else if (field.Fields is not null)
                    {
                        Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "only object form fields can contain children", [op.Id]);
                    }
                    if (field.Indexed && !field.Repeated)
                    {
                        Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "indexed form fields must be repeated", [op.Id]);
                    }
                    if (!names.Add(field.Name) || string.IsNullOrEmpty(field.Name) || field.Name.Any(c => char.IsControl(c) || c is '"' or '\\'))
                    {
                        Error(TisiliaCodes.ParameterRouteMismatch, "SV30", bp + "/fields", "form field names must be unique and safe Content-Disposition names", [op.Id]);
                    }
                    if (field.Kind == "object") { continue; }
                    if (field.Kind == "file")
                    {
                        if (field.Use is not null || form.MediaType != "multipart/form-data")
                        {
                            Error(TisiliaCodes.TextBodyRule, "SV29", bp + "/fields", "file fields require multipart without a scalar use", [op.Id]);
                        }
                    }
                    else if (field.Kind != "value" || field.Use is null)
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", bp + "/fields", "value fields require a scalar use", [op.Id]);
                    }
                    else if (CheckTypeUse(field.Use, bp + "/fields/use"))
                    {
                        var shape = _index.Types[field.Use.TypeId].Shape;
                        if (field.Use.SemanticNullable || _index.Codecs[field.Use.CodecId].Origin != CodecOrigin.Builtin || shape is not (PrimitiveShape or EnumShape)
                            || shape is PrimitiveShape scalar && new[] { "bytes", "json-value", "datetime-local-wire" }.Any(s => scalar.PrimitiveId == Builtins.Scalar(s))
                            || field.EnumDefinedOnly && shape is not EnumShape)
                        {
                            Error(TisiliaCodes.TextBodyRule, "SV29", bp + "/fields/use", "form values require non-null builtin HTTP scalars or enums; omit optional fields instead of null", [op.Id]);
                        }
                    }
                }
            }
            return;
        }
        var body = (JsonRequestBody)op.RequestBody;
        var media = HttpRules.ParseMediaType(body.MediaType);
        if (media is null || !HttpRules.JsonMediaEssences.Contains(media.Essence) || (media.Charset is not null && media.Charset != "utf-8"))
        {
            Error(TisiliaCodes.MediaTypeInvalid, "SV29", JsonPointer.Append(bp, "mediaType"), $"operation '{op.Id}': request media type '{body.MediaType}' must be one of [{string.Join(", ", HttpRules.JsonMediaEssences)}] with UTF-8 or no charset", [op.Id]);
        }

        CheckTypeUse(body.Use, JsonPointer.Append(bp, "use"));
        if (RequireProfile(body.ProfileId, JsonPointer.Append(bp, "profileId")) && _index.Codecs.TryGetValue(body.Use.CodecId, out var codec))
        {
            if (codec.ProfileIds.Count > 0 && !codec.ProfileIds.Contains(body.ProfileId, StringComparer.Ordinal))
            {
                Error(TisiliaCodes.ProfileResolution, "SV16", JsonPointer.Append(bp, "profileId"), $"operation '{op.Id}': root codec '{codec.Id}' is not applicable to profile '{body.ProfileId}' (profileIds are an applicability range, not a default choice)", [op.Id, codec.Id]);
            }
        }
    }

    private void CheckResponses(Operation op, string opPath)
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodylessStatuses = new HashSet<int>();
        var bodyStatuses = new HashSet<int>();
        for (var j = 0; j < op.Responses.Count; j++)
        {
            var r = op.Responses[j];
            var rp = JsonPointer.Append(JsonPointer.Append(opPath, "responses"), j);
            if (HttpRules.IsRedirectStatus(r.Status))
            {
                Error(TisiliaCodes.RedirectStatus, "SV33", JsonPointer.Append(rp, "status"), $"operation '{op.Id}': typed redirect status {r.Status} is not supported under the common redirect:error runtime", [op.Id]);
            }

            var adapter = _index.ResultAdapters.GetValueOrDefault(r.ResultAdapterId);
            if (adapter is null)
            {
                Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(rp, "resultAdapterId"), $"result adapter '{r.ResultAdapterId}' is not defined in resultAdapters", [op.Id, r.ResultAdapterId]);
            }

            string caseKey;
            switch (r.Body)
            {
                case NoResponseBody:
                    caseKey = r.Status + "|none";
                    bodylessStatuses.Add(r.Status);
                    if (adapter is { Kind: ResultAdapterKind.Text or ResultAdapterKind.Binary or ResultAdapterKind.Sse })
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", JsonPointer.Append(rp, "resultAdapterId"), $"operation '{op.Id}': bodyless case '{r.Id}' cannot use a text result adapter", [op.Id, r.Id]);
                    }

                    break;
                case JsonResponseBody json:
                {
                    var media = HttpRules.ParseMediaType(json.MediaType);
                    if (media is null || !HttpRules.JsonMediaEssences.Contains(media.Essence) || (media.Charset is not null && media.Charset != "utf-8"))
                    {
                        Error(TisiliaCodes.MediaTypeInvalid, "SV29", JsonPointer.Append(JsonPointer.Append(rp, "body"), "mediaType"), $"operation '{op.Id}': response media type '{json.MediaType}' must be an explicitly listed JSON media type with UTF-8 or no charset", [op.Id, r.Id]);
                    }

                    caseKey = r.Status + "|" + (media?.Essence ?? json.MediaType);
                    bodyStatuses.Add(r.Status);
                    if (HttpRules.IsBodylessStatus(r.Status) || op.Method == HttpMethodKind.HEAD)
                    {
                        Error(TisiliaCodes.BodylessStatusMismatch, "SV27", JsonPointer.Append(rp, "body"), $"operation '{op.Id}': case '{r.Id}' (status {r.Status}, {op.Method}) must be bodyless", [op.Id, r.Id]);
                    }

                    CheckTypeUse(json.Use, JsonPointer.Append(JsonPointer.Append(rp, "body"), "use"));
                    if (adapter is { Kind: ResultAdapterKind.Bodyless or ResultAdapterKind.Text or ResultAdapterKind.Binary or ResultAdapterKind.Sse })
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", JsonPointer.Append(rp, "resultAdapterId"), $"operation '{op.Id}': JSON case '{r.Id}' cannot use a '{Enum(adapter.Kind)}' result adapter", [op.Id, r.Id]);
                    }

                    if (RequireProfile(json.ProfileId, JsonPointer.Append(JsonPointer.Append(rp, "body"), "profileId")))
                    {
                        if (adapter is not null && adapter.ProfileIds.Count > 0 && !adapter.ProfileIds.Contains(json.ProfileId, StringComparer.Ordinal))
                        {
                            Error(TisiliaCodes.ProfileResolution, "SV16", JsonPointer.Append(JsonPointer.Append(rp, "body"), "profileId"), $"operation '{op.Id}': result adapter '{adapter.Id}' does not apply to profile '{json.ProfileId}'", [op.Id, adapter.Id]);
                        }

                        if (_index.Codecs.TryGetValue(json.Use.CodecId, out var codec) && codec.ProfileIds.Count > 0 && !codec.ProfileIds.Contains(json.ProfileId, StringComparer.Ordinal))
                        {
                            Error(TisiliaCodes.ProfileResolution, "SV16", JsonPointer.Append(JsonPointer.Append(rp, "body"), "profileId"), $"operation '{op.Id}': root codec '{codec.Id}' is not applicable to profile '{json.ProfileId}'", [op.Id, codec.Id]);
                        }
                    }

                    break;
                }

                case TextResponseBody text:
                {
                    var media = HttpRules.ParseMediaType(text.MediaType);
                    // text/event-stream is an unbounded stream (SSE, not supported) and text/json is a JSON body, not text
                    if (media is null || media.Type != "text" || media.Essence == "text/event-stream" || HttpRules.JsonMediaEssences.Contains(media.Essence) || (media.Charset is not null && media.Charset != "utf-8"))
                    {
                        Error(TisiliaCodes.MediaTypeInvalid, "SV29", JsonPointer.Append(JsonPointer.Append(rp, "body"), "mediaType"), $"operation '{op.Id}': text media type '{text.MediaType}' must be a text/* type with UTF-8 or no charset, other than text/event-stream and text/json", [op.Id, r.Id]);
                    }

                    caseKey = r.Status + "|" + (media?.Essence ?? text.MediaType);
                    bodyStatuses.Add(r.Status);
                    if (HttpRules.IsBodylessStatus(r.Status) || op.Method == HttpMethodKind.HEAD)
                    {
                        Error(TisiliaCodes.BodylessStatusMismatch, "SV27", JsonPointer.Append(rp, "body"), $"operation '{op.Id}': case '{r.Id}' (status {r.Status}, {op.Method}) must be bodyless", [op.Id, r.Id]);
                    }

                    if (CheckTypeUse(text.Use, JsonPointer.Append(JsonPointer.Append(rp, "body"), "use")))
                    {
                        var model = _index.Types[text.Use.TypeId];
                        if (model.Shape is not PrimitiveShape { PrimitiveId: var pid } || pid != Builtins.Scalar("string"))
                        {
                            Error(TisiliaCodes.TextBodyRule, "SV29", JsonPointer.Append(JsonPointer.Append(rp, "body"), "use"), $"operation '{op.Id}': text case '{r.Id}' must use the builtin string domain type", [op.Id, r.Id]);
                        }
                    }

                    if (adapter is not null && adapter.Kind != ResultAdapterKind.Text)
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", JsonPointer.Append(rp, "resultAdapterId"), $"operation '{op.Id}': text case '{r.Id}' requires a text result adapter", [op.Id, r.Id]);
                    }

                    break;
                }

                case SseResponseBody sse:
                {
                    caseKey = r.Status + "|text/event-stream";
                    bodyStatuses.Add(r.Status);
                    if (sse.MediaType != "text/event-stream" || sse.DataFormat is not ("text" or "json"))
                    {
                        Error(TisiliaCodes.MediaTypeInvalid, "SV29", rp + "/body", "SSE requires text/event-stream and an explicit text or JSON data format", [op.Id, r.Id]);
                    }
                    if (HttpRules.IsBodylessStatus(r.Status) || op.Method == HttpMethodKind.HEAD)
                    {
                        Error(TisiliaCodes.BodylessStatusMismatch, "SV27", rp + "/body", "this status / method must be bodyless", [op.Id, r.Id]);
                    }
                    if (adapter is not null && (adapter.Kind != ResultAdapterKind.Sse || adapter.BehaviorIds.Count != 0))
                    {
                        Error(TisiliaCodes.PipelineOrResultClosure, "SV34", rp + "/resultAdapterId", "SSE requires an SSE result adapter", [op.Id, r.Id]);
                    }
                    if (r.Hydration != Hydration.ServerOnly)
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", rp + "/hydration", "SSE cannot be automatically hydrated", [op.Id, r.Id]);
                    }
                    var validUse = CheckTypeUse(sse.Use, rp + "/body/use");
                    if (sse.DataFormat == "text")
                    {
                        if (sse.ProfileId is not null || adapter is { ProfileIds.Count: > 0 } || sse.Use.SemanticNullable
                            || (validUse && (_index.Types[sse.Use.TypeId].Shape is not PrimitiveShape primitive || primitive.PrimitiveId != Builtins.Scalar("string"))))
                        {
                            Error(TisiliaCodes.TextBodyRule, "SV29", rp + "/body", "text SSE uses non-null string data without a JSON profile", [op.Id, r.Id]);
                        }
                    }
                    else if (sse.ProfileId is null)
                    {
                        Error(TisiliaCodes.ProfileResolution, "SV16", rp + "/body", "JSON SSE requires its JSON profile", [op.Id, r.Id]);
                    }
                    else if (RequireProfile(sse.ProfileId, rp + "/body/profileId"))
                    {
                        if ((adapter is not null && !adapter.ProfileIds.Contains(sse.ProfileId, StringComparer.Ordinal))
                            || (_index.Codecs.TryGetValue(sse.Use.CodecId, out var codec) && codec.ProfileIds.Count > 0 && !codec.ProfileIds.Contains(sse.ProfileId, StringComparer.Ordinal)))
                        {
                            Error(TisiliaCodes.ProfileResolution, "SV16", rp + "/body/profileId", "SSE codec and adapter must apply to the declared profile", [op.Id, r.Id]);
                        }
                    }
                    break;
                }
                case BinaryResponseBody binary:
                {
                    var media = HttpRules.ParseMediaType(binary.MediaType);
                    caseKey = r.Status + "|" + (media?.Essence ?? binary.MediaType);
                    bodyStatuses.Add(r.Status);
                    if (media is null || media.Type.Contains('*') || media.Subtype.Contains('*') || media.Essence == "text/event-stream")
                    {
                        Error(TisiliaCodes.MediaTypeInvalid, "SV29", rp + "/body/mediaType", "binary requires a concrete media type other than SSE", [op.Id, r.Id]);
                    }
                    if (HttpRules.IsBodylessStatus(r.Status) || op.Method == HttpMethodKind.HEAD)
                    {
                        Error(TisiliaCodes.BodylessStatusMismatch, "SV27", rp + "/body", "this status / method must be bodyless", [op.Id, r.Id]);
                    }
                    if (adapter is not null && (adapter.Kind != ResultAdapterKind.Binary || adapter.ProfileIds.Count != 0 || adapter.BehaviorIds.Count != 0))
                    {
                        Error(TisiliaCodes.PipelineOrResultClosure, "SV34", rp + "/resultAdapterId", "binary requires a binary adapter without JSON dependencies", [op.Id, r.Id]);
                    }
                    if (r.Hydration != Hydration.ServerOnly)
                    {
                        Error(TisiliaCodes.TextBodyRule, "SV29", rp + "/hydration", "binary must be server-only for automatic hydration", [op.Id, r.Id]);
                    }
                    break;
                }
                default:
                    caseKey = r.Status + "|?";
                    Error(TisiliaCodes.TextBodyRule, "SV29", rp + "/body", "unknown response kind", [op.Id, r.Id]);
                    break;
            }

            if (!cases.TryAdd(caseKey, r.Id))
            {
                Error(TisiliaCodes.AmbiguousResponseCase, "SV28", JsonPointer.Append(rp, "status"), $"operation '{op.Id}': cases '{cases[caseKey]}' and '{r.Id}' share status and media type; the runtime never sniffs bodies to pick a case", [op.Id, r.Id, cases[caseKey]]);
            }

            for (var k = 0; k < r.ExposedHeaders.Count; k++)
            {
                if (!HttpRules.IsHttpToken(r.ExposedHeaders[k]))
                {
                    Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", JsonPointer.Append(JsonPointer.Append(rp, "exposedHeaders"), k), $"operation '{op.Id}': exposed header '{r.ExposedHeaders[k]}' is not a valid HTTP token", [op.Id]);
                }
            }
        }

        foreach (var status in bodylessStatuses.Intersect(bodyStatuses))
        {
            Error(TisiliaCodes.AmbiguousResponseCase, "SV28", JsonPointer.Append(opPath, "responses"), $"operation '{op.Id}': status {status} has both a bodyless case and a body case; separate the execution paths or register an explicit disambiguating result adapter", [op.Id]);
        }
    }

    private void CheckSecurity(Operation op, string opPath)
    {
        var sp = JsonPointer.Append(opPath, "security");
        RequireBuiltinOrBinding(op.Security.AuthPolicyId, JsonPointer.Append(sp, "authPolicyId"), "auth policy", [BuiltinKind.AuthPolicy], [BindingKind.AuthPolicy]);
        RequireBuiltinOrBinding(op.Security.CsrfPolicyId, JsonPointer.Append(sp, "csrfPolicyId"), "csrf policy", [BuiltinKind.CsrfPolicy], [BindingKind.CsrfPolicy]);
        for (var k = 0; k < op.Security.RequestHeaderAllowlist.Count; k++)
        {
            var h = op.Security.RequestHeaderAllowlist[k];
            var hp = JsonPointer.Append(JsonPointer.Append(sp, "requestHeaderAllowlist"), k);
            if (!HttpRules.IsHttpToken(h))
            {
                Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", hp, $"operation '{op.Id}': allowlisted header '{h}' is not a valid HTTP token", [op.Id]);
            }
            else if (HttpRules.HopByHopHeaders.Contains(h))
            {
                Error(TisiliaCodes.UnsafeRouteOrHeader, "SV32", hp, $"operation '{op.Id}': hop-by-hop header '{h}' is never forwarded", [op.Id]);
            }
        }

        if (op.Security.Redaction.Default != "mask")
        {
            Error(TisiliaCodes.RedactionRule, "SV36", JsonPointer.Append(JsonPointer.Append(sp, "redaction"), "default"), $"operation '{op.Id}': redaction default must be 'mask'", [op.Id]);
        }

        var rules = new Dictionary<string, (RedactionAction Action, int Index)>(StringComparer.Ordinal);
        for (var k = 0; k < op.Security.Redaction.Rules.Count; k++)
        {
            var rule = op.Security.Redaction.Rules[k];
            var rp = JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(sp, "redaction"), "rules"), k);
            var key = Enum(rule.Direction) + "|" + SelectorKey(rule.Selector);
            if (rules.TryGetValue(key, out var existing) && existing.Action != rule.Action)
            {
                Error(TisiliaCodes.RedactionRule, "SV36", rp, $"operation '{op.Id}': redaction rules {existing.Index} and {k} select the same target with different actions", [op.Id]);
            }
            else
            {
                rules.TryAdd(key, (rule.Action, k));
            }

            if (rule.Selector is NamedSelector { Kind: SelectorKind.Header } header && rule.Action == RedactionAction.Show && IsBuiltinSecretHeader(header.Name))
            {
                Error(TisiliaCodes.SecretExposure, "SV36", rp, $"operation '{op.Id}': the builtin secret rule for header '{header.Name}' cannot be lifted with 'show'", [op.Id]);
            }
        }
    }

    private static bool IsBuiltinSecretHeader(string name)
        => name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
           || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
           || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
           || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static string SelectorKey(Selector selector) => selector switch
    {
        NamedSelector n => Enum(n.Kind) + ":" + (n.Kind == SelectorKind.Header ? n.Name.ToLowerInvariant() : n.Name),
        BodyPathSelector b => "body:" + string.Join("/", b.Segments.Select(s => s switch
        {
            PropertySegment p => "p=" + p.Property,
            IndexSegment i => "i=" + i.Index,
            _ => "each",
        })),
        _ => "?",
    };
}
