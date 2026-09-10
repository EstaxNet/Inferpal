using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Locks the packaging invariants that decide whether the product is <b>translated at all</b>.
///
/// <para>
/// They share the property that makes them worth a test: when they break, <b>nothing says so</b>.
/// The VSIX builds, installs, the chat window opens and answers - and the entire interface is in
/// English, in all nine languages at once. Both were already paid for once, and neither was held
/// by a test.
/// </para>
/// </summary>
public class VsixPackagingTests
{
    [Fact]
    public void TheVsix_StillCarriesTheSatelliteAssemblies()
    {
        // Without that switch, dependencies resolved by NuGet are not copied next to the output -
        // and the nine Inferpal.Core.resources.dll are among them. The VSIX builds perfectly well
        // without them; it simply contains no translation at all, and the ResourceManager falls
        // back to English for everyone without throwing anything.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);

        // Witness: the satellites really are produced by the build. Without it, the rule above
        // would be guarding a switch that no longer has an object, and its green would mean
        // nothing - it would be reduced to "this text is in this file".
        var produced = new[] { "fr", "de", "es", "it", "ru", "ja", "ko", "pl", "zh-CN" }
            .Where(l => File.Exists(Path.Combine(AppContext.BaseDirectory, l, "Inferpal.Core.resources.dll")))
            .ToList();
        Assert.True(produced.Count == 9,
            $"Only {produced.Count} satellite(s) built out of 9 ({string.Join(", ", produced)}): "
            + "the packaging switch is no longer what to look at - resource generation itself is.");
    }

    [Fact]
    public void TheFlattenedSatellitePrune_RunsLateEnoughToSeeTheDuplicate()
    {
        // VSSDK packaging adds a DUPLICATE of the `de` satellite, flattened at the root of the
        // VSIX (/Inferpal.Core.resources.dll, Culture=de, no metadata entry in files.json). The
        // Extensibility host's ALC resolves that one FIRST, the culture does not match, the bind
        // fails and EVERY language falls back to English. The symptom reads as "localization is
        // broken", never as "there is one file too many".
        //
        // And the hook is the half that matters: the duplicate only appears AFTER
        // GetVsixSourceItems, so a target wired one hook earlier sees nothing, removes nothing,
        // and passes.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj"));

        var target = Regex.Match(
            csproj, @"<Target\s+Name=""PruneFlattenedCoreSatelliteFromVsix""(?<attrs>[^>]*)>");
        Assert.True(target.Success,
            "The PruneFlattenedCoreSatelliteFromVsix target is gone: the duplicate satellite comes "
            + "back to the root of the VSIX and all ten languages fall back to English, silently.");

        foreach (var hook in new[] { "GenerateTemplatesManifest", "GenerateFileManifest" })
            Assert.True(target.Groups["attrs"].Value.Contains(hook, StringComparison.Ordinal),
                $"PruneFlattenedCoreSatelliteFromVsix no longer hooks onto {hook}. The duplicate "
                + "only exists after GetVsixSourceItems: wired earlier, the target removes nothing "
                + "and its success proves nothing.");
    }

    [Fact]
    public void VsCodeChangelog_HasASectionForTheVersionBeingShipped()
    {
        // ⚠ The VS Code Marketplace page IS the package: its "Changelog" tab renders the
        // EMBEDDED vscode/CHANGELOG.md, and it cannot be edited on the web — fixing it after the
        // fact is a PUBLICATION, so a burnt version number. The same is already true of the
        // embedded README.
        //
        // Measured 2026-09-10: 1.6.10 shipped with a changelog that stops at 1.6.9. Nothing could
        // say so — the publisher checks that the VSIX CONTAINS the file, not that it mentions the
        // version being published: the tool did not fail, so the artifact looked finished.
        //
        // This test is red exactly between the version bump and the writing of the notes, and the
        // tag's CI replays it: that is where the question belongs.
        var props   = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(version.Success, "No <Version> in Directory.Build.props.");

        var path      = Path.Combine(RepoRoot(), "vscode", "CHANGELOG.md");
        var changelog = File.ReadAllText(path);

        // Witness: the file must carry version sections at all, otherwise "ours is missing" means
        // nothing — the failure mode of a scan that reads nothing.
        var sections = System.Text.RegularExpressions.Regex.Matches(changelog, @"(?m)^##\s+\d+\.\d+");
        Assert.True(sections.Count >= 5,
            $"Only {sections.Count} version section(s) in vscode/CHANGELOG.md: the format changed "
            + "and this test no longer measures anything.");

        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                changelog, @"(?m)^##\s+" + System.Text.RegularExpressions.Regex.Escape(version.Groups[1].Value) + @"\s*$"),
            $"vscode/CHANGELOG.md has no \"## {version.Groups[1].Value}\" section. That file is "
            + "embedded in all three VSIXes and IS the Marketplace's Changelog tab: publishing "
            + "without it ships a page that stops at the previous version, and that can only be "
            + "corrected by burning a version number.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
