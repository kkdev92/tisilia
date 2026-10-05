namespace Tisilia;

/// <summary>
/// Marks an MVC action as an explicitly registered Tisilia operation with a stable identifier.
/// Identifiers are never derived from action names or source positions.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TisiliaOperationAttribute : Attribute
{
    public TisiliaOperationAttribute(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        OperationId = operationId;
    }

    /// <summary>Stable operation identifier, unique across the API.</summary>
    public string OperationId { get; }

    /// <summary>Optional display tags copied to <c>operation.tags</c>.</summary>
    public string[]? Tags { get; init; }
}
