namespace Tisilia;

/// <summary>
/// Declares that the endpoint's server-sent events resume where a reconnecting client left off: the client sends the id of the
/// last event it received in <c>Last-Event-ID</c>, and the handler continues with the events after that one. Every event the
/// handler writes therefore carries its own id (<c>SseItem&lt;T&gt;.EventId</c>). A client reconnects only to an operation that
/// declares this, and only when the caller asks (the runtime's <c>reconnect</c> option).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TisiliaEventResumeAttribute : Attribute
{
}
