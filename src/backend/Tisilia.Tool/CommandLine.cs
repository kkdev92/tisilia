namespace Tisilia.Tool;

/// <summary>Minimal deterministic argument parser: <c>command [subcommand] --name value --flag</c>. No environment fallbacks.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public CommandLine(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                positional.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                var eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    _options[name[..eq]] = name[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !IsFlag(name))
                {
                    _options[name] = args[++i];
                }
                else
                {
                    _flags.Add(name);
                }
            }
            else
            {
                positional.Add(arg);
            }
        }

        Positional = positional;
    }

    private static bool IsFlag(string name) => name is "allow-execute-project" or "allow-execute-adapters" or "allow-execute-binders" or "allow-execute-build" or "force" or "help" or "version" or "verbose" or "write";

    public IReadOnlyList<string> Positional { get; }

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public bool Flag(string name) => _flags.Contains(name) || (_options.TryGetValue(name, out var v) && v is "true" or "1");

    public string Require(string name)
        => Option(name) ?? throw new UsageException($"missing required option --{name}");

    /// <summary>A whole-number option (digits only), or <paramref name="fallback"/> when it is absent; anything else is a usage error, never a silent default.</summary>
    public T WholeNumber<T>(string name, T fallback) where T : System.Numerics.IBinaryInteger<T>
        => Option(name) is not { } text ? fallback
            : T.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value
            : throw new UsageException($"--{name} '{text}' is not a whole number");
}

public sealed class UsageException(string message) : Exception(message);
