using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The code-block copy button in the Visual Studio window wrote <c>Content="Copy"</c> hard-coded: English
/// in all ten languages, while VS Code translates the same button. The label comes from the VM, set by
/// ApplyLabels from Strings. Remote UI types cannot be instantiated outside VS: the rule reads the XAML
/// and the source (without comments).
/// </summary>
public class CodeBlockCopyLabelTests
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
    public void TheCodeBlockCopyButton_IsLabelledInTheInterfaceLanguage()
    {
        var toolWindow   = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var xaml         = File.ReadAllText(Path.Combine(toolWindow, "InferpalToolWindowContent.xaml"));
        var construction = ConventionCoverageTests.CodeOnly(Path.Combine(toolWindow, "InferpalToolWindowData.Construction.cs"));

        // Witness: the code-block copy button is still there.
        Assert.Contains("{Binding CopyCodeCommand}", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Content=\"Copy\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding DataContext.LabelCopyCode, ElementName=root}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LabelCopyCode", construction, StringComparison.Ordinal);
        Assert.Contains("Strings.LabelCopyCode", construction, StringComparison.Ordinal);
    }
}
