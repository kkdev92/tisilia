using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Diff;

public sealed record DiffEntry(string Kind, string Id, string Message);

public sealed record DiffResult(string OldHash, string NewHash, IReadOnlyList<DiffEntry> Breaking, IReadOnlyList<DiffEntry> Compatible, IReadOnlyList<DiffEntry> ReviewRequired);

/// <summary>
/// The per-direction compatibility diff. Unknown changes are <c>review-required</c>, never silently compatible.
/// Only structural facts are compared; equivalence of arbitrary code is never inferred.
/// </summary>
public static class ContractDiff
{
    public static DiffResult? Compare(string oldPath, string newPath, DiagnosticBag bag)
    {
        var oldLoaded = ContractLoader.LoadFile(oldPath, bag);
        var newLoaded = ContractLoader.LoadFile(newPath, bag);
        if (oldLoaded is null || newLoaded is null)
        {
            return null;
        }

        var oldIndex = SemanticValidator.Validate(oldLoaded, bag);
        var newIndex = SemanticValidator.Validate(newLoaded, bag);
        if (oldIndex is null || newIndex is null)
        {
            return null;
        }

        var breaking = new List<DiffEntry>();
        var compatible = new List<DiffEntry>();
        var review = new List<DiffEntry>();

        foreach (var (id, oldOp) in oldIndex.Operations)
        {
            if (!newIndex.Operations.TryGetValue(id, out var newOp))
            {
                breaking.Add(new DiffEntry("operation-removed", id, "operation removed"));
                continue;
            }

            if (oldOp.Method != newOp.Method || oldOp.Route != newOp.Route || System.Text.Json.JsonSerializer.Serialize(oldOp.RoutePlan, TisiliaJson.Options) != System.Text.Json.JsonSerializer.Serialize(newOp.RoutePlan, TisiliaJson.Options))
            {
                breaking.Add(new DiffEntry("route-changed", id, $"{oldOp.Method} {oldOp.Route} → {newOp.Method} {newOp.Route}"));
            }

            CompareRequest(id, oldIndex, newIndex, oldOp, newOp, breaking, compatible, review);
            CompareResponses(id, oldIndex, newIndex, oldOp, newOp, breaking, compatible, review);
        }

        foreach (var id in newIndex.Operations.Keys.Except(oldIndex.Operations.Keys, StringComparer.Ordinal))
        {
            compatible.Add(new DiffEntry("operation-added", id, "operation added"));
        }

        foreach (var (id, oldProfile) in oldIndex.Profiles)
        {
            if (newIndex.Profiles.TryGetValue(id, out var newProfile) && oldProfile.Fingerprint != newProfile.Fingerprint)
            {
                review.Add(new DiffEntry("profile-changed", id, "serializer profile changed; wire/HTTP cases may differ"));
            }
        }

        foreach (var (id, oldBinding) in oldIndex.Bindings)
        {
            if (newIndex.Bindings.TryGetValue(id, out var newBinding) && (oldBinding.SettingsDigest != newBinding.SettingsDigest || !ImplEquals(oldBinding.Implementation, newBinding.Implementation)))
            {
                review.Add(new DiffEntry("binding-changed", id, "converter/normalization/comparer binding changed; meaning may differ even if the TypeScript type is unchanged"));
            }
        }

        foreach (var (id, oldModule) in oldIndex.Modules)
        {
            if (newIndex.Modules.TryGetValue(id, out var newModule) && !oldModule.Artifacts.Select(a => a.Digest).SequenceEqual(newModule.Artifacts.Select(a => a.Digest)))
            {
                review.Add(new DiffEntry("module-changed", id, "module artifacts changed; evidence must be re-evaluated"));
            }
        }

        return new DiffResult(oldLoaded.Document.SemanticHash, newLoaded.Document.SemanticHash, breaking, compatible, review);
    }

    private static bool ImplEquals(Impl a, Impl b) => (a, b) switch
    {
        (BuiltinImpl x, BuiltinImpl y) => x.Id == y.Id,
        (ModuleImpl x, ModuleImpl y) => x.ModuleId == y.ModuleId && x.ExportName == y.ExportName,
        _ => false,
    };

    private static void CompareRequest(string opId, ContractIndex oldIndex, ContractIndex newIndex, Operation oldOp, Operation newOp, List<DiffEntry> breaking, List<DiffEntry> compatible, List<DiffEntry> review)
    {
        foreach (var p in oldOp.Parameters)
        {
            var np = newOp.Parameters.FirstOrDefault(x => x.Name == p.Name && x.Location == p.Location);
            if (np is null)
            {
                compatible.Add(new DiffEntry("parameter-removed", opId, $"parameter '{p.Name}' removed (clients may still send it only if the server ignores it — review)"));
                review.Add(new DiffEntry("parameter-removed", opId, $"parameter '{p.Name}' removed"));
                continue;
            }

            if (p.Presence == Presence.Optional && np.Presence == Presence.Required)
            {
                breaking.Add(new DiffEntry("parameter-required", opId, $"parameter '{p.Name}' became required"));
            }

            if (!TypeShapeEquals(oldIndex, newIndex, p.Use, np.Use, WireDirection.ServerRead, []))
            {
                breaking.Add(new DiffEntry("parameter-type-changed", opId, $"parameter '{p.Name}' accepted value range changed"));
            }
        }

        foreach (var np in newOp.Parameters.Where(x => !oldOp.Parameters.Any(p => p.Name == x.Name && p.Location == x.Location)))
        {
            (np.Presence == Presence.Required ? breaking : compatible).Add(new DiffEntry("parameter-added", opId, $"parameter '{np.Name}' added ({(np.Presence == Presence.Required ? "required" : "optional")})"));
        }

        switch (oldOp.RequestBody, newOp.RequestBody)
        {
            case (NoRequestBody, JsonRequestBody nb):
                (nb.Presence == Presence.Required ? breaking : compatible).Add(new DiffEntry("body-added", opId, "request body added"));
                break;
            case (JsonRequestBody, NoRequestBody):
                review.Add(new DiffEntry("body-removed", opId, "request body removed"));
                break;
            case (JsonRequestBody ob, JsonRequestBody nb):
                if (!TypeShapeEquals(oldIndex, newIndex, ob.Use, nb.Use, WireDirection.ServerRead, []))
                {
                    breaking.Add(new DiffEntry("body-changed", opId, "request body accepted shape changed (required field added or value range narrowed)"));
                }

                break;
        }
    }

    private static void CompareResponses(string opId, ContractIndex oldIndex, ContractIndex newIndex, Operation oldOp, Operation newOp, List<DiffEntry> breaking, List<DiffEntry> compatible, List<DiffEntry> review)
    {
        foreach (var r in oldOp.Responses)
        {
            var nr = newOp.Responses.FirstOrDefault(x => x.Id == r.Id);
            if (nr is null)
            {
                breaking.Add(new DiffEntry("response-removed", opId, $"response case '{r.Id}' removed"));
                continue;
            }

            if (r.Status != nr.Status || r.Body.Kind != nr.Body.Kind)
            {
                breaking.Add(new DiffEntry("response-changed", opId, $"response case '{r.Id}' status/body kind changed"));
                continue;
            }

            if (r.Body is JsonResponseBody ob && nr.Body is JsonResponseBody nb && !TypeShapeEquals(oldIndex, newIndex, ob.Use, nb.Use, WireDirection.ServerWrite, []))
            {
                breaking.Add(new DiffEntry("response-changed", opId, $"response case '{r.Id}' type/meaning changed (field removed, nullability or type changed)"));
            }

            if (r.Hydration != nr.Hydration)
            {
                review.Add(new DiffEntry("hydration-changed", opId, $"response case '{r.Id}' hydration changed"));
            }
            if (r.Body is BinaryResponseBody binary && nr.Body is BinaryResponseBody nextBinary && binary.MediaType != nextBinary.MediaType)
            {
                breaking.Add(new DiffEntry("response-changed", opId, $"binary response case '{r.Id}' media type changed"));
            }
        }

        foreach (var nr in newOp.Responses.Where(x => !oldOp.Responses.Any(r => r.Id == x.Id)))
        {
            review.Add(new DiffEntry("response-added", opId, $"response case '{nr.Id}' added; old clients classify it as unexpected-response"));
        }
    }

    /// <summary>Structural comparison of two type uses across contracts. Direction decides what counts as compatible; anything unclear is a difference.</summary>
    private static bool TypeShapeEquals(ContractIndex oldIndex, ContractIndex newIndex, TypeUse a, TypeUse b, WireDirection direction, HashSet<(string, string)> visited)
    {
        if (a.SemanticNullable != b.SemanticNullable)
        {
            return false;
        }

        if (!visited.Add((a.TypeId, b.TypeId)))
        {
            return true;
        }

        var ta = oldIndex.Types[a.TypeId];
        var tb = newIndex.Types[b.TypeId];
        var ca = oldIndex.Codecs[a.CodecId];
        var cb = newIndex.Codecs[b.CodecId];
        if (ca.Origin != cb.Origin || ca.BindingId != cb.BindingId)
        {
            return false;
        }

        switch (ta.Shape, tb.Shape)
        {
            case (PrimitiveShape pa, PrimitiveShape pb):
                return pa.PrimitiveId == pb.PrimitiveId;
            case (EnumShape ea, EnumShape eb):
                return ea.UnderlyingPrimitiveId == eb.UnderlyingPrimitiveId && ea.Flags == eb.Flags && ea.AllowUndefinedInteger == eb.AllowUndefinedInteger
                       && ea.Members.All(m => eb.Members.Any(n => n.Name == m.Name && n.Value == m.Value && n.SerializedName == m.SerializedName))
                       && (ea.AllowUndefinedInteger || eb.Members.All(n => ea.Members.Any(m => m.Name == n.Name)));
            case (ArrayShape aa, ArrayShape ab):
                return TypeShapeEquals(oldIndex, newIndex, aa.Element, ab.Element, direction, visited);
            case (MapShape ma, MapShape mb):
                return TypeShapeEquals(oldIndex, newIndex, ma.Key, mb.Key, direction, visited) && TypeShapeEquals(oldIndex, newIndex, ma.Value, mb.Value, direction, visited);
            case (BrandShape ba, BrandShape bb):
                return ba.BrandId == bb.BrandId && TypeShapeEquals(oldIndex, newIndex, ba.Base, bb.Base, direction, visited);
            case (UnionShape ua, UnionShape ub):
                return ua.Variants.Count == ub.Variants.Count && ua.Variants.All(v => ub.Variants.Any(w => w.Tag == v.Tag && TypeShapeEquals(oldIndex, newIndex, v.Use, w.Use, direction, visited)));
            case (ObjectShape oa, ObjectShape ob):
            {
                foreach (var pa in oa.Properties)
                {
                    var pb = ob.Properties.FirstOrDefault(p => p.Name == pa.Name);
                    if (pb is null)
                    {
                        if (direction == WireDirection.ServerWrite)
                        {
                            return false; // response field removed
                        }

                        continue; // request field no longer read: compatible for clients that still send it (server ignores/rejects — reviewed elsewhere)
                    }

                    if (!TypeShapeEquals(oldIndex, newIndex, pa.Use, pb.Use, direction, visited))
                    {
                        return false;
                    }

                    if (direction == WireDirection.ServerWrite && pa.Presence == Presence.Required && pb.Presence == Presence.Optional)
                    {
                        return false; // response field may now be missing
                    }

                    if (direction == WireDirection.ServerRead && pa.Presence == Presence.Optional && pb.Presence == Presence.Required)
                    {
                        return false; // request field became required
                    }
                }

                foreach (var pb in ob.Properties.Where(p => !oa.Properties.Any(x => x.Name == p.Name)))
                {
                    if (direction == WireDirection.ServerRead && pb.Presence == Presence.Required)
                    {
                        return false; // new required request field
                    }
                }

                return (oa.Extension is CaptureExtension) == (ob.Extension is CaptureExtension);
            }

            default:
                return false;
        }
    }
}
