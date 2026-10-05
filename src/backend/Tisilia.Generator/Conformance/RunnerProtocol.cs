using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Conformance;

/// <summary>
/// The isolated conformance runner protocol (SV54): UTF-8 JSON Lines, one <c>tisilia.runner-message</c>
/// per line, requests correlated by session and request ids, fixed input/output arity per action. Shared by the
/// orchestrator (client side) and the C# runner; the TypeScript runner implements the same rules.
/// </summary>
public static class RunnerProtocol
{
    public const string ProtocolVersion = "0.1";

    /// <summary>Environment variables the orchestrator sets for a runner process.</summary>
    public const string SessionVariable = "TISILIA_RUNNER_SESSION";
    public const string EnvironmentFileVariable = "TISILIA_RUNNER_ENV";
    public const string MaxRecordBytesVariable = "TISILIA_RUNNER_MAX_RECORD_BYTES";
    public const string ContractVariable = "TISILIA_RUNNER_CONTRACT";
    public const string ModeVariable = "TISILIA_RUNNER";

    public const long DefaultMaxRecordBytes = 16L * 1024 * 1024;

    public static int InputArity(RunnerAction action) => action is RunnerAction.Compare or RunnerAction.TsProject or RunnerAction.DotnetProject ? 2 : 1;

    public const int OutputArity = 1;

    public static string NewSessionId() => "s." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    /// <summary>One line, no indentation; strings are escaped so the record never contains a raw newline.</summary>
    public static string Serialize(RunnerMessage message) => JsonSerializer.Serialize(message, TisiliaJson.Options);

    /// <summary>Strict parse: duplicate names, unknown members, schema violations and arity mismatches are all protocol failures.</summary>
    public static RunnerMessage? Parse(string line, long maxRecordBytes, out string? error)
    {
        error = null;
        if (Encoding.UTF8.GetByteCount(line) > maxRecordBytes)
        {
            error = "record exceeds the per-record limit of " + maxRecordBytes + " bytes";
            return null;
        }

        var bag = new DiagnosticBag();
        var node = TisiliaSchemas.ParseStrict(line, bag);
        if (node is null || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.RunnerMessage, node, bag))
        {
            error = string.Join("; ", bag.Items.Select(d => d.Message));
            return null;
        }

        RunnerMessage message;
        try
        {
            message = JsonSerializer.Deserialize<RunnerMessage>(node, TisiliaJson.Options)!;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return null;
        }

        if (message.Format != TisiliaJson.Formats.RunnerMessage || message.Version != ProtocolVersion)
        {
            error = "not a tisilia.runner-message 0.1 record";
            return null;
        }

        if (message is RunnerRequest request && request.Inputs.Count != InputArity(request.Action))
        {
            error = $"action '{request.Action}' takes {InputArity(request.Action)} input(s) but {request.Inputs.Count} were given";
            return null;
        }

        if (message is RunnerSuccess success && success.Outputs.Count != OutputArity)
        {
            error = $"success records carry exactly {OutputArity} output but {success.Outputs.Count} were given";
            return null;
        }

        return message;
    }

    public static RunnerRequest Request(string sessionId, string requestId, RunnerAction action, string adapterId, string profileId, IReadOnlyList<NameValue> context, IReadOnlyList<JsonValue> inputs) => new()
    {
        Format = TisiliaJson.Formats.RunnerMessage,
        Version = ProtocolVersion,
        SessionId = sessionId,
        RequestId = requestId,
        Action = action,
        AdapterId = adapterId,
        ProfileId = profileId,
        Context = context,
        Inputs = inputs,
    };

    public static RunnerSuccess Success(string sessionId, string requestId, JsonValue output) => new()
    {
        Format = TisiliaJson.Formats.RunnerMessage,
        Version = ProtocolVersion,
        SessionId = sessionId,
        RequestId = requestId,
        Outputs = [output],
    };

    public static RunnerFailure Failure(string sessionId, string requestId, RunnerFailureCode code, string safeMessageId, string path) => new()
    {
        Format = TisiliaJson.Formats.RunnerMessage,
        Version = ProtocolVersion,
        SessionId = sessionId,
        RequestId = requestId,
        Code = code,
        SafeMessageId = SafeId(safeMessageId),
        Path = path,
    };

    /// <summary>Coerces free text into the <c>id</c> grammar so that a failure record always validates.</summary>
    public static string SafeId(string raw)
    {
        var chars = raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '@' or '/' or '-' ? c : '-').ToArray();
        var s = new string(chars);
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]))
        {
            s = "m." + s;
        }

        return s.Length > 160 ? s[..160] : s;
    }
}

/// <summary>Raised by the orchestrator when a runner breaks the protocol (correlation, extra/duplicate records, exit, malformed AST, timeout).</summary>
public sealed class RunnerProtocolException(string runner, string message) : Exception($"{runner} runner: {message}")
{
    public string Runner { get; } = runner;
}
