using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// One effective <see cref="JsonSerializerOptions"/> instance turned into a profile: every option is
/// recorded explicitly, unknown/unsupported settings become diagnostics, and scopes are appended as types are mapped.
/// </summary>
public sealed class ProfileContext
{
    private readonly ContractBuilder _builder;
    private readonly List<Scope> _scopes = [];
    private readonly HashSet<string> _scopeIds = new(StringComparer.Ordinal);
    private readonly List<Behavior> _behaviors = [];
    private Profile _profile;

    private ProfileContext(ContractBuilder builder, Profile profile, JsonSerializerOptions options)
    {
        _builder = builder;
        _profile = profile;
        Options = options;
    }

    public Profile Profile => _profile;

    /// <summary>
    /// Adds (once) the module export and the binding of a registered resolver and returns the binding id the profile references;
    /// null (with an SV44 diagnostic) when the module is unknown. Profiles are recorded before any paired codec maps a type, so a
    /// resolver binding's module is usually its own, declared with artifacts.
    /// </summary>
    private static string? RegisterResolverBinding(ContractBuilder builder, Bindings.ResolverRegistration reg, DiagnosticBag bag, string path)
    {
        var module = builder.GetModule(reg.ModuleId);
        if (module is null)
        {
            if (reg.Artifacts.Count == 0)
            {
                bag.Error(TisiliaCodes.ModuleArtifact, "SV44", path + "/options/resolverIds", $"resolver binding '{reg.Id}': module '{reg.ModuleId}' is not registered (profiles are recorded before paired codecs map their modules) and the registration declares no artifacts", [reg.Id, reg.ModuleId],
                    "declare the module's artifacts on the ResolverRegistration");
                return null;
            }

            module = new Module
            {
                Id = reg.ModuleId,
                Version = reg.ModuleVersion,
                Abi = TisiliaJson.DraftVersion,
                Artifacts = reg.Artifacts.Select(a => new Artifact { Target = a.Target, Path = a.Path, Digest = a.Digest ?? TisiliaHash.Sha256OfBytes(a.Bytes!()) }).ToList(),
                Exports = [],
                DependencyIds = [],
                License = reg.License,
                NoticeFiles = [],
            };
        }

        if (module.Exports.All(e => e.Name != reg.DotnetExport))
        {
            builder.AddModule(module with { Exports = [.. module.Exports, new ModuleExport { Name = reg.DotnetExport, Role = ExportRole.Resolver, Targets = [ArtifactTarget.Dotnet] }] });
        }

        builder.AddBinding(new Binding
        {
            Id = reg.Id,
            Kind = BindingKind.Resolver,
            Version = reg.ModuleVersion,
            Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DotnetExport },
            SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(reg.SettingsText)),
            DependencyIds = [],
            Context = reg.Context,
        });
        return reg.Id;
    }

    /// <summary>
    /// The binding of an application's source-generated <see cref="JsonSerializerContext"/> (reflection, source-generated
    /// metadata and the source-generated fast path are told apart). Contracts come from the metadata the context generates for the
    /// profile's options; System.Text.Json takes a type's fast path (its SerializeHandler) only when those options match the options the
    /// context was generated with. The binding records the context, those generation options and the types that have a fast path, so a
    /// change to any of them is a change of the profile.
    /// </summary>
    private static string SourceGeneratedResolverBinding(ContractBuilder builder, string profileId, JsonSerializerContext context)
    {
        var type = context.GetType();
        var fastPath = new List<string>();
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(JsonTypeInfo<>) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                if (property.GetValue(context) is JsonTypeInfo info && property.PropertyType.GetProperty(nameof(JsonTypeInfo<object>.SerializeHandler))?.GetValue(info) is not null)
                {
                    fastPath.Add(TypeName(info.Type));
                }
            }
            catch (System.Reflection.TargetInvocationException)
            {
                // a type the context cannot describe for its own options has no fast path either
            }
        }

        fastPath.Sort(StringComparer.Ordinal);
        var assembly = type.Assembly.GetName();
        BindingContextEntry[] entries =
        [
            new() { Name = "context", Value = type.FullName ?? type.Name, Confidential = false },
            new() { Name = "assembly", Value = assembly.Name + "/" + assembly.Version, Confidential = false },
            new() { Name = "generation-options", Value = GenerationOptions(context.Options), Confidential = false },
            new() { Name = "fast-path", Value = string.Join(",", fastPath), Confidential = false },
        ];
        var name = string.Concat((type.FullName ?? type.Name).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '-'));
        var id = profileId + ".resolver." + name;
        var binding = new Binding
        {
            Id = id.Length <= 160 ? id : id[..160],
            Kind = BindingKind.Resolver,
            Version = TisiliaJson.DraftVersion + ".0",
            Implementation = new BuiltinImpl { Id = Builtins.ResolverStjSourceGenerated },
            SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(e => e.Name + "=" + e.Value)))),
            DependencyIds = [],
            Context = entries,
        };
        binding = builder.FindEquivalentBinding(binding) ?? binding;
        builder.AddBinding(binding);
        return binding.Id;

        static string TypeName(Type t) => t.IsGenericType
            ? (t.GetGenericTypeDefinition().FullName ?? t.Name).Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">"
            : t.FullName ?? t.Name;
    }

    /// <summary>The settings of the options a context was generated with that decide whether its fast path can run (and what it writes).</summary>
    private static string GenerationOptions(JsonSerializerOptions o)
    {
        static string Policy(JsonNamingPolicy? p) => p is null ? "none"
            : p == JsonNamingPolicy.CamelCase ? "camelCase" : p == JsonNamingPolicy.SnakeCaseLower ? "snake_case_lower" : p == JsonNamingPolicy.SnakeCaseUpper ? "SNAKE_CASE_UPPER"
            : p == JsonNamingPolicy.KebabCaseLower ? "kebab-case-lower" : p == JsonNamingPolicy.KebabCaseUpper ? "KEBAB-CASE-UPPER" : p.GetType().FullName ?? "custom";
        return string.Join(";",
            "naming=" + Policy(o.PropertyNamingPolicy),
            "keys=" + Policy(o.DictionaryKeyPolicy),
            "ignore=" + o.DefaultIgnoreCondition,
            "readOnlyProperties=" + o.IgnoreReadOnlyProperties,
            "readOnlyFields=" + o.IgnoreReadOnlyFields,
            "fields=" + o.IncludeFields,
            "numbers=" + o.NumberHandling,
            "caseInsensitive=" + o.PropertyNameCaseInsensitive,
            "unmapped=" + o.UnmappedMemberHandling,
            "creation=" + o.PreferredObjectCreationHandling,
            "nullableAnnotations=" + o.RespectNullableAnnotations,
            "requiredConstructorParameters=" + o.RespectRequiredConstructorParameters,
            "duplicates=" + o.AllowDuplicateProperties,
            "converters=" + string.Join(",", o.Converters.Select(c => c.GetType().FullName)));
    }

    /// <summary>
    /// Whether two option sets describe every type alike, so that their profiles can share model ids: the settings the type mapper
    /// reads are equal — naming and dictionary key policies (by instance), ignore conditions, read-only and field inclusion, number
    /// handling, name matching, unmapped members, object creation, nullable annotations, required constructor parameters, duplicate
    /// properties, metadata order, the reference handler (Preserve writes reference metadata), the converter types and the application's
    /// resolvers. Depth limits, reading leniency and the encoder change no model. Two instances of one converter type with other
    /// settings are not told apart here; their models differ, which the contract builder reports (ContractBuilder.ProfileConflicts).
    /// </summary>
    public static bool DescribeTypesAlike(JsonSerializerOptions first, JsonSerializerOptions second)
    {
        static List<object?> Key(JsonSerializerOptions source)
        {
            var o = new JsonSerializerOptions(source);
            o.MakeReadOnly(populateMissingResolver: true);
            var resolvers = o.TypeInfoResolverChain
                .Where(r => r is not JsonSerializerContext context || !FrameworkResolvers.ContainsKey(context.GetType().FullName ?? ""))
                .Select(r => r is DefaultJsonTypeInfoResolver { Modifiers.Count: 0 } ? "reflection" : (object)r).ToList();
#pragma warning disable SYSLIB0020 // the obsolete setting still changes what is written
            List<object?> key = [o.PropertyNamingPolicy, o.DictionaryKeyPolicy, o.DefaultIgnoreCondition, o.IgnoreNullValues, o.IgnoreReadOnlyProperties,
                o.IgnoreReadOnlyFields, o.IncludeFields, o.NumberHandling, o.PropertyNameCaseInsensitive, o.UnmappedMemberHandling, o.PreferredObjectCreationHandling,
                o.RespectNullableAnnotations, o.RespectRequiredConstructorParameters, o.AllowDuplicateProperties, o.AllowOutOfOrderMetadataProperties, o.ReferenceHandler];
#pragma warning restore SYSLIB0020
            key.Add(o.Converters.Count);
            key.AddRange(o.Converters.Select(c => c.GetType()));
            key.Add(resolvers.Count);
            key.AddRange(resolvers);
            return key;
        }

        return Key(first).SequenceEqual(Key(second));
    }

    /// <summary>A read-only copy of the application's options used for metadata resolution (the app instance is never mutated).</summary>
    public JsonSerializerOptions Options { get; }

    public string NameMatchingId { get; private set; } = Builtins.NamesOrdinal;
    public string DuplicatePolicyId { get; private set; } = Builtins.DuplicatesReject;
    public NumberProfile Numbers { get; private set; }

    public static ProfileContext Create(ContractBuilder builder, string id, JsonSerializerOptions source, DiagnosticBag bag)
        => Create(builder, id, source, new Bindings.ResolverBindingCollection(), bag);

    public static ProfileContext Create(ContractBuilder builder, string id, JsonSerializerOptions source, Bindings.ResolverBindingCollection resolverBindings, DiagnosticBag bag)
    {
        var copy = new JsonSerializerOptions(source);
        copy.MakeReadOnly(populateMissingResolver: true);
        var path = "/profiles/" + id;
        var nameMatching = StandardProfile.NameMatchingBinding(id, copy.PropertyNameCaseInsensitive);
        // a second profile with the same name-matching settings shares the binding: one claim per scope (SV24)
        nameMatching = builder.FindEquivalentBinding(nameMatching) ?? nameMatching;
        builder.AddBinding(nameMatching);
        var naming = NamingPolicyId(copy.PropertyNamingPolicy, id, "propertyNamingPolicyId", bag, path);
        var keyNaming = NamingPolicyId(copy.DictionaryKeyPolicy, id, "dictionaryKeyPolicyId", bag, path);
        var encoder = copy.Encoder is null || copy.Encoder == JavaScriptEncoder.Default ? Builtins.EncoderDefault
            : copy.Encoder == JavaScriptEncoder.UnsafeRelaxedJsonEscaping ? Builtins.EncoderUnsafeRelaxed
            : Unsupported(bag, path + "/options/encoderId", $"profile '{id}': custom JavaScriptEncoder '{copy.Encoder.GetType().Name}' requires an encoder binding", Builtins.EncoderDefault);
        var resolvers = new List<string>();
        foreach (var resolver in copy.TypeInfoResolverChain)
        {
            if (resolver is DefaultJsonTypeInfoResolver { Modifiers.Count: 0 })
            {
                resolvers.Add(Builtins.ResolverReflection);
            }
            else if (resolver is JsonSerializerContext context && FrameworkResolvers.TryGetValue(context.GetType().FullName ?? "", out var builtin))
            {
                // inserted by the framework in front of the reflection resolver (AddProblemDetails, AddOpenApi, AddIdentityApiEndpoints,
                // AddBearerToken); each serves its own internal types only, with the built-in converters reflection would use for the
                // shared ones (string, JsonNode, JsonElement) — the evidence matrix records the framework version
                resolvers.Add(builtin);
            }
            else if (resolverBindings.Find(resolver) is { } registration)
            {
                // a registered resolver binding: the module export that implements the resolver or the contract modifiers
                if (RegisterResolverBinding(builder, registration, bag, path) is { } bindingId)
                {
                    resolvers.Add(bindingId);
                }
            }
            else if (resolver is JsonSerializerContext generated)
            {
                // the application's own source-generated context (Native AOT, trimming): the exporter reads the metadata it generates
                // for these options, and the binding records what decides the execution path
                resolvers.Add(SourceGeneratedResolverBinding(builder, id, generated));
            }
            else
            {
                bag.Error(TisiliaCodes.ProfileResolution, "SV16", path + "/options/resolverIds", $"profile '{id}': custom IJsonTypeInfoResolver '{resolver.GetType().Name}' (or contract modifiers) requires a resolver binding", [id]);
            }
        }

        var numberHandling = new List<NumberHandlingFlag>();
        if (copy.NumberHandling.HasFlag(JsonNumberHandling.AllowReadingFromString))
        {
            numberHandling.Add(NumberHandlingFlag.AllowReadingFromString);
        }

        if (copy.NumberHandling.HasFlag(JsonNumberHandling.WriteAsString))
        {
            numberHandling.Add(NumberHandlingFlag.WriteAsString);
        }

        if (copy.NumberHandling.HasFlag(JsonNumberHandling.AllowNamedFloatingPointLiterals))
        {
            numberHandling.Add(NumberHandlingFlag.AllowNamedFloatingPointLiterals);
        }

        if (copy.Converters.Count > 0)
        {
            // Global converters are described per type when they become effective; a bare list entry is not enough.
        }

        if (copy.ReferenceHandler is not null && !ReferenceEquals(copy.ReferenceHandler, ReferenceHandler.Preserve)
            && !ReferenceEquals(copy.ReferenceHandler, ReferenceHandler.IgnoreCycles))
        {
            bag.Error(TisiliaCodes.ReferencePreserve, "SV20", path + "/options/referenceHandling", $"profile '{id}': a custom ReferenceHandler has no declared reference semantics", [id],
                "use a standard ReferenceHandler or a profile without reference handling");
        }
        var referenceHandling = copy.ReferenceHandler is null ? ReferenceHandling.None
            : ReferenceEquals(copy.ReferenceHandler, ReferenceHandler.Preserve) ? ReferenceHandling.Preserve
            : ReferenceEquals(copy.ReferenceHandler, ReferenceHandler.IgnoreCycles) ? ReferenceHandling.IgnoreCycles
            : ReferenceHandling.Preserve;
        var stjOptions = new StjOptions
        {
            PropertyNameCaseInsensitive = copy.PropertyNameCaseInsensitive,
            PropertyNamingPolicyId = naming,
            DictionaryKeyPolicyId = keyNaming,
            EncoderId = encoder,
            NumberHandling = numberHandling,
            DefaultIgnoreCondition = (IgnoreCondition)Enum.Parse(typeof(IgnoreCondition), copy.DefaultIgnoreCondition.ToString()),
#pragma warning disable SYSLIB0020 // the obsolete value is recorded on purpose (SV18)
            IgnoreNullValues = copy.IgnoreNullValues,
#pragma warning restore SYSLIB0020
            IgnoreReadOnlyProperties = copy.IgnoreReadOnlyProperties,
            IgnoreReadOnlyFields = copy.IgnoreReadOnlyFields,
            IncludeFields = copy.IncludeFields,
            RespectNullableAnnotations = copy.RespectNullableAnnotations,
            RespectRequiredConstructorParameters = copy.RespectRequiredConstructorParameters,
            AllowDuplicateProperties = copy.AllowDuplicateProperties,
            AllowOutOfOrderMetadataProperties = copy.AllowOutOfOrderMetadataProperties,
            AllowTrailingCommas = copy.AllowTrailingCommas,
            ReadCommentHandling = copy.ReadCommentHandling == JsonCommentHandling.Skip ? CommentHandling.Skip : CommentHandling.Disallow,
            MaxDepthRaw = copy.MaxDepth,
            MaxDepthEffective = copy.MaxDepth == 0 ? 64 : copy.MaxDepth,
            PreferredObjectCreationHandling = copy.PreferredObjectCreationHandling == JsonObjectCreationHandling.Populate ? ObjectCreationHandling.Populate : ObjectCreationHandling.Replace,
            UnmappedMemberHandling = copy.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow ? UnmappedMemberHandling.Disallow : UnmappedMemberHandling.Skip,
            ReferenceHandling = referenceHandling,
            ResolverIds = resolvers,
            ConverterBindingIds = [],
        };
        if (copy.ReadCommentHandling == JsonCommentHandling.Allow)
        {
            bag.Error(TisiliaCodes.ProfileResolution, "SV16", path + "/options/readCommentHandling", $"profile '{id}': JsonCommentHandling.Allow is not valid for the serializer", [id]);
        }

        var version = typeof(JsonSerializer).Assembly.GetName().Version ?? new Version(10, 0, 0);
        var profile = StandardProfile.Create(id, Environment.Version.ToString(3), version.ToString(3), stjOptions, nameMatching.Id);
        builder.AddProfile(profile);
        var ctx = new ProfileContext(builder, profile, copy)
        {
            NameMatchingId = nameMatching.Id,
            DuplicatePolicyId = StandardProfile.DuplicatePolicyFor(stjOptions),
            Numbers = StandardProfile.NumberProfileFor(stjOptions),
        };
        return ctx;
    }

    private static string Unsupported(DiagnosticBag bag, string path, string message, string fallback)
    {
        bag.Error(TisiliaCodes.ProfileResolution, "SV16", path, message);
        return fallback;
    }

    private static string NamingPolicyId(JsonNamingPolicy? policy, string profileId, string field, DiagnosticBag bag, string path)
    {
        if (policy is null)
        {
            return Builtins.NamingNone;
        }

        if (ReferenceEquals(policy, JsonNamingPolicy.CamelCase))
        {
            return "tisilia.naming.camel-case@0.1";
        }

        if (ReferenceEquals(policy, JsonNamingPolicy.SnakeCaseLower))
        {
            return "tisilia.naming.snake-case-lower@0.1";
        }

        if (ReferenceEquals(policy, JsonNamingPolicy.SnakeCaseUpper))
        {
            return "tisilia.naming.snake-case-upper@0.1";
        }

        if (ReferenceEquals(policy, JsonNamingPolicy.KebabCaseLower))
        {
            return "tisilia.naming.kebab-case-lower@0.1";
        }

        if (ReferenceEquals(policy, JsonNamingPolicy.KebabCaseUpper))
        {
            return "tisilia.naming.kebab-case-upper@0.1";
        }

        bag.Error(TisiliaCodes.ProfileResolution, "SV16", path + "/options/" + field, $"profile '{profileId}': custom JsonNamingPolicy '{policy.GetType().Name}' requires a naming-policy binding", [profileId]);
        return Builtins.NamingNone;
    }

    /// <summary>Records a scope for a reached CLR type or member.</summary>
    public void AddScope(string clrPath, ScopeKind kind, string? readCodecId, string? writeCodecId, bool readRequired, Nullability getterNullable, Nullability setterNullable, ObjectCreationHandling creation, IgnoreCondition ignore, IReadOnlyList<string>? behaviorIds = null)
    {
        var id = _profile.Id + ".scope." + IdPart(_profile.Id + ".scope.", clrPath, "", 0);
        if (!_scopeIds.Add(id))
        {
            // the scope was recorded by the other direction first: it contributes its own codec id (a request model's codec has no
            // response capability and vice versa), and a behavior found now (server read) still belongs to the scope
            var index = _scopes.FindIndex(s => s.Id == id);
            if (index >= 0)
            {
                var existing = _scopes[index];
                _scopes[index] = existing with
                {
                    ReadCodecId = existing.ReadCodecId ?? readCodecId,
                    WriteCodecId = existing.WriteCodecId ?? writeCodecId,
                    BehaviorIds = existing.BehaviorIds.Count == 0 && behaviorIds is { Count: > 0 } ? behaviorIds : existing.BehaviorIds,
                    EffectiveObjectCreationHandling = existing.BehaviorIds.Count == 0 && behaviorIds is { Count: > 0 } ? creation : existing.EffectiveObjectCreationHandling,
                };
            }

            return;
        }

        _scopes.Add(new Scope
        {
            Id = id,
            Kind = kind,
            ClrPath = clrPath,
            ReadCodecId = readCodecId,
            WriteCodecId = writeCodecId,
            ReadRequired = readRequired,
            GetterNullable = getterNullable,
            SetterNullable = setterNullable,
            EffectiveObjectCreationHandling = creation,
            EffectiveIgnoreCondition = ignore,
            BehaviorIds = behaviorIds ?? [],
        });
    }

    /// <summary>Records a behavior of this profile (Populate / initializer / callback effect); ids are unique per profile.</summary>
    public void AddBehavior(Behavior behavior)
    {
        if (_behaviors.All(b => b.Id != behavior.Id))
        {
            _behaviors.Add(behavior);
        }
    }

    public void AddConverterBinding(string bindingId)
    {
        if (!_profile.Options.ConverterBindingIds.Contains(bindingId, StringComparer.Ordinal))
        {
            _profile = _profile with { Options = _profile.Options with { ConverterBindingIds = [.. _profile.Options.ConverterBindingIds, bindingId] } };
        }
    }

    public void Finish()
    {
        _profile = _profile with
        {
            Scopes = _scopes.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
            Behaviors = _behaviors.OrderBy(b => b.Id, StringComparer.Ordinal).ToList(),
        };
        _builder.AddProfile(_profile);
    }

    /// <summary>
    /// Source-generated contexts ASP.NET Core inserts at the front of the HTTP JSON options' resolver chain (aspnetcore v10.0.0:
    /// ProblemDetailsJsonOptionsSetup, OpenApiSchemaJsonOptions, IdentityEndpointsJsonOptionsSetup, BearerTokenConfigureJsonOptions).
    /// </summary>
    private static readonly Dictionary<string, string> FrameworkResolvers = new(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Http.ProblemDetailsJsonContext"] = Builtins.ResolverAspNetCoreProblemDetails,
        ["Microsoft.AspNetCore.OpenApi.OpenApiJsonSchemaContext"] = Builtins.ResolverAspNetCoreOpenApi,
        ["Microsoft.AspNetCore.Identity.Data.IdentityEndpointsJsonSerializerContext"] = Builtins.ResolverAspNetCoreIdentityEndpoints,
        ["Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenJsonSerializerContext"] = Builtins.ResolverAspNetCoreBearerToken,
    };

    /// <summary>The longest id the contract schema admits (common.schema.json: <c>^[A-Za-z][A-Za-z0-9_.:@/\-]{0,159}$</c>).</summary>
    public const int MaxIdLength = 160;

    /// <summary>
    /// <see cref="Sanitize"/>d <paramref name="raw"/> for use between <paramref name="prefix"/> and <paramref name="suffix"/>, shortened with a
    /// stable digest of the full text when the id — plus <paramref name="reserve"/> characters the builder appends later (".codec.nullable")
    /// — would exceed <see cref="MaxIdLength"/>. Deeply generic CLR names (Box&lt;Dictionary&lt;string, Dictionary&lt;string, int&gt;&gt;&gt;) stay valid ids.
    /// </summary>
    public static string IdPart(string prefix, string raw, string suffix, int reserve)
    {
        var part = Sanitize(raw);
        var budget = MaxIdLength - prefix.Length - suffix.Length - reserve;
        if (part.Length <= budget)
        {
            return part;
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..16];
        return part[..Math.Max(1, budget - hash.Length - 1)] + "-" + hash;
    }

    public static string Sanitize(string raw)
    {
        var chars = raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray();
        var s = new string(chars);
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]))
        {
            s = "t" + s;
        }

        if (s.Length > 150)
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..16];
            s = s[..120] + "-" + hash;
        }

        return s;
    }
}
