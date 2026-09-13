using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio settings window copied the units hard-coded into its XAML
/// (<c>TextBlock Text="turns"</c>, "tokens", "msg", "iter", h/min/s…): English in all ten languages. Each unit
/// is bound to a property ApplyLabels fills from Strings. The XAML is read as is.
/// </summary>
public class SettingsUnitLabelTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheVsSettingsWindow_ShowsNoLiteralUnit()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));

        // Witness: the compact fields with a unit are still there.
        Assert.Contains("ContextWindowKeepTurnsText", xaml, StringComparison.Ordinal);
        Assert.Contains("ContextWindowSizeText", xaml, StringComparison.Ordinal);

        var literals = Regex.Matches(xaml, "<TextBlock Text=\"(s|min|h|GB|tokens|turns|msg|chunks|iter)\"")
                            .Select(m => m.Groups[1].Value)
                            .ToList();
        Assert.True(literals.Count == 0,
            "Literal units in the VS settings window (English in every language): " + string.Join(", ", literals));
    }
}
