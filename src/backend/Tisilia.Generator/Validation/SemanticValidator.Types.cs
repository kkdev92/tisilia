using System.Globalization;
using System.Numerics;
using Tisilia.Contract;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.Generator.Validation;

public sealed partial class SemanticValidator
{
    // ---------------------------------------------------------------- types (SV03/SV04/SV08/SV10/SV49)

    private void CheckTypes()
    {
        for (var i = 0; i < _doc.Types.Count; i++)
        {
            var model = _doc.Types[i];
            var tp = JsonPointer.Append("/types", i);
            var sp = JsonPointer.Append(tp, "shape");
            switch (model.Shape)
            {
                case PrimitiveShape prim:
                    if (!Builtins.Is(prim.PrimitiveId, BuiltinKind.Scalar))
                    {
                        Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(sp, "primitiveId"),
                            $"type '{model.Id}': primitiveId '{prim.PrimitiveId}' is not a builtin scalar of {TisiliaJson.BuiltinSet}", [model.Id]);
                    }

                    break;
                case EnumShape en:
                    CheckEnum(model, en, sp);
                    break;
                case ObjectShape obj:
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (var j = 0; j < obj.Properties.Count; j++)
                    {
                        var prop = obj.Properties[j];
                        var pp = JsonPointer.Append(JsonPointer.Append(sp, "properties"), j);
                        if (!names.Add(prop.Name))
                        {
                            Error(TisiliaCodes.DuplicatePropertyName, "SV10", JsonPointer.Append(pp, "name"), $"type '{model.Id}' declares property '{prop.Name}' twice", [model.Id]);
                        }

                        CheckTypeUse(prop.Use, JsonPointer.Append(pp, "use"));
                    }

                    if (obj.Extension is CaptureExtension capture)
                    {
                        CheckTypeUse(capture.Value, JsonPointer.Append(JsonPointer.Append(sp, "extension"), "value"));
                        if (capture.Collision != "reject")
                        {
                            Error(TisiliaCodes.ExtensionDataMismatch, "SV11", JsonPointer.Append(JsonPointer.Append(sp, "extension"), "collision"), "extension collision policy must be 'reject'");
                        }
                    }

                    break;
                case ArrayShape arr:
                    CheckTypeUse(arr.Element, JsonPointer.Append(sp, "element"));
                    break;
                case MapShape map:
                    CheckTypeUse(map.Key, JsonPointer.Append(sp, "key"));
                    CheckTypeUse(map.Value, JsonPointer.Append(sp, "value"));
                    if (map.Key.SemanticNullable)
                    {
                        Error(TisiliaCodes.MapKeyRule, "SV08", JsonPointer.Append(JsonPointer.Append(sp, "key"), "semanticNullable"), $"type '{model.Id}': map keys are never nullable", [model.Id]);
                    }

                    if (!_index.Comparers.ContainsKey(map.ComparerId))
                    {
                        Error(TisiliaCodes.UnresolvedReference, "SV08", JsonPointer.Append(sp, "comparerId"), $"type '{model.Id}': comparer '{map.ComparerId}' is not defined in comparers", [model.Id, map.ComparerId]);
                    }

                    break;
                case BrandShape brand:
                    CheckTypeUse(brand.Base, JsonPointer.Append(sp, "base"));
                    break;
                case UnionShape union:
                    var tags = new HashSet<string>(StringComparer.Ordinal);
                    for (var j = 0; j < union.Variants.Count; j++)
                    {
                        var v = union.Variants[j];
                        var vp = JsonPointer.Append(JsonPointer.Append(sp, "variants"), j);
                        if (!tags.Add(v.Tag))
                        {
                            Error(TisiliaCodes.DuplicatePropertyName, "SV10", JsonPointer.Append(vp, "tag"), $"type '{model.Id}' declares union tag '{v.Tag}' twice", [model.Id]);
                        }

                        CheckTypeUse(v.Use, JsonPointer.Append(vp, "use"));
                    }

                    break;
            }
        }
    }

    private void CheckEnum(Model model, EnumShape en, string sp)
    {
        if (!Builtins.TryGet(en.UnderlyingPrimitiveId, out var entry) || entry.Kind != BuiltinKind.Scalar || !Builtins.TryGetIntegerRange(entry.Name, out var min, out var max))
        {
            Error(TisiliaCodes.EnumInvalid, "SV49", JsonPointer.Append(sp, "underlyingPrimitiveId"),
                $"type '{model.Id}': enum underlying primitive '{en.UnderlyingPrimitiveId}' must be one of the builtin integer scalars", [model.Id]);
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var serialized = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < en.Members.Count; j++)
        {
            var m = en.Members[j];
            var mp = JsonPointer.Append(JsonPointer.Append(sp, "members"), j);
            if (!names.Add(m.Name))
            {
                Error(TisiliaCodes.EnumInvalid, "SV49", JsonPointer.Append(mp, "name"), $"type '{model.Id}': enum member name '{m.Name}' is not unique", [model.Id]);
            }

            if (m.SerializedName is not null && !serialized.Add(m.SerializedName))
            {
                Error(TisiliaCodes.EnumInvalid, "SV49", JsonPointer.Append(mp, "serializedName"), $"type '{model.Id}': serialized name '{m.SerializedName}' is not unique", [model.Id]);
            }

            if (!BigInteger.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                Error(TisiliaCodes.EnumInvalid, "SV49", JsonPointer.Append(mp, "value"), $"type '{model.Id}': enum member '{m.Name}' value '{m.Value}' is outside {entry.Name} [{min}, {max}]", [model.Id]);
            }
        }
    }

    /// <summary>SV03 + SV04 for one type usage position.</summary>
    private bool CheckTypeUse(TypeUse use, string path)
    {
        var ok = RequireType(use.TypeId, JsonPointer.Append(path, "typeId"));
        ok &= RequireCodec(use.CodecId, JsonPointer.Append(path, "codecId"));
        if (ok && _index.Codecs[use.CodecId].TypeId != use.TypeId)
        {
            Error(TisiliaCodes.CodecTypeMismatch, "SV04", JsonPointer.Append(path, "codecId"),
                $"codec '{use.CodecId}' is declared for type '{_index.Codecs[use.CodecId].TypeId}' but is used for type '{use.TypeId}'", [use.CodecId, use.TypeId]);
            return false;
        }

        return ok;
    }

    // ---------------------------------------------------------------- wires (SV03/SV10/SV12/SV13/SV50)

    private void CheckWires()
    {
        for (var i = 0; i < _doc.Wires.Count; i++)
        {
            var wire = _doc.Wires[i];
            var wp = JsonPointer.Append("/wires", i);
            var sp = JsonPointer.Append(wp, "shape");
            switch (wire.Shape)
            {
                case LiteralWire lit:
                    if (lit.Value is JsonArrayValue or JsonObjectValue)
                    {
                        Error(TisiliaCodes.LiteralInvalid, "SV50", JsonPointer.Append(sp, "value"), $"wire '{wire.Id}': literal values must be scalars", [wire.Id]);
                    }

                    break;
                case StringWire str:
                    RequireBuiltinOrBinding(str.GrammarId, JsonPointer.Append(sp, "grammarId"), "string grammar", [BuiltinKind.Grammar, BuiltinKind.KeyGrammar], [BindingKind.Grammar]);
                    if (str.MinUtf16Length is { } minLen && str.MaxUtf16Length is { } maxLen && minLen > maxLen)
                    {
                        Error(TisiliaCodes.StringLengthInvalid, "SV12", JsonPointer.Append(sp, "maxUtf16Length"), $"wire '{wire.Id}': minUtf16Length exceeds maxUtf16Length", [wire.Id]);
                    }

                    break;
                case NumberWire num:
                    RequireBuiltinOrBinding(num.GrammarId, JsonPointer.Append(sp, "grammarId"), "number grammar", [BuiltinKind.Grammar], [BindingKind.Grammar]);
                    break;
                case ArrayWire arr:
                    RequireWireInDirection(arr.Element, wire, JsonPointer.Append(sp, "element"));
                    break;
                case ObjectWire obj:
                    CheckObjectWire(wire, obj, sp);
                    break;
                case TokenUnionWire tu:
                    CheckTokenUnion(wire, tu, sp);
                    break;
                case TaggedUnionWire tg:
                    CheckTaggedUnion(wire, tg, sp);
                    break;
                case LosslessJsonWire lj:
                    if (lj.GrammarId != Builtins.JsonRfc8259)
                    {
                        Error(TisiliaCodes.UnresolvedReference, "SV03", JsonPointer.Append(sp, "grammarId"), $"wire '{wire.Id}': lossless-json grammar must be {Builtins.JsonRfc8259}", [wire.Id]);
                    }

                    break;
            }
        }
    }

    private Wire? RequireWireInDirection(WireRef wireRef, Wire parent, string path)
    {
        var child = RequireWire(wireRef, path);
        if (child is not null && wireRef.Direction != parent.Direction)
        {
            Error(TisiliaCodes.DirectionMismatch, "SV03", JsonPointer.Append(path, "direction"), $"wire '{parent.Id}' ({Enum(parent.Direction)}) references '{child.Id}' in direction {Enum(wireRef.Direction)}", [parent.Id, child.Id]);
            return null;
        }

        return child;
    }

    private void CheckObjectWire(Wire wire, ObjectWire obj, string sp)
    {
        var nameMatchingOk = RequireBuiltinOrBinding(obj.NameMatchingId, JsonPointer.Append(sp, "nameMatchingId"), "name matching", [BuiltinKind.NameMatching], [BindingKind.NameMatching]);
        RequireBuiltinOrBinding(obj.DuplicatePolicyId, JsonPointer.Append(sp, "duplicatePolicyId"), "duplicate policy", [BuiltinKind.DuplicatePolicy], [BindingKind.DuplicatePolicy]);
        var comparer = nameMatchingOk && obj.NameMatchingId == Builtins.NamesOrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var names = new HashSet<string>(comparer);
        for (var j = 0; j < obj.Properties.Count; j++)
        {
            var prop = obj.Properties[j];
            var pp = JsonPointer.Append(JsonPointer.Append(sp, "properties"), j);
            if (!names.Add(prop.Name))
            {
                Error(TisiliaCodes.DuplicatePropertyName, "SV10", JsonPointer.Append(pp, "name"), $"wire '{wire.Id}': property '{prop.Name}' collides under the wire's name matching rule", [wire.Id]);
            }

            RequireWireInDirection(prop.Wire, wire, JsonPointer.Append(pp, "wire"));
        }

        if (obj.Additional is CaptureAdditional capture)
        {
            RequireWireInDirection(capture.Wire, wire, JsonPointer.Append(JsonPointer.Append(sp, "additional"), "wire"));
        }
    }

    private void CheckTokenUnion(Wire wire, TokenUnionWire tu, string sp)
    {
        var tokens = new HashSet<JsonToken>();
        for (var j = 0; j < tu.Branches.Count; j++)
        {
            var branch = tu.Branches[j];
            var bp = JsonPointer.Append(JsonPointer.Append(sp, "branches"), j);
            if (!tokens.Add(branch.Token))
            {
                Error(TisiliaCodes.TokenUnionInvalid, "SV12", JsonPointer.Append(bp, "token"), $"wire '{wire.Id}': token '{Enum(branch.Token)}' appears in more than one branch; branches are never tried in order", [wire.Id]);
            }

            var child = RequireWireInDirection(branch.Wire, wire, JsonPointer.Append(bp, "wire"));
            if (child is null)
            {
                continue;
            }

            var root = RootToken(child, []);
            if (root is null)
            {
                Error(TisiliaCodes.TokenUnionInvalid, "SV12", JsonPointer.Append(bp, "wire"), $"wire '{wire.Id}': branch wire '{child.Id}' does not consume a single JSON token kind (unions and lossless-json cannot be branches)", [wire.Id, child.Id]);
            }
            else if (root != branch.Token)
            {
                Error(TisiliaCodes.TokenUnionInvalid, "SV12", JsonPointer.Append(bp, "token"), $"wire '{wire.Id}': branch declares token '{Enum(branch.Token)}' but wire '{child.Id}' consumes '{Enum(root.Value)}'", [wire.Id, child.Id]);
            }
        }
    }

    /// <summary>The single JSON token kind a wire consumes at its root, or null for unions / lossless-json (SV12).</summary>
    private JsonToken? RootToken(Wire wire, HashSet<string> visiting)
    {
        return wire.Shape switch
        {
            NullWire => JsonToken.Null,
            BooleanWire => JsonToken.Boolean,
            StringWire => JsonToken.String,
            NumberWire => JsonToken.Number,
            LiteralWire lit => lit.Value.Token,
            ArrayWire => JsonToken.Array,
            ObjectWire => JsonToken.Object,
            TaggedUnionWire => JsonToken.Object,
            _ => null,
        };
    }

    private void CheckTaggedUnion(Wire wire, TaggedUnionWire tg, string sp)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < tg.Variants.Count; j++)
        {
            var variant = tg.Variants[j];
            var vp = JsonPointer.Append(JsonPointer.Append(sp, "variants"), j);
            var tagKey = variant.Tag switch
            {
                StringTag s => "s:" + s.Value,
                NumberTag n => "n:" + n.Text,
                _ => "?",
            };
            if (!tags.Add(tagKey))
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV13", JsonPointer.Append(vp, "tag"), $"wire '{wire.Id}': discriminator value is not unique", [wire.Id]);
            }

            var child = RequireWireInDirection(variant.Wire, wire, JsonPointer.Append(vp, "wire"));
            if (child is null)
            {
                continue;
            }

            if (child.Shape is not ObjectWire objWire)
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV13", JsonPointer.Append(vp, "wire"), $"wire '{wire.Id}': variant wire '{child.Id}' must be an object wire", [wire.Id, child.Id]);
                continue;
            }

            var disc = objWire.Properties.FirstOrDefault(p => string.Equals(p.Name, tg.Discriminator, StringComparison.Ordinal));
            if (disc is null)
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV50", JsonPointer.Append(vp, "wire"), $"wire '{wire.Id}': variant wire '{child.Id}' has no property '{tg.Discriminator}'", [wire.Id, child.Id]);
                continue;
            }

            if (disc.Presence != Presence.Required)
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV50", JsonPointer.Append(vp, "wire"), $"wire '{wire.Id}': discriminator '{tg.Discriminator}' of '{child.Id}' must be required", [wire.Id, child.Id]);
            }

            if (!_index.Wires.TryGetValue(disc.Wire.WireId, out var discWire) || discWire.Shape is not LiteralWire literal)
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV50", JsonPointer.Append(vp, "wire"), $"wire '{wire.Id}': discriminator '{tg.Discriminator}' of '{child.Id}' must reference a literal wire", [wire.Id, child.Id]);
                continue;
            }

            var matches = (variant.Tag, literal.Value) switch
            {
                (StringTag s, JsonStringValue ls) => string.Equals(s.Value, ls.Value, StringComparison.Ordinal),
                (NumberTag n, JsonNumberValue ln) => string.Equals(n.Text, ln.Text, StringComparison.Ordinal),
                _ => false,
            };
            if (!matches)
            {
                Error(TisiliaCodes.TaggedUnionInvalid, "SV50", JsonPointer.Append(vp, "tag"), $"wire '{wire.Id}': discriminator literal of '{child.Id}' does not equal the variant tag (kind and value must match)", [wire.Id, child.Id]);
            }
        }
    }

    // ---------------------------------------------------------------- codecs (SV03/SV05/SV06/SV11/SV14/SV21/SV23/SV29)

    private void CheckCodecs()
    {
        for (var i = 0; i < _doc.Codecs.Count; i++)
        {
            var codec = _doc.Codecs[i];
            var cp = JsonPointer.Append("/codecs", i);
            var typeOk = RequireType(codec.TypeId, JsonPointer.Append(cp, "typeId"));
            RequireBuiltinOrBinding(codec.BindingId, JsonPointer.Append(cp, "bindingId"), "codec binding", [BuiltinKind.Binding], [BindingKind.Converter, BindingKind.Factory]);
            RequireImpl(codec.ValidateDomain, JsonPointer.Append(cp, "validateDomain"), "validateDomain", [BuiltinKind.CodecImpl], [ExportRole.Validator], TsTargets);
            for (var j = 0; j < codec.Dependencies.Count; j++)
            {
                var dep = codec.Dependencies[j];
                if (dep == codec.Id)
                {
                    Error(TisiliaCodes.UndeclaredDependency, "SV14", JsonPointer.Append(JsonPointer.Append(cp, "dependencies"), j), $"codec '{codec.Id}' lists itself as a dependency", [codec.Id]);
                }
                else
                {
                    RequireCodec(dep, JsonPointer.Append(JsonPointer.Append(cp, "dependencies"), j));
                }
            }

            for (var j = 0; j < codec.ProfileIds.Count; j++)
            {
                RequireProfile(codec.ProfileIds[j], JsonPointer.Append(JsonPointer.Append(cp, "profileIds"), j));
            }

            if (codec.Capabilities.Request is { } req)
            {
                CheckValueCapability(codec, req, WireDirection.ServerRead, JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "request"), EquivalenceScope.Request);
            }

            if (codec.Capabilities.Response is { } res)
            {
                CheckValueCapability(codec, res, WireDirection.ServerWrite, JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "response"), EquivalenceScope.Response);
            }

            if (codec.Capabilities.RequestKey is { } rk)
            {
                CheckKeyCapability(codec, rk, JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "requestKey"));
            }

            if (codec.Capabilities.ResponseKey is { } sk)
            {
                CheckKeyCapability(codec, sk, JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "responseKey"));
            }

            if (codec.Capabilities.RequestInput is { } input)
            {
                RequireImpl(input.Implementation, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "requestInput"), "implementation"), "requestInput", [BuiltinKind.CodecImpl], [ExportRole.RequestInput], [ArtifactTarget.Browser]);
                RequireBuiltinOrBinding(input.EditorId, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), "requestInput"), "editorId"), "editor", [BuiltinKind.Editor], [BindingKind.Editor]);
            }

            if (codec.Origin == CodecOrigin.Builtin)
            {
                if (codec.ValidateDomain is not BuiltinImpl || (codec.Capabilities.Request?.Implementation is ModuleImpl) || (codec.Capabilities.Response?.Implementation is ModuleImpl))
                {
                    Error(TisiliaCodes.MissingCapability, "SV23", JsonPointer.Append(cp, "origin"), $"codec '{codec.Id}' has origin builtin but uses module implementations", [codec.Id]);
                }
            }
            else if (codec.Capabilities.Request?.Implementation is BuiltinImpl && codec.Capabilities.Response?.Implementation is BuiltinImpl && codec.ValidateDomain is BuiltinImpl)
            {
                Error(TisiliaCodes.MissingCapability, "SV23", JsonPointer.Append(cp, "origin"), $"codec '{codec.Id}' has origin {Enum(codec.Origin)} but every implementation is builtin", [codec.Id]);
            }

            if (typeOk)
            {
                CheckDeclaredDependencies(codec, cp);
                CheckBuiltinObjectCorrespondence(codec, cp);
            }
        }
    }

    private void CheckValueCapability(Codec codec, ValueCapability cap, WireDirection direction, string path, EquivalenceScope scope)
    {
        var wire = RequireWire(cap.Wire, JsonPointer.Append(path, "wire"));
        if (wire is not null && wire.Direction != direction)
        {
            Error(TisiliaCodes.DirectionMismatch, "SV05", JsonPointer.Append(JsonPointer.Append(path, "wire"), "direction"),
                $"codec '{codec.Id}': the {(scope == EquivalenceScope.Request ? "request" : "response")} capability must reference a {Enum(direction)} wire", [codec.Id, wire.Id]);
        }

        RequireImpl(cap.Implementation, JsonPointer.Append(path, "implementation"), scope == EquivalenceScope.Request ? "encodeRequest" : "decodeResponse", [BuiltinKind.CodecImpl], [ExportRole.Codec], TsTargets);
        RequireBuiltinOrBinding(cap.DomainRuleId, JsonPointer.Append(path, "domainRuleId"), "domain rule", [BuiltinKind.DomainRule], [BindingKind.DomainRule]);
        if (RequireEquivalence(cap.EquivalenceId, JsonPointer.Append(path, "equivalenceId")))
        {
            var eq = _index.Equivalences[cap.EquivalenceId];
            if (eq.DomainTypeId != codec.TypeId)
            {
                Error(TisiliaCodes.EquivalenceMismatch, "SV21", JsonPointer.Append(path, "equivalenceId"), $"equivalence '{eq.Id}' is declared for type '{eq.DomainTypeId}' but codec '{codec.Id}' converts type '{codec.TypeId}'", [eq.Id, codec.Id]);
            }

            if (eq.Scope != scope)
            {
                Error(TisiliaCodes.EquivalenceMismatch, "SV21", JsonPointer.Append(path, "equivalenceId"), $"equivalence '{eq.Id}' has scope '{Enum(eq.Scope)}' but is used for the {Enum(scope)} capability of codec '{codec.Id}'", [eq.Id, codec.Id]);
            }
        }

        // SV06: bypass requires an identity null branch on the wire (a lossless JSON wire admits the null token as such).
        if (wire is not null && cap.NullBehavior == NullBehavior.Bypass && !AdmitsNull(wire))
        {
            Error(TisiliaCodes.NullabilityMismatch, "SV06", JsonPointer.Append(path, "nullBehavior"), $"codec '{codec.Id}': nullBehavior 'bypass' requires wire '{wire.Id}' to have a null branch (identity null mapping)", [codec.Id, wire.Id]);
        }
    }

    private void CheckKeyCapability(Codec codec, KeyCapability cap, string path)
    {
        RequireImpl(cap.Implementation, JsonPointer.Append(path, "implementation"), "key codec", [BuiltinKind.CodecImpl], [ExportRole.KeyCodec], TsTargets);
        RequireBuiltinOrBinding(cap.GrammarId, JsonPointer.Append(path, "grammarId"), "key grammar", [BuiltinKind.KeyGrammar, BuiltinKind.Grammar], [BindingKind.Grammar]);
        if (cap.Collision != "reject")
        {
            Error(TisiliaCodes.MapKeyRule, "SV08", JsonPointer.Append(path, "collision"), $"codec '{codec.Id}': key collision policy must be 'reject'", [codec.Id]);
        }

        if (RequireEquivalence(cap.EquivalenceId, JsonPointer.Append(path, "equivalenceId")))
        {
            var eq = _index.Equivalences[cap.EquivalenceId];
            if (eq.DomainTypeId != codec.TypeId || eq.Scope != EquivalenceScope.Key)
            {
                Error(TisiliaCodes.EquivalenceMismatch, "SV21", JsonPointer.Append(path, "equivalenceId"), $"key capability of codec '{codec.Id}' requires a key-scoped equivalence for type '{codec.TypeId}'", [eq.Id, codec.Id]);
            }
        }
    }

    private bool HasNullBranch(Wire wire) => wire.Shape switch
    {
        NullWire => true,
        LiteralWire { Value: JsonNullValue } => true,
        TokenUnionWire tu => tu.Branches.Any(b => b.Token == JsonToken.Null),
        _ => false,
    };

    /// <summary>
    /// Whether the wire accepts a JSON null token at its root: a null branch, or a lossless JSON wire (json-value), for which null is
    /// an ordinary value — a nullable json-value use maps that token to the semantic null (bypass), a non-nullable one keeps it as a value.
    /// </summary>
    private bool AdmitsNull(Wire wire) => HasNullBranch(wire) || wire.Shape is LosslessJsonWire;

    /// <summary>SV14: every codec id used directly by the codec's model shape must be listed in dependencies.</summary>
    private void CheckDeclaredDependencies(Codec codec, string cp)
    {
        var model = _index.Types[codec.TypeId];
        var required = new List<string>();
        switch (model.Shape)
        {
            case ObjectShape obj:
                required.AddRange(obj.Properties.Select(p => p.Use.CodecId));
                if (obj.Extension is CaptureExtension capture)
                {
                    required.Add(capture.Value.CodecId);
                }

                break;
            case ArrayShape arr:
                required.Add(arr.Element.CodecId);
                break;
            case MapShape map:
                required.Add(map.Key.CodecId);
                required.Add(map.Value.CodecId);
                break;
            case BrandShape brand:
                required.Add(brand.Base.CodecId);
                break;
            case UnionShape union:
                required.AddRange(union.Variants.Select(v => v.Use.CodecId));
                break;
        }

        var declared = new HashSet<string>(codec.Dependencies, StringComparer.Ordinal);
        foreach (var dep in required.Distinct(StringComparer.Ordinal))
        {
            if (dep != codec.Id && !declared.Contains(dep))
            {
                Error(TisiliaCodes.UndeclaredDependency, "SV14", JsonPointer.Append(cp, "dependencies"), $"codec '{codec.Id}' reaches child codec '{dep}' through type '{model.Id}' but does not declare it as a dependency", [codec.Id, dep]);
            }
        }
    }

    /// <summary>
    /// SV11: a builtin object codec maps domain properties to same-named wire properties in each direction it
    /// implements, with matching presence, matching child wires and matching extension capture.
    /// </summary>
    private void CheckBuiltinObjectCorrespondence(Codec codec, string cp)
    {
        var model = _index.Types[codec.TypeId];
        if (model.Shape is not ObjectShape obj || codec.BindingId != Builtins.BindingFor("object"))
        {
            return;
        }

        foreach (var (cap, direction, name) in new[] { (codec.Capabilities.Request, WireDirection.ServerRead, "request"), (codec.Capabilities.Response, WireDirection.ServerWrite, "response") })
        {
            if (cap is null || !_index.Wires.TryGetValue(cap.Wire.WireId, out var wire))
            {
                continue;
            }

            var path = JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(cp, "capabilities"), name), "wire");
            // a nullable wrapper (bypass) carries `null | <object wire>`: the object rule applies to the non-null branch
            if (cap.NullBehavior == NullBehavior.Bypass && wire.Shape is TokenUnionWire { Branches.Count: 2 } union && union.Branches.Any(b => b.Token == JsonToken.Null)
                && _index.Wires.TryGetValue(union.Branches.First(b => b.Token != JsonToken.Null).Wire.WireId, out var innerWire))
            {
                wire = innerWire;
            }

            if (wire.Shape is not ObjectWire objWire)
            {
                Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"builtin object codec '{codec.Id}' requires an object wire for its {name} capability but '{wire.Id}' is '{wire.Shape.Kind}'", [codec.Id, wire.Id]);
                continue;
            }

            var wireProps = objWire.Properties.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
            foreach (var prop in obj.Properties)
            {
                if (!wireProps.TryGetValue(prop.Name, out var wp))
                {
                    Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"builtin object codec '{codec.Id}': domain property '{prop.Name}' has no same-named property on wire '{wire.Id}'; structural differences need a portable or paired codec", [codec.Id, wire.Id]);
                    continue;
                }

                if (wp.Presence != prop.Presence)
                {
                    Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"builtin object codec '{codec.Id}': property '{prop.Name}' is {Enum(prop.Presence)} in the domain but {Enum(wp.Presence)} on wire '{wire.Id}'", [codec.Id, wire.Id]);
                }

                if (_index.Codecs.TryGetValue(prop.Use.CodecId, out var childCodec))
                {
                    var childCap = direction == WireDirection.ServerRead ? childCodec.Capabilities.Request : childCodec.Capabilities.Response;
                    // a union variant's discriminator is a literal wire of the tag while its domain property is the tag's scalar (SV50)
                    var discriminatorLiteral = _index.Wires.TryGetValue(wp.Wire.WireId, out var propertyWire) && propertyWire.Shape is LiteralWire literal
                        && _index.Types.TryGetValue(childCodec.TypeId, out var childType) && childType.Shape is PrimitiveShape childPrimitive
                        && ((literal.Value is JsonStringValue && childPrimitive.PrimitiveId == Builtins.Scalar("string")) || (literal.Value is JsonNumberValue && childPrimitive.PrimitiveId.StartsWith("tisilia.", StringComparison.Ordinal) && childPrimitive.PrimitiveId.EndsWith("@0.1", StringComparison.Ordinal) && Builtins.IsNumberTokenScalar(childPrimitive.PrimitiveId["tisilia.".Length..^"@0.1".Length])));
                    if (childCap is not null && childCap.Wire.WireId != wp.Wire.WireId && !discriminatorLiteral)
                    {
                        Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"builtin object codec '{codec.Id}': property '{prop.Name}' uses codec '{childCodec.Id}' whose {name} wire is '{childCap.Wire.WireId}' but wire '{wire.Id}' references '{wp.Wire.WireId}'", [codec.Id, wire.Id]);
                    }

                    // SV06 for members: response wires that may carry null need a nullable public type.
                    if (direction == WireDirection.ServerWrite && !prop.Use.SemanticNullable && _index.Wires.TryGetValue(wp.Wire.WireId, out var childWire) && HasNullBranch(childWire) && childCap?.NullBehavior != NullBehavior.Converter)
                    {
                        Error(TisiliaCodes.NullabilityMismatch, "SV06", path, $"builtin object codec '{codec.Id}': property '{prop.Name}' may be written as null by the server (wire '{childWire.Id}') but its public type is not semanticNullable; either make it nullable or register a server-side non-null guard binding", [codec.Id, childWire.Id]);
                    }
                }
            }

            foreach (var wp in objWire.Properties)
            {
                if (!obj.Properties.Any(p => string.Equals(p.Name, wp.Name, StringComparison.Ordinal)))
                {
                    Error(TisiliaCodes.ObjectCodecMismatch, "SV11", path, $"builtin object codec '{codec.Id}': wire '{wire.Id}' property '{wp.Name}' has no domain counterpart", [codec.Id, wire.Id]);
                }
            }

            var domainCapture = obj.Extension as CaptureExtension;
            var wireCapture = objWire.Additional as CaptureAdditional;
            if ((domainCapture is null) != (wireCapture is null))
            {
                Error(TisiliaCodes.ExtensionDataMismatch, "SV11", path, $"codec '{codec.Id}': domain extension capture and wire additional capture must both be present or both absent ('{model.Id}' vs '{wire.Id}')", [codec.Id, wire.Id]);
            }
            else if (domainCapture is not null && wireCapture is not null && _index.Codecs.TryGetValue(domainCapture.Value.CodecId, out var valueCodec))
            {
                var valueCap = direction == WireDirection.ServerRead ? valueCodec.Capabilities.Request : valueCodec.Capabilities.Response;
                if (valueCap is null)
                {
                    Error(TisiliaCodes.ExtensionDataMismatch, "SV11", path, $"codec '{codec.Id}': extension value codec '{valueCodec.Id}' lacks the {name} capability", [codec.Id, valueCodec.Id]);
                }
                else if (valueCap.Wire.WireId != wireCapture.Wire.WireId)
                {
                    Error(TisiliaCodes.ExtensionDataMismatch, "SV11", path, $"codec '{codec.Id}': extension value wire '{valueCap.Wire.WireId}' differs from wire '{wire.Id}' capture wire '{wireCapture.Wire.WireId}'", [codec.Id, wire.Id]);
                }
            }
        }
    }

    // ---------------------------------------------------------------- equivalences / projections (SV21/SV22)

    private void CheckEquivalencesAndProjections()
    {
        for (var i = 0; i < _doc.Equivalences.Count; i++)
        {
            var eq = _doc.Equivalences[i];
            var ep = JsonPointer.Append("/equivalences", i);
            RequireType(eq.DomainTypeId, JsonPointer.Append(ep, "domainTypeId"));
            RequireBuiltinOrBinding(eq.DomainRuleId, JsonPointer.Append(ep, "domainRuleId"), "domain rule", [BuiltinKind.DomainRule], [BindingKind.DomainRule]);
            RequireBuiltinOrBinding(eq.NormalizationId, JsonPointer.Append(ep, "normalizationId"), "normalization", [BuiltinKind.Normalization], [BindingKind.Normalization]);
            RequireImpl(eq.DotnetOracle, JsonPointer.Append(ep, "dotnetOracle"), "dotnet oracle", [BuiltinKind.Oracle], [ExportRole.Oracle], DotnetTargets);
            RequireImpl(eq.TypescriptOracle, JsonPointer.Append(ep, "typescriptOracle"), "typescript oracle", [BuiltinKind.Oracle], [ExportRole.Oracle], TsTargets);
            if (eq.Preserved.Intersect(eq.NotPreserved, StringComparer.Ordinal).Any())
            {
                Error(TisiliaCodes.EquivalenceMismatch, "SV22", JsonPointer.Append(ep, "notPreserved"), $"equivalence '{eq.Id}' lists the same aspect as preserved and not preserved", [eq.Id]);
            }

            if (eq.Scope == EquivalenceScope.RoundTrip)
            {
                if (eq.ProjectionId is null)
                {
                    Error(TisiliaCodes.EquivalenceMismatch, "SV21", JsonPointer.Append(ep, "scope"), $"round-trip equivalence '{eq.Id}' requires an explicit bridge projection (projectionId)", [eq.Id],
                        "response and request DTOs are never cast into each other; declare a projection with both implementations");
                }
            }

            if (eq.ProjectionId is not null && RequireProjection(eq.ProjectionId, JsonPointer.Append(ep, "projectionId")))
            {
                var projection = _index.Projections[eq.ProjectionId];
                if (projection.SourceTypeId != eq.DomainTypeId)
                {
                    Error(TisiliaCodes.ProjectionMismatch, "SV22", JsonPointer.Append(ep, "projectionId"), $"projection '{projection.Id}' starts from '{projection.SourceTypeId}' but equivalence '{eq.Id}' compares '{eq.DomainTypeId}'", [projection.Id, eq.Id]);
                }
            }
        }

        for (var i = 0; i < _doc.Projections.Count; i++)
        {
            var pr = _doc.Projections[i];
            var pp = JsonPointer.Append("/projections", i);
            RequireType(pr.SourceTypeId, JsonPointer.Append(pp, "sourceTypeId"));
            RequireType(pr.TargetTypeId, JsonPointer.Append(pp, "targetTypeId"));
            RequireImpl(pr.DotnetImplementation, JsonPointer.Append(pp, "dotnetImplementation"), "dotnet projection", [BuiltinKind.Projection], [ExportRole.Projection], DotnetTargets);
            RequireImpl(pr.TypescriptImplementation, JsonPointer.Append(pp, "typescriptImplementation"), "typescript projection", [BuiltinKind.Projection], [ExportRole.Projection], TsTargets);
            if (pr.Preserved.Intersect(pr.NotPreserved, StringComparer.Ordinal).Any())
            {
                Error(TisiliaCodes.ProjectionMismatch, "SV22", JsonPointer.Append(pp, "notPreserved"), $"projection '{pr.Id}' lists the same aspect as preserved and not preserved", [pr.Id]);
            }

            if (pr.DotnetImplementation is BuiltinImpl { Id: Builtins.ProjectionIdentity } && pr.SourceTypeId != pr.TargetTypeId)
            {
                Error(TisiliaCodes.ProjectionMismatch, "SV22", JsonPointer.Append(pp, "dotnetImplementation"), $"identity projection '{pr.Id}' must have equal source and target types", [pr.Id]);
            }
        }
    }

    // ---------------------------------------------------------------- comparers (SV08/SV25)

    private void CheckComparers()
    {
        for (var i = 0; i < _doc.Comparers.Count; i++)
        {
            var c = _doc.Comparers[i];
            var cp = JsonPointer.Append("/comparers", i);
            RequireBuiltinOrBinding(c.BindingId, JsonPointer.Append(cp, "bindingId"), "comparer", [BuiltinKind.Comparer], [BindingKind.Comparer]);
            if (c.Collision != "reject")
            {
                Error(TisiliaCodes.ComparerRule, "SV08", JsonPointer.Append(cp, "collision"), $"comparer '{c.Id}' collision policy must be 'reject'", [c.Id]);
            }

            if (RequireEquivalence(c.EquivalenceId, JsonPointer.Append(cp, "equivalenceId")) && _index.Equivalences[c.EquivalenceId].Scope != EquivalenceScope.Key)
            {
                Error(TisiliaCodes.ComparerRule, "SV25", JsonPointer.Append(cp, "equivalenceId"), $"comparer '{c.Id}' must reference a key-scoped equivalence that states the key identity semantics", [c.Id]);
            }
        }
    }
}
