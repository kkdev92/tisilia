using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The version being built, the newest changelog entry, the readme's status line, the package-validation baseline and the npm
/// packages agree.
/// </summary>
/// <remarks>
/// <para>
/// The version is stated by hand in several places, and each statement is only correct relative to the others.
/// <c>VersionPrefix</c> says what is being built, the changelog says what shipped, the readme's status line says which release
/// its claims are about, <c>PackageValidationBaselineVersion</c> says what the assemblies are compared against, and the npm
/// packages are released together with the NuGet ones at the same version.
/// </para>
/// <para>
/// <c>VersionPrefix</c> is bumped in the release commit rather than ahead of one. So once something has shipped, the newest
/// dated changelog entry is the version being built; before the first release there is no dated entry at all, the work is under
/// <c>[Unreleased]</c>, and the only version that can be being built is the first one.
/// </para>
/// </remarks>
public sealed partial class ReleaseVersionTests
{
    private static readonly string Root = FixtureTests.RepoRoot();

    private static string? Property(string file, string name)
        => XDocument.Load(Path.Combine(Root, file)).Descendants(name).FirstOrDefault()?.Value;

    private static string? BuildProperty(string name) => Property("Directory.Build.props", name);

    private static string? PackageProperty(string name) => Property(Path.Combine("src", "backend", "Directory.Build.targets"), name);

    /// <summary>The version this build produces, spelled the way a package file name spells it.</summary>
    private static string BuiltVersion
    {
        get
        {
            var prefix = BuildProperty("VersionPrefix");
            var suffix = BuildProperty("VersionSuffix");
            Assert.False(string.IsNullOrWhiteSpace(prefix), "VersionPrefix is not set.");
            return string.IsNullOrWhiteSpace(suffix) ? prefix! : $"{prefix}-{suffix}";
        }
    }

    private static string Changelog => File.ReadAllText(Path.Combine(Root, "CHANGELOG.md"));

    /// <summary>The released versions the changelog names, newest first; <c>[Unreleased]</c> is not a release.</summary>
    private static IReadOnlyList<(string Version, string Date)> Released()
        =>
        [
            .. ReleaseHeading().Matches(Changelog)
                .Select(match => (match.Groups["version"].Value, match.Groups["date"].Value))
                .Where(entry => !entry.Item1.Equals("Unreleased", StringComparison.OrdinalIgnoreCase)),
        ];

    [GeneratedRegex(@"^## \[(?<version>[^\]]+)\](?: - (?<date>\S+))?", RegexOptions.Multiline)]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"^## \[Unreleased\]\s*$", RegexOptions.Multiline)]
    private static partial Regex UnreleasedHeading();

    [Fact]
    public void The_changelogs_newest_entry_is_the_version_being_built()
    {
        Assert.Matches(UnreleasedHeading(), Changelog);
        var released = Released();
        if (released.Count == 0)
        {
            // Nothing has shipped: the version being built is the first one, the one the baseline exemption was written for.
            Assert.Equal(PackageProperty("LastVersionWithoutBaseline"), BuildProperty("VersionPrefix"));
            return;
        }

        Assert.Equal(BuiltVersion, released[0].Version);
    }

    /// <summary>A version heading is written when the version ships, so the date it shipped (UTC) is what belongs there.</summary>
    [Fact]
    public void Every_released_entry_is_dated()
    {
        foreach (var (version, date) in Released())
        {
            Assert.True(DateOnly.TryParseExact(date, "yyyy-MM-dd", out _), $"The changelog entry for {version} reads '{date}', not a date.");
        }
    }

    /// <summary>
    /// What follows the status line is a claim about that release and nothing else, so a stale number does not read as an
    /// out-of-date label: it reads as those claims being about a version they were never true of.
    /// </summary>
    [Fact]
    public void The_readme_status_line_names_the_version_being_built()
    {
        var status = StatusLine().Match(File.ReadAllText(Path.Combine(Root, "README.md")));
        Assert.True(status.Success, "The readme has no `**Status:** `x.y.z`` line to check.");
        Assert.Equal(BuiltVersion, status.Groups["version"].Value);
    }

    [GeneratedRegex(@"\*\*Status:\*\* `(?<version>[^`]+)`")]
    private static partial Regex StatusLine();

    /// <summary>
    /// Between releases the readme follows main, which is then ahead of the published packages. It says so, naming the latest
    /// release, exactly while the changelog lists changes under Unreleased; the release that moves them under its version removes it.
    /// </summary>
    [Fact]
    public void The_readme_says_when_main_is_ahead_of_the_latest_release()
    {
        var ahead = AheadNote().Match(File.ReadAllText(Path.Combine(Root, "README.md")));
        var unreleased = UnreleasedSection().Match(Changelog);
        var pending = unreleased.Success && PendingEntry().IsMatch(unreleased.Groups["body"].Value);
        var released = Released();
        if (!pending || released.Count == 0)
        {
            Assert.False(ahead.Success, "The readme says main is ahead of a release, but no release precedes unreleased changes.");
            return;
        }

        Assert.True(ahead.Success, "The changelog lists unreleased changes, but the readme does not say that main is ahead of the latest release.");
        Assert.Equal(released[0].Version, ahead.Groups["version"].Value);
    }

    [GeneratedRegex(@"This README follows the `main` branch, which is ahead of `(?<version>[^`]+)`")]
    private static partial Regex AheadNote();

    [GeneratedRegex(@"^## \[Unreleased\][^\n]*\n(?<body>[\s\S]*?)(?=^## \[|\z)", RegexOptions.Multiline)]
    private static partial Regex UnreleasedSection();

    [GeneratedRegex(@"^- ", RegexOptions.Multiline)]
    private static partial Regex PendingEntry();

    /// <summary>The baseline names the release immediately before the one being built, and there is none until there is one.</summary>
    [Fact]
    public void The_baseline_names_the_previous_release()
    {
        var baseline = PackageProperty("PackageValidationBaselineVersion");
        var previous = Released().Select(entry => entry.Version).Where(version => version != BuiltVersion).FirstOrDefault();
        if (previous is null)
        {
            Assert.True(string.IsNullOrEmpty(baseline), $"No release precedes {BuiltVersion}, but the baseline names {baseline}.");
            return;
        }

        Assert.Equal(previous, baseline);
    }

    /// <summary>
    /// The npm packages ship with the NuGet packages at the same version: a generated client names the runtime of the CLI that
    /// generated it, and the Nuxt module and the Explorer depend on that runtime exactly.
    /// </summary>
    [Fact]
    public void The_npm_packages_are_the_version_being_built()
    {
        var version = BuiltVersion;
        Assert.Equal(version, Manifest("package.json")["version"]?.GetValue<string>());
        foreach (var package in new[] { "runtime", "nuxt", "explorer" })
        {
            var manifest = Manifest(Path.Combine("src", "frontend", package, "package.json"));
            Assert.Equal(version, manifest["version"]?.GetValue<string>());
            if (manifest["dependencies"]?["@kkdev92/tisilia-runtime"] is { } runtime)
            {
                Assert.Equal(version, runtime.GetValue<string>());
            }
        }

        var index = File.ReadAllText(Path.Combine(Root, "src", "frontend", "runtime", "src", "index.ts"));
        Assert.Contains($"export const runtimeVersion = \"{version}\";", index, StringComparison.Ordinal);
    }

    private static JsonNode Manifest(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, path)))!;
}
