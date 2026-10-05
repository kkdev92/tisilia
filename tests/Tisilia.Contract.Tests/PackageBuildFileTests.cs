using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// What the Kkdev92.Tisilia.AspNetCore package's build files do to the project that references it: the XML documentation file the
/// /// comments travel in is on unless the project decides itself (its project file or a Directory.Build.props), and when the package
/// turns it on, missing comments do not warn and malformed ones never become errors. Evaluated with <c>dotnet msbuild -getProperty</c>
/// over projects that import the package's props file before the project file, where NuGet's generated props import it.
/// </summary>
public sealed class PackageBuildFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-build-" + Guid.NewGuid().ToString("N"));

    public PackageBuildFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Dictionary<string, string> Evaluate(string name, string projectProperties, string? directoryBuildProps = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        var props = Path.Combine(FixtureTests.RepoRoot(), "src", "backend", "Tisilia.AspNetCore", "build", "Kkdev92.Tisilia.AspNetCore.props");
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{props}" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                {projectProperties}
              </PropertyGroup>
            </Project>
            """);
        if (directoryBuildProps is not null)
        {
            File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), $"<Project><PropertyGroup>{directoryBuildProps}</PropertyGroup></Project>");
        }

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in new[] { "msbuild", name + ".csproj", "-nologo", "-getProperty:GenerateDocumentationFile", "-getProperty:DocumentationFile", "-getProperty:NoWarn", "-getProperty:WarningsNotAsErrors" })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr.Result + stdout.Result);
        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    [Fact]
    public void The_documentation_file_is_on_unless_the_project_decides()
    {
        var on = Evaluate("plain", "");
        Assert.Equal("true", on["GenerateDocumentationFile"]);
        Assert.EndsWith("plain.xml", on["DocumentationFile"], StringComparison.Ordinal);
        Assert.Superset(new HashSet<string> { "CS1591", "CS1573", "CS1712" }, on["NoWarn"].Split(';').ToHashSet());
        Assert.Superset(new HashSet<string> { "CS1570", "CS1572", "CS1574", "CS1587" }, on["WarningsNotAsErrors"].Split(';').ToHashSet());

        // decided in the project file, which comes after the package's props: kept, with the compiler's warnings as they are
        var mine = Evaluate("mine", "<GenerateDocumentationFile>true</GenerateDocumentationFile>");
        Assert.Equal("true", mine["GenerateDocumentationFile"]);
        Assert.DoesNotContain("CS1591", mine["NoWarn"].Split(';'));
        Assert.DoesNotContain("CS1570", mine["WarningsNotAsErrors"].Split(';'));
        var off = Evaluate("off", "<GenerateDocumentationFile>false</GenerateDocumentationFile>");
        Assert.Equal(("false", ""), (off["GenerateDocumentationFile"], off["DocumentationFile"]));
        var path = Evaluate("path", "<DocumentationFile>docs/api.xml</DocumentationFile>");
        Assert.Equal(("true", "docs/api.xml"), (path["GenerateDocumentationFile"], path["DocumentationFile"]));
        Assert.DoesNotContain("CS1591", path["NoWarn"].Split(';'));

        // decided in Directory.Build.props, which comes before it
        var directory = Evaluate("directory", "", "<GenerateDocumentationFile>false</GenerateDocumentationFile>");
        Assert.Equal(("false", ""), (directory["GenerateDocumentationFile"], directory["DocumentationFile"]));
    }
}
