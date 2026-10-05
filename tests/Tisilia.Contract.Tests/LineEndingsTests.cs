using System.Text;
using Tisilia.Generator.Canonical;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>Text Tisilia writes survives a CRLF checkout (Git for Windows' default core.autocrlf=true); module bytes stay exact.</summary>
public class LineEndingsTests
{
    /// <summary>What git does to a text file on checkout with core.autocrlf=true: every LF becomes CRLF.</summary>
    internal static void CheckOutWithCrLf(string path)
    {
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Crlf_reads_as_lf_and_a_lone_cr_stays()
    {
        Assert.Equal(Utf8("a\nb\n"), LineEndings.ToLf(Utf8("a\r\nb\r\n")));
        Assert.Equal(Utf8("a\rb\n\n"), LineEndings.ToLf(Utf8("a\rb\r\n\r\n")));
        Assert.Equal(Utf8("é\n"), LineEndings.ToLf(Utf8("é\r\n")));
        Assert.True(LineEndings.SameText(Utf8("x\r\ny\n"), Utf8("x\ny\r\n")));
        Assert.False(LineEndings.SameText(Utf8("x\ry"), Utf8("x\ny")));
    }

    [Fact]
    public void A_file_still_holds_what_was_written_through_a_crlf_checkout_but_not_through_an_edit()
    {
        var written = Utf8("export const a = 1;\n");
        var digest = TisiliaHash.Sha256OfBytes(written);
        Assert.True(LineEndings.StillWritten(written, digest));
        Assert.True(LineEndings.StillWritten(Utf8("export const a = 1;\r\n"), digest));
        Assert.False(LineEndings.StillWritten(Utf8("export const a = 2;\r\n"), digest));
    }

    [Fact]
    public void A_module_artifact_that_differs_only_in_line_endings_says_so_in_both_directions()
    {
        var file = Path.Combine(Path.GetTempPath(), "modules", "demo.money", "money.js");
        var lf = Utf8("export const a = 1;\nexport const b = 2;\n");
        var crlf = Utf8("export const a = 1;\r\nexport const b = 2;\r\n");
        // a CRLF checkout of an artifact exported from LF bytes
        var checkout = LineEndings.ArtifactHint(crlf, TisiliaHash.Sha256OfBytes(lf), file);
        Assert.NotNull(checkout);
        Assert.Contains("git checked it out with CRLF", checkout, StringComparison.Ordinal);
        Assert.Contains("in " + Path.GetDirectoryName(file), checkout, StringComparison.Ordinal);
        // an LF checkout of an artifact exported from a CRLF checkout
        var exported = LineEndings.ArtifactHint(lf, TisiliaHash.Sha256OfBytes(crlf), file);
        Assert.NotNull(exported);
        Assert.Contains("exported from a checkout with CRLF", exported, StringComparison.Ordinal);
        Assert.EndsWith("then export again", exported, StringComparison.Ordinal);
        // another module altogether
        Assert.Null(LineEndings.ArtifactHint(Utf8("export const a = 3;\n"), TisiliaHash.Sha256OfBytes(lf), file));
    }

    [Fact]
    public void The_pinning_gitattributes_names_the_files_or_the_whole_folder()
    {
        var named = LineEndings.PinningGitAttributes("test", ["demo.portable.portable.js", "demo.portable.portable.d.ts"]);
        Assert.EndsWith("/demo.portable.portable.js -text\n/demo.portable.portable.d.ts -text\n", named, StringComparison.Ordinal);
        Assert.All(named.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.StartsWith('#') || line.EndsWith(" -text", StringComparison.Ordinal), line));
        Assert.EndsWith("\n* -text\n", LineEndings.PinningGitAttributes("test"), StringComparison.Ordinal);
        // a name a pattern cannot carry as it is pins the folder instead
        Assert.EndsWith("\n* -text\n", LineEndings.PinningGitAttributes("test", ["my module.js"]), StringComparison.Ordinal);
    }
}
