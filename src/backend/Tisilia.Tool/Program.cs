using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Tisilia.Tool;

// Pipes and files receive UTF-8 (RFC 8259 §8.1 for --format json; on Windows the default would be the console's ANSI code page).
// Console.OutputEncoding is not set: on Windows its setter calls SetConsoleOutputCP and changes the calling console's code page.
if (Console.IsOutputRedirected)
{
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
}

if (Console.IsErrorRedirected)
{
    Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
}

var cli = new CommandLine(args);
var json = cli.Option("format") == "json";
var stderr = Console.Error;
if (cli.Option("format") is { } format && format is not ("json" or "text"))
{
    // a mistyped --format would otherwise print human text where a script expects JSON
    stderr.WriteLine($"usage: --format '{format}' is neither json nor text");
    return ExitCodes.ConfigOrSchema;
}

try
{
    var command = cli.Positional.Count > 0 ? cli.Positional[0] : "help";
    return command switch
    {
        "init" => Commands.Init(cli, json),
        "validate" => Validate(cli, json),
        "hash" => Hash(cli, json),
        "closure" => ClosureCommand(cli, json),
        "generate" => await Commands.GenerateAsync(cli, json),
        "check" => await Commands.CheckAsync(cli, json),
        "diff" => Commands.Diff(cli, json),
        "export" => await Commands.ExportAsync(cli, json),
        "doctor" => await Commands.ExportAsync(cli, json, doctor: true),
        "conformance" => await Commands.ConformanceAsync(cli, json),
        "codec" => Commands.Codec(cli, json),
        "explorer" => await Commands.ExplorerAsync(cli, json),
        "watch" => await Commands.WatchAsync(cli, json),
        "version" => Version(),
        "help" => Help(),
        _ => Unknown(command),
    };
}
catch (UsageException e)
{
    stderr.WriteLine("usage: " + e.Message);
    Help();
    return ExitCodes.ConfigOrSchema;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    // a file the command reads or writes: read-only (a checkout that locks files), denied, used by another process, gone.
    // Reported like any other problem, with its own exit code, instead of an unhandled exception
    var bag = new DiagnosticBag();
    bag.Add(new Diagnostic { Code = TisiliaCodes.OutputPath, Severity = DiagnosticSeverity.Error, Message = e.Message, Fix = "make the file accessible (writable, not held by another program) and run the command again" });
    Output.Report(bag, json, null);
    return ExitCodes.ConfigOrSchema;
}

static int Help()
{
    Console.WriteLine($"tisilia {Tisilia.Generator.Pipeline.ProductVersion(typeof(Program).Assembly)} — .NET and TypeScript, connected down to what the types mean");
    Console.WriteLine("""

        commands:
          init      --contract <file> --output <dir> [--config tisilia.json --api-id <id> --module-mode bundler|nodenext --force]
                                                               write a config with the default settings (paths relative to the config)
          validate  --contract <file> [--format json]          structural + semantic validation (SV01–SV54), no code execution
          hash      --contract <file> [--write]                recompute semanticHash / profile fingerprints (--write updates the file)
          closure   --contract <file> --operation <id>[,..]    print the qualification closure record input
          generate  --config <file> [--evidence a.json,b.json --trusted-issuer id]
                                                               TypeScript client generation; valid evidence marks coverage qualified (SV45/46)
          check     --config <file>                            compare owned generated files with what would be generated (no writes)
          diff      --old <file> --new <file>                  per-direction compatibility diff
          export    --project <dir> --allow-execute-project [--output <file> --configuration <c> --environment <e> --no-build --timeout <s>]
                                                               build, then run the application's export host (executes user code; 120 s by default)
          doctor    --project <dir> --allow-execute-project [--format json --output <report.json> --no-build --timeout <s>]
                                                               aggregate adoption diagnostics; startup executes user code; no handler probing and no sandbox
          conformance --config <file> --project <dir> --allow-execute-adapters [--client <dir> --output <evidence.json> --report <file> --issuer <id> --seed <n> --cases <n> --operation a,b]
                                                               run the standard suite through the C# and TypeScript runners and write evidence
          codec generate --project <file>                      portable codec C#/TypeScript generation
          codec install-additional --project <dir> [--force]   install the additional codec module (Int128, Half, Uri, …) under the content root
          explorer build --registry <file> --allow-execute-build [--output <dir>]
                                                               bundle the Explorer with the registry's trusted modules (runs the package build)
          watch     --config <file> [--runs <n> --debounce-ms <n>]   rerun generate when the contract/config/evidence files change (never exports or runs npm)

        exit codes: 0 ok, 2 config/schema, 3 semantic/unsupported, 4 diff mismatch, 5 conformance, 6 external process, 7 safety policy
        """);
    return ExitCodes.Success;
}

// a mistyped command must fail: a script running `tisilia genrate` would otherwise go on as if it had generated
static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'");
    Help();
    return ExitCodes.ConfigOrSchema;
}

static int Version()
{
    Console.WriteLine(Tisilia.Generator.Pipeline.ProductVersion(typeof(Program).Assembly));
    return ExitCodes.Success;
}

static int Validate(CommandLine cli, bool json)
{
    var path = cli.Require("contract");
    var bag = new DiagnosticBag { File = path };
    var loaded = ContractLoader.LoadFile(path, bag);
    ContractIndex? index = null;
    if (loaded is not null)
    {
        index = SemanticValidator.Validate(loaded, bag);
    }

    Output.Report(bag, json, index is null ? null : new { operations = index.Operations.Count, types = index.Types.Count, codecs = index.Codecs.Count, semanticHash = loaded!.Document.SemanticHash });
    if (loaded is null)
    {
        return ExitCodes.ConfigOrSchema;
    }

    return bag.HasErrors ? ExitCodes.SemanticOrUnsupported : ExitCodes.Success;
}

static int Hash(CommandLine cli, bool json)
{
    var path = cli.Require("contract");
    var bag = new DiagnosticBag { File = path };
    var node = TisiliaSchemas.ParseStrict(File.ReadAllText(path), bag);
    if (node is not JsonObject root || bag.HasErrors)
    {
        Output.Report(bag, json, null);
        return ExitCodes.ConfigOrSchema;
    }

    if (!TisiliaSchemas.Instance.ValidateStructure(DocumentKind.Contract, root, bag))
    {
        Output.Report(bag, json, null);
        return ExitCodes.ConfigOrSchema;
    }

    var fingerprints = new Dictionary<string, string>();
    if (root["profiles"] is JsonArray profiles)
    {
        foreach (var profile in profiles.OfType<JsonObject>())
        {
            var fp = TisiliaHash.ProfileFingerprint(profile);
            fingerprints[profile["id"]!.GetValue<string>()] = fp;
            if (cli.Flag("write"))
            {
                profile["fingerprint"] = fp;
            }
        }
    }

    var hash = TisiliaHash.SemanticHash(root);
    if (cli.Flag("write"))
    {
        root["semanticHash"] = hash;
        File.WriteAllText(path, root.ToJsonString(TisiliaJson.IndentedOptions) + "\n");
    }

    Output.Report(bag, json, new { semanticHash = hash, profileFingerprints = fingerprints, written = cli.Flag("write") });
    return ExitCodes.Success;
}

static int ClosureCommand(CommandLine cli, bool json)
{
    var path = cli.Require("contract");
    var bag = new DiagnosticBag { File = path };
    var loaded = ContractLoader.LoadFile(path, bag);
    var index = loaded is null ? null : SemanticValidator.Validate(loaded, bag);
    if (index is null)
    {
        Output.Report(bag, json, null);
        return loaded is null ? ExitCodes.ConfigOrSchema : ExitCodes.SemanticOrUnsupported;
    }

    var ops = (cli.Option("operation") ?? string.Join(",", index.Operations.Keys)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    // like conformance: a closure of an id the contract does not have is a mistake, not an empty record
    if (ops.Where(id => !index.Operations.ContainsKey(id)).ToList() is { Count: > 0 } unknown)
    {
        bag.Error(TisiliaCodes.UnresolvedReference, "SV03", "/operations", "unknown operation id(s): " + string.Join(", ", unknown));
        Output.Report(bag, json, null);
        return ExitCodes.SemanticOrUnsupported;
    }

    var closure = Tisilia.Generator.Closure.ContractClosure.Compute(index, ops);
    var record = closure.ToRecord(loaded!.Document.SemanticHash, closure.DeclaredModuleArtifacts(), [], new Tisilia.Contract.RuntimeMatrix
    {
        Dotnet = "0.0.0",
        Aspnetcore = "0.0.0",
        Stj = "0.0.0",
        Typescript = "0.0.0",
        Os = "unknown",
        Architecture = "unknown",
    }, [], Tisilia.Contract.Limits.Default);
    Console.WriteLine(JsonSerializer.Serialize(record, TisiliaJson.IndentedOptions));
    return ExitCodes.Success;
}
