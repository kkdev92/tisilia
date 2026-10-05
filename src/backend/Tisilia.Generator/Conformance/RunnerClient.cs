using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Tisilia.Contract;
using JsonValue = Tisilia.Contract.JsonValue;
using Tisilia.Documents;

namespace Tisilia.Generator.Conformance;

/// <summary>How to start a runner process. Nothing is executed unless the CLI was given <c>--allow-execute-adapters</c>.</summary>
public sealed record RunnerLaunch(string Name, string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Client side of the runner protocol: one process, one session, strictly one response per request. Any deviation
/// (wrong session/request id, extra or duplicate records, exit, malformed record, timeout) is a
/// <see cref="RunnerProtocolException"/>; the orchestrator treats it as a runner failure.
/// </summary>
public sealed class RunnerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly List<string> _stderr = [];
    private readonly Task _stderrPump;
    private readonly TextWriter _log;
    private int _counter;

    private RunnerClient(string name, Process process, string sessionId, long maxRecordBytes, TextWriter log)
    {
        Name = name;
        _process = process;
        SessionId = sessionId;
        MaxRecordBytes = maxRecordBytes;
        _log = log;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n";
        _stdin.AutoFlush = true;
        _stdout = process.StandardOutput;
        _stderrPump = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Add(line);
                }

                _log.WriteLine($"[{name}] {line}");
            }
        });
    }

    public string Name { get; }

    public string SessionId { get; }

    public long MaxRecordBytes { get; }

    public IReadOnlyList<string> StderrLines
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToList();
            }
        }
    }

    public static RunnerClient Start(RunnerLaunch launch, string sessionId, long maxRecordBytes, TextWriter log)
    {
        var psi = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = launch.WorkingDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in launch.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in launch.Environment)
        {
            psi.Environment[key] = value;
        }

        psi.Environment[RunnerProtocol.SessionVariable] = sessionId;
        psi.Environment[RunnerProtocol.MaxRecordBytesVariable] = maxRecordBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var process = Process.Start(psi) ?? throw new RunnerProtocolException(launch.Name, "failed to start " + launch.FileName);
        return new RunnerClient(launch.Name, process, sessionId, maxRecordBytes, log);
    }

    /// <summary>Sends one request and waits for exactly one correlated response.</summary>
    public async Task<RunnerMessage> SendAsync(RunnerAction action, string adapterId, string profileId, IReadOnlyList<NameValue> context, IReadOnlyList<JsonValue> inputs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = "r." + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = RunnerProtocol.Request(SessionId, requestId, action, adapterId, profileId, context, inputs);
        var line = RunnerProtocol.Serialize(request);
        if (Encoding.UTF8.GetByteCount(line) > MaxRecordBytes)
        {
            // a test-side limit, reported like the runner would report its own
            return RunnerProtocol.Failure(SessionId, requestId, RunnerFailureCode.Limit, "orchestrator.record-limit", "");
        }

        if (_process.HasExited)
        {
            throw new RunnerProtocolException(Name, $"exited with code {_process.ExitCode} before {requestId}; stderr: {LastStderr()}");
        }

        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new RunnerProtocolException(Name, $"stdin closed while sending {requestId}: {e.Message}; stderr: {LastStderr()}");
        }

        var read = _stdout.ReadLineAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Kill();
            throw new RunnerProtocolException(Name, $"timeout after {timeout.TotalMilliseconds:0} ms waiting for {requestId} ({action} {adapterId}); the runner was terminated");
        }

        var response = await read.ConfigureAwait(false);
        if (response is null)
        {
            await Task.WhenAny(_stderrPump, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
            throw new RunnerProtocolException(Name, $"stdout closed (exit code {ExitCodeText()}) before answering {requestId}; stderr: {LastStderr()}");
        }

        var message = RunnerProtocol.Parse(response, MaxRecordBytes, out var error)
            ?? throw new RunnerProtocolException(Name, $"malformed record for {requestId}: {error}");
        if (message is RunnerRequest)
        {
            throw new RunnerProtocolException(Name, "a runner must not send request records");
        }

        if (!string.Equals(message.SessionId, SessionId, StringComparison.Ordinal))
        {
            throw new RunnerProtocolException(Name, $"session id mismatch: expected '{SessionId}', got '{message.SessionId}'");
        }

        if (!string.Equals(message.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new RunnerProtocolException(Name, $"response for '{message.RequestId}' while waiting for '{requestId}' (extra or duplicate record)");
        }

        return message;
    }

    /// <summary>Waits for the runner's environment report file (written once at startup, before it serves requests).</summary>
    public async Task<JsonObject> WaitForEnvironmentAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    if (JsonNode.Parse(text) is JsonObject obj && obj["ready"]?.GetValue<bool>() == true)
                    {
                        return obj;
                    }
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
                {
                    // partially written; retry
                }
            }

            if (_process.HasExited)
            {
                await Task.WhenAny(_stderrPump, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
                throw new RunnerProtocolException(Name, $"exited with code {_process.ExitCode} before reporting its environment; stderr: {LastStderr()}");
            }

            if (DateTime.UtcNow > deadline)
            {
                Kill();
                throw new RunnerProtocolException(Name, $"did not report its environment within {timeout.TotalSeconds:0} s; stderr: {LastStderr()}");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private string ExitCodeText()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "running";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private string LastStderr()
    {
        lock (_stderr)
        {
            return string.Join(" | ", _stderr.TakeLast(5));
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stdin.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }

        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
    }
}
