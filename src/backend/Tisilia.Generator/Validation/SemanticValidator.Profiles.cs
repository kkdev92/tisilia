using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

public sealed partial class SemanticValidator
{
    // ---------------------------------------------------------------- profiles (SV16–SV20/SV24)

    private void CheckProfiles()
    {
        for (var i = 0; i < _doc.Profiles.Count; i++)
        {
            var profile = _doc.Profiles[i];
            var pp = JsonPointer.Append("/profiles", i);
            var op = JsonPointer.Append(pp, "options");
            var o = profile.Options;
            RequireBuiltinOrBinding(o.PropertyNamingPolicyId, JsonPointer.Append(op, "propertyNamingPolicyId"), "naming policy", [BuiltinKind.NamingPolicy], [BindingKind.NamingPolicy]);
            RequireBuiltinOrBinding(o.DictionaryKeyPolicyId, JsonPointer.Append(op, "dictionaryKeyPolicyId"), "dictionary key policy", [BuiltinKind.NamingPolicy], [BindingKind.NamingPolicy]);
            RequireBuiltinOrBinding(o.EncoderId, JsonPointer.Append(op, "encoderId"), "encoder", [BuiltinKind.Encoder], [BindingKind.Encoder]);
            for (var j = 0; j < o.ResolverIds.Count; j++)
            {
                RequireBuiltinOrBinding(o.ResolverIds[j], JsonPointer.Append(JsonPointer.Append(op, "resolverIds"), j), "resolver", [BuiltinKind.Resolver], [BindingKind.Resolver]);
            }

            for (var j = 0; j < o.ConverterBindingIds.Count; j++)
            {
                RequireBinding(o.ConverterBindingIds[j], JsonPointer.Append(JsonPointer.Append(op, "converterBindingIds"), j), BindingKind.Converter, BindingKind.Factory);
            }

            // SV17
            var expectedDepth = o.MaxDepthRaw == 0 ? 64 : o.MaxDepthRaw;
            if (o.MaxDepthEffective != expectedDepth)
            {
                Error(TisiliaCodes.MaxDepth, "SV17", JsonPointer.Append(op, "maxDepthEffective"), $"profile '{profile.Id}': maxDepthRaw {o.MaxDepthRaw} implies effective depth {expectedDepth} (0 means the .NET default 64)", [profile.Id]);
            }

            // SV18
            if (o.IgnoreNullValues && o.DefaultIgnoreCondition != IgnoreCondition.Never)
            {
                Error(TisiliaCodes.IgnoreCondition, "SV18", JsonPointer.Append(op, "ignoreNullValues"), $"profile '{profile.Id}': IgnoreNullValues cannot be combined with a non-Never DefaultIgnoreCondition (System.Text.Json throws)", [profile.Id]);
            }

            if (o.DefaultIgnoreCondition == IgnoreCondition.Always)
            {
                Error(TisiliaCodes.IgnoreCondition, "SV18", JsonPointer.Append(op, "defaultIgnoreCondition"), $"profile '{profile.Id}': DefaultIgnoreCondition.Always is rejected by System.Text.Json", [profile.Id]);
            }

            var behaviorsInProfile = profile.Behaviors.ToDictionary(b => b.Id, b => b, StringComparer.Ordinal);
            for (var j = 0; j < profile.Behaviors.Count; j++)
            {
                var b = profile.Behaviors[j];
                var bp = JsonPointer.Append(JsonPointer.Append(pp, "behaviors"), j);
                RequireImpl(b.Implementation, JsonPointer.Append(bp, "implementation"), "behavior", [BuiltinKind.Behavior], [ExportRole.Behavior], DotnetTargets);
                if (b.Effect == BehaviorEffect.Normalized && b.ProjectionId is null)
                {
                    Error(TisiliaCodes.BehaviorEffect, "SV19", JsonPointer.Append(bp, "effect"), $"behavior '{b.Id}': effect 'normalized' requires projectionId", [b.Id]);
                }

                if (b.ProjectionId is not null)
                {
                    RequireProjection(b.ProjectionId, JsonPointer.Append(bp, "projectionId"));
                }

                if (b.Effect == BehaviorEffect.Identity && b.Implementation is not BuiltinImpl { Id: Builtins.BehaviorIdentity } && b.Kind is not (BehaviorKind.Populate or BehaviorKind.Constructor or BehaviorKind.Initializer or BehaviorKind.Setter or BehaviorKind.Getter or BehaviorKind.Callback or BehaviorKind.ShouldSerialize or BehaviorKind.NestedOptions or BehaviorKind.SourcegenHandler or BehaviorKind.Other))
                {
                    Error(TisiliaCodes.BehaviorEffect, "SV19", JsonPointer.Append(bp, "effect"), $"behavior '{b.Id}': unknown kind", [b.Id]);
                }
            }

            var clrPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < profile.Scopes.Count; j++)
            {
                var s = profile.Scopes[j];
                var sp = JsonPointer.Append(JsonPointer.Append(pp, "scopes"), j);
                if (!clrPaths.Add(s.ClrPath))
                {
                    Error(TisiliaCodes.BindingScopeClaim, "SV24", JsonPointer.Append(sp, "clrPath"), $"profile '{profile.Id}': scope '{s.ClrPath}' is claimed by more than one scope entry", [profile.Id, s.Id]);
                }

                if (s.ReadCodecId is not null && RequireCodec(s.ReadCodecId, JsonPointer.Append(sp, "readCodecId")))
                {
                    var codec = _index.Codecs[s.ReadCodecId];
                    if (codec.Capabilities.Request is null)
                    {
                        Error(TisiliaCodes.MissingCapability, "SV20", JsonPointer.Append(sp, "readCodecId"), $"scope '{s.Id}': read codec '{codec.Id}' has no request capability (server read = Converter.Read)", [s.Id, codec.Id]);
                    }

                    if (codec.ProfileIds.Count > 0 && !codec.ProfileIds.Contains(profile.Id, StringComparer.Ordinal))
                    {
                        Error(TisiliaCodes.ProfileResolution, "SV16", JsonPointer.Append(sp, "readCodecId"), $"scope '{s.Id}': codec '{codec.Id}' is not applicable to profile '{profile.Id}'", [s.Id, codec.Id]);
                    }
                }

                if (s.WriteCodecId is not null && RequireCodec(s.WriteCodecId, JsonPointer.Append(sp, "writeCodecId")))
                {
                    var codec = _index.Codecs[s.WriteCodecId];
                    if (codec.Capabilities.Response is null)
                    {
                        Error(TisiliaCodes.MissingCapability, "SV20", JsonPointer.Append(sp, "writeCodecId"), $"scope '{s.Id}': write codec '{codec.Id}' has no response capability (server write = Converter.Write)", [s.Id, codec.Id]);
                    }

                    if (codec.ProfileIds.Count > 0 && !codec.ProfileIds.Contains(profile.Id, StringComparer.Ordinal))
                    {
                        Error(TisiliaCodes.ProfileResolution, "SV16", JsonPointer.Append(sp, "writeCodecId"), $"scope '{s.Id}': codec '{codec.Id}' is not applicable to profile '{profile.Id}'", [s.Id, codec.Id]);
                    }
                }

                if (s.EffectiveIgnoreCondition == IgnoreCondition.Always && s.Kind == ScopeKind.Type)
                {
                    Error(TisiliaCodes.IgnoreCondition, "SV18", JsonPointer.Append(sp, "effectiveIgnoreCondition"), $"scope '{s.Id}': 'Always' applies to members, not to types", [s.Id]);
                }

                var hasOpaque = false;
                for (var k = 0; k < s.BehaviorIds.Count; k++)
                {
                    var bid = s.BehaviorIds[k];
                    if (!behaviorsInProfile.TryGetValue(bid, out var behavior))
                    {
                        Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(JsonPointer.Append(sp, "behaviorIds"), k), $"scope '{s.Id}': behavior '{bid}' is not declared in profile '{profile.Id}'", [s.Id, bid]);
                        continue;
                    }

                    hasOpaque |= behavior.Effect == BehaviorEffect.Opaque;
                }

                // SV19: an opaque behavior on the path forbids G2/G3 claims for the type that owns the scope (a member's own codec,
                // e.g. std.string, is shared and unaffected; the owner object's round trip is what the opaque effect breaks)
                if (hasOpaque)
                {
                    var owner = s.Kind == ScopeKind.Member && s.ClrPath.LastIndexOf('.') is var dot && dot > 0 ? s.ClrPath[..dot] : s.ClrPath;
                    foreach (var model in _index.Types.Values.Where(t => t.ClrIdentity == owner))
                    {
                        foreach (var codec in _index.CodecsByType.TryGetValue(model.Id, out var owned) ? owned : [])
                        {
                            foreach (var cap in new[] { codec.Capabilities.Request, codec.Capabilities.Response })
                            {
                                if (cap is not null && _index.Equivalences.TryGetValue(cap.EquivalenceId, out var eq) && eq.Grade != Grade.G1)
                                {
                                    Error(TisiliaCodes.BehaviorEffect, "SV19", JsonPointer.Append(sp, "behaviorIds"), $"scope '{s.Id}' has an opaque behavior but codec '{codec.Id}' of its owner type claims {eq.Grade} equivalence '{eq.Id}'; opaque paths are G1 only", [s.Id, codec.Id, eq.Id]);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// ReferenceHandler.Preserve writes reference metadata — <c>$id</c> first, collections as <c>{"$id","$values"}</c>, <c>{"$ref"}</c>
    /// for a value written before — where the server-write wires say <c>referenceMetadata</c>, which the runtime reads, and reads it in a
    /// request where the server-read wires say so, which the runtime then writes for a value reached again. Only a body of a profile that
    /// preserves references reaches such a wire (SV20).
    /// </summary>
    private void CheckReferenceMetadata()
    {
        static bool Marked(WireShape shape) => shape is ObjectWire { ReferenceMetadata: true } or ArrayWire { ReferenceMetadata: true } or TaggedUnionWire { ReferenceMetadata: true };

        // the first marked wire a body reaches, or null
        string? MarkedWireReached(string start)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([start]);
            while (pending.TryPop(out var wireId))
            {
                if (!seen.Add(wireId) || !_index.Wires.TryGetValue(wireId, out var wire))
                {
                    continue;
                }

                if (Marked(wire.Shape))
                {
                    return wireId;
                }

                IEnumerable<WireRef> children = wire.Shape switch
                {
                    ArrayWire array => [array.Element],
                    ObjectWire obj => obj.Properties.Select(p => p.Wire).Concat(obj.Additional is CaptureAdditional capture ? [capture.Wire] : []),
                    TokenUnionWire tokens => tokens.Branches.Select(b => b.Wire),
                    TaggedUnionWire tagged => tagged.Variants.Select(v => v.Wire),
                    _ => [],
                };
                foreach (var child in children)
                {
                    pending.Push(child.WireId);
                }
            }

            return null;
        }

        bool Preserves(string profileId) => _index.Profiles.TryGetValue(profileId, out var profile) && profile.Options.ReferenceHandling == ReferenceHandling.Preserve;

        for (var i = 0; i < _doc.Operations.Count; i++)
        {
            var operation = _doc.Operations[i];
            if (operation.RequestBody is JsonRequestBody body && !Preserves(body.ProfileId) && _index.Codecs.TryGetValue(body.Use.CodecId, out var bodyCodec)
                && bodyCodec.Capabilities.Request is { } request && MarkedWireReached(request.Wire.WireId) is { } readWire)
            {
                Error(TisiliaCodes.ReferencePreserve, "SV20", JsonPointer.Append(JsonPointer.Append("/operations", i), "requestBody"), $"operation '{operation.Id}': profile '{body.ProfileId}' does not preserve references, but the request body reaches wire '{readWire}', which says the server reads reference metadata", [operation.Id, readWire]);
            }

            for (var j = 0; j < operation.Responses.Count; j++)
            {
                var (use, profileId) = operation.Responses[j].Body switch
                {
                    JsonResponseBody json => (json.Use, json.ProfileId),
                    SseResponseBody { ProfileId: { } sseProfile } sse => (sse.Use, sseProfile),
                    _ => (null, null),
                };
                if (use is null || profileId is null || Preserves(profileId) || !_index.Codecs.TryGetValue(use.CodecId, out var codec) || codec.Capabilities.Response is not { } response)
                {
                    continue;
                }

                if (MarkedWireReached(response.Wire.WireId) is { } writeWire)
                {
                    Error(TisiliaCodes.ReferencePreserve, "SV20", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append("/operations", i), "responses"), j), $"operation '{operation.Id}': profile '{profileId}' does not preserve references, but the response reaches wire '{writeWire}', which says the server writes reference metadata", [operation.Id, writeWire]);
                }
            }
        }
    }

    // ---------------------------------------------------------------- usage graph (SV05/SV06/SV07/SV08)

    private enum Usage
    {
        Request,
        Response,
        Domain,
    }

    private void CheckUsageGraph()
    {
        var visited = new HashSet<(string TypeId, string CodecId, Usage Usage)>();
        for (var i = 0; i < _doc.Operations.Count; i++)
        {
            var op = _doc.Operations[i];
            var opPath = JsonPointer.Append("/operations", i);
            for (var j = 0; j < op.Parameters.Count; j++)
            {
                var p = op.Parameters[j];
                Walk(p.Use, Usage.Domain, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(opPath, "parameters"), j), "use"), visited, $"operation '{op.Id}' parameter '{p.Name}'");
            }

            if (op.RequestBody is XmlRequestBody xmlBody)
            {
                Walk(xmlBody.Use, Usage.Request, JsonPointer.Append(JsonPointer.Append(opPath, "requestBody"), "use"), visited, $"operation '{op.Id}' request body");
            }

            if (op.RequestBody is JsonRequestBody body)
            {
                Walk(body.Use, Usage.Request, JsonPointer.Append(JsonPointer.Append(opPath, "requestBody"), "use"), visited, $"operation '{op.Id}' request body");
            }
            if (op.RequestBody is FormRequestBody form)
            {
                foreach (var field in FormField.Descendants(form.Fields))
                {
                    if (field.Use is not null) { Walk(field.Use, Usage.Domain, opPath + "/requestBody/fields", visited, $"operation '{op.Id}' form field '{field.Name}'"); }
                }
            }

            for (var j = 0; j < op.Responses.Count; j++)
            {
                var r = op.Responses[j];
                var rp = JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(opPath, "responses"), j), "body");
                switch (r.Body)
                {
                    case SseResponseBody sse:
                        Walk(sse.Use, sse.DataFormat == "json" ? Usage.Response : Usage.Domain, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
                        break;
                    case JsonResponseBody json:
                        Walk(json.Use, Usage.Response, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
                        break;
                    case TextResponseBody text:
                        Walk(text.Use, Usage.Domain, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
                        break;
                    case XmlResponseBody xml:
                        Walk(xml.Use, Usage.Response, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
                        break;
                }
            }
        }
    }

    /// <summary>Recursively checks capabilities and nullability at every reachable usage position (SV05/SV06/SV07/SV08).</summary>
    private void Walk(TypeUse use, Usage usage, string path, HashSet<(string, string, Usage)> visited, string context)
    {
        if (!_index.Types.TryGetValue(use.TypeId, out var model) || !_index.Codecs.TryGetValue(use.CodecId, out var codec) || codec.TypeId != use.TypeId)
        {
            return; // reported by SV03/SV04
        }

        var cap = usage switch
        {
            Usage.Request => codec.Capabilities.Request,
            Usage.Response => codec.Capabilities.Response,
            _ => null,
        };
        if (usage != Usage.Domain && cap is null)
        {
            Error(TisiliaCodes.MissingCapability, "SV05", JsonPointer.Append(path, "codecId"), $"{context}: codec '{codec.Id}' lacks the {(usage == Usage.Request ? "request (encodeRequest)" : "response (decodeResponse)")} capability required at this position", [codec.Id, use.TypeId]);
        }

        // an XML codec never sees null: xsi:nil, an absent member or empty content belongs to the member or the root (SV55)
        if (cap is not null && !IsXmlCodec(codec))
        {
            if (use.SemanticNullable && cap.NullBehavior == NullBehavior.Reject)
            {
                Error(TisiliaCodes.NullabilityMismatch, "SV06", JsonPointer.Append(path, "semanticNullable"), $"{context}: public type allows null but codec '{codec.Id}' rejects null in this direction", [codec.Id]);
            }

            if (_index.Wires.TryGetValue(cap.Wire.WireId, out var wire))
            {
                var wireNullable = HasNullBranch(wire);
                if (use.SemanticNullable && !AdmitsNull(wire) && cap.NullBehavior != NullBehavior.Converter)
                {
                    Error(TisiliaCodes.NullabilityMismatch, "SV06", JsonPointer.Append(path, "semanticNullable"), $"{context}: public type allows null but wire '{wire.Id}' has no null branch and codec '{codec.Id}' does not convert null itself", [codec.Id, wire.Id]);
                }

                if (usage == Usage.Response && !use.SemanticNullable && wireNullable && cap.NullBehavior != NullBehavior.Converter)
                {
                    Error(TisiliaCodes.NullabilityMismatch, "SV06", JsonPointer.Append(path, "semanticNullable"), $"{context}: the server may write null (wire '{wire.Id}') but the public type is not nullable; nullable annotations alone never remove a null branch", [codec.Id, wire.Id]);
                }
            }
        }

        if (!visited.Add((use.TypeId, use.CodecId, usage)))
        {
            return;
        }

        var childUsage = usage;
        switch (model.Shape)
        {
            case ObjectShape obj:
                foreach (var prop in obj.Properties)
                {
                    Walk(prop.Use, childUsage, path, visited, $"{context} → {model.Id}.{prop.Name}");
                }

                if (obj.Extension is CaptureExtension capture)
                {
                    Walk(capture.Value, childUsage, path, visited, $"{context} → {model.Id}[extension]");
                }

                break;
            case ArrayShape arr:
                Walk(arr.Element, childUsage, path, visited, $"{context} → {model.Id}[]");
                break;
            case MapShape map:
                if (usage != Usage.Domain && _index.Codecs.TryGetValue(map.Key.CodecId, out var keyCodec))
                {
                    var keyCap = usage == Usage.Request ? keyCodec.Capabilities.RequestKey : keyCodec.Capabilities.ResponseKey;
                    if (keyCap is null)
                    {
                        Error(TisiliaCodes.MissingCapability, "SV05", path, $"{context} → {model.Id}: key codec '{keyCodec.Id}' lacks the {(usage == Usage.Request ? "requestKey (encodeKey)" : "responseKey (decodeKey)")} capability", [keyCodec.Id]);
                    }
                }

                Walk(map.Key, Usage.Domain, path, visited, $"{context} → {model.Id}<key>");
                Walk(map.Value, childUsage, path, visited, $"{context} → {model.Id}<value>");
                break;
            case BrandShape brand:
                Walk(brand.Base, childUsage, path, visited, $"{context} → {model.Id}(brand)");
                break;
            case UnionShape union:
                foreach (var v in union.Variants)
                {
                    Walk(v.Use, childUsage, path, visited, $"{context} → {model.Id}|{v.Tag}");
                }

                break;
        }
    }

    // ---------------------------------------------------------------- productivity (SV09)

    private void CheckProductivity()
    {
        // Least fixed point over types: a type is productive when a finite value can be constructed.
        var productive = new HashSet<string>(StringComparer.Ordinal);
        bool UseProductive(TypeUse use) => use.SemanticNullable || productive.Contains(use.TypeId);
        bool changed;
        do
        {
            changed = false;
            foreach (var model in _doc.Types)
            {
                if (productive.Contains(model.Id))
                {
                    continue;
                }

                var ok = model.Shape switch
                {
                    PrimitiveShape or EnumShape or ArrayShape or MapShape => true,
                    ObjectShape obj => obj.Properties.Where(p => p.Presence == Presence.Required).All(p => UseProductive(p.Use)),
                    BrandShape brand => UseProductive(brand.Base),
                    UnionShape union => union.Variants.Any(v => UseProductive(v.Use)),
                    _ => false,
                };
                if (ok)
                {
                    productive.Add(model.Id);
                    changed = true;
                }
            }
        }
        while (changed);

        for (var i = 0; i < _doc.Types.Count; i++)
        {
            var model = _doc.Types[i];
            if (!productive.Contains(model.Id))
            {
                Error(TisiliaCodes.NonProductiveRecursion, "SV09", JsonPointer.Append(JsonPointer.Append("/types", i), "shape"), $"type '{model.Id}' has no finite value: every path through required members recurses without an optional, nullable or empty-collection exit", [model.Id],
                    "make a member optional, mark a use semanticNullable, or route the recursion through an array/map");
            }
        }

        // Same for wires.
        var productiveWires = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            changed = false;
            foreach (var wire in _doc.Wires)
            {
                if (productiveWires.Contains(wire.Id))
                {
                    continue;
                }

                var ok = wire.Shape switch
                {
                    NullWire or BooleanWire or StringWire or NumberWire or LiteralWire or LosslessJsonWire or ArrayWire or XmlTextWire or XmlItemsWire => true,
                    ObjectWire obj => obj.Properties.Where(p => p.Presence == Presence.Required).All(p => productiveWires.Contains(p.Wire.WireId)),
                    // a repeated member may have no element and a nillable one may be xsi:nil
                    XmlElementWire element => MembersOf(element).Where(m => m.Presence == Presence.Required && m.Repeated != true && m.Nillable != true).All(m => productiveWires.Contains(m.Wire.WireId)),
                    TokenUnionWire tu => tu.Branches.Any(b => productiveWires.Contains(b.Wire.WireId)),
                    TaggedUnionWire tg => tg.Variants.Any(v => productiveWires.Contains(v.Wire.WireId)),
                    _ => false,
                };
                if (ok)
                {
                    productiveWires.Add(wire.Id);
                    changed = true;
                }
            }
        }
        while (changed);

        for (var i = 0; i < _doc.Wires.Count; i++)
        {
            var wire = _doc.Wires[i];
            if (!productiveWires.Contains(wire.Id))
            {
                Error(TisiliaCodes.NonProductiveRecursion, "SV09", JsonPointer.Append(JsonPointer.Append("/wires", i), "shape"), $"wire '{wire.Id}' cannot describe any finite JSON value", [wire.Id]);
            }
        }

        // Cycles that consume no token: union → variant → ... → union, brand → base → ... (SV09: a cycle that follows references only and
        // never consumes a value).
        DetectNonConsumingCycles();
    }

    private void DetectNonConsumingCycles()
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var model in _doc.Types)
        {
            var targets = model.Shape switch
            {
                UnionShape u => u.Variants.Select(v => v.Use.TypeId).ToList(),
                BrandShape b => [b.Base.TypeId],
                _ => new List<string>(),
            };
            edges[model.Id] = targets;
        }

        foreach (var cycle in FindCycles(edges))
        {
            var index = _doc.Types.ToList().FindIndex(t => t.Id == cycle[0]);
            Error(TisiliaCodes.NonProductiveRecursion, "SV09", JsonPointer.Append(JsonPointer.Append("/types", Math.Max(index, 0)), "shape"), $"types [{string.Join(" → ", cycle)}] form a cycle through unions/brands only; no value is consumed", cycle);
        }

        var wireEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var wire in _doc.Wires)
        {
            wireEdges[wire.Id] = wire.Shape is TokenUnionWire tu ? tu.Branches.Select(b => b.Wire.WireId).ToList() : [];
        }

        foreach (var cycle in FindCycles(wireEdges))
        {
            var index = _doc.Wires.ToList().FindIndex(w => w.Id == cycle[0]);
            Error(TisiliaCodes.NonProductiveRecursion, "SV09", JsonPointer.Append(JsonPointer.Append("/wires", Math.Max(index, 0)), "shape"), $"wires [{string.Join(" → ", cycle)}] form a cycle through token-unions only", cycle);
        }
    }

    private static List<List<string>> FindCycles(Dictionary<string, List<string>> edges)
    {
        var result = new List<List<string>>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 = new, 1 = visiting, 2 = done
        var stack = new List<string>();
        foreach (var start in edges.Keys)
        {
            Visit(start);
        }

        return result;

        void Visit(string node)
        {
            if (state.TryGetValue(node, out var s))
            {
                if (s == 1)
                {
                    var at = stack.IndexOf(node);
                    result.Add(stack.Skip(at).Append(node).ToList());
                }

                return;
            }

            state[node] = 1;
            stack.Add(node);
            if (edges.TryGetValue(node, out var next))
            {
                foreach (var n in next)
                {
                    Visit(n);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }
    }
}
