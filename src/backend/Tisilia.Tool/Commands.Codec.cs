using Tisilia.Generator.Additional;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Portable;

namespace Tisilia.Tool;

public static partial class Commands
{
    /// <summary>
    /// <c>codec generate --project file [--content-root dir] [--force]</c>: portable project → C# converters, TypeScript module, codec manifest. Executes nothing.
    /// <c>codec install-additional --project dir [--force]</c>: installs the additional codec module under the content root.
    /// </summary>
    public static int Codec(CommandLine cli, bool json)
    {
        if (cli.Positional.Count >= 2 && cli.Positional[1] == "install-additional")
        {
            return InstallAdditional(cli, json);
        }

        if (cli.Positional.Count < 2 || cli.Positional[1] != "generate")
        {
            Console.Error.WriteLine("usage: tisilia codec generate --project <portable-project.json> [--content-root <dir>] [--force]");
            Console.Error.WriteLine("       tisilia codec install-additional --project <content-root> [--force]");
            return ExitCodes.ConfigOrSchema;
        }

        var projectPath = cli.Require("project");
        var bag = new DiagnosticBag { File = projectPath };
        var plan = PortableCodegen.Plan(projectPath, cli.Option("content-root"), bag);
        if (plan is null)
        {
            Output.Report(bag, json, null);
            return bag.Items.Any(d => d.Code == TisiliaCodes.SchemaViolation || d.Code == TisiliaCodes.FormatOrVersion || d.Code == TisiliaCodes.PortableProject) ? ExitCodes.ConfigOrSchema : ExitCodes.SemanticOrUnsupported;
        }

        if (!PortableCodegen.Write(plan, cli.Flag("force"), bag))
        {
            Output.Report(bag, json, null);
            return ExitCodes.SafetyPolicyViolation;
        }

        Output.Report(bag, json, new
        {
            project = plan.Project.Project.ProjectId,
            definitions = plan.Project.Definitions.Count,
            csharp = plan.Project.CsharpOutput,
            typescript = plan.Project.TypescriptOutput,
            files = plan.CsharpFiles.Count + plan.TypescriptFiles.Count + 1,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Writes the module Tisilia ships for its additional codecs (Int128, UInt128, BigInteger, Half, Uri, Version, IPAddress, Rune) to
    /// <c>&lt;project&gt;/modules/tisilia-additional/</c>, where the exported contract's artifact path points and the generated client imports
    /// it from. A file with other content is never replaced without <c>--force</c>. Writes files only; executes nothing.
    /// </summary>
    private static int InstallAdditional(CommandLine cli, bool json)
    {
        var project = cli.Require("project");
        var bag = new DiagnosticBag { File = project };
        if (!Directory.Exists(project))
        {
            bag.Error(TisiliaCodes.ConfigInvalid, "SV39", "/project", $"project directory '{project}' does not exist (pass the application's content root, where the contract is exported)");
            Output.Report(bag, json, null);
            return ExitCodes.ConfigOrSchema;
        }

        var installed = AdditionalModule.Install(project, cli.Flag("force"));
        foreach (var conflict in installed.Where(f => f.Status == AdditionalModule.InstallStatus.Conflict))
        {
            bag.Error(TisiliaCodes.OutputPath, "SV38", "/files", $"'{conflict.Path}' exists with other content; nothing was written (re-run with --force to replace it)");
        }

        Output.Report(bag, json, new
        {
            module = AdditionalModule.ModuleId,
            version = AdditionalModule.Version,
            files = installed.Select(f => new { path = Path.GetFullPath(f.Path), status = f.Status.ToString().ToLowerInvariant() }).ToList(),
        });
        return bag.HasErrors ? ExitCodes.SafetyPolicyViolation : ExitCodes.Success;
    }
}
