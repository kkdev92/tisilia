using System.Collections;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Tisilia.AspNetCore.Bindings;
using Tisilia.Contract;

namespace Tisilia.AspNetCore.Export;

/// <summary>
/// One declared custom binding called with a request that records what it reads: <c>matches</c> when it read nothing the
/// declaration does not name, <c>mismatch</c> otherwise (<see cref="UndeclaredReads"/>), <c>failed</c> when the code threw
/// (<see cref="Failure"/> names the exception type; its text is withheld). <see cref="UnreadDeclarations"/> lists declared values this
/// call did not read, which code may read only on some requests. Route values cannot be recorded (RouteValueDictionary is not
/// replaceable); the declared ones are supplied.
/// </summary>
public sealed record DoctorBindingProbe(string OperationId, string Parameter, string Binding, string Status, IReadOnlyList<string> UndeclaredReads,
    IReadOnlyList<string> UnreadDeclarations, string? Failure = null);

public sealed partial class TisiliaContractExporter
{
    /// <summary>
    /// Calls every declared custom binding of the selected operations — a type's BindAsync, an MVC model binder — with a request that
    /// records the query values, headers, cookies, body and form it reads (and, for a model binder, the values it asks its value
    /// provider for), and compares them with the declaration (TisiliaOptions.CustomBinding). This runs application code.
    /// </summary>
    public async Task<IReadOnlyList<DoctorBindingProbe>> ProbeCustomBindingsAsync(CancellationToken cancellationToken = default)
    {
        var probes = new List<DoctorBindingProbe>();
        var declarations = options.Value.CustomBinding;
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>() is not null).OrderBy(e => e.Metadata.GetMetadata<TisiliaOperationAttribute>()!.OperationId, StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            var operationId = endpoint.Metadata.GetMetadata<TisiliaOperationAttribute>()!.OperationId;
            foreach (var bound in endpoint.Metadata.GetOrderedMetadata<IParameterBindingMetadata>().Where(b => b.HasBindAsync))
            {
                var type = Nullable.GetUnderlyingType(bound.ParameterInfo.ParameterType) ?? bound.ParameterInfo.ParameterType;
                if (declarations.ForBindAsync(type) is not { } reads) { continue; }
                probes.Add(await ProbeAsync(operationId, bound.Name, $"{FriendlyName(type)}.BindAsync", reads, bound.Name, endpoint,
                    context => InvokeBindAsync(type, context, bound.ParameterInfo), cancellationToken));
            }
            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action) { continue; }
            foreach (var parameter in action.Parameters.OfType<ControllerParameterDescriptor>())
            {
                if (parameter.BindingInfo?.BinderType is not { } binderType || declarations.ForModelBinder(binderType) is not { } reads) { continue; }
                var modelName = parameter.BindingInfo.BinderModelName ?? parameter.Name;
                probes.Add(await ProbeAsync(operationId, parameter.Name, $"model binder {FriendlyName(binderType)}", reads, modelName, endpoint,
                    context => InvokeModelBinderAsync(binderType, parameter, modelName, context), cancellationToken));
            }
        }
        return probes;
    }

    private async Task<DoctorBindingProbe> ProbeAsync(string operationId, string parameter, string binding, RequestReads reads, string modelName, RouteEndpoint endpoint,
        Func<RecordingContext, Task> invoke, CancellationToken cancellationToken)
    {
        var declared = reads.Values.Select(r => (r.Location, Name: r.Name.Replace(RequestReads.ModelName, modelName, StringComparison.Ordinal))).ToList();
        await using var scope = services.CreateAsyncScope();
        var recording = new RecordingContext(scope.ServiceProvider, declared.Where(d => d.Location == ParameterLocation.Path).Select(d => d.Name), endpoint, cancellationToken);
        try
        {
            await invoke(recording);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DoctorBindingProbe(operationId, parameter, binding, "failed", [], [], "the binding threw " + (e is TargetInvocationException { InnerException: { } inner } ? inner : e).GetType().Name);
        }
        // a model binder asks its value provider by name, whatever source it comes from: a declared value of any location answers it
        bool Declared(string read) => read.Split(':', 2) is [var location, var name] && (location == "value"
            ? declared.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            : declared.Any(d => Location(d.Location) == location && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));
        var undeclared = recording.Reads.Where(r => !Declared(r)).Order(StringComparer.Ordinal).ToArray();
        var unread = reads.NothingFromRequest ? [] : declared.Where(d => d.Location != ParameterLocation.Path
            && !recording.Reads.Contains(Location(d.Location) + ":" + d.Name) && !recording.Reads.Contains("value:" + d.Name)).Select(d => Location(d.Location) + ":" + d.Name).ToArray();
        return new DoctorBindingProbe(operationId, parameter, binding, undeclared.Length == 0 ? "matches" : "mismatch", undeclared, unread);
    }

    private static string Location(ParameterLocation location) => location switch { ParameterLocation.Path => "route", ParameterLocation.Query => "query", _ => "header" };

    private static async Task InvokeBindAsync(Type type, RecordingContext recording, ParameterInfo parameter)
    {
        // IBindableFromHttpContext<T>.BindAsync(HttpContext, ParameterInfo), else a public static BindAsync(HttpContext[, ParameterInfo])
        var bindable = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBindableFromHttpContext<>));
        var method = bindable is not null ? type.GetInterfaceMap(bindable).TargetMethods.Single(m => m.Name.EndsWith("BindAsync", StringComparison.Ordinal))
            : type.GetMethod("BindAsync", BindingFlags.Public | BindingFlags.Static, [typeof(HttpContext), typeof(ParameterInfo)])
              ?? type.GetMethod("BindAsync", BindingFlags.Public | BindingFlags.Static, [typeof(HttpContext)])
              ?? throw new InvalidOperationException("no BindAsync");
        var result = method.Invoke(null, method.GetParameters().Length == 2 ? [recording.Context, parameter] : [recording.Context])!;
        await (Task)result.GetType().GetMethod("AsTask")!.Invoke(result, null)!;
    }

    private async Task InvokeModelBinderAsync(Type binderType, ControllerParameterDescriptor parameter, string modelName, RecordingContext recording)
    {
        var binder = (IModelBinder)ActivatorUtilities.CreateInstance(recording.Services, binderType);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(parameter.ParameterType);
        var actionContext = new ActionContext(recording.Context, new RouteData(recording.Context.Request.RouteValues), new ActionDescriptor());
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(actionContext, new RecordingValueProvider(recording), metadata, parameter.BindingInfo, modelName);
        await binder.BindModelAsync(bindingContext);
    }

    /// <summary>A request whose query, headers, cookies, body and form record every value read from them.</summary>
    private sealed class RecordingContext
    {
        public HashSet<string> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HttpContext Context { get; }
        public IServiceProvider Services { get; }

        public RecordingContext(IServiceProvider services, IEnumerable<string> routeValues, RouteEndpoint endpoint, CancellationToken cancellationToken)
        {
            Services = services;
            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(new HttpRequestFeature
            {
                Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET",
                Path = "/",
                Headers = new RecordingHeaders(this),
                Body = new RecordingStream(this),
            });
            features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
            features.Set<IQueryFeature>(new QueryFeature(new RecordingQuery(this)));
            features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(new RecordingCookies(this)));
            features.Set<IFormFeature>(new RecordingForm(this));
            // a route value is supplied, not recorded: the declared ones read as text
            features.Set<IRouteValuesFeature>(new RouteValuesFeature { RouteValues = new RouteValueDictionary(routeValues.ToDictionary(v => v, _ => (object?)"1")) });
            features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = cancellationToken });
            Context = new DefaultHttpContext(features) { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity()) };
        }

        public void Read(string location, string name) => Reads.Add(location + ":" + name);
    }

    private sealed class RecordingQuery(RecordingContext recording) : IQueryCollection
    {
        public StringValues this[string key] { get { recording.Read("query", key); return StringValues.Empty; } }
        public int Count => 0;
        public ICollection<string> Keys { get { recording.Read("query", "*"); return []; } }
        public bool ContainsKey(string key) { recording.Read("query", key); return false; }
        public bool TryGetValue(string key, out StringValues value) { recording.Read("query", key); value = StringValues.Empty; return false; }
        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() { recording.Read("query", "*"); return Enumerable.Empty<KeyValuePair<string, StringValues>>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingCookies(RecordingContext recording) : IRequestCookieCollection
    {
        public string? this[string key] { get { recording.Read("cookie", key); return null; } }
        public int Count => 0;
        public ICollection<string> Keys { get { recording.Read("cookie", "*"); return []; } }
        public bool ContainsKey(string key) { recording.Read("cookie", key); return false; }
        public bool TryGetValue(string key, out string value) { recording.Read("cookie", key); value = ""; return false; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() { recording.Read("cookie", "*"); return Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingHeaders(RecordingContext recording) : IHeaderDictionary
    {
        private readonly HeaderDictionary _inner = new();
        public StringValues this[string key] { get { recording.Read("header", key); return _inner[key]; } set => _inner[key] = value; }
        public long? ContentLength { get { recording.Read("header", "Content-Length"); return _inner.ContentLength; } set => _inner.ContentLength = value; }
        public ICollection<string> Keys { get { recording.Read("header", "*"); return _inner.Keys; } }
        public ICollection<StringValues> Values { get { recording.Read("header", "*"); return _inner.Values; } }
        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public void Add(string key, StringValues value) => _inner.Add(key, value);
        public void Add(KeyValuePair<string, StringValues> item) => _inner.Add(item);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<string, StringValues> item) { recording.Read("header", item.Key); return _inner.Contains(item); }
        public bool ContainsKey(string key) { recording.Read("header", key); return _inner.ContainsKey(key); }
        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) { recording.Read("header", "*"); _inner.CopyTo(array, arrayIndex); }
        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() { recording.Read("header", "*"); return _inner.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(string key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<string, StringValues> item) => _inner.Remove(item);
        public bool TryGetValue(string key, out StringValues value) { recording.Read("header", key); return _inner.TryGetValue(key, out value); }
    }

    private sealed class RecordingForm(RecordingContext recording) : IFormFeature
    {
        public bool HasFormContentType { get { recording.Read("form", "*"); return false; } }
        public IFormCollection? Form { get { recording.Read("form", "*"); return FormCollection.Empty; } set { } }
        public IFormCollection ReadForm() { recording.Read("form", "*"); return FormCollection.Empty; }
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) { recording.Read("form", "*"); return Task.FromResult<IFormCollection>(FormCollection.Empty); }
    }

    private sealed class RecordingStream(RecordingContext recording) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { recording.Read("body", "*"); return 0; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { recording.Read("body", "*"); return ValueTask.FromResult(0); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { recording.Read("body", "*"); return Task.FromResult(0); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>MVC's value providers merge the route, query and form: a model binder's reads are recorded by name.</summary>
    private sealed class RecordingValueProvider(RecordingContext recording) : IValueProvider
    {
        public bool ContainsPrefix(string prefix) { recording.Read("value", prefix); return false; }
        public ValueProviderResult GetValue(string key) { recording.Read("value", key); return ValueProviderResult.None; }
    }
}
