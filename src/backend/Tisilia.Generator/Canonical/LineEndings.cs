namespace Tisilia.Generator.Canonical;

/// <summary>
/// Git for Windows checks text files out with CRLF unless .gitattributes says otherwise: its installer sets
/// <c>core.autocrlf=true</c> by default, GitHub's Windows runners included. Text Tisilia writes itself (generated code, its
/// manifests, contracts) is therefore compared with CRLF read as LF, so a checkout neither makes it stale nor looks like an edit.
/// Module artifacts are code whose bytes the contract binds (SV44) and stay byte-exact; a mismatch that is only line endings
/// says so (<see cref="ArtifactHint"/>).
/// </summary>
public static class LineEndings
{
    /// <summary>
    /// The .gitattributes written beside module artifacts Tisilia generates or installs (when the folder has none): the files named
    /// (relative to the folder; null for every file in it) are stored and checked out byte for byte.
    /// </summary>
    public static string PinningGitAttributes(string writtenBy, IEnumerable<string>? files = null)
    {
        var patterns = files?.ToList() is { Count: > 0 } named && named.All(f => !f.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\'))
            ? named.Select(f => "/" + f)
            : ["*"];
        return $"# Written by {writtenBy}: the contract records these files' digests byte for byte (SV44),\n# so git must not convert their line endings (core.autocrlf=true is the Git for Windows default).\n"
            + string.Concat(patterns.Select(p => p + " -text\n"));
    }

    /// <summary>Writes <see cref="PinningGitAttributes"/> into <paramref name="directory"/> unless it has a .gitattributes; whether it did.</summary>
    public static bool PinDirectory(string directory, string writtenBy, IEnumerable<string>? files = null)
    {
        var path = Path.Combine(directory, ".gitattributes");
        if (File.Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, PinningGitAttributes(writtenBy, files));
        return true;
    }

    /// <summary>The bytes with every CRLF pair as LF (a lone CR stays). In UTF-8, 0x0D 0x0A is only ever CR LF.</summary>
    public static byte[] ToLf(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length];
        var length = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            result[length++] = bytes[i];
        }

        return result.AsSpan(0, length).ToArray();
    }

    /// <summary>Whether two texts are the same once CRLF is read as LF.</summary>
    public static bool SameText(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => ToLf(a).AsSpan().SequenceEqual(ToLf(b));

    /// <summary>SHA-256 of a text file with CRLF read as LF: the digest is the same in every checkout.</summary>
    public static string Sha256OfTextFile(string path) => TisiliaHash.Sha256OfBytes(ToLf(File.ReadAllBytes(path)));

    /// <summary>Whether a file still holds what Tisilia wrote with <paramref name="recordedDigest"/>, its line endings converted or not.</summary>
    public static bool StillWritten(ReadOnlySpan<byte> current, string recordedDigest)
        => TisiliaHash.Sha256OfBytes(current) == recordedDigest || TisiliaHash.Sha256OfBytes(ToLf(current)) == recordedDigest;

    /// <summary>
    /// For a module artifact (<paramref name="file"/>) whose digest does not match: a sentence when it differs from the expected
    /// file only in its line endings (a CRLF checkout of an LF artifact, or a contract exported from a CRLF checkout), else null.
    /// </summary>
    public static string? ArtifactHint(ReadOnlySpan<byte> actual, string expectedDigest, string file)
    {
        var pin = $"put a .gitattributes with `* -text` in {Path.GetDirectoryName(Path.GetFullPath(file))}, delete the files there and restore them with `git checkout -- .` in it";
        if (TisiliaHash.Sha256OfBytes(ToLf(actual)) == expectedDigest)
        {
            return "it differs only in line endings: git checked it out with CRLF (core.autocrlf=true, the Git for Windows default); " + pin;
        }

        if (TisiliaHash.Sha256OfBytes(ToCrLf(actual)) == expectedDigest)
        {
            return "it differs only in line endings: the contract was exported from a checkout with CRLF (core.autocrlf=true, the Git for Windows default); " + pin + ", then export again";
        }

        return null;
    }

    private static byte[] ToCrLf(ReadOnlySpan<byte> bytes)
    {
        var result = new List<byte>(bytes.Length + bytes.Length / 16);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
            {
                result.Add((byte)'\r');
            }

            result.Add(bytes[i]);
        }

        return [.. result];
    }
}
