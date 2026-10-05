using System.IO;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio's chat built its pinned-file chips ONCE, at startup, then edited the configuration from them: a file
/// pinned in the settings window was silently unpinned by the next pin or unpin in the chat, and a file disabled there
/// (<c>#path</c>) was enabled again. The host reads the live setting on every edit; the window now does too, and its
/// chips are rebuilt from the setting whenever the configuration is saved.
/// </summary>
public class PinnedChipsFollowTheSettingTests
{
    private static string Vm(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", file));
    }

    [Fact]
    public void ThePinEdits_StartFromTheLiveSetting_NeverFromTheChips()
    {
        var code = Vm("InferpalToolWindowData.Attachments.cs");
        Assert.DoesNotContain("PinnedFiles.Select(p => p.Path)", code, StringComparison.Ordinal);
        Assert.Contains("var current = PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles);", code, StringComparison.Ordinal);
        Assert.Contains("SavePinnedFiles(PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChips_AreRebuiltWhenTheSettingIsSaved()
    {
        var attachments = Vm("InferpalToolWindowData.Attachments.cs");
        var load = attachments[attachments.IndexOf("private void LoadPinnedFilesFromConfig()", StringComparison.Ordinal)..];
        Assert.Contains("PinnedFiles.Clear();", load[..load.IndexOf('}')], StringComparison.Ordinal);

        Assert.Contains("_config.Saved         += () => Post(LoadPinnedFilesFromConfig);",
                        Vm("InferpalToolWindowData.Chrome.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsPinAndADisabledLine_SurviveAChatEdit()   // the property the window now relies on
    {
        // Settings pinned B and disabled C; the chat pins D from the live setting.
        const string setting = "A\nB\n#C";
        var saved = PinnedFilesPolicy.Serialize([.. PinnedFilesPolicy.ParseActive(setting), "D"], setting);

        Assert.Equal(["A", "B", "D"], PinnedFilesPolicy.ParseActive(saved));
        Assert.Contains("#C", saved, StringComparison.Ordinal);
    }
}
