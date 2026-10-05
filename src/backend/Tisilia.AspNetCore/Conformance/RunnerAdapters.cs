using System.Text.Json;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Generator.Building;

namespace Tisilia.AspNetCore.Conformance;

/// <summary>
/// What the C# runner needs to execute one codec id: the closed CLR type, the effective serializer options of the
/// profile the codec was exported under and, for paired codecs, the registration with its trusted projection/factory
/// delegates. Recorded by the exporter while mapping types, never inferred at runner time. <c>Converter</c> is
/// the converter instance in effect for a member-level binding (property context); null when the type's own converter applies.
/// </summary>
public sealed record RunnerAdapter(string CodecId, string ProfileId, Type ClrType, JsonSerializerOptions Options, NumberProfile Numbers, PairedCodecRegistration? Paired, System.Text.Json.Serialization.JsonConverter? Converter = null);

/// <summary>Codec id → adapters (one per profile that reached the codec).</summary>
public sealed class RunnerAdapterTable
{
    private readonly Dictionary<string, List<RunnerAdapter>> _adapters = new(StringComparer.Ordinal);

    public void Add(RunnerAdapter adapter)
    {
        if (!_adapters.TryGetValue(adapter.CodecId, out var list))
        {
            list = [];
            _adapters[adapter.CodecId] = list;
        }

        if (!list.Any(a => a.ProfileId == adapter.ProfileId))
        {
            list.Add(adapter);
        }
    }

    public RunnerAdapter? Find(string codecId, string profileId)
    {
        if (!_adapters.TryGetValue(codecId, out var list))
        {
            return null;
        }

        return list.FirstOrDefault(a => a.ProfileId == profileId) ?? (list.Count == 1 && string.IsNullOrEmpty(profileId) ? list[0] : null);
    }

    public IEnumerable<string> CodecIds => _adapters.Keys;
}
