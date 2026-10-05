using System.Text;
using Tisilia.Generator.Canonical;

namespace Tisilia.Generator.TypeScript;

/// <summary>A generated file: relative path (forward slashes), UTF-8/LF content, no BOM, no timestamps.</summary>
public sealed record GeneratedFile(string Path, string Content)
{
    public byte[] Bytes => Encoding.UTF8.GetBytes(Content);

    public string Digest => TisiliaHash.Sha256OfBytes(Bytes);
}

/// <summary>Indented code writer producing LF line endings.</summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _sb.Append('\n');
        }
        else
        {
            _sb.Append(' ', _indent * 2).Append(text).Append('\n');
        }

        return this;
    }

    public CodeWriter Open(string text)
    {
        Line(text);
        _indent++;
        return this;
    }

    public CodeWriter Close(string text = "}")
    {
        _indent--;
        Line(text);
        return this;
    }

    /// <summary>Appends pre-formatted text verbatim (no indentation), ensuring it ends with a newline.</summary>
    public CodeWriter Raw(string text)
    {
        _sb.Append(text);
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            _sb.Append('\n');
        }

        return this;
    }

    public override string ToString() => _sb.ToString();
}
