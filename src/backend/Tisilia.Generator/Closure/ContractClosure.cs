using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Closure;

/// <summary>
/// Collects everything an operation set reaches (types, wires, codecs, bindings, projections,
/// equivalences, profiles, result adapters, binders, modules) to a fixed point and produces the sorted
/// <c>tisilia.closure-record</c> input. Unreached definitions are never included.
/// </summary>
public sealed class ContractClosure
{
    private readonly ContractIndex _index;
    private readonly SortedSet<(RegistryName Registry, string Id)> _refs = new(RegistryRefComparer.Instance);
    private readonly SortedSet<string> _equivalenceIds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _profileIds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _bindingIds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _moduleIds = new(StringComparer.Ordinal);
    private readonly SortedSet<ClosureCapability> _capabilities = [];

    private ContractClosure(ContractIndex index) => _index = index;

    public static ContractClosure Compute(ContractIndex index, IEnumerable<string> operationIds)
    {
        var closure = new ContractClosure(index);
        foreach (var id in operationIds)
        {
            closure.VisitOperation(id);
        }

        closure.CloseModules();
        return closure;
    }

    public IReadOnlySet<(RegistryName Registry, string Id)> RegistryRefs => _refs;
    public IReadOnlySet<string> EquivalenceIds => _equivalenceIds;
    public IReadOnlySet<string> ProfileIds => _profileIds;
    public IReadOnlySet<string> BindingIds => _bindingIds;
    public IReadOnlySet<string> ModuleIds => _moduleIds;
    public IReadOnlySet<ClosureCapability> Capabilities => _capabilities;

    public IEnumerable<string> OperationIds => _refs.Where(r => r.Registry == RegistryName.Operations).Select(r => r.Id);

    /// <summary>Builds the exact hash input. Module artifacts and application artifacts are supplied by the caller (built before/after the contract respectively).</summary>
    public ClosureRecord ToRecord(string semanticHash, IReadOnlyList<IdentifiedArtifact> moduleArtifacts, IReadOnlyList<Artifact> applicationArtifacts, RuntimeMatrix matrix, IReadOnlyList<NameValue> context, Limits limits)
    {
        return new ClosureRecord
        {
            Format = TisiliaJson.Formats.ClosureRecord,
            Version = TisiliaJson.DraftVersion,
            SemanticHash = semanticHash,
            OperationIds = OperationIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Capabilities = _capabilities.OrderBy(c => c.ToString(), StringComparer.Ordinal).ToList(),
            EquivalenceIds = _equivalenceIds.ToList(),
            RegistryRefs = _refs.Select(r => new RegistryRef { Registry = r.Registry, Id = r.Id }).ToList(),
            ProfileFingerprints = _profileIds.Select(id => new ProfileFingerprintEntry { Id = id, Fingerprint = _index.Profiles[id].Fingerprint }).ToList(),
            BindingSettings = _bindingIds.Select(id => new BindingSettingsEntry { Id = id, SettingsDigest = _index.Bindings[id].SettingsDigest }).ToList(),
            ModuleArtifacts = moduleArtifacts
                .Where(a => _moduleIds.Contains(a.ModuleId))
                .OrderBy(a => a.ModuleId, StringComparer.Ordinal).ThenBy(a => a.Artifact.Target).ThenBy(a => a.Artifact.Path, StringComparer.Ordinal)
                .ToList(),
            ApplicationArtifacts = applicationArtifacts.OrderBy(a => a.Target).ThenBy(a => a.Path, StringComparer.Ordinal).ToList(),
            Abi = TisiliaJson.DraftVersion,
            Matrix = matrix,
            Context = context.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
            Limits = limits,
        };
    }

    /// <summary>Module artifacts of every reached module, taken from the contract's own module declarations.</summary>
    public IReadOnlyList<IdentifiedArtifact> DeclaredModuleArtifacts()
        => _moduleIds.SelectMany(id => _index.Modules[id].Artifacts.Select(a => new IdentifiedArtifact { ModuleId = id, Artifact = a })).ToList();

    private void VisitOperation(string id)
    {
        if (!_index.Operations.TryGetValue(id, out var op) || !_refs.Add((RegistryName.Operations, id)))
        {
            return;
        }

        foreach (var p in op.Parameters)
        {
            _capabilities.Add(ClosureCapability.Binder);
            VisitBinder(p.BinderId);
            VisitUse(p.Use, null);
        }

        if (op.RequestBody is JsonRequestBody body)
        {
            _capabilities.Add(ClosureCapability.Request);
            VisitProfile(body.ProfileId);
            VisitUse(body.Use, WireDirection.ServerRead);
        }

        foreach (var r in op.Responses)
        {
            _capabilities.Add(ClosureCapability.Result);
            VisitResultAdapter(r.ResultAdapterId);
            if (r.Hydration == Hydration.BrowserSafe)
            {
                _capabilities.Add(ClosureCapability.Hydration);
            }

            switch (r.Body)
            {
                case JsonResponseBody json:
                    _capabilities.Add(ClosureCapability.Response);
                    VisitProfile(json.ProfileId);
                    VisitUse(json.Use, WireDirection.ServerWrite);
                    break;
                case TextResponseBody text:
                    _capabilities.Add(ClosureCapability.Response);
                    VisitUse(text.Use, null);
                    break;
                case NoResponseBody:
                case BinaryResponseBody:
                    break; // no JSON codec/profile dependency; HTTP transfer is not codec evidence
                default:
                    throw new InvalidOperationException("Unknown response body kind");
            }
        }

        foreach (var b in op.PipelineBindingIds)
        {
            VisitBinding(b);
        }

        VisitBinding(op.Security.AuthPolicyId);
        VisitBinding(op.Security.CsrfPolicyId);
    }

    private void VisitUse(TypeUse use, WireDirection? direction)
    {
        VisitType(use.TypeId);
        VisitCodec(use.CodecId, direction);
    }

    private void VisitType(string id)
    {
        if (!_index.Types.TryGetValue(id, out var model) || !_refs.Add((RegistryName.Types, id)))
        {
            return;
        }

        switch (model.Shape)
        {
            case ObjectShape obj:
                foreach (var p in obj.Properties)
                {
                    VisitUse(p.Use, null);
                }

                if (obj.Extension is CaptureExtension capture)
                {
                    VisitUse(capture.Value, null);
                }

                break;
            case ArrayShape arr:
                VisitUse(arr.Element, null);
                break;
            case MapShape map:
                VisitUse(map.Key, null);
                VisitUse(map.Value, null);
                VisitComparer(map.ComparerId);
                break;
            case BrandShape brand:
                VisitUse(brand.Base, null);
                break;
            case UnionShape union:
                foreach (var v in union.Variants)
                {
                    VisitUse(v.Use, null);
                }

                break;
        }
    }

    private void VisitCodec(string id, WireDirection? direction)
    {
        if (!_index.Codecs.TryGetValue(id, out var codec))
        {
            return;
        }

        var added = _refs.Add((RegistryName.Codecs, id));
        VisitBinding(codec.BindingId);
        VisitImpl(codec.ValidateDomain);
        foreach (var profileId in codec.ProfileIds)
        {
            VisitProfile(profileId);
        }

        var caps = codec.Capabilities;
        if (caps.Request is { } req && direction is null or WireDirection.ServerRead)
        {
            VisitWire(req.Wire.WireId);
            VisitImpl(req.Implementation);
            VisitEquivalence(req.EquivalenceId);
            VisitBinding(req.DomainRuleId);
        }

        if (caps.Response is { } res && direction is null or WireDirection.ServerWrite)
        {
            VisitWire(res.Wire.WireId);
            VisitImpl(res.Implementation);
            VisitEquivalence(res.EquivalenceId);
            VisitBinding(res.DomainRuleId);
        }

        if (caps.RequestKey is { } rk)
        {
            _capabilities.Add(ClosureCapability.RequestKey);
            VisitImpl(rk.Implementation);
            VisitEquivalence(rk.EquivalenceId);
            VisitBinding(rk.GrammarId);
        }

        if (caps.ResponseKey is { } sk)
        {
            _capabilities.Add(ClosureCapability.ResponseKey);
            VisitImpl(sk.Implementation);
            VisitEquivalence(sk.EquivalenceId);
            VisitBinding(sk.GrammarId);
        }

        if (caps.RequestInput is { } input)
        {
            _capabilities.Add(ClosureCapability.RequestInput);
            VisitImpl(input.Implementation);
            VisitBinding(input.EditorId);
        }

        if (added)
        {
            foreach (var dep in codec.Dependencies)
            {
                VisitCodec(dep, null);
            }
        }
    }

    private void VisitWire(string id)
    {
        if (!_index.Wires.TryGetValue(id, out var wire) || !_refs.Add((RegistryName.Wires, id)))
        {
            return;
        }

        switch (wire.Shape)
        {
            case StringWire s:
                VisitBinding(s.GrammarId);
                break;
            case NumberWire n:
                VisitBinding(n.GrammarId);
                break;
            case ArrayWire a:
                VisitWire(a.Element.WireId);
                break;
            case ObjectWire o:
                VisitBinding(o.DuplicatePolicyId);
                VisitBinding(o.NameMatchingId);
                foreach (var p in o.Properties)
                {
                    VisitWire(p.Wire.WireId);
                }

                if (o.Additional is CaptureAdditional c)
                {
                    VisitWire(c.Wire.WireId);
                }

                break;
            case TokenUnionWire tu:
                foreach (var b in tu.Branches)
                {
                    VisitWire(b.Wire.WireId);
                }

                break;
            case TaggedUnionWire tg:
                foreach (var v in tg.Variants)
                {
                    VisitWire(v.Wire.WireId);
                }

                break;
        }
    }

    private void VisitEquivalence(string id)
    {
        if (!_index.Equivalences.TryGetValue(id, out var eq) || !_refs.Add((RegistryName.Equivalences, id)))
        {
            return;
        }

        _equivalenceIds.Add(id);
        if (eq.Scope == EquivalenceScope.RoundTrip)
        {
            _capabilities.Add(ClosureCapability.RoundTrip);
        }

        VisitType(eq.DomainTypeId);
        VisitBinding(eq.DomainRuleId);
        VisitBinding(eq.NormalizationId);
        VisitImpl(eq.DotnetOracle);
        VisitImpl(eq.TypescriptOracle);
        if (eq.ProjectionId is not null)
        {
            VisitProjection(eq.ProjectionId);
        }
    }

    private void VisitProjection(string id)
    {
        if (!_index.Projections.TryGetValue(id, out var pr) || !_refs.Add((RegistryName.Projections, id)))
        {
            return;
        }

        VisitType(pr.SourceTypeId);
        VisitType(pr.TargetTypeId);
        VisitImpl(pr.DotnetImplementation);
        VisitImpl(pr.TypescriptImplementation);
    }

    private void VisitComparer(string id)
    {
        if (!_index.Comparers.TryGetValue(id, out var c) || !_refs.Add((RegistryName.Comparers, id)))
        {
            return;
        }

        VisitBinding(c.BindingId);
        VisitEquivalence(c.EquivalenceId);
    }

    private void VisitBinder(string id)
    {
        if (!_index.Binders.TryGetValue(id, out var b) || !_refs.Add((RegistryName.Binders, id)))
        {
            return;
        }

        VisitBinding(b.BindingId);
        VisitType(b.TypeId);
        VisitImpl(b.Implementation);
        VisitBinding(b.GrammarId);
        VisitBinding(b.NormalizationId);
        VisitBinding(b.ServerAcceptanceId);
    }

    private void VisitResultAdapter(string id)
    {
        if (!_index.ResultAdapters.TryGetValue(id, out var r) || !_refs.Add((RegistryName.ResultAdapters, id)))
        {
            return;
        }

        VisitBinding(r.BindingId);
        VisitImpl(r.Implementation);
        foreach (var p in r.ProfileIds)
        {
            VisitProfile(p);
        }
    }

    private void VisitProfile(string id)
    {
        if (!_index.Profiles.TryGetValue(id, out var p) || !_refs.Add((RegistryName.Profiles, id)))
        {
            return;
        }

        _profileIds.Add(id);
        VisitBinding(p.Options.PropertyNamingPolicyId);
        VisitBinding(p.Options.DictionaryKeyPolicyId);
        VisitBinding(p.Options.EncoderId);
        foreach (var r in p.Options.ResolverIds)
        {
            VisitBinding(r);
        }

        foreach (var c in p.Options.ConverterBindingIds)
        {
            VisitBinding(c);
        }

        foreach (var b in p.Behaviors)
        {
            VisitImpl(b.Implementation);
            if (b.ProjectionId is not null)
            {
                VisitProjection(b.ProjectionId);
            }
        }

        foreach (var s in p.Scopes)
        {
            if (s.ReadCodecId is not null)
            {
                VisitCodec(s.ReadCodecId, WireDirection.ServerRead);
            }

            if (s.WriteCodecId is not null)
            {
                VisitCodec(s.WriteCodecId, WireDirection.ServerWrite);
            }
        }
    }

    private void VisitBinding(string id)
    {
        // builtin ids are fixed by the builtin set already bound to the contract; only contract bindings enter the closure
        if (!_index.Bindings.TryGetValue(id, out var b) || !_refs.Add((RegistryName.Bindings, id)))
        {
            return;
        }

        _bindingIds.Add(id);
        VisitImpl(b.Implementation);
        foreach (var dep in b.DependencyIds)
        {
            VisitBinding(dep);
        }
    }

    private void VisitImpl(Impl impl)
    {
        if (impl is ModuleImpl m)
        {
            VisitModule(m.ModuleId);
        }
    }

    private void VisitModule(string id)
    {
        if (!_index.Modules.TryGetValue(id, out var module) || !_refs.Add((RegistryName.Modules, id)))
        {
            return;
        }

        _moduleIds.Add(id);
        foreach (var dep in module.DependencyIds)
        {
            VisitModule(dep);
        }
    }

    private void CloseModules()
    {
        // module dependency cycles are finite sets; already handled by the visited set
    }

    private sealed class RegistryRefComparer : IComparer<(RegistryName Registry, string Id)>
    {
        public static RegistryRefComparer Instance { get; } = new();

        public int Compare((RegistryName Registry, string Id) x, (RegistryName Registry, string Id) y)
        {
            var c = string.CompareOrdinal(RegistryText(x.Registry), RegistryText(y.Registry));
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        }

        private static string RegistryText(RegistryName r) => r switch
        {
            RegistryName.ResultAdapters => "resultAdapters",
            _ => r.ToString().ToLowerInvariant(),
        };
    }
}
