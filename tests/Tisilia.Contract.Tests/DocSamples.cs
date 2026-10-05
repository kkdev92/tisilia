using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

// Shapes for DocumentationTests: their documentation ids and comments, as the compiler writes them into this assembly's XML file.
// Never called.
#pragma warning disable CA1822, CA1052, IDE0060

namespace Tisilia.Contract.Tests.DocSamples;

/// <summary>A widget.</summary>
public class Widget
{
    /// <summary>Makes one.</summary>
    /// <param name="name">Its name.</param>
    public Widget(string name) => Name = name;

    /// <summary>The name.</summary>
    public string Name { get; set; }

    /// <summary>A count.</summary>
    public int Count;

    /// <summary>By index.</summary>
    /// <param name="i">The index.</param>
    public int this[int i] => i;

    /// <summary>Nothing.</summary>
    public void Ping() { }

    /// <summary>By reference.</summary>
    /// <param name="a">a</param>
    /// <param name="b">b</param>
    /// <param name="c">c</param>
    public void Fill(ref int a, out string b, int[][] c) => b = "";

    /// <summary>A grid.</summary>
    /// <param name="g">g</param>
    public void Grid(int[,] g) { }

    /// <summary>A sum.</summary>
    /// <param name="items">items</param>
    public int Sum(List<Dictionary<string, int>> items) => 0;

    /// <summary>Maybe.</summary>
    /// <param name="v">v</param>
    public void Maybe(int? v) { }

    /// <summary>Picks.</summary>
    /// <typeparam name="T">T</typeparam>
    /// <param name="value">value</param>
    /// <param name="list">list</param>
    public T Pick<T>(T value, List<T> list) => value;

    /// <summary>Nested.</summary>
    public class Nested { }
}

/// <summary>A box.</summary>
/// <typeparam name="T">T</typeparam>
public class Box<T>
{
    /// <summary>Puts.</summary>
    /// <param name="value">value</param>
    public void Put(T value) { }

    /// <summary>Inner.</summary>
    /// <typeparam name="A">A</typeparam>
    /// <typeparam name="B">B</typeparam>
    public class Inner<A, B> { }
}

/// <summary>Colours.</summary>
public enum Color
{
    /// <summary>Red.</summary>
    [Description("The colour of blood.")]
    Red,

    /// <summary>Sky.</summary>
    Blue,
}

/// <summary>A point.</summary>
/// <param name="X">Across.</param>
/// <param name="Y">Not this one.</param>
public record Point(int X, [Description("Down.")] int Y);

/// <summary>An order, as a client sends it.</summary>
/// <remarks>Orders are kept for a year.</remarks>
public class Order
{
    /// <summary>The order's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Ignored: the attribute wins.</summary>
    [Description("How many.")]
    [Range(1, 100, MaximumIsExclusive = true)]
    public int Quantity { get; set; }

    [StringLength(8, MinimumLength = 2)]
    [RegularExpression("^[A-Z]+$")]
    public string Code { get; set; } = "";

    [MinLength(1)]
    [MaxLength(5)]
    public string[] Tags { get; set; } = [];

    [AllowedValues("a", "b")]
    [DefaultValue("a")]
    public string Kind { get; set; } = "a";

    [DeniedValues(Color.Red)]
    [DefaultValue(Color.Blue)]
    public Color Shade { get; set; } = Color.Blue;

    /// <summary>Free text.</summary>
    [Obsolete("Use code.")]
    public string? Note { get; set; }

    public string Plain { get; set; } = "";
}

/// <summary>Query values of a list, bound from the query by MVC property by property.</summary>
public class Filter
{
    /// <summary>The page.</summary>
    [Obsolete("Use the cursor.")]
    [Range(1, 10)]
    public int Page { get; set; }
}

/// <summary>The base type's comment.</summary>
public class Base { }

/// <inheritdoc/>
public class Derived : Base { }
