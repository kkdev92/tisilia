namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary>
    /// <c>watch --config file [--evidence …] [--trusted-issuer …] [--runs n] [--debounce-ms n]</c>: regenerate the client whenever the
    /// contract, the config or an evidence file changes. Exactly the <c>generate</c> pipeline runs: no export, no
    /// npm, no module execution — those stay separate commands. <c>--runs</c> stops after that many generations (CI / tests).
    /// </summary>
    public static async Task<int> WatchAsync(CommandLine cli, bool json)
    {
        var configPath = Path.GetFullPath(cli.Require("config"));
        var maxRuns = cli.WholeNumber("runs", int.MaxValue);
        var debounce = TimeSpan.FromMilliseconds(cli.WholeNumber("debounce-ms", 300));
        if (maxRuns < 1)
        {
            throw new UsageException("--runs must be at least 1");
        }

        var evidence = EvidencePaths(cli);
        var trusted = TrustedIssuers(cli);
        var runs = 0;
        var last = Generate(configPath, evidence, trusted, cli.Flag("force"), json, out var inputs);
        runs++;
        if (runs >= maxRuns)
        {
            return last;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        using var signal = new SemaphoreSlim(0);
        var watched = new HashSet<string>(inputs, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var directory in watched.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime };
                void OnChange(object sender, FileSystemEventArgs e)
                {
                    if (watched.Contains(Path.GetFullPath(e.FullPath)))
                    {
                        signal.Release();
                    }
                }

                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Renamed += (sender, e) => OnChange(sender, e);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            Console.Error.WriteLine($"tisilia watch: watching {watched.Count} file(s) for changes; `generate` reruns on change (export and npm are never run here). Ctrl+C stops.");
            while (runs < maxRuns)
            {
                try
                {
                    await signal.WaitAsync(cts.Token).ConfigureAwait(false);
                    await Task.Delay(debounce, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (signal.CurrentCount > 0)
                {
                    signal.Wait(0);
                }

                Console.Error.WriteLine($"tisilia watch: change detected, run {runs + 1}");
                try
                {
                    last = Generate(configPath, evidence, trusted, cli.Flag("force"), json, out inputs);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a file still being written by another program (an editor saving, an export replacing the contract): this run
                    // fails, watching goes on, and the next change runs again
                    Console.Error.WriteLine($"tisilia watch: run {runs + 1} failed: {e.Message}");
                    last = ExitCodes.ConfigOrSchema;
                }

                runs++;
                watched = new HashSet<string>(inputs, watched.Comparer);
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }

        return last;
    }
}
