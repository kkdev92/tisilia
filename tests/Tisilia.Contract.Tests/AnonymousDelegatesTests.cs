using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tisilia.Generator.Canonical;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Assemblies whose anonymous delegate types two builds of the same source can number differently (SV44 export warning).</summary>
public sealed class AnonymousDelegatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-delegates-" + Guid.NewGuid().ToString("N"));

    public AnonymousDelegatesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_generic_one_created_in_more_than_one_place_is_reported_with_the_methods_that_create_it()
    {
        var assembly = Compile("Handlers", """
            using System;
            using System.Threading.Tasks;

            namespace App;

            public static class Handlers
            {
                public static int Get(int id, string? filter = null) => id;

                public static Delegate ByMethod() => Get;

                public static async Task<Delegate> Later()
                {
                    await Task.Yield();
                    return (int id, string? filter = null) => id;
                }

                public static Func<Delegate> Nested() => () => (int id, string? filter = null) => id;

                public static Delegate Params() => (params int[] values) => values.Length;
            }
            """);

        // the method group, the async method and the lambda in a lambda create one type; the params lambda its own, in one place
        var numbered = Assert.Single(AnonymousDelegates.UnstablyNumbered(assembly));
        Assert.Matches("^<>f__AnonymousDelegate[01]`3$", numbered.Name);
        Assert.Equal(3, numbered.Uses);
        Assert.Equal(["Handlers.ByMethod", "Handlers.Later", "Handlers.Nested"], numbered.Methods);
    }

    [Fact]
    public void Types_that_always_get_the_same_number_are_not_reported()
    {
        // one numbered type is always number 0
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(Compile("OneType", """
            public static class A { public static System.Delegate Get() => (int id, string? filter = null) => id; }
            public static class B { public static System.Delegate Get() => (int id, string? filter = null) => id; }
            """)));

        // a type created in one place has one location
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(Compile("CreatedOnce", """
            public static class A { public static System.Delegate Get() => (int id, string? filter = null) => id; }
            public static class B { public static System.Delegate Get() => (int a, int b, string? filter = null) => a + b; }
            """)));

        // a pointer is no type argument: the type is not generic, and the compiler keeps the earliest of its locations
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(Compile("NotGeneric", """
            public static unsafe class A { public static System.Delegate Get() => (int* p, int x = 0) => x; }
            public static unsafe class B { public static System.Delegate Get() => (int* p, int x = 0) => x; }
            public static class C { public static System.Delegate Get() => (int id, string? filter = null) => id; }
            """)));

        // without an optional or params parameter a lambda is a Func or an Action
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(Compile("NoOptional", """
            public static class A { public static System.Delegate Get() => (int id, string? filter) => id; }
            public static class B { public static System.Delegate Get() => (int a, int b, string? filter) => a + b; }
            public static class C { public static System.Delegate Get() => (int id, string? filter) => id; }
            """)));
    }

    [Fact]
    public void A_file_that_is_no_assembly_reports_nothing()
    {
        var text = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(text, "not an assembly");
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(text));
        Assert.Empty(AnonymousDelegates.UnstablyNumbered(Path.Combine(_dir, "missing.dll")));
    }

    // compiled as the SDK compiles a project, binding method bodies in parallel, into a file of its own
    private string Compile(string name, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: name + ".cs");
        var compilation = CSharpCompilation.Create(name, [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true, deterministic: true));
        var path = Path.Combine(_dir, name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        // the framework this process runs
        var shared = Path.DirectorySeparatorChar + "shared" + Path.DirectorySeparatorChar + "Microsoft.NETCore.App" + Path.DirectorySeparatorChar;
        return [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => path.Contains(shared, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });
}
