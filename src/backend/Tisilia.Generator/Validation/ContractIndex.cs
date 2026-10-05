using Tisilia.Contract;

namespace Tisilia.Generator.Validation;

/// <summary>Id-indexed view of a contract. Ids are looked up ordinally; duplicates are reported by SV02 before use.</summary>
public sealed class ContractIndex
{
    public ContractIndex(ContractDocument document)
    {
        Document = document;
        Profiles = Unique(document.Profiles, p => p.Id);
        Types = Unique(document.Types, t => t.Id);
        Wires = Unique(document.Wires, w => w.Id);
        Codecs = Unique(document.Codecs, c => c.Id);
        Bindings = Unique(document.Bindings, b => b.Id);
        Equivalences = Unique(document.Equivalences, e => e.Id);
        Projections = Unique(document.Projections, p => p.Id);
        Comparers = Unique(document.Comparers, c => c.Id);
        Binders = Unique(document.Binders, b => b.Id);
        ResultAdapters = Unique(document.ResultAdapters, r => r.Id);
        Operations = Unique(document.Operations, o => o.Id);
        Modules = Unique(document.Modules, m => m.Id);
        var behaviors = new Dictionary<string, (Profile Profile, Behavior Behavior)>(StringComparer.Ordinal);
        foreach (var profile in document.Profiles)
        {
            foreach (var behavior in profile.Behaviors)
            {
                behaviors.TryAdd(behavior.Id, (profile, behavior));
            }
        }

        Behaviors = behaviors;
        var codecsByType = new Dictionary<string, List<Codec>>(StringComparer.Ordinal);
        foreach (var codec in document.Codecs)
        {
            if (!codecsByType.TryGetValue(codec.TypeId, out var list))
            {
                list = [];
                codecsByType[codec.TypeId] = list;
            }

            list.Add(codec);
        }

        CodecsByType = codecsByType;
    }

    public ContractDocument Document { get; }
    public IReadOnlyDictionary<string, Profile> Profiles { get; }
    public IReadOnlyDictionary<string, Model> Types { get; }
    public IReadOnlyDictionary<string, Wire> Wires { get; }
    public IReadOnlyDictionary<string, Codec> Codecs { get; }
    public IReadOnlyDictionary<string, Binding> Bindings { get; }
    public IReadOnlyDictionary<string, Equivalence> Equivalences { get; }
    public IReadOnlyDictionary<string, Projection> Projections { get; }
    public IReadOnlyDictionary<string, Contract.Comparer> Comparers { get; }
    public IReadOnlyDictionary<string, Binder> Binders { get; }
    public IReadOnlyDictionary<string, ResultAdapter> ResultAdapters { get; }
    public IReadOnlyDictionary<string, Operation> Operations { get; }
    public IReadOnlyDictionary<string, Module> Modules { get; }
    public IReadOnlyDictionary<string, (Profile Profile, Behavior Behavior)> Behaviors { get; }
    public IReadOnlyDictionary<string, List<Codec>> CodecsByType { get; }

    private static Dictionary<string, T> Unique<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var dict = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            dict.TryAdd(key(item), item);
        }

        return dict;
    }
}
