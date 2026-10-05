using Tisilia.Generator.TypeScript;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Generated files import only what they use (noUnusedLocals in the create-vite TypeScript templates).</summary>
public class TsImportsTests
{
    [Fact]
    public void Unused_specifiers_and_imports_are_dropped()
    {
        const string file = """
            // header
            import { execute, prepareRequest } from "@kkdev92/tisilia-runtime";
            import type { Guid, Int64, Duration as TisiliaDuration } from "@kkdev92/tisilia-runtime";
            import type * as models from "../models/index.js";
            import * as codecs from "../codecs/index.js";

            export type A = { readonly id: Guid; readonly length: TisiliaDuration };
            export const run = () => execute(codecs.aCodec);
            """;
        Assert.Equal("""
            // header
            import { execute } from "@kkdev92/tisilia-runtime";
            import type { Guid, Duration as TisiliaDuration } from "@kkdev92/tisilia-runtime";
            import * as codecs from "../codecs/index.js";

            export type A = { readonly id: Guid; readonly length: TisiliaDuration };
            export const run = () => execute(codecs.aCodec);
            """, TsImports.Prune(file));
    }

    [Theory]
    // comments and string contents never count as a use
    [InlineData("/** Guid of the user */ const a = 1;", "Guid", false)]
    [InlineData("const a = 1; // Guid", "Guid", false)]
    [InlineData("const a = \"Guid\";", "Guid", false)]
    [InlineData("const a = 'it\\'s Guid';", "Guid", false)]
    [InlineData("const a = `Guid ${x}`;", "Guid", false)]
    [InlineData("const a = \"//\"; const b: Guid = c;", "Guid", true)]
    // a template substitution is code
    [InlineData("const a = `id ${Guid.from(x)} done`;", "Guid", true)]
    [InlineData("const a = `id ${f({ k: Guid })} done`;", "Guid", true)]
    // a member after "." is not the import, a spread is
    [InlineData("const a = models.Guid;", "Guid", false)]
    [InlineData("const a = x?.Guid;", "Guid", false)]
    [InlineData("const a = { ...Guid };", "Guid", true)]
    // a property key at the start of a line (interface member, enum member, object entry) is not a use; its type is
    [InlineData("  readonly Guid: number;", "Guid", false)]
    [InlineData("  Guid: 1,", "Guid", false)]
    [InlineData("  readonly id?: Guid;", "Guid", true)]
    [InlineData("  Guid: Guid,", "Guid", true)]
    // a bigint suffix is not an identifier
    [InlineData("const a = 1n;", "n", false)]
    public void Uses_are_identifiers_of_the_code(string code, string name, bool used) => Assert.Equal(used, TsImports.UsedIdentifiers(code).Contains(name));

    [Fact]
    public void Line_breaks_survive_comment_removal()
    {
        Assert.Equal("a\n\nb", TsImports.CodeOnly("a\n/*\n*/b").Replace(" ", "", StringComparison.Ordinal));
    }
}
