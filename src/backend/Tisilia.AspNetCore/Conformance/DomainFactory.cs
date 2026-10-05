using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Conformance;
using Tisilia.Generator.Validation;
using JsonValue = Tisilia.Contract.JsonValue;

namespace Tisilia.AspNetCore.Conformance;

/// <summary>
/// Trusted factory that builds CLR values from projected domain ASTs. It is deliberately not the
/// HTTP body reader: the AST is rewritten by the contract model (extension entries inlined, discriminators first) and
/// deserialized with a copy of the profile options in which paired types are constructed by their registration's
/// <c>Construct</c> delegate and numbers/named literals are accepted in their canonical AST forms.
/// </summary>
public sealed class DomainFactory(ContractIndex index, IReadOnlyCollection<PairedCodecRegistration> paired)
{
    private readonly ConditionalWeakTable<JsonSerializerOptions, Dictionary<string, JsonSerializerOptions>> _domainOptions = new();
    private readonly Dictionary<(Type Clr, Type Converter), PairedCodecRegistration> _byConverter = paired.Where(p => p.Construct is not null).ToDictionary(p => (p.ClrType, p.ConverterType), p => p);

    public object? Construct(JsonValue domain, TypeUse use, RunnerAdapter adapter)
    {
        var text = DomainAst.ToJsonText(Rewrite(domain, use));
        var options = DomainOptions(adapter);
        try
        {
            return JsonSerializer.Deserialize(text, adapter.ClrType, options);
        }
        catch (JsonException e)
        {
            throw new RunnerFailureException(RunnerFailureCode.InvalidInput, "factory.construct", e.Path ?? "");
        }
        catch (NotSupportedException)
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "factory.unsupported-clr-type");
        }
        catch (InvalidOperationException)
        {
            throw new RunnerFailureException(RunnerFailureCode.Contract, "factory.invalid-metadata");
        }
    }

    /// <summary>Domain AST → the JSON the domain options understand (model-driven, lossless for everything else).</summary>
    private JsonValue Rewrite(JsonValue value, TypeUse use)
    {
        if (value is JsonNullValue || !index.Types.TryGetValue(use.TypeId, out var model))
        {
            return value;
        }

        switch (model.Shape)
        {
            case ObjectShape shape when value is JsonObjectValue obj:
            {
                var entries = new List<(string, JsonValue)>();
                foreach (var entry in obj.Entries)
                {
                    var prop = shape.Properties.FirstOrDefault(p => p.Name == entry.Name);
                    if (prop is not null)
                    {
                        entries.Add((entry.Name, Rewrite(entry.Value, prop.Use)));
                    }
                    else if (entry.Name == "extensions" && shape.Extension is CaptureExtension capture && entry.Value is JsonObjectValue ext)
                    {
                        entries.AddRange(ext.Entries.Select(e => (e.Name, Rewrite(e.Value, capture.Value))));
                    }
                    else
                    {
                        entries.Add((entry.Name, entry.Value));
                    }
                }

                return DomainAst.Object(entries);
            }

            case ArrayShape a when value is JsonArrayValue arr:
                return DomainAst.Array(arr.Items.Select(i => Rewrite(i, a.Element)));
            case MapShape m when value is JsonObjectValue mobj:
                return DomainAst.Object(mobj.Entries.Select(e => (e.Name, Rewrite(e.Value, m.Value))));
            case BrandShape b:
                return Rewrite(value, b.Base);
            case UnionShape u when value is JsonObjectValue uo:
            {
                foreach (var variant in u.Variants)
                {
                    var vm = index.Types[variant.Use.TypeId];
                    if (vm.Shape is ObjectShape vs && vs.Properties.Count > 0 && DomainAst.Entry(uo, vs.Properties[0].Name) is { } tagValue && TagText(tagValue) == variant.Tag)
                    {
                        var rewritten = (JsonObjectValue)Rewrite(value, variant.Use);
                        // System.Text.Json requires the discriminator first unless AllowOutOfOrderMetadataProperties is set
                        var discriminator = vs.Properties[0].Name;
                        var ordered = rewritten.Entries.Where(e => e.Name == discriminator).Concat(rewritten.Entries.Where(e => e.Name != discriminator)).Select(e => (e.Name, e.Value));
                        return DomainAst.Object(ordered);
                    }
                }

                return value;
            }

            default:
                return value;
        }
    }

    private static string? TagText(JsonValue value) => value switch
    {
        JsonStringValue s => s.Value,
        JsonNumberValue n => n.Text,
        _ => null,
    };

    /// <summary>
    /// Domain options per profile options and per root registration: type-level registrations (the converter System.Text.Json
    /// uses for the type) construct every value of their type, member-level ones are swapped in per property, and the codec
    /// being run constructs its own root value even when another converter binds the same CLR type at type level.
    /// </summary>
    private JsonSerializerOptions DomainOptions(RunnerAdapter adapter)
    {
        var perRoot = _domainOptions.GetValue(adapter.Options, _ => new Dictionary<string, JsonSerializerOptions>(StringComparer.Ordinal));
        var key = adapter.Paired?.ModelId ?? "";
        lock (perRoot)
        {
            if (!perRoot.TryGetValue(key, out var options))
            {
                options = BuildDomainOptions(adapter.Options, adapter.Paired);
                perRoot[key] = options;
            }

            return options;
        }
    }

    private JsonSerializerOptions BuildDomainOptions(JsonSerializerOptions source, PairedCodecRegistration? root)
    {
        var o = new JsonSerializerOptions(source)
        {
            NumberHandling = source.NumberHandling | JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        foreach (var reg in paired)
        {
            if (reg.Construct is not null && reg != root && IsTypeLevel(source, reg))
            {
                o.Converters.Insert(0, new PairedDomainConverterFactory(reg));
            }
        }

        if (root?.Construct is not null)
        {
            // first: the root value of this codec is built by its own registration whatever binds the type elsewhere
            o.Converters.Insert(0, new PairedDomainConverterFactory(root));
        }

        if (_byConverter.Count > 0)
        {
            var resolver = source.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            o.TypeInfoResolver = resolver.WithAddedModifier(SwapMemberConverters);
        }

        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>True when the registration's converter is the one System.Text.Json uses for the type itself (options list or type attribute), not only for some members.</summary>
    private static bool IsTypeLevel(JsonSerializerOptions source, PairedCodecRegistration reg)
        => Export.ClrTypeMapper.EffectiveConverterType(source, reg.ClrType) == reg.ConverterType;

    /// <summary>Member-level [JsonConverter] attributes take precedence over options converters; swap them for the domain converter of that (type, converter) registration.</summary>
    private void SwapMemberConverters(JsonTypeInfo typeInfo)
    {
        foreach (var prop in typeInfo.Properties)
        {
            if (prop.CustomConverter is { } converter && _byConverter.TryGetValue((Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType, converter.GetType()), out var reg))
            {
                prop.CustomConverter = new PairedDomainConverterFactory(reg);
            }
        }
    }
}

/// <summary>Creates the domain converter for a paired CLR type (and its Nullable form).</summary>
public sealed class PairedDomainConverterFactory(PairedCodecRegistration registration) : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == registration.ClrType || Nullable.GetUnderlyingType(typeToConvert) == registration.ClrType;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (Nullable.GetUnderlyingType(typeToConvert) is { } inner)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(NullablePairedDomainConverter<>).MakeGenericType(inner), registration)!;
        }

        return (JsonConverter)Activator.CreateInstance(typeof(PairedDomainConverter<>).MakeGenericType(typeToConvert), registration)!;
    }
}

public sealed class PairedDomainConverter<T>(PairedCodecRegistration registration) : JsonConverter<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var ast = DomainAst.FromElement(document.RootElement);
        try
        {
            return (T)registration.Construct!(ast);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            throw new JsonException("paired domain factory rejected the value: " + e.GetType().Name);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (registration.Project is null)
        {
            throw new NotSupportedException("paired registration has no Project delegate");
        }

        DomainAst.WriteTo(writer, registration.Project(value!));
    }
}

public sealed class NullablePairedDomainConverter<T>(PairedCodecRegistration registration) : JsonConverter<T?> where T : struct
{
    private readonly PairedDomainConverter<T> _inner = new(registration);

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : _inner.Read(ref reader, typeof(T), options);

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            _inner.Write(writer, value.Value, options);
        }
    }
}
