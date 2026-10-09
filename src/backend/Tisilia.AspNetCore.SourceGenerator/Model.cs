using System.Collections;
using Microsoft.CodeAnalysis;

namespace Tisilia.AspNetCore.SourceGenerator;

/// <summary>
/// An operation the source registers: where its id is declared, its handler, and either the responses every return path of the
/// handler produces or the first path that could not be read. Types are C# type expressions (<c>global::App.Todo</c>) the
/// generated code names with <c>typeof</c>; null where generated code cannot name the type.
/// </summary>
internal sealed record OperationModel(
    string Id,
    SourceSpot At,
    string Flavor,
    HandlerModel Handler,
    string? Failure,
    SourceSpot? FailureAt,
    EquatableArray<ResponseModel> Responses,
    EquatableArray<HelperModel> Helpers,
    EquatableArray<WriteModel> Writes);

/// <summary>A place in a source file: the full path (made relative to the project when emitted), 1-based line and column.</summary>
internal sealed record SourceSpot(string Path, int Line, int Column)
{
    public static SourceSpot Of(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceSpot(span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}

/// <summary>
/// The handler, for the exporter to check that the endpoint it describes runs this code: a method by its declaring type and name,
/// a lambda or local function by the type it is written in. Both by parameter names and types and the return type.
/// </summary>
internal sealed record HandlerModel(string Kind, string? ContainingType, string? Name, EquatableArray<ParameterModel> Parameters, string? Returns);

internal sealed record ParameterModel(string Name, string? Type);

/// <summary>
/// A response a return path produces. <see cref="Result"/> is a TypedResults type whose own endpoint metadata describes it (the
/// exporter reads it from the type); otherwise <see cref="Status"/>, <see cref="Kind"/> (none, json, text, value, problem,
/// validation), the body type and the media type the helper writes.
/// </summary>
internal sealed record ResponseModel(int? Status, string Kind, string? Result, string? Body, string? Media);

/// <summary>A ControllerBase helper an action calls, which the exporter checks the controller does not override.</summary>
internal sealed record HelperModel(string Name, EquatableArray<string> Parameters);

/// <summary>
/// A ControllerBase helper an action passes a value to, with the status code it writes (null when the source does not fix it).
/// MVC writes such a value with its own type (an ObjectResult without a DeclaredType), whatever the action declares: recorded for
/// every MVC action, ActionResult&lt;T&gt; included, and also when another path cannot be read.
/// </summary>
internal sealed record WriteModel(int? Status, string Helper);

/// <summary>An immutable array compared by its elements, so that the generator's models compare by value between runs.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    private readonly T[]? items;

    public EquatableArray(IEnumerable<T> items)
    {
        this.items = items.ToArray();
    }

    public int Length => items?.Length ?? 0;

    public T this[int index] => items![index];

    public bool Equals(EquatableArray<T> other)
    {
        if (Length != other.Length)
        {
            return false;
        }

        for (var i = 0; i < Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(items![i], other.items![i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in this)
        {
            hash = unchecked((hash * 31) + EqualityComparer<T>.Default.GetHashCode(item!));
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
