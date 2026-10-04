using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A project reference that skips the target-framework negotiation and points at a MULTI-targeted
/// project lands on the outer, cross-targeting project, which has no GetTargetPath. The command
/// line never notices: it asks the reference for Build, which the outer project has. Visual Studio
/// asks for GetTargetPath when it builds a project (MSB4057), so the referencing project fails to
/// build in the IDE, and every project that references IT loses all of its project references in
/// the editor — thousands of CS0234 in Inferpal.Tests while every command-line build, CI included,
/// passes. Nothing outside Visual Studio can see that failure: the project files are what is read.
/// </summary>
public class VsixProjectReferenceTests
{
    [Fact]
    public void AReferenceThatSkipsTheNegotiation_ToAMultiTargetedProject_NamesOneOfItsFrameworks()
    {
        var examined = 0;
        foreach (var project in SolutionProjects())
        {
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
            {
                var setTargetFramework = (string?)reference.Attribute("SetTargetFramework") ?? "";
                var skips = (string?)reference.Attribute("SkipGetTargetFrameworkProperties") == "true"
                            || setTargetFramework.Length > 0;
                var include = ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                var frameworks = MultiTargetedFrameworks(target);
                if (!skips || frameworks.Length == 0)
                    continue;

                examined++;
                var named = setTargetFramework.StartsWith("TargetFramework=", StringComparison.Ordinal)
                    ? setTargetFramework["TargetFramework=".Length..].Trim()
                    : "";
                Assert.True(frameworks.Contains(named),
                    $"{Path.GetFileName(project)} references the multi-targeted {Path.GetFileName(target)} " +
                    "without negotiating its framework" +
                    (named.Length == 0
                        ? " and without naming one (SetTargetFramework=\"TargetFramework=…\")"
                        : $" and names '{named}', which it does not target ({string.Join(", ", frameworks)})") +
                    ". Visual Studio then asks the outer project for GetTargetPath (MSB4057): the " +
                    "project fails to build in the IDE and its dependents lose every project " +
                    "reference in the editor, while the command line stays green.");
            }
        }

        // The rule must have met the case it exists for, or a renamed attribute makes it vacuous.
        Assert.True(examined >= 1,
            "No project reference skipped the negotiation towards a multi-targeted project: the " +
            "VSIX project's reference to Inferpal.InProc is expected to be one.");
    }

    [Fact]
    public void TheInProcReference_NamesTheBuildTheVsixEmbeds()
    {
        var vsixProject = Path.Combine(RepoRoot(), "Inferpal", "Inferpal.csproj");
        var vsix = XDocument.Load(vsixProject);

        var reference = vsix.Descendants("ProjectReference")
            .Single(r => ((string?)r.Attribute("Include") ?? "").EndsWith("Inferpal.InProc.csproj", StringComparison.Ordinal));
        Assert.Equal("TargetFramework=net472", (string?)reference.Attribute("SetTargetFramework"));

        // The build the reference orders first is the one packaged: the net472 assembly devenv loads.
        Assert.Contains(vsix.Descendants("Content"),
            c => ((string?)c.Attribute("Include") ?? "").EndsWith(@"\net472\Inferpal.InProc.dll", StringComparison.Ordinal));
    }

    /// <summary>The frameworks of a multi-targeted project; empty for a single-target one.</summary>
    private static string[] MultiTargetedFrameworks(string projectPath) =>
        XDocument.Load(projectPath).Descendants("TargetFrameworks")
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToArray();

    private static string[] SolutionProjects()
    {
        var root = RepoRoot();
        var projects = Regex.Matches(File.ReadAllText(Path.Combine(root, "Inferpal.sln")),
                @"^Project\(""[^""]+""\)\s*=\s*""[^""]+"",\s*""([^""]+\.csproj)""", RegexOptions.Multiline)
            .Select(m => Path.Combine(root, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();

        // Witness kept under the real count (six): it says the read is alive, not that nothing moved.
        Assert.True(projects.Length >= 4, $"Only {projects.Length} project(s) read from Inferpal.sln.");
        return projects;
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
