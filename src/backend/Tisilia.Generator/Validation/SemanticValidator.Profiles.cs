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

            // ReferenceHandling=Preserve is representable but the runtime does not support it.
            if (o.ReferenceHandling == ReferenceHandling.Preserve)
            {
                Error(TisiliaCodes.ReferencePreserve, "SV20", JsonPointer.Append(op, "referenceHandling"), $"profile '{profile.Id}': ReferenceHandler.Preserve ($id/$ref graphs) is not supported by the runtime", [profile.Id],
                    "use a profile without reference preservation for operations exported to Tisilia");
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

            if (op.RequestBody is JsonRequestBody body)
            {
                Walk(body.Use, Usage.Request, JsonPointer.Append(JsonPointer.Append(opPath, "requestBody"), "use"), visited, $"operation '{op.Id}' request body");
            }

            for (var j = 0; j < op.Responses.Count; j++)
            {
                var r = op.Responses[j];
                var rp = JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(opPath, "responses"), j), "body");
                switch (r.Body)
                {
                    case JsonResponseBody json:
                        Walk(json.Use, Usage.Response, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
                        break;
                    case TextResponseBody text:
                        Walk(text.Use, Usage.Domain, JsonPointer.Append(rp, "use"), visited, $"operation '{op.Id}' case '{r.Id}'");
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

        if (cap is not null)
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
                    NullWire or BooleanWire or StringWire or NumberWire or LiteralWire or LosslessJsonWire or ArrayWire => true,
                    ObjectWire obj => obj.Properties.Where(p => p.Presence == Presence.Required).All(p => productiveWires.Contains(p.Wire.WireId)),
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
