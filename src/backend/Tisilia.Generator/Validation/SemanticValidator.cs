using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

/// <summary>
/// Semantic validation (SV01–SV54 as far as they apply to a contract document). Runs after structural
/// schema validation and never downgrades an error to a warning. Every rule reports a JSON Pointer.
/// </summary>
public sealed partial class SemanticValidator
{
    private readonly LoadedContract _loaded;
    private readonly ContractDocument _doc;
    private readonly ContractIndex _index;
    private readonly DiagnosticBag _diagnostics;

    private SemanticValidator(LoadedContract loaded, DiagnosticBag diagnostics)
    {
        _loaded = loaded;
        _doc = loaded.Document;
        _index = new ContractIndex(loaded.Document);
        _diagnostics = diagnostics;
    }

    /// <summary>Validates a structurally valid contract. Returns the index when no error was found.</summary>
    public static ContractIndex? Validate(LoadedContract loaded, DiagnosticBag diagnostics, bool verifyHashes = true)
    {
        var validator = new SemanticValidator(loaded, diagnostics);
        validator.Run(verifyHashes);
        return diagnostics.HasErrors ? null : validator._index;
    }

    private void Run(bool verifyHashes)
    {
        CheckFormat();
        CheckIdentifiers();
        if (_diagnostics.HasErrors)
        {
            // Duplicate ids make every later lookup ambiguous; stop at the earliest stage.
            return;
        }

        CheckDocumentation();
        CheckModules();
        CheckBindings();
        CheckTypes();
        CheckWires();
        CheckCodecs();
        CheckEquivalencesAndProjections();
        CheckComparers();
        CheckBinders();
        CheckResultAdapters();
        CheckProfiles();
        CheckOperations();
        CheckUsageGraph();
        CheckProductivity();
        if (verifyHashes)
        {
            CheckHashes();
        }
    }

    private void Error(string code, string rule, string path, string message, IReadOnlyList<string>? ids = null, string? fix = null)
        => _diagnostics.Error(code, rule, path, message, ids, fix);

    // ---------------------------------------------------------------- SV01

    private void CheckFormat()
    {
        if (_doc.Format != TisiliaJson.Formats.Contract || _doc.Version != TisiliaJson.ContractVersion)
        {
            Error(TisiliaCodes.FormatOrVersion, "SV01", "/format", "format/version must be tisilia.contract 0.1; re-export with Tisilia 0.1.0-alpha");
        }

        if (_doc.ApiId.StartsWith(Builtins.Prefix, StringComparison.Ordinal))
        {
            Error(TisiliaCodes.ReservedId, "SV02", "/apiId", "apiId must not use the reserved 'tisilia.' prefix");
        }
    }

    // ---------------------------------------------------------------- SV02

    /// <summary>Every id the contract defines (any registry, scopes, behaviors, parameters, response cases), by its path.</summary>
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    private void CheckIdentifiers()
    {
        var seen = _ids;

        void Claim(string id, string path, string registry)
        {
            if (id.StartsWith(Builtins.Prefix, StringComparison.Ordinal))
            {
                Error(TisiliaCodes.ReservedId, "SV02", path, $"{registry} id '{id}' uses the reserved builtin prefix 'tisilia.'", [id],
                    "builtin identifiers are fixed by tisilia.builtins@0.1 and user registries must not shadow them");
            }

            if (!seen.TryAdd(id, path))
            {
                Error(TisiliaCodes.DuplicateId, "SV02", path, $"id '{id}' is already defined at {seen[id]}; ids are unique across all registries", [id]);
            }
        }

        for (var i = 0; i < _doc.Profiles.Count; i++)
        {
            var p = _doc.Profiles[i];
            var pp = JsonPointer.Append("/profiles", i);
            Claim(p.Id, JsonPointer.Append(pp, "id"), "profile");
            for (var j = 0; j < p.Scopes.Count; j++)
            {
                Claim(p.Scopes[j].Id, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(pp, "scopes"), j), "id"), "scope");
            }

            for (var j = 0; j < p.Behaviors.Count; j++)
            {
                Claim(p.Behaviors[j].Id, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(pp, "behaviors"), j), "id"), "behavior");
            }
        }

        ClaimAll(_doc.Types, "types", t => t.Id, "type");
        ClaimAll(_doc.Wires, "wires", w => w.Id, "wire");
        ClaimAll(_doc.Codecs, "codecs", c => c.Id, "codec");
        ClaimAll(_doc.Bindings, "bindings", b => b.Id, "binding");
        ClaimAll(_doc.Equivalences, "equivalences", e => e.Id, "equivalence");
        ClaimAll(_doc.Projections, "projections", p => p.Id, "projection");
        ClaimAll(_doc.Comparers, "comparers", c => c.Id, "comparer");
        ClaimAll(_doc.Binders, "binders", b => b.Id, "binder");
        ClaimAll(_doc.ResultAdapters, "resultAdapters", r => r.Id, "resultAdapter");
        ClaimAll(_doc.Modules, "modules", m => m.Id, "module");
        for (var i = 0; i < _doc.Operations.Count; i++)
        {
            var op = _doc.Operations[i];
            var op0 = JsonPointer.Append("/operations", i);
            Claim(op.Id, JsonPointer.Append(op0, "id"), "operation");
            for (var j = 0; j < op.Parameters.Count; j++)
            {
                Claim(op.Parameters[j].Id, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(op0, "parameters"), j), "id"), "parameter");
            }

            for (var j = 0; j < op.Responses.Count; j++)
            {
                Claim(op.Responses[j].Id, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(op0, "responses"), j), "id"), "response case");
            }
        }

        void ClaimAll<T>(IReadOnlyList<T> items, string registry, Func<T, string> id, string label)
        {
            for (var i = 0; i < items.Count; i++)
            {
                Claim(id(items[i]), JsonPointer.Append(JsonPointer.Append("/" + registry, i), "id"), label);
            }
        }
    }

    // ---------------------------------------------------------------- documentation (SV02/SV03)

    /// <summary>
    /// Documentation is display text outside the semantic hash, but its targetId is an id reference like any other
    /// (references resolve to the IR): an entry for an id the contract does not define — a renamed operation, a typo — describes
    /// nothing, and two entries for one target leave it open which one is meant.
    /// </summary>
    private void CheckDocumentation()
    {
        var described = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _doc.Documentation.Count; i++)
        {
            var targetId = _doc.Documentation[i].TargetId;
            var path = JsonPointer.Append(JsonPointer.Append("/documentation", i), "targetId");
            if (!_ids.ContainsKey(targetId))
            {
                Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"documentation entry for '{targetId}', which is no id of this contract", [targetId],
                    "document an operation, parameter, response case or type by its id (TisiliaOptions.Documentation keys), or remove the entry");
            }

            if (!described.TryAdd(targetId, path))
            {
                Error(TisiliaCodes.DuplicateId, "SV02", path, $"'{targetId}' is documented twice (also at {described[targetId]})", [targetId]);
            }
        }
    }

    // ---------------------------------------------------------------- reference helpers (SV03)

    private bool RequireType(string id, string path)
    {
        if (_index.Types.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"type '{id}' is not defined in types", [id]);
        return false;
    }

    private bool RequireCodec(string id, string path)
    {
        if (_index.Codecs.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"codec '{id}' is not defined in codecs", [id]);
        return false;
    }

    private Wire? RequireWire(WireRef wireRef, string path)
    {
        if (!_index.Wires.TryGetValue(wireRef.WireId, out var wire))
        {
            Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(path, "wireId"), $"wire '{wireRef.WireId}' is not defined in wires", [wireRef.WireId]);
            return null;
        }

        if (wire.Direction != wireRef.Direction)
        {
            Error(TisiliaCodes.DirectionMismatch, "SV03", JsonPointer.Append(path, "direction"),
                $"wireRef direction '{Enum(wireRef.Direction)}' does not match wire '{wire.Id}' direction '{Enum(wire.Direction)}'", [wire.Id]);
            return null;
        }

        return wire;
    }

    private bool RequireProfile(string id, string path)
    {
        if (_index.Profiles.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"profile '{id}' is not defined in profiles", [id]);
        return false;
    }

    private bool RequireEquivalence(string id, string path)
    {
        if (_index.Equivalences.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"equivalence '{id}' is not defined in equivalences", [id]);
        return false;
    }

    private bool RequireProjection(string id, string path)
    {
        if (_index.Projections.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"projection '{id}' is not defined in projections", [id]);
        return false;
    }

    private bool RequireModule(string id, string path)
    {
        if (_index.Modules.ContainsKey(id))
        {
            return true;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"module '{id}' is not defined in modules", [id]);
        return false;
    }

    /// <summary>Resolves an id to a builtin of one of the given kinds or to a contract binding of one of the given kinds (SV51).</summary>
    private bool RequireBuiltinOrBinding(string id, string path, string what, BuiltinKind[] builtinKinds, BindingKind[] bindingKinds)
    {
        if (Builtins.TryGet(id, out var entry))
        {
            if (Array.IndexOf(builtinKinds, entry.Kind) >= 0)
            {
                return true;
            }

            Error(TisiliaCodes.AuxiliaryBinding, "SV51", path, $"builtin '{id}' is a {entry.Kind} and cannot be used as {what}", [id]);
            return false;
        }

        if (id.StartsWith(Builtins.Prefix, StringComparison.Ordinal))
        {
            Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"'{id}' is not a member of {TisiliaJson.BuiltinSet}; unknown builtin ids are never accepted by prefix", [id]);
            return false;
        }

        if (_index.Bindings.TryGetValue(id, out var binding))
        {
            if (Array.IndexOf(bindingKinds, binding.Kind) >= 0)
            {
                return true;
            }

            Error(TisiliaCodes.AuxiliaryBinding, "SV51", path, $"binding '{id}' has kind '{Enum(binding.Kind)}' but {what} requires one of [{string.Join(", ", bindingKinds.Select(Enum))}]", [id]);
            return false;
        }

        Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"{what} '{id}' resolves neither to a builtin nor to a binding", [id]);
        return false;
    }

    private bool RequireBinding(string id, string path, params BindingKind[] kinds)
    {
        if (!_index.Bindings.TryGetValue(id, out var binding))
        {
            Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"binding '{id}' is not defined in bindings", [id]);
            return false;
        }

        if (kinds.Length > 0 && Array.IndexOf(kinds, binding.Kind) < 0)
        {
            Error(TisiliaCodes.AuxiliaryBinding, "SV51", path, $"binding '{id}' has kind '{Enum(binding.Kind)}' but one of [{string.Join(", ", kinds.Select(Enum))}] is required", [id]);
            return false;
        }

        return true;
    }

    /// <summary>SV23: an implementation must be a builtin of an allowed kind or a module export with the right role and target.</summary>
    private bool RequireImpl(Impl impl, string path, string what, BuiltinKind[] builtinKinds, ExportRole[] roles, ArtifactTarget[] targets)
    {
        switch (impl)
        {
            case BuiltinImpl b:
                if (Builtins.TryGet(b.Id, out var entry))
                {
                    if (Array.IndexOf(builtinKinds, entry.Kind) >= 0)
                    {
                        return true;
                    }

                    Error(TisiliaCodes.ModuleExportMismatch, "SV23", JsonPointer.Append(path, "id"), $"builtin '{b.Id}' ({entry.Kind}) cannot implement {what}", [b.Id]);
                    return false;
                }

                Error(TisiliaCodes.UnresolvedReference, "SV23", JsonPointer.Append(path, "id"), $"'{b.Id}' is not a member of {TisiliaJson.BuiltinSet}", [b.Id]);
                return false;
            case ModuleImpl m:
                if (!_index.Modules.TryGetValue(m.ModuleId, out var module))
                {
                    Error(TisiliaCodes.UnresolvedReference, "SV23", JsonPointer.Append(path, "moduleId"), $"module '{m.ModuleId}' is not defined in modules", [m.ModuleId]);
                    return false;
                }

                var export = module.Exports.FirstOrDefault(e => string.Equals(e.Name, m.ExportName, StringComparison.Ordinal));
                if (export is null)
                {
                    Error(TisiliaCodes.ModuleExportMismatch, "SV23", JsonPointer.Append(path, "exportName"), $"module '{m.ModuleId}' has no export '{m.ExportName}'", [m.ModuleId]);
                    return false;
                }

                if (Array.IndexOf(roles, export.Role) < 0)
                {
                    Error(TisiliaCodes.ModuleExportMismatch, "SV23", JsonPointer.Append(path, "exportName"),
                        $"export '{m.ExportName}' of module '{m.ModuleId}' has role '{Enum(export.Role)}' but {what} requires one of [{string.Join(", ", roles.Select(Enum))}]", [m.ModuleId]);
                    return false;
                }

                if (!export.Targets.Any(t => Array.IndexOf(targets, t) >= 0))
                {
                    Error(TisiliaCodes.ModuleExportMismatch, "SV23", JsonPointer.Append(path, "exportName"),
                        $"export '{m.ExportName}' of module '{m.ModuleId}' targets [{string.Join(", ", export.Targets.Select(Enum))}] but {what} runs on [{string.Join(", ", targets.Select(Enum))}]", [m.ModuleId]);
                    return false;
                }

                if (!module.Artifacts.Any(a => Array.IndexOf(targets, a.Target) >= 0))
                {
                    Error(TisiliaCodes.ModuleArtifact, "SV44", JsonPointer.Append(path, "moduleId"),
                        $"module '{m.ModuleId}' declares no artifact for [{string.Join(", ", targets.Select(Enum))}]", [m.ModuleId]);
                    return false;
                }

                return true;
            default:
                Error(TisiliaCodes.SchemaViolation, "SV23", path, "unknown implementation kind");
                return false;
        }
    }

    private static readonly ArtifactTarget[] DotnetTargets = [ArtifactTarget.Dotnet];
    private static readonly ArtifactTarget[] TsTargets = [ArtifactTarget.Browser, ArtifactTarget.Node];

    private static string Enum<T>(T value) where T : struct, System.Enum
    {
        var member = typeof(T).GetField(value.ToString()!);
        var attr = member?.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute), false)
            .OfType<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>().FirstOrDefault();
        return attr?.Name ?? value.ToString()!;
    }

    // ---------------------------------------------------------------- modules (SV23/SV44)

    private void CheckModules()
    {
        for (var i = 0; i < _doc.Modules.Count; i++)
        {
            var module = _doc.Modules[i];
            var mp = JsonPointer.Append("/modules", i);
            if (module.Abi != TisiliaJson.DraftVersion)
            {
                Error(TisiliaCodes.FormatOrVersion, "SV44", JsonPointer.Append(mp, "abi"), $"module '{module.Id}' must declare abi 0.1", [module.Id]);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < module.Exports.Count; j++)
            {
                if (!names.Add(module.Exports[j].Name))
                {
                    Error(TisiliaCodes.DuplicateId, "SV02", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(mp, "exports"), j), "name"),
                        $"module '{module.Id}' exports '{module.Exports[j].Name}' more than once", [module.Id]);
                }
            }

            var paths = new HashSet<(ArtifactTarget, string)>();
            for (var j = 0; j < module.Artifacts.Count; j++)
            {
                var a = module.Artifacts[j];
                if (!paths.Add((a.Target, a.Path)))
                {
                    Error(TisiliaCodes.DuplicateId, "SV02", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(mp, "artifacts"), j), "path"),
                        $"module '{module.Id}' lists artifact '{a.Path}' for target '{Enum(a.Target)}' more than once", [module.Id]);
                }
            }

            for (var j = 0; j < module.DependencyIds.Count; j++)
            {
                var dep = module.DependencyIds[j];
                if (dep == module.Id)
                {
                    Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(JsonPointer.Append(mp, "dependencyIds"), j), $"module '{module.Id}' depends on itself", [module.Id]);
                }
                else
                {
                    RequireModule(dep, JsonPointer.Append(JsonPointer.Append(mp, "dependencyIds"), j));
                }
            }
        }
    }

    // ---------------------------------------------------------------- bindings (SV23/SV24/SV51)

    private static readonly Dictionary<BindingKind, (BuiltinKind[] Builtins, ExportRole[] Roles, ArtifactTarget[] Targets)> BindingImplRules = new()
    {
        [BindingKind.Converter] = ([BuiltinKind.Binding], [ExportRole.Codec], DotnetTargets),
        [BindingKind.Factory] = ([BuiltinKind.Binding], [ExportRole.Codec], DotnetTargets),
        [BindingKind.Binder] = ([BuiltinKind.Binder], [ExportRole.Binder], DotnetTargets),
        [BindingKind.Result] = ([BuiltinKind.Result], [ExportRole.Result], DotnetTargets),
        [BindingKind.Behavior] = ([BuiltinKind.Behavior], [ExportRole.Behavior], DotnetTargets),
        [BindingKind.DomainRule] = ([BuiltinKind.DomainRule], [ExportRole.DomainRule], [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node]),
        [BindingKind.Normalization] = ([BuiltinKind.Normalization], [ExportRole.Normalization], [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node]),
        [BindingKind.Pipeline] = ([BuiltinKind.Pipeline], [ExportRole.Pipeline], DotnetTargets),
        [BindingKind.Comparer] = ([BuiltinKind.Comparer], [ExportRole.Comparer], [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node]),
        [BindingKind.AuthPolicy] = ([BuiltinKind.AuthPolicy], [ExportRole.AuthPolicy], DotnetTargets),
        [BindingKind.CsrfPolicy] = ([BuiltinKind.CsrfPolicy], [ExportRole.CsrfPolicy], DotnetTargets),
        [BindingKind.Grammar] = ([BuiltinKind.Grammar, BuiltinKind.KeyGrammar], [ExportRole.Grammar], TsTargets),
        [BindingKind.NameMatching] = ([BuiltinKind.NameMatching], [ExportRole.NameMatching], TsTargets),
        [BindingKind.DuplicatePolicy] = ([BuiltinKind.DuplicatePolicy], [ExportRole.DuplicatePolicy], TsTargets),
        [BindingKind.Editor] = ([BuiltinKind.Editor], [ExportRole.Editor], [ArtifactTarget.Browser]),
        [BindingKind.NamingPolicy] = ([BuiltinKind.NamingPolicy], [ExportRole.NamingPolicy], DotnetTargets),
        [BindingKind.Encoder] = ([BuiltinKind.Encoder], [ExportRole.Encoder], DotnetTargets),
        [BindingKind.Resolver] = ([BuiltinKind.Resolver], [ExportRole.Resolver], DotnetTargets),
        [BindingKind.ServerAcceptance] = ([BuiltinKind.ServerAcceptance], [ExportRole.ServerAcceptance], DotnetTargets),
    };

    private void CheckBindings()
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _doc.Bindings.Count; i++)
        {
            var b = _doc.Bindings[i];
            var bp = JsonPointer.Append("/bindings", i);
            var rule = BindingImplRules[b.Kind];
            RequireImpl(b.Implementation, JsonPointer.Append(bp, "implementation"), $"binding kind '{Enum(b.Kind)}'", rule.Builtins, rule.Roles, rule.Targets);
            for (var j = 0; j < b.DependencyIds.Count; j++)
            {
                var dep = b.DependencyIds[j];
                if (dep == b.Id)
                {
                    Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(JsonPointer.Append(bp, "dependencyIds"), j), $"binding '{b.Id}' depends on itself", [b.Id]);
                }
                else
                {
                    RequireBinding(dep, JsonPointer.Append(JsonPointer.Append(bp, "dependencyIds"), j));
                }
            }

            for (var j = 0; j < b.Context.Count; j++)
            {
                if (b.Context[j].Confidential)
                {
                    Error(TisiliaCodes.SecretExposure, "SV24", JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(bp, "context"), j), "confidential"),
                        $"binding '{b.Id}' context '{b.Context[j].Name}' is confidential; secrets never enter the contract", [b.Id]);
                }
            }

            // SV24: the same (kind, implementation, settings, context) claimed twice is an ambiguous scope claim.
            var claimKey = Enum(b.Kind) + "|" + ImplKey(b.Implementation) + "|" + b.SettingsDigest + "|" +
                           string.Join("&", b.Context.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name + "=" + c.Value));
            if (!claims.TryAdd(claimKey, b.Id))
            {
                Error(TisiliaCodes.BindingScopeClaim, "SV24", bp, $"binding '{b.Id}' claims the same scope as '{claims[claimKey]}' (same kind, implementation, settings and context)", [b.Id, claims[claimKey]]);
            }
        }
    }

    private static string ImplKey(Impl impl) => impl switch
    {
        BuiltinImpl b => "builtin:" + b.Id,
        ModuleImpl m => "module:" + m.ModuleId + "#" + m.ExportName,
        _ => "?",
    };

    // ---------------------------------------------------------------- hashes (SV43)

    private void CheckHashes()
    {
        var expected = TisiliaHash.SemanticHash(_loaded.Root);
        if (!string.Equals(expected, _doc.SemanticHash, StringComparison.Ordinal))
        {
            Error(TisiliaCodes.HashMismatch, "SV43", "/semanticHash", $"semanticHash does not match its recomputation (expected {expected})",
                fix: "re-export the contract or recompute the hash with `tisilia hash`; never hand-edit a hashed contract");
        }

        if (_loaded.Root["profiles"] is JsonArray profiles)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] is JsonObject profile)
                {
                    var fingerprint = TisiliaHash.ProfileFingerprint(profile);
                    var declared = profile["fingerprint"]?.GetValue<string>();
                    if (!string.Equals(fingerprint, declared, StringComparison.Ordinal))
                    {
                        Error(TisiliaCodes.HashMismatch, "SV43", JsonPointer.Append(JsonPointer.Append("/profiles", i), "fingerprint"),
                            $"profile fingerprint does not match the recomputation (expected {fingerprint})");
                    }
                }
            }
        }
    }
}
