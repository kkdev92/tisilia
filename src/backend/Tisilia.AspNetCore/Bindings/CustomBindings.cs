using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tisilia.Contract;

namespace Tisilia.AspNetCore.Bindings;

/// <summary>
/// What a parameter's own binding code reads from the request: a type's <c>BindAsync</c> (minimal APIs) or an MVC model binder
/// (<c>[ModelBinder]</c>). Tisilia cannot see into that code, so it exports such a parameter only as declared here: each value it
/// reads becomes a route, query or header parameter of the contract, which the client writes as that type's canonical text and the
/// application's code parses (server acceptance <c>server-parsed</c>). <c>tisilia doctor --allow-execute-binders</c> calls the
/// code with a request that records what it reads and reports reads outside the declaration.
/// </summary>
public sealed class CustomBindingCollection
{
    private readonly Dictionary<Type, RequestReads> _bindAsync = [];
    private readonly Dictionary<Type, RequestReads> _modelBinders = [];

    /// <summary>Declares what <typeparamref name="T"/>'s <c>BindAsync</c> reads from the request.</summary>
    public CustomBindingCollection BindAsync<T>(Action<RequestReads> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var declared = new RequestReads();
        reads(declared);
        _bindAsync[typeof(T)] = declared;
        return this;
    }

    /// <summary>
    /// Declares what the MVC model binder <typeparamref name="TBinder"/> reads; <see cref="RequestReads.ModelName"/> stands for the
    /// name it binds under (the parameter name, or the <c>[ModelBinder(Name)]</c>).
    /// </summary>
    public CustomBindingCollection ModelBinder<TBinder>(Action<RequestReads> reads) where TBinder : IModelBinder
    {
        ArgumentNullException.ThrowIfNull(reads);
        var declared = new RequestReads();
        reads(declared);
        _modelBinders[typeof(TBinder)] = declared;
        return this;
    }

    internal RequestReads? ForBindAsync(Type type) => _bindAsync.GetValueOrDefault(Nullable.GetUnderlyingType(type) ?? type);

    internal RequestReads? ForModelBinder(Type binder) => _modelBinders.GetValueOrDefault(binder);

    internal IEnumerable<KeyValuePair<Type, RequestReads>> BindAsyncDeclarations => _bindAsync;

    internal IEnumerable<KeyValuePair<Type, RequestReads>> ModelBinderDeclarations => _modelBinders;
}

/// <summary>The request values a custom binding reads: each becomes a parameter of the contract.</summary>
public sealed class RequestReads
{
    /// <summary>For a model binder: the name it binds under — the parameter name, or the <c>[ModelBinder(Name)]</c>.</summary>
    public const string ModelName = "{model}";

    internal List<RequestRead> Values { get; } = [];

    internal bool NothingFromRequest { get; private set; }

    /// <summary>A route value: the route template must have it.</summary>
    public RequestReads Route<T>(string name) => Add(ParameterLocation.Path, name, typeof(T), optional: false);

    /// <summary>A query value; an array or list type reads every value of the name, a nullable or optional one may be absent.</summary>
    public RequestReads Query<T>(string name, bool optional = false) => Add(ParameterLocation.Query, name, typeof(T), optional);

    /// <summary>A header; an array or list type reads every value of the name, a nullable or optional one may be absent.</summary>
    public RequestReads Header<T>(string name, bool optional = false) => Add(ParameterLocation.Header, name, typeof(T), optional);

    /// <summary>The binding reads nothing the client sends — claims, features, services: the parameter is not part of the contract.</summary>
    public RequestReads NotFromRequest()
    {
        NothingFromRequest = true;
        return this;
    }

    private RequestReads Add(ParameterLocation location, string name, Type type, bool optional)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Values.Add(new RequestRead(location, name, type, optional));
        return this;
    }
}

internal sealed record RequestRead(ParameterLocation Location, string Name, Type Type, bool Optional);
