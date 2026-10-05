using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// Maps closed CLR types to Contract IR through the resolved <see cref="JsonTypeInfo"/> of one profile.
/// Object, collection and union models are emitted per direction (<c>XRequest</c> / <c>XResponse</c>)
/// because presence and nullability differ between what the server reads and what it writes. Standard types use builtin
/// codecs; custom converters require a registered paired binding; anything else is a diagnostic — never <c>any</c>.
/// </summary>
public sealed class ClrTypeMapper(ContractBuilder builder, ProfileContext profile, TisiliaOptions options, DiagnosticBag bag, Conformance.RunnerAdapterTable adapters)
{
    private readonly Dictionary<(Type, WireDirection), TypeUse?> _cache = new();

    /// <summary>The models this mapper wrote with the CLR type behind each (and an object's members): what documentation is read from.</summary>
    public List<DocumentedType> Documented { get; } = [];
    private readonly HashSet<(Type, WireDirection)> _inProgress = [];
    private readonly HashSet<string> _pairedRegistered = new(StringComparer.Ordinal);
    private readonly Dictionary<NumberProfile, JsonSerializerOptions> _numberOptions = new();

    /// <summary>Options equal to the profile's except for the number handling of a member override (runner adapters for root-level scalars).</summary>
    private JsonSerializerOptions OptionsForNumbers(NumberProfile numbers)
    {
        if (numbers == profile.Numbers)
        {
            return profile.Options;
        }

        if (!_numberOptions.TryGetValue(numbers, out var opts))
        {
            var handling = JsonNumberHandling.Strict;
            if (numbers.ReadFromString)
            {
                handling |= JsonNumberHandling.AllowReadingFromString;
            }

            if (numbers.WriteAsString)
            {
                handling |= JsonNumberHandling.WriteAsString;
            }

            if (numbers.NamedLiterals)
            {
                handling |= JsonNumberHandling.AllowNamedFloatingPointLiterals;
            }

            opts = new JsonSerializerOptions(profile.Options) { NumberHandling = handling };
            opts.MakeReadOnly(populateMissingResolver: true);
            _numberOptions[numbers] = opts;
        }

        return opts;
    }

    private void RecordAdapter(TypeUse use, Type clrType, NumberProfile numbers, PairedCodecRegistration? paired = null, JsonConverter? converter = null)
    {
        adapters.Add(new Conformance.RunnerAdapter(use.CodecId, profile.Profile.Id, clrType, paired is null || paired.NumberHandling ? OptionsForNumbers(numbers) : profile.Options, numbers, paired, converter));
        // the codec lists the profiles it was derived in (builtin codecs too), so the suite runs it under a profile the runner serves
        builder.TagCodecProfile(use.CodecId, profile.Profile.Id);
    }

    /// <summary>
    /// Whether this System.Text.Json writes <c>[JsonExtensionData] JsonObject</c> entries as members (dotnet/runtime#97225: up to
    /// .NET 10 the object is written as a nested value without a name, which is invalid JSON). Observed, not inferred from a version.
    /// </summary>
    private static readonly Lazy<bool> JsonObjectExtensionDataWritesValidJson = new(() =>
    {
        try
        {
            var written = JsonSerializer.Serialize(new ExtensionDataProbe { Extra = new JsonObject { ["x"] = 1 } }, TisiliaJson.Plain);
            using var document = JsonDocument.Parse(written);
            return document.RootElement.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    });

    private sealed class ExtensionDataProbe
    {
        [JsonExtensionData]
        public JsonObject? Extra { get; set; }
    }

    public static string? ScalarNameOf(Type type)
    {
        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(char))
        {
            return "char";
        }

        if (type == typeof(Guid))
        {
            return "guid";
        }

        // System.Text.Json's MemoryByteConverter/ReadOnlyMemoryByteConverter read and write base64 exactly like byte[] (a JSON null reads as
        // an empty value, never written back as null)
        if (type == typeof(byte[]) || type == typeof(Memory<byte>) || type == typeof(ReadOnlyMemory<byte>))
        {
            return "bytes";
        }

        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonDocument) || type == typeof(object))
        {
            return "json-value";
        }

        if (type == typeof(sbyte))
        {
            return "int8";
        }

        if (type == typeof(byte))
        {
            return "uint8";
        }

        if (type == typeof(short))
        {
            return "int16";
        }

        if (type == typeof(ushort))
        {
            return "uint16";
        }

        if (type == typeof(int))
        {
            return "int32";
        }

        if (type == typeof(uint))
        {
            return "uint32";
        }

        if (type == typeof(long))
        {
            return "int64";
        }

        if (type == typeof(ulong))
        {
            return "uint64";
        }

        if (type == typeof(decimal))
        {
            return "decimal";
        }

        if (type == typeof(float))
        {
            return "float32";
        }

        if (type == typeof(double))
        {
            return "float64";
        }

        if (type == typeof(DateOnly))
        {
            return "date-only";
        }

        if (type == typeof(TimeOnly))
        {
            return "time-only";
        }

        if (type == typeof(DateTimeOffset))
        {
            return "datetime-offset";
        }

        if (type == typeof(TimeSpan))
        {
            return "duration";
        }

        return null;
    }

    /// <summary>
    /// Maps a type at a position of the given direction. Returns null (with diagnostics) when it cannot be described. <paramref name="nullableRoot"/>:
    /// a root reference the server may write as the JSON null (a handler's nullable return value).
    /// </summary>
    public TypeUse? Map(Type type, WireDirection direction, string path, bool isRoot, JsonPropertyInfo? member = null, bool nullableRoot = false)
    {
        var numbers = NumbersFor(member);
        var nullable = false;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            nullable = true;
            type = underlying;
        }
        else if (!type.IsValueType && member is null)
        {
            nullable = nullableRoot;
        }
        else if (!type.IsValueType && member is not null)
        {
            // Responses: without RespectNullableAnnotations the server may write null for any reference member
            // (observed on .NET 10), so the null branch stays unless the profile enforces annotations.
            // Requests: the client only sends null where the setter is annotated nullable.
            nullable = direction == WireDirection.ServerWrite ? member.IsGetNullable || !profile.Options.RespectNullableAnnotations : member.IsSetNullable;
        }

        var use = MapCore(type, direction, path, isRoot, member, numbers);
        if (use is null)
        {
            return null;
        }

        if (!nullable)
        {
            return use;
        }

        var nullableUse = builder.Nullable(use);
        var memberConverter = member?.CustomConverter is { } custom && !IsBuiltinConverter(custom) ? custom : null;
        RecordAdapter(nullableUse, type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type, numbers, options.Codecs.Paired.FirstOrDefault(r => r.ModelId == use.TypeId), memberConverter);
        return nullableUse;
    }

    /// <summary>
    /// The converter type System.Text.Json uses for a type at type level, in its own precedence (dotnet/runtime v10.0.0,
    /// <c>DefaultJsonTypeInfoResolver.GetConverterForType</c>): the first options converter that can convert the type, else the
    /// type's <c>[JsonConverter]</c>, else the resolved built-in converter. A factory is reported as the factory type — what a
    /// registration names — whereas <see cref="JsonTypeInfo.Converter"/> holds the converter the factory created.
    /// </summary>
    public static Type EffectiveConverterType(JsonSerializerOptions options, Type type, JsonTypeInfo? info = null)
    {
        foreach (var converter in options.Converters)
        {
            if (converter.CanConvert(type))
            {
                return converter.GetType();
            }
        }

        if (type.GetCustomAttribute<JsonConverterAttribute>(inherit: false) is { } attribute)
        {
            if (attribute.ConverterType is { } declared)
            {
                return declared;
            }

            if (attribute.CreateConverter(type) is { } created)
            {
                return created.GetType();
            }
        }

        try
        {
            return (info ?? options.GetTypeInfo(type)).Converter.GetType();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return typeof(JsonConverter);
        }
    }

    private NumberProfile NumbersFor(JsonPropertyInfo? member)
    {
        // System.Text.Json precedence: the property attribute, then the declaring type's [JsonNumberHandling], then the options
        var handling = member?.NumberHandling ?? DeclaringTypeNumberHandling(member);
        if (handling is null)
        {
            return profile.Numbers;
        }

        return new NumberProfile(handling.Value.HasFlag(JsonNumberHandling.AllowReadingFromString), handling.Value.HasFlag(JsonNumberHandling.WriteAsString), handling.Value.HasFlag(JsonNumberHandling.AllowNamedFloatingPointLiterals));
    }

    private JsonNumberHandling? DeclaringTypeNumberHandling(JsonPropertyInfo? member)
    {
        if (member is null)
        {
            return null;
        }

        try
        {
            return profile.Options.GetTypeInfo(member.DeclaringType).NumberHandling;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private TypeUse? MapCore(Type type, WireDirection direction, string path, bool isRoot, JsonPropertyInfo? member, NumberProfile numbers)
    {
        if (member?.CustomConverter is { } custom && !IsBuiltinConverter(custom))
        {
            // property context: the member's converter decides the codec, and the runner gets that instance
            return MapPaired(type, custom.GetType(), path, custom, numbers);
        }

        // a converter the application registers for a scalar type (the options list or [JsonConverter] on the type) decides that type's
        // wire, never the builtin scalar of the type: such types take the paired path below
        var applicationConverter = (type == typeof(DateTime) || ScalarNameOf(type) is not null)
            && EffectiveConverterInstance(type) is { } effectiveScalarConverter && !IsBuiltinConverter(effectiveScalarConverter);
        if (type == typeof(DateTime) && !applicationConverter)
        {
            return MapDateTime(path, member, numbers);
        }

        // JsonObject and JsonArray fix the JSON kind of an otherwise arbitrary value: an object (string keys, unique, any JSON values)
        // and an array of any JSON values. JsonNode? members/items are null for JSON null, so the values are nullable json-value.
        if (type == typeof(JsonObject) || type == typeof(JsonArray))
        {
            var anyJson = builder.Scalar("json-value");
            RecordAdapter(anyJson, typeof(JsonNode), numbers);
            var anyOrNull = builder.Nullable(anyJson);
            RecordAdapter(anyOrNull, typeof(JsonNode), numbers);
            RecordTypeScope(type);
            var nodesId = ModelId(type, direction);
            TypeUse nodesUse;
            if (type == typeof(JsonObject))
            {
                var keyUse = builder.Scalar("string");
                RecordAdapter(keyUse, typeof(string), numbers);
                nodesUse = builder.MapOf(nodesId, UniqueTsName("JsonObjectMap", nodesId, direction), CleanName(type), keyUse, anyOrNull, builder.StandardComparer("string"));
            }
            else
            {
                nodesUse = builder.ArrayOf(nodesId, UniqueTsName("JsonNodeArray", nodesId, direction), CleanName(type), anyOrNull);
            }

            RecordAdapter(nodesUse, type, numbers);
            return nodesUse;
        }

        if (type == typeof(System.Text.Json.Nodes.JsonValue))
        {
            // the additional codecs describe it exactly (a JSON string, number or boolean, as read): JsonScalar
            var valueConverter = EffectiveConverterType(profile.Options, type);
            if (options.Codecs.TryGetPaired(type, valueConverter, out _))
            {
                return MapPaired(type, valueConverter, path, numbers: numbers);
            }

            return Unsupported(type, path, "JsonValue is a JSON string, number or boolean (never an object, an array or null); the builtin json-value domain cannot express that restriction — declare the member as JsonNode or JsonElement (any JSON value), or use the additional codecs: TisiliaOptions.Codecs.AddAdditionalCodecs(contentRoot) and options.AddTisiliaAdditionalConverters() (JsonValueJsonConverter; System.Text.Json's own answers an object or array with InvalidOperationException, a 500)");
        }

        var scalar = applicationConverter ? null : ScalarNameOf(type);
        if (scalar is not null)
        {
            RecordTypeScope(type);
            var scalarUse = builder.Scalar(scalar, numbers);
            RecordAdapter(scalarUse, type, numbers);
            return scalarUse;
        }

        var key = (type, direction);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_inProgress.Contains(key))
        {
            var id = ModelId(type, direction);
            return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = false };
        }

        JsonTypeInfo info;
        try
        {
            info = profile.Options.GetTypeInfo(type);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"System.Text.Json cannot resolve metadata for '{type}': {e.Message}");
            return null;
        }

        // the registration must name the converter System.Text.Json actually uses for the type here (type attribute or options
        // converter); a registration for another converter of the same type is a member-level binding and never applies silently
        var effectiveConverter = EffectiveConverterType(profile.Options, type, info);
        if (options.Codecs.TryGetPaired(type, effectiveConverter, out var typeLevel))
        {
            var pairedUse = MapPaired(type, effectiveConverter, path, numbers: numbers);
            if (!typeLevel.NumberHandling)
            {
                // a codec that follows the position's number handling is one per handling: never reuse the first position's
                _cache[key] = pairedUse;
            }

            return pairedUse;
        }

        if (options.Codecs.PairedFor(type).Any())
        {
            var registered = string.Join(", ", options.Codecs.PairedFor(type).Select(r => r.ConverterType.Name));
            var additional = options.Codecs.PairedFor(type).Any(r => r.ModuleId == Generator.Additional.AdditionalModule.ModuleId && r.ConverterType.Assembly == typeof(ClrTypeMapper).Assembly);
            bag.Error(TisiliaCodes.MissingCapability, "SV05", path, $"type '{type}' is bound by paired converter(s) {registered} but System.Text.Json serializes it with '{effectiveConverter.Name}' at this position; a type-level binding must name the converter in effect", [type.FullName ?? type.Name],
                additional ? "add the converter to the JSON options the application serves with: options.AddTisiliaAdditionalConverters() (Tisilia.AspNetCore.Codecs)" : null);
            _cache[key] = null;
            return null;
        }

        if (!IsBuiltinConverter(info.Converter))
        {
            bag.Error(TisiliaCodes.MissingCapability, "SV05", path, $"type '{type}' is serialized by custom converter '{info.Converter.GetType()}' which has no registered Tisilia binding; Tisilia never falls back to any/unknown", [type.FullName ?? type.Name],
                "register a paired codec with TisiliaOptions.Codecs.AddPaired(...) or a portable definition");
            _cache[key] = null;
            return null;
        }

        if (info.Kind == JsonTypeInfoKind.Object && info.PolymorphismOptions is null && FrameworkObjectProblem(type, info) is { } frameworkProblem)
        {
            _cache[key] = null;
            return Unsupported(type, path, frameworkProblem);
        }

        // a request type must be one System.Text.Json can create: an interface, an abstract class, a type without a usable constructor or a
        // read-only/abstract collection would be a request contract the server answers with an exception (G1 needs acceptance)
        if (direction == WireDirection.ServerRead && info.PolymorphismOptions is null && ServerCannotRead(type, info) is { } readProblem)
        {
            bag.Error(TisiliaCodes.MissingCapability, "SV05", path, $"type '{type}' cannot be read by System.Text.Json ({readProblem}); it can only appear in responses", [type.FullName ?? type.Name],
                "use a concrete type with a public parameterless constructor (or a [JsonConstructor]) for requests, e.g. List<T> instead of a read-only collection");
            _cache[key] = null;
            return null;
        }

        _inProgress.Add(key);
        try
        {
            TypeUse? result = info.Kind switch
            {
                JsonTypeInfoKind.Enumerable => MapEnumerable(type, info, direction, path),
                JsonTypeInfoKind.Dictionary => MapDictionary(type, info, direction, path, numbers),
                JsonTypeInfoKind.Object => MapObject(type, info, direction, path),
                _ when type.IsEnum => MapEnum(type, member),
                _ => Unsupported(type, path, $"type '{type}' (JsonTypeInfoKind.None with converter {info.Converter.GetType().Name}) is not a standard Tisilia type; it needs a qualified additional codec — Int128, UInt128, Half, Uri and Version have one: TisiliaOptions.Codecs.AddAdditionalCodecs(contentRoot)"),
            };
            _cache[key] = result;
            if (result is not null)
            {
                RecordAdapter(result, type, numbers);
            }

            return result;
        }
        finally
        {
            _inProgress.Remove(key);
        }
    }

    private TypeUse? Unsupported(Type type, string path, string message)
    {
        bag.Error(TisiliaCodes.UnresolvedReference, "SV03", path, message, [type.FullName ?? type.Name]);
        return null;
    }

    /// <summary>
    /// .NET types whose public properties System.Text.Json writes through its generic object converter although they are not data
    /// transfer objects (BigInteger, Complex, Rune, Index, Range, CultureInfo, TimeZoneInfo, Exception…): they need a certified
    /// additional codec. KeyValuePair, DictionaryEntry and the tuples are documented object contracts; a value tuple
    /// has members only when fields are included.
    /// </summary>
    private string? FrameworkObjectProblem(Type type, JsonTypeInfo info)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        if (!(assembly == "System.Private.CoreLib" || assembly.StartsWith("System.", StringComparison.Ordinal)) || !(type.Namespace ?? "").StartsWith("System", StringComparison.Ordinal))
        {
            return null;
        }

        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (definition == typeof(KeyValuePair<,>) || definition == typeof(System.Collections.DictionaryEntry) || (definition.FullName ?? "").StartsWith("System.Tuple`", StringComparison.Ordinal))
        {
            return null;
        }

        if ((definition.FullName ?? "").StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            return info.Properties.Count > 0 ? null : "a value tuple has no serializable members unless JsonSerializerOptions.IncludeFields is set; System.Text.Json writes it as {}";
        }

        return type == typeof(System.Numerics.BigInteger) || type == typeof(System.Net.IPAddress) || type == typeof(System.Text.Rune)
            || type == typeof(System.Net.IPNetwork) || type == typeof(Index) || type == typeof(Range) || type == typeof(System.Numerics.Complex)
            || type == typeof(TimeZoneInfo) || type == typeof(System.Globalization.CultureInfo)
            ? $"'{type}' has no System.Text.Json converter: it would be written as its public properties. Tisilia ships one with an additional codec: add it to the JSON options (options.AddTisiliaAdditionalConverters()) and register the codecs (TisiliaOptions.Codecs.AddAdditionalCodecs(contentRoot))"
            : $"'{type}' is a .NET framework type without a System.Text.Json converter: it would be written as its public properties and is not a data contract (it needs a certified additional codec)";
    }

    /// <summary>Why System.Text.Json cannot deserialize the type, or null when it can (abstract/interface types, no usable constructor, read-only or abstract collections).</summary>
    private string? ServerCannotRead(Type type, JsonTypeInfo info)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                if (type.IsInterface || type.IsAbstract)
                {
                    return "interfaces and abstract classes cannot be instantiated";
                }

                return info.CreateObject is null && !info.Properties.Any(p => p.AssociatedParameter is not null) && !type.IsValueType
                    ? "no public parameterless constructor and no single constructor System.Text.Json can bind"
                    : null;
            case JsonTypeInfoKind.Enumerable:
            case JsonTypeInfoKind.Dictionary:
            {
                // System.Text.Json decides on the first token whether it can create and fill the collection; a one-item probe also
                // catches collections whose Add is typed narrower than the element type it reports (StringCollection)
                var probes = info.Kind == JsonTypeInfoKind.Dictionary ? new[] { "{}" } : info.ElementType == typeof(object) ? new[] { "[]", "[\"x\"]" } : new[] { "[]" };
                foreach (var probe in probes)
                {
                    try
                    {
                        JsonSerializer.Deserialize(probe, type, profile.Options);
                    }
                    catch (Exception e) when (e is NotSupportedException or InvalidCastException or InvalidOperationException)
                    {
                        return e.GetType().Name + ": " + e.Message.Split('.')[0];
                    }
                    catch (JsonException e) when (e.Path is null or "$")
                    {
                        return "JsonException: " + e.Message.Split('.')[0];
                    }
                    catch (JsonException)
                    {
                        // an element-level failure says nothing about the collection type itself
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>The nullable form of a collection item or dictionary value of reference type: System.Text.Json never enforces the nullability of collection elements (learn.microsoft.com "Respect nullable annotations", limitations), so null items are read and written.</summary>
    private TypeUse NullableItem(TypeUse use, Type clrType)
    {
        if (use.SemanticNullable || clrType.IsValueType)
        {
            return use;
        }

        var nullableUse = builder.Nullable(use);
        RecordAdapter(nullableUse, clrType, NumberProfileOfCodecId(use.CodecId), options.Codecs.Paired.FirstOrDefault(r => r.ModelId == use.TypeId));
        return nullableUse;
    }

    /// <summary>
    /// Collections whose read changes the value the client sent: sets drop duplicates (and hash/sorted sets reorder), stacks
    /// enumerate in reverse of the JSON order. Returns the notPreserved aspects of the request, or null for order- and duplicate-keeping collections.
    /// </summary>
    private static string[]? NormalizingCollection(Type type)
    {
        static bool Implements(Type t, Type open) => (t.IsGenericType && t.GetGenericTypeDefinition() == open) || t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == open);
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (definition == typeof(Stack<>) || definition == typeof(System.Collections.Concurrent.ConcurrentStack<>) || Implements(type, typeof(System.Collections.Immutable.IImmutableStack<>)) || type == typeof(System.Collections.Stack))
        {
            return ["order"];
        }

        var sorted = definition == typeof(SortedSet<>) || definition == typeof(System.Collections.Immutable.ImmutableSortedSet<>);
        var hashOrdered = definition == typeof(System.Collections.Immutable.ImmutableHashSet<>) || definition == typeof(System.Collections.Immutable.IImmutableSet<>);
        if (sorted || hashOrdered)
        {
            return ["duplicates", "order"];
        }

        return Implements(type, typeof(ISet<>)) || Implements(type, typeof(IReadOnlySet<>)) ? ["duplicates"] : null;
    }

    /// <summary>Downgrades a structural equivalence to G1 with the value aspects the server does not keep (no G2 claim for a normalization the builtin registry cannot express).</summary>
    private void DowngradeToG1(string equivalenceId, IReadOnlyList<string> notPreserved)
    {
        if (builder.GetEquivalence(equivalenceId) is { } eq)
        {
            builder.AddEquivalence(eq with { Grade = Grade.G1, NotPreserved = [.. eq.NotPreserved.Concat(notPreserved).Distinct(StringComparer.Ordinal)] });
        }
    }

    /// <summary>A DateTime with its declared wire (<see cref="TisiliaOptions.DateTimes"/>): the builtin datetime scalar of that kind.</summary>
    private TypeUse? MapDateTime(string path, JsonPropertyInfo? member, NumberProfile numbers)
    {
        if (DateTimeScalar(member) is not { } scalar)
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"System.DateTime at '{path}' has a runtime-dependent Kind (Utc/Local/Unspecified) and no declared wire",
                fix: "declare the existing meaning using TisiliaOptions.DateTimes.Default or DateTimes.Add(typeof(T), \"Member\", …): Utc only for values already Utc, Unspecified for zone-less values, Local for server-zone-dependent JSON (not HTTP parameters). Mixed Kind needs a separate codec. A custom converter requires its own binding; no value or serializer setting is changed");
            return null;
        }

        RecordTypeScope(typeof(DateTime));
        var use = builder.Scalar(scalar, numbers);
        RecordAdapter(use, typeof(DateTime), numbers);
        return use;
    }

    /// <summary>The builtin scalar of the DateTime wire declared for a position (a member declaration, else the default), or null.</summary>
    public string? DateTimeScalar(JsonPropertyInfo? member) => options.DateTimes.Find(member?.DeclaringType, member?.AttributeProvider as MemberInfo, member?.Name) switch
    {
        DateTimeWire.Utc => "datetime-utc",
        DateTimeWire.Unspecified => "datetime-unspecified",
        DateTimeWire.Local => "datetime-local-wire",
        _ => null,
    };

    private static bool IsBuiltinConverter(JsonConverter converter)
    {
        var ns = converter.GetType().Namespace ?? "";
        return ns.StartsWith("System.Text.Json", StringComparison.Ordinal);
    }

    private static string DirectionSuffix(WireDirection direction) => direction == WireDirection.ServerRead ? "request" : "response";

    /// <summary>The longest suffix the builder appends to a model id (".codec.nullable"), reserved so that every derived id fits the 160-character id grammar.</summary>
    private const int IdReserve = 16;

    private string ModelId(Type type, WireDirection direction) => options.ApiId + "." + ProfileContext.IdPart(options.ApiId + ".", CleanName(type), "." + DirectionSuffix(direction), IdReserve) + "." + DirectionSuffix(direction);

    private string NeutralModelId(Type type) => options.ApiId + "." + ProfileContext.IdPart(options.ApiId + ".", CleanName(type), ".string", IdReserve);

    /// <summary>
    /// Anonymous types (minimal API handlers often return them) carry compiler names such as <c>&lt;&gt;f__AnonymousType0`2</c> whose
    /// index Roslyn assigns across the whole assembly: an unrelated anonymous type elsewhere can renumber them. Their identity in
    /// the contract is their shape — member names and types — which is all the wire depends on.
    /// </summary>
    private static bool IsAnonymous(Type type)
        => type.Namespace is null && type.Name.Contains("AnonymousType", StringComparison.Ordinal) && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

    private static string CleanName(Type type)
    {
        if (IsAnonymous(type))
        {
            return "Anonymous{" + string.Join(",", type.GetProperties().Select(p => p.Name + ":" + CleanName(p.PropertyType))) + "}";
        }

        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                name = name[..tick];
            }

            return name + "<" + string.Join(",", type.GetGenericArguments().Select(CleanName)) + ">";
        }

        if (type.IsArray)
        {
            return CleanName(type.GetElementType()!) + "[]";
        }

        return type.FullName ?? type.Name;
    }

    private static string BaseTsName(Type type)
    {
        if (type.IsArray)
        {
            return BaseTsName(type.GetElementType()!) + "Array";
        }

        if (IsAnonymous(type))
        {
            var names = type.GetProperties().Select(p => Capitalize(new string(p.Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()))).ToList();
            return "Anonymous" + string.Concat(names.Take(4)) + (names.Count > 4 ? "And" + (names.Count - 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + "More" : "");
        }

        // closed generic DTOs (Page<User>, Page<Order>) are different models: their arguments are part of the TypeScript name
        var arguments = type.IsGenericType && !type.IsGenericTypeDefinition ? "Of" + string.Join("And", type.GetGenericArguments().Select(BaseTsName)) : "";
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        var s = AsciiNamePart(name) + arguments;
        return s.Length == 0 || char.IsAsciiDigit(s[0]) ? "T" + s : s;
    }

    /// <summary>
    /// A name part for a TypeScript name, which the contract schema restricts to ASCII (<c>^[A-Za-z_$][A-Za-z0-9_$]*$</c>): ASCII letters, digits and
    /// underscores stay, any other identifier character becomes its code point (<c>商品</c> → <c>U5546U54C1</c>) — one name per CLR name, decodable,
    /// never a collision of two different names; everything else is dropped.
    /// </summary>
    private static string AsciiNamePart(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else if (c > 0x7f && Tisilia.Generator.TypeScript.TsNames.IsIdentifierPart(c))
            {
                sb.Append('U').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// The TypeScript name of an object or union model: the CLR name (with generic arguments) plus the direction suffix, made unique
    /// with a counter when another model already uses it (Ns1.Item and Ns2.Item, SV52). <paramref name="reserved"/> is a name that is
    /// about to be taken (a union's own name while its variants are mapped).
    /// </summary>
    private string ObjectTsName(string baseName, string modelId, WireDirection direction, string? reserved = null)
    {
        var suffix = direction == WireDirection.ServerRead ? "Request" : "Response";
        var stem = baseName.EndsWith(suffix, StringComparison.Ordinal) ? baseName[..^suffix.Length] : baseName;
        for (var i = 1; ; i++)
        {
            var counter = i == 1 ? "" : (stem.Length > 0 && char.IsAsciiDigit(stem[^1]) ? "_" : "") + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = stem + counter + suffix;
            if (!string.Equals(candidate, reserved, StringComparison.OrdinalIgnoreCase) && !builder.IsTsNameTaken(candidate, modelId))
            {
                return candidate;
            }
        }
    }

    /// <summary>A discriminator value as a TypeScript identifier part ("dog" → "Dog", "2" → "2").</summary>
    private static string TagName(string tag)
    {
        var s = AsciiNamePart(new string(tag.Where(char.IsLetterOrDigit).ToArray()));
        return s.Length == 0 ? "Variant" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private TypeUse? MapEnumerable(Type type, JsonTypeInfo info, WireDirection direction, string path)
    {
        var elementType = info.ElementType;
        if (elementType is null)
        {
            return Unsupported(type, path, $"collection '{type}' has no element type");
        }

        var element = Map(elementType, direction, path + "/element", isRoot: false);
        if (element is null)
        {
            return null;
        }

        var elementName = builder.GetType(element.TypeId)?.TsName ?? "Item";
        element = NullableItem(element, elementType);
        RecordTypeScope(type);
        var arrayId = ModelId(type, direction);
        var arrayUse = builder.ArrayOf(arrayId, UniqueTsName(Capitalize(elementName) + "Array", arrayId, direction), CleanName(type), element);
        // the model has both capabilities whichever direction built it: the read normalizes for every use of the CLR type
        if (NormalizingCollection(type) is { } aspects)
        {
            DowngradeToG1(arrayId + ".request", aspects);
        }

        return arrayUse;
    }

    /// <summary>
    /// Collection models of different CLR types or directions (List&lt;string&gt; read, IReadOnlyList&lt;string&gt; written) would share
    /// one TypeScript name (SV52); the first keeps the plain name, later ones carry the direction and, if needed, a counter.
    /// </summary>
    private string UniqueTsName(string baseName, string modelId, WireDirection direction)
    {
        if (!builder.IsTsNameTaken(baseName, modelId))
        {
            return baseName;
        }

        var directed = baseName + (direction == WireDirection.ServerRead ? "Request" : "Response");
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? directed : directed + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!builder.IsTsNameTaken(candidate, modelId))
            {
                return candidate;
            }
        }
    }

    private TypeUse? MapDictionary(Type type, JsonTypeInfo info, WireDirection direction, string path, NumberProfile numbers)
    {
        var keyType = info.KeyType;
        var valueType = info.ElementType;
        if (keyType is null || valueType is null)
        {
            return Unsupported(type, path, $"dictionary '{type}' has no key/value types");
        }

        // System.Text.Json's NullableConverter<T> has no property-name support (v10.0.0): a Nullable<T> key throws NotSupportedException
        // on read and on write
        if (Nullable.GetUnderlyingType(keyType) is not null)
        {
            return Unsupported(type, path, $"dictionary key type '{keyType}' is Nullable<T>, which System.Text.Json cannot read or write as a property name; use the underlying type as the key");
        }

        var keyScalar = ScalarNameOf(keyType);
        TypeUse? key;
        if (keyType.IsEnum)
        {
            key = Map(keyType, direction, path + "/key", isRoot: false);
        }
        else if (keyType == typeof(DateTime))
        {
            // System.Text.Json writes and reads DateTime property names like DateTime values (WritePropertyName(DateTime)); a key has no
            // member of its own, so the declared default applies
            if (DateTimeScalar(null) is not { } dateKey)
            {
                return Unsupported(type, path, "a DateTime dictionary key has a runtime-dependent Kind and no declared wire; declare TisiliaOptions.DateTimes.Default");
            }

            key = builder.Scalar(dateKey, numbers);
            RecordAdapter(key, typeof(DateTime), numbers);
        }
        else if (keyScalar is null && PairedKeyConverter(keyType) is { } keyConverter)
        {
            // System.Text.Json reads and writes property names with the type-level converter's property-name methods, which no number
            // handling affects: the registration's key capability, in its strict variant
            key = MapPaired(keyType, keyConverter, path + "/key", numbers: NumberProfile.Strict);
        }
        else if (keyScalar is null || keyScalar is "bytes" or "json-value")
        {
            return Unsupported(type, path, $"dictionary key type '{keyType}' has no key codec; register a key-capable codec");
        }
        else
        {
            key = builder.Scalar(keyScalar, numbers);
            RecordAdapter(key, Nullable.GetUnderlyingType(keyType) ?? keyType, numbers);
        }

        if (key is null)
        {
            return null;
        }

        var value = Map(valueType, direction, path + "/value", isRoot: false);
        if (value is null)
        {
            return null;
        }

        var valueName = builder.GetType(value.TypeId)?.TsName ?? "Value";
        value = NullableItem(value, valueType);

        // The comparer is an instance property invisible in metadata: the ordinal default is recorded and API authors
        // register a comparer binding for case-insensitive dictionaries.
        var comparerId = builder.StandardComparer(keyScalar ?? "string");
        RecordTypeScope(type);
        var mapId = ModelId(type, direction);
        var mapUse = builder.MapOf(mapId, UniqueTsName(Capitalize(valueName) + "Map", mapId, direction), CleanName(type), key, value, comparerId);
        // DictionaryKeyPolicy is applied when System.Text.Json writes string and enum keys, never when it reads them (observed on .NET 10):
        // the response does not keep the key names of the value the server holds
        if (profile.Options.DictionaryKeyPolicy is not null && (keyType == typeof(string) || keyType.IsEnum))
        {
            DowngradeToG1(mapId + ".response", ["key-names"]);
        }

        return mapUse;
    }

    private TypeUse? MapEnum(Type type, JsonPropertyInfo? member)
    {
        var underlyingType = Enum.GetUnderlyingType(type);
        var underlying = ScalarNameOf(underlyingType)!;
        var stringForm = EnumIsStringForm(type, member);
        var flags = type.GetCustomAttribute<FlagsAttribute>() is not null;
        var members = new List<Contract.EnumMember>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var raw = field.GetRawConstantValue()!;
            var value = underlyingType == typeof(ulong)
                ? ((ulong)raw).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var serialized = stringForm ? SerializedEnumName(type, field, member) : null;
            members.Add(new Contract.EnumMember { Name = field.Name, Value = value, SerializedName = serialized == field.Name ? null : serialized });
        }

        // the contract schema's enum shape requires at least one member (common.schema.json: members minItems 1)
        if (members.Count == 0)
        {
            bag.Error(TisiliaCodes.EnumInvalid, "SV49", "", $"enum '{type}' has no members; the contract's enum shape needs at least one (common.schema.json members minItems 1)", fix: "declare a member, or use the underlying integer type for an open numeric value");
            return null;
        }

        RecordTypeScope(type);
        var id = NeutralModelId(type) + (stringForm ? ".string" : ".number");
        Documented.Add(new DocumentedType(id, type, type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => new DocumentedMember(f.Name, type, f, null)).ToList()));
        return builder.EnumOf(id, EnumTsName(type, id, stringForm), CleanName(type), underlying, members, flags, stringForm);
    }

    /// <summary>
    /// The TypeScript name of a paired model: the registration's name, numbered when an application model already uses it (an enum named
    /// Version next to the additional codec of System.Version, SV52). A model keeps the name it got first.
    /// </summary>
    private string PairedTsName(PairedCodecRegistration reg)
    {
        if (builder.GetType(reg.ModelId) is { } existing)
        {
            return existing.TsName;
        }

        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? reg.TsName : reg.TsName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!builder.IsTsNameTaken(candidate, reg.ModelId))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The TypeScript name of an enum model: its CLR name, unless another model already uses it (SV52). The same enum in the other form
    /// (one member written by a JsonStringEnumConverter) gets the form as a suffix; an enum of another type is qualified by its declaring
    /// type or its namespace's last segment (Ns1.Status and Ns2.Status → Status, Ns2Status); a number is the last resort.
    /// </summary>
    private string EnumTsName(Type type, string modelId, bool stringForm)
    {
        if (builder.GetType(modelId) is { } existing)
        {
            return existing.TsName;
        }

        var name = BaseTsName(type);
        if (!builder.IsTsNameTaken(name, modelId))
        {
            return name;
        }

        var clr = CleanName(type);
        var candidates = new List<string>();
        if (builder.TypesNamed(name).Any(m => m.ClrIdentity == clr))
        {
            candidates.Add(name + (stringForm ? "Name" : "Number"));
        }

        var lastSegment = (type.Namespace ?? "").Split('.').LastOrDefault() ?? "";
        var qualifier = type.DeclaringType is { } outer ? BaseTsName(outer) : AsciiNamePart(lastSegment);
        if (qualifier.Length > 0)
        {
            candidates.Add(Capitalize(qualifier) + name);
        }

        foreach (var candidate in candidates)
        {
            if (!builder.IsTsNameTaken(candidate, modelId))
            {
                return candidate;
            }
        }

        for (var i = 2; ; i++)
        {
            var candidate = name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!builder.IsTsNameTaken(candidate, modelId))
            {
                return candidate;
            }
        }
    }

    /// <summary>Observes whether the effective converter writes names (JsonStringEnumConverter) or numbers, without touching internals.</summary>
    private bool EnumIsStringForm(Type type, JsonPropertyInfo? member)
    {
        var values = Enum.GetValues(type);
        if (values.Length == 0)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(values.GetValue(0)!, type, OptionsFor(member));
        return json.StartsWith('"');
    }

    private JsonSerializerOptions OptionsFor(JsonPropertyInfo? member)
    {
        if (member?.CustomConverter is { } converter)
        {
            var tmp = new JsonSerializerOptions(profile.Options);
            tmp.Converters.Insert(0, converter);
            return tmp;
        }

        return profile.Options;
    }

    private string SerializedEnumName(Type type, FieldInfo field, JsonPropertyInfo? member)
    {
        var json = JsonSerializer.Serialize(field.GetValue(null)!, type, OptionsFor(member));
        return json.StartsWith('"') ? JsonSerializer.Deserialize<string>(json, TisiliaJson.Plain)! : field.Name;
    }

    private sealed record MemberPlan(JsonPropertyInfo Property, Presence Presence, IgnoreCondition Ignore, bool CanRead, bool CanWrite);

    private TypeUse? MapObject(Type type, JsonTypeInfo info, WireDirection direction, string path, string? variantId = null, string? variantTsName = null)
    {
        // a variant of a polymorphic type is the derived type's own contract plus the discriminator: even when the derived type is
        // itself polymorphic (the base listed as one of its own variants), the variant is a plain object, never the union again
        if (variantId is null && info.PolymorphismOptions is { } poly)
        {
            return MapPolymorphic(type, poly, direction, path);
        }

        // Populate (type-level attribute or options preference) applies to every member that can be populated; each such member
        // needs a registered behavior, resolved below per member
        var typePopulate = info.PreferredPropertyObjectCreationHandling == JsonObjectCreationHandling.Populate || profile.Options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Populate;

        // The effect of constructors, initializers and serialization callbacks is registered per type; callbacks
        // (IJsonOnDeserializing etc.) are detected and need a registration, other kinds are declared by the application
        var id = variantId ?? ModelId(type, direction);
        var hasCallbacks = info.OnDeserializing is not null || info.OnDeserialized is not null || info.OnSerializing is not null || info.OnSerialized is not null;
        var typeRegistration = options.Behaviors.FindType(type);
        if (hasCallbacks && typeRegistration?.Kind != BehaviorKind.Callback)
        {
            bag.Error(TisiliaCodes.BehaviorEffect, "SV19", path, $"type '{type}' has serialization callbacks (IJsonOnDeserializing etc.) whose effect is not described; register a BehaviorRegistration of kind 'callback' for the type (identity, normalized with a projection, or opaque)", [NeutralModelId(type)]);
            return null;
        }

        IReadOnlyList<string>? typeBehaviorIds = null;
        var opaque = false;
        if (typeRegistration is not null)
        {
            var typeBehaviorId = RegisterBehavior(type, null, typeRegistration, path);
            if (typeBehaviorId is null)
            {
                return null;
            }

            typeBehaviorIds = [typeBehaviorId];
            opaque |= typeRegistration.Effect == BehaviorEffect.Opaque;
        }
        var props = new List<PropertySpec>();
        var documented = new List<DocumentedMember>();
        var failed = false;
        Extension extension = new NoExtension();
        AdditionalPolicy additional = direction == WireDirection.ServerRead
            ? (profile.Options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow || info.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow ? new RejectAdditional() : new IgnoreAdditional())
            : new IgnoreAdditional();
        // what the server constructs from {} — initializers, constructor defaults and constructor logic — observed once per type
        var constructedFromEmpty = direction == WireDirection.ServerRead && typeRegistration is null ? new Lazy<object?>(() => ConstructFromEmpty(type)) : null;
        // members the server writes but never reads: (CLR name, wire name)
        var computed = new List<(string Member, string Wire)>();
        foreach (var prop in info.Properties)
        {
            var pPath = path + "/" + prop.Name;
            if (prop.IsExtensionData)
            {
                if (direction == WireDirection.ServerWrite && prop.PropertyType == typeof(JsonObject) && !JsonObjectExtensionDataWritesValidJson.Value)
                {
                    bag.Error(TisiliaCodes.UnresolvedReference, "SV03", pPath, $"extension data '{type.Name}.{prop.Name}' is a JsonObject: this System.Text.Json ({typeof(JsonSerializer).Assembly.GetName().Version}) writes its entries as invalid JSON ({{\"a\":1,{{\"x\":1}}}}, dotnet/runtime#97225, fixed in .NET 11); a response cannot carry it", [id],
                        "declare the extension data as Dictionary<string, JsonElement> or Dictionary<string, object>");
                    failed = true;
                    continue;
                }

                var valueType = prop.PropertyType.IsGenericType ? prop.PropertyType.GetGenericArguments().Last() : typeof(object);
                var valueUse = Map(valueType, direction, pPath, isRoot: false);
                if (valueUse is null)
                {
                    failed = true;
                    continue;
                }

                extension = new CaptureExtension { Value = valueUse, Collision = "reject" };
                var valueCodec = builder.GetCodec(valueUse.CodecId)!;
                var cap = direction == WireDirection.ServerRead ? valueCodec.Capabilities.Request : valueCodec.Capabilities.Response;
                if (cap is not null)
                {
                    additional = new CaptureAdditional { Wire = cap.Wire };
                }

                continue;
            }

            // Populate reuses the initialized instance through the getter, so a get-only collection/object member is readable on the
            // server (learn.microsoft.com "Populate initialized properties": read-only properties can be populated)
            var populate = (prop.ObjectCreationHandling ?? (typePopulate ? JsonObjectCreationHandling.Populate : JsonObjectCreationHandling.Replace)) == JsonObjectCreationHandling.Populate && CanPopulate(prop.PropertyType);
            var canRead = prop.Set is not null || prop.AssociatedParameter is not null || (populate && prop.Get is not null);
            var canWrite = prop.Get is not null;
            // System.Text.Json (JsonPropertyInfo.DetermineSerializationCapabilities, v10.0.0): without a member-level [JsonIgnore], a
            // collection member that only has a setter is never read, and a read-only member of another type is not written under
            // IgnoreReadOnlyProperties (properties) / IgnoreReadOnlyFields (fields) unless a ShouldSerialize predicate was set
            var memberInfo = prop.AttributeProvider as MemberInfo;
            if (memberInfo?.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is null)
            {
                var collectionMember = IsCollectionType(prop.PropertyType);
                if (collectionMember && prop.Get is null && prop.Set is not null && prop.AssociatedParameter is null)
                {
                    canRead = false;
                }

                var ignoreReadOnly = memberInfo is PropertyInfo ? profile.Options.IgnoreReadOnlyProperties : memberInfo is FieldInfo && profile.Options.IgnoreReadOnlyFields;
                if (!collectionMember && prop.Get is not null && prop.Set is null && ignoreReadOnly && prop.ShouldSerialize is null)
                {
                    canWrite = false;
                }
            }
            var ignore = EffectiveIgnoreCondition(prop);
            if (ignore == IgnoreCondition.Always || (direction == WireDirection.ServerRead && !canRead) || (direction == WireDirection.ServerWrite && !canWrite))
            {
                continue;
            }

            var conditional = ignore is IgnoreCondition.WhenWritingNull or IgnoreCondition.WhenWritingDefault or IgnoreCondition.WhenWriting;
            // [JsonIgnore(Condition = Never)] is expressed as a ShouldSerialize predicate that always answers true: the member is always written
            var alwaysWritten = ignore == IgnoreCondition.Never && memberInfo?.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is { Condition: JsonIgnoreCondition.Never };
            var memberName = (prop.AttributeProvider as MemberInfo)?.Name;
            var memberRegistration = options.Behaviors.FindMember(type, memberName, prop.Name);
            // Source-generated metadata expresses [JsonIgnore(Condition = WhenWritingNull/Default)] as a ShouldSerialize predicate;
            // only a predicate that no ignore condition explains is an undescribed delegate and needs a registration
            if (direction == WireDirection.ServerWrite && prop.ShouldSerialize is not null && !conditional && !alwaysWritten && memberRegistration?.Kind != BehaviorKind.ShouldSerialize)
            {
                bag.Error(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"property '{type.Name}.{prop.Name}' has a ShouldSerialize delegate whose condition is not described; register a BehaviorRegistration of kind 'should-serialize' for the member", [id]);
                failed = true;
                continue;
            }

            var use = Map(prop.PropertyType, direction, pPath, isRoot: false, prop);
            if (use is null)
            {
                failed = true;
                continue;
            }

            var presence = direction == WireDirection.ServerRead
                ? (prop.IsRequired || (profile.Options.RespectRequiredConstructorParameters && prop.AssociatedParameter is { HasDefaultValue: false }) ? Presence.Required : Presence.Optional)
                : (conditional || (prop.ShouldSerialize is not null && !alwaysWritten) ? Presence.Optional : Presence.Required);
            var creation = populate ? ObjectCreationHandling.Populate : ObjectCreationHandling.Replace;
            // a populated member changes the constructed value and needs a registration of kind 'populate'; other registered
            // member effects (setter, getter, other) are recorded as declared by the application
            if (creation == ObjectCreationHandling.Populate && direction == WireDirection.ServerRead && memberRegistration?.Kind != BehaviorKind.Populate)
            {
                bag.Error(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"member '{type.Name}.{prop.Name}' uses Populate object creation: the received JSON and the constructed value differ; register the effect with TisiliaOptions.Behaviors.Add(...) as identity, normalized (with a projection) or opaque", [id],
                    "new BehaviorRegistration { ClrType = typeof(" + type.Name + "), MemberName = \"" + prop.Name + "\", Kind = BehaviorKind.Populate, Effect = BehaviorEffect.Normalized, ... }");
                failed = true;
                continue;
            }

            // An omitted optional nullable member reads as null (the suite's request oracle); an initializer, a constructor
            // default or constructor logic that constructs something else changes the received value and must be registered
            if (constructedFromEmpty is not null && presence == Presence.Optional && use.SemanticNullable && !populate && memberRegistration is null
                && ConstructedMemberValue(prop, constructedFromEmpty.Value) is { } constructed)
            {
                bag.Error(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"member '{type.Name}.{prop.Name}' is optional and nullable in the request, but when the client omits it the server constructs {constructed} instead of null (an initializer, a constructor default or constructor logic)", [id],
                    "make the member required, remove the default, or register a BehaviorRegistration of kind 'initializer' or 'constructor' for it (normalized with a projection, or opaque)");
                failed = true;
                continue;
            }

            // What a hand-written setter stores cannot be read from metadata (auto-property accessors are
            // [CompilerGenerated]); without a registration the request claim rests on the conformance suite alone
            if (direction == WireDirection.ServerRead && memberRegistration is null && typeRegistration is null && prop.AssociatedParameter is null && prop.Set is not null
                && memberInfo is PropertyInfo { SetMethod: { } setter } && !setter.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            {
                bag.Warning(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"property '{type.Name}.{prop.Name}' has a hand-written setter: what it stores cannot be read from metadata, so the request claim rests on the conformance suite", [id],
                    "register a BehaviorRegistration of kind 'setter' for the member: identity when it stores the value unchanged, normalized with a projection, or opaque");
            }

            IReadOnlyList<string>? behaviorIds = null;
            if (memberRegistration is not null)
            {
                var behaviorId = RegisterBehavior(type, prop.Name, memberRegistration, pPath);
                if (behaviorId is null)
                {
                    failed = true;
                    continue;
                }

                behaviorIds = [behaviorId];
                opaque |= memberRegistration.Effect == BehaviorEffect.Opaque;
            }

            // A member that System.Text.Json writes but cannot read (no setter, init accessor, constructor parameter
            // or Populate; [JsonIgnore(Condition = WhenReading)] removes the setter) holds what the type's own code puts there, an
            // initializer or a computed getter. The runner builds server values by reading generated ones, so the response round trip
            // through it is observable one way only, unless a registered getter behavior describes the effect with a projection (or a
            // type-level one does: normalized, its projection may compute the member; opaque, the type is G1 already; an identity
            // registration — a validating callback — says nothing about the member)
            if (direction == WireDirection.ServerWrite && !canRead && memberRegistration is null && (typeRegistration is null || typeRegistration.Effect == BehaviorEffect.Identity))
            {
                computed.Add((memberName ?? prop.Name, prop.Name));
            }

            // the use is direction-specific (a request model's codec has no response capability): each direction records its own codec
            // id and the scope merges the two when the type is used both ways (SV20)
            profile.AddScope(CleanName(type) + "." + prop.Name, ScopeKind.Member, canRead && direction == WireDirection.ServerRead ? use.CodecId : null, canWrite && direction == WireDirection.ServerWrite ? use.CodecId : null, prop.IsRequired,
                prop.IsGetNullable ? Nullability.Yes : Nullability.No, prop.IsSetNullable ? Nullability.Yes : Nullability.No, creation, ignore, behaviorIds);
            props.Add(new PropertySpec(prop.Name, use, presence, presence, presence));
            documented.Add(new DocumentedMember(prop.Name, prop.PropertyType, memberInfo, prop.AssociatedParameter?.AttributeProvider, OptionsFor(prop)));
        }

        if (failed)
        {
            return null;
        }

        RecordTypeScope(type, typeBehaviorIds);
        var objectUse = builder.ObjectOf(id, variantTsName ?? ObjectTsName(BaseTsName(type), id, direction), CleanName(type), props, profile.NameMatchingId, profile.DuplicatePolicyId,
            readAdditional: additional, writeAdditional: additional, extension: extension,
            emitRead: direction == WireDirection.ServerRead, emitWrite: direction == WireDirection.ServerWrite);
        Documented.Add(new DocumentedType(id, type, documented));
        if (computed.Count > 0)
        {
            // the structural equivalence preserves "member-values"; these members' values are the exception
            var aspects = computed.Select(c => "member-value:" + c.Wire).ToList();
            DowngradeToG1(id + ".response", aspects);
            var members = string.Join(", ", computed.Select(c => c.Member));
            var one = computed.Count == 1;
            bag.Warning(TisiliaCodes.BehaviorEffect, "SV19", path, $"'{type.Name}' writes {members} but System.Text.Json never reads {(one ? "it" : "them")} (no setter, init accessor or constructor parameter, or [JsonIgnore(Condition = WhenReading)]): {(one ? "its value comes" : "their values come")} from the type itself (an initializer or a computed getter), so the conformance runner cannot build a server value from a generated one and the response claims no round trip (G1, notPreserved {string.Join(", ", aspects)})", [id],
                $"to claim G2, let System.Text.Json read {(one ? "it" : "each of them")} (an init accessor or a constructor parameter), or register a BehaviorRegistration of kind 'getter' for {(one ? "the member" : "each member")} (normalized, with a projection that computes the value)");
        }

        if (opaque)
        {
            // SV19: an opaque behavior on the path forbids G2 claims — the type's own equivalences are G1 (no round-trip claim)
            foreach (var suffix in new[] { "request", "response" })
            {
                if (builder.GetEquivalence(id + "." + suffix) is { } eq && eq.Grade != Grade.G1)
                {
                    builder.AddEquivalence(eq with { Grade = Grade.G1 });
                }
            }
        }

        return objectUse;
    }

    /// <summary>
    /// The value System.Text.Json constructs for an empty JSON object — what the server holds for every omitted member — or null
    /// when it cannot build one (required members, a constructor or callback that rejects it). The application's own code runs,
    /// exactly as for a request with an empty body; any failure only means the effect cannot be observed.
    /// </summary>
    private object? ConstructFromEmpty(Type type)
    {
        try
        {
            return JsonSerializer.Deserialize("{}", type, profile.Options);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The member's value in the object constructed from {} as JSON, or null when it is null or cannot be read.</summary>
    private string? ConstructedMemberValue(JsonPropertyInfo prop, object? constructed)
    {
        if (constructed is null || prop.Get is null)
        {
            return null;
        }

        try
        {
            return prop.Get(constructed) is { } value ? Trimmed(JsonSerializer.Serialize(value, value.GetType(), profile.Options), 80) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string Trimmed(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private TypeUse? MapPolymorphic(Type type, JsonPolymorphismOptions poly, WireDirection direction, string path)
    {
        if (poly.UnknownDerivedTypeHandling != JsonUnknownDerivedTypeHandling.FailSerialization)
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV03", path, $"polymorphic type '{type}' uses UnknownDerivedTypeHandling.{poly.UnknownDerivedTypeHandling}; falling back to the base type loses information and needs an explicit projection", [NeutralModelId(type)]);
            return null;
        }

        var discriminator = poly.TypeDiscriminatorPropertyName ?? "$type";
        var id = ModelId(type, direction);
        var unionTsName = ObjectTsName(BaseTsName(type), id, direction);
        var variants = new List<UnionVariant>();
        var wireVariants = new List<TaggedVariant>();
        var deps = new List<string>();
        foreach (var derived in poly.DerivedTypes)
        {
            if (derived.TypeDiscriminator is null)
            {
                bag.Error(TisiliaCodes.TaggedUnionInvalid, "SV13", path, $"derived type '{derived.DerivedType}' of '{type}' has no discriminator; untagged derived types cannot be dispatched", [NeutralModelId(type)]);
                return null;
            }

            DiscriminatorTag tag = derived.TypeDiscriminator switch
            {
                string s => new StringTag { Value = s },
                int i => new NumberTag { Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                _ => throw new InvalidOperationException("unexpected discriminator type"),
            };
            var tagText = derived.TypeDiscriminator is string ts ? ts : derived.TypeDiscriminator.ToString()!;
            var derivedInfo = profile.Options.GetTypeInfo(derived.DerivedType);
            // the variant's own model: the same derived type used directly (declared type Dog, no discriminator on the wire) is a
            // different model, and the base type may be one of its own variants
            var variantId = options.ApiId + "." + ProfileContext.IdPart(options.ApiId + ".", CleanName(type) + "." + tagText, "." + DirectionSuffix(direction), IdReserve) + "." + DirectionSuffix(direction);
            var variantTsName = ObjectTsName(derived.DerivedType == type ? BaseTsName(type) + TagName(tagText) : BaseTsName(derived.DerivedType), variantId, direction, unionTsName);
            var derivedUse = MapDerivedWithDiscriminator(type, derived.DerivedType, derivedInfo, discriminator, tag, direction, path + "/" + derived.DerivedType.Name, variantId, variantTsName);
            if (derivedUse is null)
            {
                return null;
            }

            variants.Add(new UnionVariant { Tag = tagText, Use = derivedUse });
            deps.Add(derivedUse.CodecId);
            var codec = builder.GetCodec(derivedUse.CodecId)!;
            var cap = direction == WireDirection.ServerRead ? codec.Capabilities.Request : codec.Capabilities.Response;
            if (cap is null)
            {
                return Unsupported(type, path, $"derived type '{derived.DerivedType}' has no {DirectionSuffix(direction)} wire");
            }

            wireVariants.Add(new TaggedVariant { Tag = tag, Wire = cap.Wire });
        }

        if (direction == WireDirection.ServerWrite && !type.IsAbstract && !type.IsInterface && poly.DerivedTypes.All(d => d.DerivedType != type))
        {
            bag.Error(TisiliaCodes.TaggedUnionInvalid, "SV13", path, $"polymorphic type '{type}' is concrete but not one of its own derived types: System.Text.Json writes instances of '{type.Name}' without a discriminator, which the tagged union cannot decode", [NeutralModelId(type)],
                $"make '{type.Name}' abstract, or list it with [JsonDerivedType(typeof({type.Name}), \"…\")]");
            return null;
        }

        if (variants.Count == 0)
        {
            return Unsupported(type, path, $"polymorphic type '{type}' declares no derived types");
        }

        builder.AddType(new Model { Id = id, TsName = unionTsName, ClrIdentity = CleanName(type), Shape = new UnionShape { Variants = variants } });
        Documented.Add(new DocumentedType(id, type, []));
        var wireId = id + (direction == WireDirection.ServerRead ? ".read" : ".write");
        builder.AddWire(new Wire { Id = wireId, Direction = direction, Shape = new TaggedUnionWire { Discriminator = discriminator, Variants = wireVariants } });
        var eqId = id + "." + DirectionSuffix(direction);
        builder.AddEquivalence(new Equivalence
        {
            Id = eqId,
            Version = "0.1.0",
            DomainTypeId = id,
            Scope = direction == WireDirection.ServerRead ? EquivalenceScope.Request : EquivalenceScope.Response,
            Grade = Grade.G2,
            DomainRuleId = Builtins.DomainRule("union"),
            DotnetOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
            TypescriptOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
            NormalizationId = Builtins.NormalizeIdentity,
            Preserved = ["variant", "member-values"],
            NotPreserved = ["reference-identity"],
        });
        var valueCap = new ValueCapability
        {
            Wire = new WireRef { WireId = wireId, Direction = direction },
            Implementation = new BuiltinImpl { Id = Builtins.CodecImpl("union", direction == WireDirection.ServerRead ? "encode" : "decode") },
            NullBehavior = NullBehavior.Reject,
            EquivalenceId = eqId,
            DomainRuleId = Builtins.DomainRule("union"),
        };
        builder.AddCodec(new Codec
        {
            Id = id + ".codec",
            TypeId = id,
            Origin = CodecOrigin.Builtin,
            BindingId = Builtins.BindingFor("union"),
            ValidateDomain = new BuiltinImpl { Id = Builtins.CodecImpl("union", "validate") },
            Capabilities = new Capabilities
            {
                Request = direction == WireDirection.ServerRead ? valueCap : null,
                Response = direction == WireDirection.ServerWrite ? valueCap : null,
                RequestInput = new InputCapability { Implementation = new BuiltinImpl { Id = Builtins.CodecImpl("union", "parse-input") }, InputKind = InputKind.JsonValue, EditorId = Builtins.EditorJson },
            },
            Dependencies = deps.Distinct(StringComparer.Ordinal).ToList(),
            ProfileIds = [],
        });
        RecordTypeScope(type);
        return new TypeUse { TypeId = id, CodecId = id + ".codec", SemanticNullable = false };
    }

    /// <summary>A derived type as an object model whose wire carries the discriminator as a required literal property (SV13/SV50).</summary>
    private TypeUse? MapDerivedWithDiscriminator(Type baseType, Type derivedType, JsonTypeInfo derivedInfo, string discriminator, DiscriminatorTag tag, WireDirection direction, string path, string variantId, string variantTsName)
    {
        var baseUse = MapObject(derivedType, derivedInfo, direction, path, variantId, variantTsName);
        if (baseUse is not null)
        {
            // the derived codec's runner adapter serializes and deserializes through the polymorphic base type: System.Text.Json
            // writes and reads the discriminator only when the declared type is the base
            RecordAdapter(baseUse, baseType, profile.Numbers);
        }

        if (baseUse is null)
        {
            return null;
        }

        var model = builder.GetType(baseUse.TypeId)!;
        var codec = builder.GetCodec(baseUse.CodecId)!;
        if (model.Shape is not ObjectShape shape || shape.Properties.Any(p => p.Name == discriminator))
        {
            return baseUse;
        }

        var suffix = direction == WireDirection.ServerRead ? ".read" : ".write";
        Contract.JsonValue literal = tag switch
        {
            StringTag s => new JsonStringValue { Value = s.Value },
            NumberTag n => new JsonNumberValue { Text = n.Text },
            _ => throw new InvalidOperationException(),
        };
        var litWireId = baseUse.TypeId + ".tag" + suffix;
        builder.AddWire(new Wire { Id = litWireId, Direction = direction, Shape = new LiteralWire { Value = literal } });
        var wireId = baseUse.TypeId + suffix;
        var wireNode = builder.BuildJson()["wires"]!.AsArray().First(w => w!["id"]!.GetValue<string>() == wireId)!;
        var objWire = JsonSerializer.Deserialize<Wire>(wireNode, TisiliaJson.Options)!;
        if (objWire.Shape is ObjectWire ow)
        {
            // System.Text.Json writes metadata first; the domain and the wire both carry the discriminator as a required member
            var wireProps = new List<WireProperty> { new() { Name = discriminator, Wire = new WireRef { WireId = litWireId, Direction = direction }, Presence = Presence.Required } };
            wireProps.AddRange(ow.Properties);
            builder.AddWire(objWire with { Shape = ow with { Properties = wireProps } });
        }

        var tagUse = tag is StringTag ? builder.Scalar("string") : builder.Scalar("int32");
        var domainProps = new List<DomainProperty> { new() { Name = discriminator, Use = tagUse, Presence = Presence.Required } };
        domainProps.AddRange(shape.Properties);
        builder.AddType(model with { Shape = shape with { Properties = domainProps } });
        builder.AddCodec(codec with { Dependencies = codec.Dependencies.Append(tagUse.CodecId).Distinct(StringComparer.Ordinal).ToList() });
        return baseUse;
    }

    private static IgnoreCondition EffectiveIgnoreCondition(JsonPropertyInfo prop)
    {
        var attr = prop.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).OfType<JsonIgnoreAttribute>().FirstOrDefault();
        if (attr is not null)
        {
            return (IgnoreCondition)Enum.Parse(typeof(IgnoreCondition), attr.Condition.ToString());
        }

        // System.Text.Json (JsonPropertyInfo.DetermineIgnoreCondition, v10.0.0): the obsolete IgnoreNullValues skips null values of every
        // member whose type can be null on write (and ignores null tokens on read), unless the member configures its own condition
#pragma warning disable SYSLIB0020 // the obsolete option is still honoured by System.Text.Json and must be described
        if (prop.Options.IgnoreNullValues && (!prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) is not null))
#pragma warning restore SYSLIB0020
        {
            return IgnoreCondition.WhenWritingNull;
        }

        return (IgnoreCondition)Enum.Parse(typeof(IgnoreCondition), prop.Options.DefaultIgnoreCondition.ToString());
    }

    private void RecordTypeScope(Type type, IReadOnlyList<string>? behaviorIds = null)
    {
        profile.AddScope(CleanName(type), ScopeKind.Type, null, null, false, Nullability.Unknown, Nullability.Unknown,
            profile.Options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Populate ? ObjectCreationHandling.Populate : ObjectCreationHandling.Replace,
            (IgnoreCondition)Enum.Parse(typeof(IgnoreCondition), profile.Options.DefaultIgnoreCondition.ToString()), behaviorIds);
    }

    // ------------------------------------------------------------------ paired converters

    private TypeUse? MapPaired(Type type, Type converterType, string path, JsonConverter? converterInstance = null, NumberProfile? numbers = null)
    {
        if (!options.Codecs.TryGetPaired(type, converterType, out var reg))
        {
            bag.Error(TisiliaCodes.MissingCapability, "SV05", path, $"custom converter '{converterType}' for '{type}' has no registered paired binding", [type.FullName ?? type.Name],
                "TisiliaOptions.Codecs.AddPaired(new PairedCodecRegistration { ClrType = ..., ConverterType = ..., ... })");
            return null;
        }

        // SV15: the binding key includes the settings of the instance in effect. Another instance of the same
        // converter type (other constructor arguments, another profile) gets its own binding/codec ids and its own conformance
        // cases, so the TypeScript model/codec of one meaning is never shared with another (member-level instances: MapCore)
        var instance = converterInstance ?? EffectiveConverterInstance(type);
        var settings = reg.DescribeInstance is not null && instance is not null ? reg.DescribeInstance(instance) : null;
        // a type System.Text.Json treats as a number follows the position's number handling: the handling joins the binding settings and
        // the TypeScript codec's context, so another handling is another binding/codec
        var effectiveNumbers = reg.NumberHandling ? numbers ?? profile.Numbers : profile.Numbers;
        if (reg.NumberHandling && effectiveNumbers.Suffix.Length > 0)
        {
            var flags = effectiveNumbers.Suffix[1..];
            var baseSettings = settings ?? new PairedInstanceSettings(reg.SettingsText, reg.ConverterContext);
            settings = baseSettings with
            {
                SettingsText = baseSettings.SettingsText + "; numbers=" + flags,
                Context = [.. baseSettings.Context, new BindingContextEntry { Name = "numbers", Value = flags, Confidential = false }],
            };
        }

        var variant = settings is null || settings.SettingsText == reg.SettingsText ? "" : "." + VariantSuffix(settings.SettingsText);
        var codecId = reg.ModelId + ".codec" + variant;
        if (_pairedRegistered.Add(codecId))
        {
            RegisterPaired(reg, path, variant, settings, effectiveNumbers);
        }

        var pairedUse = new TypeUse { TypeId = reg.ModelId, CodecId = codecId, SemanticNullable = false };
        RecordAdapter(pairedUse, type, effectiveNumbers, reg, converterInstance);
        return pairedUse;
    }

    /// <summary>
    /// The model/codec of a route, query or header parameter of a paired type whose registration declares a parameter grammar
    /// (ASP.NET Core binds parameters with TryParse, not System.Text.Json, so no number handling applies: the strict codec).
    /// </summary>
    public (TypeUse Use, PairedCodecRegistration Registration)? PairedParameter(Type type, string path)
    {
        var registration = options.Codecs.PairedFor(type).FirstOrDefault(r => r.ParameterGrammarId is not null);
        if (registration is null)
        {
            return null;
        }

        var use = MapPaired(type, registration.ConverterType, path, numbers: NumberProfile.Strict);
        return use is null ? null : (use, registration);
    }

    /// <summary>The converter of a paired registration with a key capability that System.Text.Json uses for <paramref name="keyType"/> at type level; null otherwise.</summary>
    private Type? PairedKeyConverter(Type keyType)
    {
        var converter = EffectiveConverterType(profile.Options, keyType);
        return options.Codecs.TryGetPaired(keyType, converter, out var registration) && registration.Key is not null ? converter : null;
    }

    /// <summary>The converter instance System.Text.Json uses for the type in this profile (options list instance, factory product or the attribute-created instance).</summary>
    private JsonConverter? EffectiveConverterInstance(Type type)
    {
        try
        {
            return profile.Options.GetConverter(type);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Eight hex digits of the settings digest: a stable, readable id suffix for a converter instance's binding/codec.</summary>
    private static string VariantSuffix(string settingsText)
    {
        var digest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(settingsText));
        var hex = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest["sha256:".Length..] : digest;
        return hex[..8];
    }

    private void RegisterPaired(PairedCodecRegistration reg, string path, string variant, PairedInstanceSettings? settings, NumberProfile numbers)
    {
        // referenced paired codecs first: their models/codecs/wires must exist before this shape and these wires point at them
        foreach (var dependency in reg.Dependencies)
        {
            if (options.Codecs.TryGetPaired(dependency, out var depReg))
            {
                MapPaired(dependency, depReg.ConverterType, path, numbers: numbers);
            }
            else
            {
                var bound = options.Codecs.PairedFor(dependency).Count();
                bag.Error(TisiliaCodes.MissingCapability, "SV05", path, bound == 0
                    ? $"paired codec '{reg.ModelId}' depends on '{dependency}', which has no paired registration"
                    : $"paired codec '{reg.ModelId}' depends on '{dependency}', which is bound by {bound} converters; a dependency must resolve to exactly one registration", [reg.ModelId]);
            }
        }

        var artifacts = new List<Artifact>();
        foreach (var spec in reg.Artifacts)
        {
            var digest = spec.Digest;
            if (digest is null)
            {
                if (spec.Bytes is not null)
                {
                    digest = TisiliaHash.Sha256OfBytes(spec.Bytes());
                }
                else
                {
                    bag.Error(TisiliaCodes.ModuleArtifact, "SV44", path, $"paired module '{reg.ModuleId}' artifact '{spec.Path}' has neither a digest nor bytes; artifact digests bind the contract to the executing code", [reg.ModuleId]);
                    continue;
                }
            }

            artifacts.Add(new Artifact { Target = spec.Target, Path = spec.Path, Digest = digest });
        }

        var exports = new List<ModuleExport>
        {
            new() { Name = reg.DotnetConverterExport, Role = ExportRole.Codec, Targets = [ArtifactTarget.Dotnet] },
            new() { Name = reg.DomainRuleExport, Role = ExportRole.DomainRule, Targets = [ArtifactTarget.Dotnet, ArtifactTarget.Browser, ArtifactTarget.Node] },
        };
        if (!reg.BuiltinOracles)
        {
            exports.Add(new ModuleExport { Name = reg.DotnetOracleExport, Role = ExportRole.Oracle, Targets = [ArtifactTarget.Dotnet] });
            exports.Add(new ModuleExport { Name = reg.TypescriptOracleExport, Role = ExportRole.Oracle, Targets = [ArtifactTarget.Browser, ArtifactTarget.Node] });
        }
        void AddExport(string name, ExportRole role, params ArtifactTarget[] targets)
        {
            if (!exports.Any(e => e.Name == name))
            {
                exports.Add(new ModuleExport { Name = name, Role = role, Targets = targets });
            }
        }

        if (reg.Request is { } req)
        {
            AddExport(req.CodecExport, ExportRole.Codec, ArtifactTarget.Browser, ArtifactTarget.Node);
            AddExport(req.ValidateExport, ExportRole.Validator, ArtifactTarget.Browser, ArtifactTarget.Node);
            if (req.RequestInputExport is not null)
            {
                AddExport(req.RequestInputExport, ExportRole.RequestInput, ArtifactTarget.Browser);
            }
        }

        if (reg.Response is { } res)
        {
            AddExport(res.CodecExport, ExportRole.Codec, ArtifactTarget.Browser, ArtifactTarget.Node);
            AddExport(res.ValidateExport, ExportRole.Validator, ArtifactTarget.Browser, ArtifactTarget.Node);
        }

        if (reg.Key is { } keyExports)
        {
            AddExport(keyExports.EncodeKeyExport, ExportRole.KeyCodec, ArtifactTarget.Browser, ArtifactTarget.Node);
            AddExport(keyExports.DecodeKeyExport, ExportRole.KeyCodec, ArtifactTarget.Browser, ArtifactTarget.Node);
        }

        foreach (var grammar in reg.Grammars)
        {
            AddExport(grammar.ExportName, ExportRole.Grammar, ArtifactTarget.Browser, ArtifactTarget.Node);
            builder.AddBinding(new Binding
            {
                Id = grammar.Id,
                Kind = BindingKind.Grammar,
                Version = reg.ModuleVersion,
                Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = grammar.ExportName },
                SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes("grammar:" + grammar.Id)),
                DependencyIds = [],
                Context = [],
            });
        }

        // several registrations may share one module (a portable project is one module with an export per definition): merge exports
        var existingModule = builder.GetModule(reg.ModuleId);
        builder.AddModule(existingModule is null ? new Contract.Module
        {
            Id = reg.ModuleId,
            Version = reg.ModuleVersion,
            Abi = TisiliaJson.DraftVersion,
            Artifacts = artifacts,
            Exports = exports,
            DependencyIds = [],
            License = reg.License,
            NoticeFiles = [],
        } : existingModule with { Exports = existingModule.Exports.Concat(exports.Where(e => existingModule.Exports.All(x => x.Name != e.Name))).ToList() });
        var bindingId = reg.ModelId + ".converter" + variant;
        builder.AddBinding(new Binding
        {
            Id = bindingId,
            Kind = BindingKind.Converter,
            Version = reg.ModuleVersion,
            Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DotnetConverterExport },
            SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(settings?.SettingsText ?? reg.SettingsText)),
            DependencyIds = [],
            Context = settings?.Context ?? reg.ConverterContext,
        });
        profile.AddConverterBinding(bindingId);
        var domainRuleId = reg.ModelId + ".domain-rule";
        builder.AddBinding(new Binding
        {
            Id = domainRuleId,
            Kind = BindingKind.DomainRule,
            Version = reg.ModuleVersion,
            Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DomainRuleExport },
            SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes("domain-rule:" + reg.ModelId)),
            DependencyIds = [],
            Context = [],
        });
        var factory = new ContractWireFactory(builder);
        var shape = reg.Shape(factory);
        builder.AddType(new Model { Id = reg.ModelId, TsName = PairedTsName(reg), ClrIdentity = CleanName(reg.ClrType), Shape = shape });
        Documented.Add(new DocumentedType(reg.ModelId, reg.ClrType, []));
        RecordStandardAdapters(shape);
        ValueCapability? request = null;
        ValueCapability? response = null;
        InputCapability? input = null;
        foreach (var (dir, scope, suffix) in new[] { (reg.Request, EquivalenceScope.Request, "request"), (reg.Response, EquivalenceScope.Response, "response") })
        {
            if (dir is null)
            {
                continue;
            }

            var eqId = reg.ModelId + "." + suffix + variant;
            builder.AddEquivalence(new Equivalence
            {
                Id = eqId,
                Version = reg.ModuleVersion,
                DomainTypeId = reg.ModelId,
                Scope = scope,
                Grade = reg.Grade,
                DomainRuleId = domainRuleId,
                DotnetOracle = reg.BuiltinOracles ? new BuiltinImpl { Id = Builtins.OracleStructural } : new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DotnetOracleExport },
                TypescriptOracle = reg.BuiltinOracles ? new BuiltinImpl { Id = Builtins.OracleStructural } : new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.TypescriptOracleExport },
                NormalizationId = Builtins.NormalizeIdentity,
                Preserved = reg.Preserved,
                NotPreserved = settings?.NotPreserved ?? reg.NotPreserved,
            });
            var cap = new ValueCapability
            {
                Wire = reg.NumberHandling && dir.NumberWire is { } numberWire ? numberWire(factory, numbers) : dir.Wire(factory),
                Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = dir.CodecExport },
                NullBehavior = dir.NullBehavior,
                EquivalenceId = eqId,
                DomainRuleId = domainRuleId,
            };
            if (scope == EquivalenceScope.Request)
            {
                request = cap;
                if (dir.RequestInputExport is not null)
                {
                    input = new InputCapability { Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = dir.RequestInputExport }, InputKind = InputKind.Text, EditorId = Builtins.EditorText };
                }
            }
            else
            {
                response = cap;
            }
        }

        KeyCapability? requestKey = null;
        KeyCapability? responseKey = null;
        if (reg.Key is { } key)
        {
            // System.Text.Json's property-name methods (ReadAsPropertyName/WriteAsPropertyName) of the converter: one key equivalence
            var keyEqId = reg.ModelId + ".key" + variant;
            builder.AddEquivalence(new Equivalence
            {
                Id = keyEqId,
                Version = reg.ModuleVersion,
                DomainTypeId = reg.ModelId,
                Scope = EquivalenceScope.Key,
                Grade = reg.Grade,
                DomainRuleId = domainRuleId,
                DotnetOracle = reg.BuiltinOracles ? new BuiltinImpl { Id = Builtins.OracleStructural } : new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DotnetOracleExport },
                TypescriptOracle = reg.BuiltinOracles ? new BuiltinImpl { Id = Builtins.OracleStructural } : new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.TypescriptOracleExport },
                NormalizationId = Builtins.NormalizeIdentity,
                Preserved = reg.Preserved,
                NotPreserved = settings?.NotPreserved ?? reg.NotPreserved,
            });
            requestKey = new KeyCapability { Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = key.EncodeKeyExport }, GrammarId = key.GrammarId, EquivalenceId = keyEqId, Collision = "reject" };
            responseKey = new KeyCapability { Implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = key.DecodeKeyExport }, GrammarId = key.ResponseGrammarId ?? key.GrammarId, EquivalenceId = keyEqId, Collision = "reject" };
        }

        var validate = reg.Response?.ValidateExport ?? reg.Request!.ValidateExport;
        var codecId = reg.ModelId + ".codec" + variant;
        // the same instance settings reached from another profile share the codec: the profile list is the union
        var profileIds = builder.GetCodec(codecId) is { } existingCodec
            ? existingCodec.ProfileIds.Append(profile.Profile.Id).Distinct(StringComparer.Ordinal).ToList()
            : [profile.Profile.Id];
        builder.AddCodec(new Codec
        {
            Id = codecId,
            TypeId = reg.ModelId,
            Origin = reg.Origin,
            BindingId = bindingId,
            ValidateDomain = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = validate },
            Capabilities = new Capabilities { Request = request, Response = response, RequestKey = requestKey, ResponseKey = responseKey, RequestInput = input },
            Dependencies = ChildCodecIds(shape),
            ProfileIds = profileIds,
        });
    }

    // ------------------------------------------------------------------ behaviors

    private readonly Dictionary<string, string> _behaviors = new(StringComparer.Ordinal);

    /// <summary>Whether System.Text.Json treats the type as a collection or dictionary (its read-only policy does not apply to them).</summary>
    private bool IsCollectionType(Type type)
    {
        try
        {
            return profile.Options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type).Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether System.Text.Json can populate a member of this type (collections and objects are reused; scalars never are).</summary>
    private bool CanPopulate(Type propertyType)
    {
        try
        {
            return profile.Options.GetTypeInfo(propertyType).Kind != JsonTypeInfoKind.None;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Behavior kinds whose effect shows when the server writes (the projection then maps the response model); every other kind acts on the read side.</summary>
    private static bool IsWriteSide(BehaviorKind kind) => kind is BehaviorKind.ShouldSerialize or BehaviorKind.Getter;

    /// <summary>
    /// Records a registered behavior of a type or member: identity effects use the builtin identity
    /// implementation (or a module export when given), normalized effects add a projection with both implementations from the
    /// affected model to itself — the suite projects the generated value with it and both runners must agree —
    /// and opaque effects make the owner's equivalences G1 (the caller downgrades them).
    /// </summary>
    private string? RegisterBehavior(Type type, string? memberName, BehaviorRegistration reg, string pPath)
    {
        var clrPath = CleanName(type) + (memberName is null ? "" : "." + memberName);
        if (_behaviors.TryGetValue(clrPath, out var known))
        {
            return known;
        }

        var modelId = ModelId(type, IsWriteSide(reg.Kind) ? WireDirection.ServerWrite : WireDirection.ServerRead);
        var normalized = reg.Effect == BehaviorEffect.Normalized;
        if (normalized && (reg.ProjectionDotnetExport is null || reg.ProjectionTypescriptExport is null))
        {
            bag.Error(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"behavior for '{clrPath}': effect 'normalized' requires projection exports for both .NET and TypeScript", [modelId]);
            return null;
        }

        if (reg.Effect != BehaviorEffect.Identity && (reg.ModuleId is null || reg.DotnetBehaviorExport is null))
        {
            bag.Error(TisiliaCodes.BehaviorEffect, "SV19", pPath, $"behavior for '{clrPath}': a {reg.Effect.ToString().ToLowerInvariant()} effect must name the module and .NET export that implements it; only an identity effect may use the builtin implementation", [modelId]);
            return null;
        }

        Impl implementation = new BuiltinImpl { Id = Builtins.BehaviorIdentity };
        string? projectionId = null;
        if (reg.ModuleId is not null && reg.DotnetBehaviorExport is not null)
        {
            var module = builder.GetModule(reg.ModuleId);
            if (module is null)
            {
                if (reg.Artifacts.Count == 0)
                {
                    bag.Error(TisiliaCodes.ModuleArtifact, "SV44", pPath, $"behavior for '{clrPath}': module '{reg.ModuleId}' is not registered by a paired codec and the registration declares no artifacts", [reg.ModuleId]);
                    return null;
                }

                module = new Contract.Module
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

            var exports = new List<ModuleExport>(module.Exports);
            void AddExport(string name, ExportRole role, params ArtifactTarget[] targets)
            {
                if (exports.All(e => e.Name != name))
                {
                    exports.Add(new ModuleExport { Name = name, Role = role, Targets = targets });
                }
            }

            AddExport(reg.DotnetBehaviorExport, ExportRole.Behavior, ArtifactTarget.Dotnet);
            if (normalized)
            {
                AddExport(reg.ProjectionDotnetExport!, ExportRole.Projection, ArtifactTarget.Dotnet);
                AddExport(reg.ProjectionTypescriptExport!, ExportRole.Projection, ArtifactTarget.Browser, ArtifactTarget.Node);
                projectionId = reg.ModuleId + ".projection." + ProfileContext.IdPart(reg.ModuleId + ".projection.", clrPath, "", 0);
                builder.AddProjection(new Projection
                {
                    Id = projectionId,
                    Version = reg.ModuleVersion,
                    SourceTypeId = modelId,
                    TargetTypeId = modelId,
                    DotnetImplementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.ProjectionDotnetExport! },
                    TypescriptImplementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.ProjectionTypescriptExport! },
                    Preserved = reg.Preserved,
                    NotPreserved = reg.NotPreserved,
                });
            }

            builder.AddModule(module with { Exports = exports });
            implementation = new ModuleImpl { ModuleId = reg.ModuleId, ExportName = reg.DotnetBehaviorExport };
        }

        var behaviorId = profile.Profile.Id + ".behavior." + ProfileContext.IdPart(profile.Profile.Id + ".behavior.", clrPath, "", 0);
        profile.AddBehavior(new Behavior
        {
            Id = behaviorId,
            Kind = reg.Kind,
            Effect = reg.Effect,
            Implementation = implementation,
            ProjectionId = projectionId,
            SettingsDigest = TisiliaHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(reg.SettingsText)),
        });
        _behaviors[clrPath] = behaviorId;
        return behaviorId;
    }

    /// <summary>Paired shapes reference standard scalars directly (not through Map); the runner still needs adapters for them.</summary>
    /// <summary>
    /// Runner adapters for every codec reachable from a paired shape: standard scalars, nullable wrappers of paired types and the
    /// array models a paired registration declares (their CLR type is <c>IReadOnlyList&lt;element&gt;</c>). The suite exercises each
    /// codec of the closure on its own, so each needs a CLR type and options to run with.
    /// </summary>
    private void RecordStandardAdapters(DomainShape shape)
    {
        foreach (var use in ChildUses(shape))
        {
            RecordUseAdapter(use, 0);
        }
    }

    private void RecordUseAdapter(TypeUse use, int depth)
    {
        if (depth > 32 || ClrTypeOfUse(use) is not { } clr)
        {
            return;
        }

        var paired = options.Codecs.Paired.FirstOrDefault(r => r.ModelId == use.TypeId);
        var nullable = use.SemanticNullable || use.CodecId.EndsWith(".nullable", StringComparison.Ordinal);
        RecordAdapter(use, nullable && clr.IsValueType ? typeof(Nullable<>).MakeGenericType(clr) : clr, NumberProfileOfCodecId(use.CodecId), paired);
        if (paired is null && builder.GetType(use.TypeId)?.Shape is ArrayShape array)
        {
            RecordUseAdapter(array.Element, depth + 1);
        }
    }

    private Type? ClrTypeOfUse(TypeUse use)
    {
        if (use.TypeId.StartsWith(ContractBuilder.StdPrefix, StringComparison.Ordinal))
        {
            return ClrTypeOfScalar(use.TypeId[ContractBuilder.StdPrefix.Length..]);
        }

        if (options.Codecs.Paired.FirstOrDefault(r => r.ModelId == use.TypeId) is { } paired)
        {
            return paired.ClrType;
        }

        if (builder.GetType(use.TypeId)?.Shape is ArrayShape array && ClrTypeOfUse(array.Element) is { } element)
        {
            var elementClr = array.Element.SemanticNullable && element.IsValueType ? typeof(Nullable<>).MakeGenericType(element) : element;
            return typeof(IReadOnlyList<>).MakeGenericType(elementClr);
        }

        return null;
    }

    private static IEnumerable<TypeUse> ChildUses(DomainShape shape) => shape switch
    {
        ObjectShape o => o.Properties.Select(p => p.Use).Concat(o.Extension is CaptureExtension c ? [c.Value] : []),
        ArrayShape a => [a.Element],
        MapShape m => [m.Key, m.Value],
        BrandShape b => [b.Base],
        UnionShape u => u.Variants.Select(v => v.Use),
        _ => [],
    };

    /// <summary>Inverse of the builder's codec id suffix (<c>.codec[.r][w][n][.nullable]</c>).</summary>
    private static NumberProfile NumberProfileOfCodecId(string codecId)
    {
        var id = codecId.EndsWith(".nullable", StringComparison.Ordinal) ? codecId[..^".nullable".Length] : codecId;
        var marker = id.LastIndexOf(".codec", StringComparison.Ordinal);
        var suffix = marker < 0 ? "" : id[(marker + ".codec".Length)..].TrimStart('.');
        return new NumberProfile(suffix.Contains('r'), suffix.Contains('w'), suffix.Contains('n'));
    }

    public static Type? ClrTypeOfScalar(string name) => name switch
    {
        "string" => typeof(string),
        "boolean" => typeof(bool),
        "char" => typeof(char),
        "guid" => typeof(Guid),
        "bytes" => typeof(byte[]),
        "json-value" => typeof(JsonElement),
        "int8" => typeof(sbyte),
        "uint8" => typeof(byte),
        "int16" => typeof(short),
        "uint16" => typeof(ushort),
        "int32" => typeof(int),
        "uint32" => typeof(uint),
        "int64" => typeof(long),
        "uint64" => typeof(ulong),
        "decimal" => typeof(decimal),
        "float32" => typeof(float),
        "float64" => typeof(double),
        "date-only" => typeof(DateOnly),
        "time-only" => typeof(TimeOnly),
        // DateTime kinds reach a contract only through portable definitions (raw members are SV03); the runner reads them as DateTime
        "datetime-utc" or "datetime-unspecified" or "datetime-local-wire" => typeof(DateTime),
        "datetime-offset" => typeof(DateTimeOffset),
        "duration" => typeof(TimeSpan),
        _ => null,
    };

    private static IReadOnlyList<string> ChildCodecIds(DomainShape shape)
    {
        var ids = shape switch
        {
            ObjectShape o => o.Properties.Select(p => p.Use.CodecId).Concat(o.Extension is CaptureExtension c ? [c.Value.CodecId] : []),
            ArrayShape a => [a.Element.CodecId],
            MapShape m => [m.Key.CodecId, m.Value.CodecId],
            BrandShape b => [b.Base.CodecId],
            UnionShape u => u.Variants.Select(v => v.Use.CodecId),
            _ => [],
        };
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }
}
