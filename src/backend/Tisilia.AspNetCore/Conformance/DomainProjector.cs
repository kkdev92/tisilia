using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Validation;
using JsonValue = Tisilia.Contract.JsonValue;

namespace Tisilia.AspNetCore.Conformance;

/// <summary>A runner-level failure with the fixed code set of the protocol.</summary>
public sealed class RunnerFailureException(RunnerFailureCode code, string safeMessageId, string path = "") : Exception(safeMessageId + (path.Length > 0 ? " at " + path : ""))
{
    public RunnerFailureCode Code { get; } = code;
    public string SafeMessageId { get; } = safeMessageId;
    public string Path { get; } = path;
}

/// <summary>
/// Explicit projection of a CLR value to the projected domain AST, driven by the contract model:
/// scalars use the canonical text of <see cref="DomainAst"/>, object members are read through the profile's
/// <see cref="System.Text.Json.Serialization.Metadata.JsonPropertyInfo"/> getters (member order of the model), paired
/// types use the registration's trusted <c>Project</c> delegate. Nothing is inferred from the CLR type alone.
/// </summary>
public sealed class DomainProjector(ContractIndex index, RunnerAdapterTable adapters)
{
    public JsonValue Project(object? value, TypeUse use, JsonSerializerOptions options, string profileId, string path = "")
    {
        if (value is null)
        {
            return DomainAst.Null;
        }

        if (!index.Types.TryGetValue(use.TypeId, out var model))
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.unknown-type", path);
        }

        var adapter = adapters.Find(use.CodecId, profileId);
        if (adapter?.Paired is { } reg)
        {
            if (reg.Project is null)
            {
                throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.paired-without-project-delegate", path);
            }

            return reg.Project(value);
        }

        switch (model.Shape)
        {
            case PrimitiveShape p:
                return Scalar(value, ScalarName(p), path);
            case EnumShape:
                return EnumValue(value);
            case ObjectShape o:
                return ProjectObject(value, model, o, options, profileId, path);
            case ArrayShape a:
            {
                if (Items(value) is not { } items)
                {
                    throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.not-enumerable", path);
                }

                var list = new List<JsonValue>();
                var i = 0;
                foreach (var item in items)
                {
                    list.Add(Project(item, a.Element, options, profileId, path + "/" + i.ToString(CultureInfo.InvariantCulture)));
                    i++;
                }

                return DomainAst.Array(list);
            }

            case MapShape m:
            {
                var entries = new List<(string, JsonValue)>();
                foreach (var (key, item) in EnumeratePairs(value, path))
                {
                    var keyAst = Project(key, m.Key, options, profileId, path);
                    var keyText = KeyText(keyAst) ?? throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.key-not-scalar", path);
                    entries.Add((keyText, Project(item, m.Value, options, profileId, path + "/" + keyText)));
                }

                return DomainAst.Object(entries);
            }

            case BrandShape b:
                return Project(value, b.Base, options, profileId, path);
            case UnionShape u:
            {
                var clrName = CleanName(value.GetType());
                foreach (var variant in u.Variants)
                {
                    var vm = index.Types[variant.Use.TypeId];
                    if (vm.ClrIdentity == clrName && vm.Shape is ObjectShape vs)
                    {
                        return ProjectObject(value, vm, vs, options, profileId, path, variant.Tag);
                    }
                }

                throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.unknown-variant", path);
            }

            default:
                throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.unknown-shape", path);
        }
    }

    private JsonValue ProjectObject(object value, Model model, ObjectShape shape, JsonSerializerOptions options, string profileId, string path, string? tag = null)
    {
        var info = options.GetTypeInfo(value.GetType());
        var entries = new List<(string, JsonValue)>();
        var first = true;
        foreach (var prop in shape.Properties)
        {
            var member = info.Properties.FirstOrDefault(p => string.Equals(p.Name, prop.Name, StringComparison.Ordinal));
            if (member?.Get is null)
            {
                if (first && (tag ?? TagFor(model.Id)) is { } t)
                {
                    // the discriminator is not a CLR member; System.Text.Json writes it from the polymorphism metadata
                    entries.Add((prop.Name, IsNumberUse(prop.Use) ? DomainAst.Number(t) : DomainAst.String(t)));
                    first = false;
                    continue;
                }

                throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.member-missing", path + "/" + prop.Name);
            }

            first = false;
            entries.Add((prop.Name, Project(member.Get(value), prop.Use, options, profileId, path + "/" + prop.Name)));
        }

        if (shape.Extension is CaptureExtension capture)
        {
            var ext = info.Properties.FirstOrDefault(p => p.IsExtensionData);
            if (ext?.Get?.Invoke(value) is { } data)
            {
                var extEntries = new List<(string, JsonValue)>();
                foreach (var (key, item) in EnumeratePairs(data, path))
                {
                    extEntries.Add(((string)key, Project(item, capture.Value, options, profileId, path + "/" + key)));
                }

                if (extEntries.Count > 0)
                {
                    entries.Add(("extensions", DomainAst.Object(extEntries)));
                }
            }
        }

        return DomainAst.Object(entries);
    }

    private string? TagFor(string variantModelId)
    {
        foreach (var t in index.Types.Values)
        {
            if (t.Shape is UnionShape u)
            {
                var v = u.Variants.FirstOrDefault(x => x.Use.TypeId == variantModelId);
                if (v is not null)
                {
                    return v.Tag;
                }
            }
        }

        return null;
    }

    private bool IsNumberUse(TypeUse use) => index.Types.TryGetValue(use.TypeId, out var t) && t.Shape is PrimitiveShape p && ScalarName(p) != "string";

    private static IEnumerable<(object Key, object? Value)> EnumeratePairs(object value, string path)
    {
        switch (value)
        {
            case JsonObject jo:
                foreach (var kv in jo)
                {
                    yield return (kv.Key, kv.Value);
                }

                yield break;
            case IDictionary dict:
                foreach (DictionaryEntry e in dict)
                {
                    yield return (e.Key, e.Value);
                }

                yield break;
            case IEnumerable enumerable when value is not string:
                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var type = item.GetType();
                    if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                    {
                        throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.not-a-dictionary", path);
                    }

                    var key = type.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)!.GetValue(item)!;
                    var val = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(item);
                    yield return (key, val);
                }

                yield break;
            default:
                throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.not-a-dictionary", path);
        }
    }

    public static string? KeyText(JsonValue key) => key switch
    {
        JsonStringValue s => s.Value,
        JsonNumberValue n => n.Text,
        JsonBooleanValue b => b.Value ? "True" : "False",
        _ => null,
    };

    private static string ScalarName(PrimitiveShape p)
    {
        var id = p.PrimitiveId;
        return id.StartsWith(Builtins.Prefix, StringComparison.Ordinal) && id.EndsWith("@0.1", StringComparison.Ordinal) ? id[Builtins.Prefix.Length..^4] : id;
    }

    private static JsonValue EnumValue(object value)
    {
        var underlying = Enum.GetUnderlyingType(value.GetType());
        return DomainAst.Number(underlying == typeof(ulong)
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The items of a value System.Text.Json writes as a JSON array: an IEnumerable (not a string), Memory&lt;T&gt; and
    /// ReadOnlyMemory&lt;T&gt; (arrays on the wire, not enumerable themselves) and IAsyncEnumerable&lt;T&gt; (drained here: the runner
    /// projects a sequence it read itself).
    /// </summary>
    private static IEnumerable? Items(object? value)
    {
        switch (value)
        {
            case null or string:
                return null;
            case IEnumerable enumerable:
                return enumerable;
        }

        var type = value.GetType();
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Memory<>) || type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>)))
        {
            return (IEnumerable?)type.GetMethod("ToArray", Type.EmptyTypes)?.Invoke(value, null);
        }

        var sequence = type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        return sequence is null ? null : (IEnumerable?)DrainMethod.MakeGenericMethod(sequence.GetGenericArguments()[0]).Invoke(null, [value]);
    }

    private static readonly MethodInfo DrainMethod = typeof(DomainProjector).GetMethod(nameof(Drain), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static List<T> Drain<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                items.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return items;
    }

    private static JsonValue Scalar(object value, string name, string path)
    {
        try
        {
            switch (name)
            {
                case "string":
                    return DomainAst.String((string)value);
                case "char":
                    return DomainAst.String(((char)value).ToString());
                case "boolean":
                    return DomainAst.Boolean((bool)value);
                case "guid":
                    return DomainAst.String(DomainAst.FormatGuid((Guid)value));
                case "bytes":
                    return DomainAst.String(Convert.ToBase64String((byte[])value));
                case "json-value":
                    return value switch
                    {
                        JsonElement e => DomainAst.FromElement(e),
                        JsonDocument d => DomainAst.FromElement(d.RootElement),
                        JsonNode n => DomainAst.ParseJsonText(n.ToJsonString()),
                        _ => DomainAst.FromElement(JsonSerializer.SerializeToElement(value, TisiliaJson.Plain)),
                    };
                case "int8":
                case "uint8":
                case "int16":
                case "uint16":
                case "int32":
                case "uint32":
                case "int64":
                case "uint64":
                    return DomainAst.Number(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                case "decimal":
                    return DomainAst.Number(DomainAst.FormatDecimal((decimal)value));
                case "float32":
                    return DomainAst.Float32((float)value);
                case "float64":
                    return DomainAst.Float64((double)value);
                case "date-only":
                case "time-only":
                case "datetime":
                case "datetime-utc":
                case "datetime-unspecified":
                case "datetime-local-wire":
                case "datetime-offset":
                case "duration":
                    // the "O" writer keeps the DateTime kind on the wire: Utc → Z, Local → the server zone's offset, Unspecified → none
                    return DomainAst.String(JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value, value.GetType(), TisiliaJson.Plain), TisiliaJson.Plain)!);
                default:
                    throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.scalar-unsupported", path);
            }
        }
        catch (InvalidCastException)
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "projection.clr-type-mismatch", path);
        }
    }

    /// <summary>Same identity text the exporter records in <c>Model.ClrIdentity</c>.</summary>
    public static string CleanName(Type type)
    {
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
}
