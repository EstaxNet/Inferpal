using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio settings window shares its configuration instance with the chat. Its save writes
/// the form into a COPY of what it displayed when it opened, then lays over the shared instance only what
/// the form changed (<c>ApplyChangesFrom</c>, tested in <c>ConfigApplyChangesTests</c>): a /model or a pin
/// made from the chat is no longer reverted. The view model cannot be instantiated outside VS: the guard
/// reads its code.
/// </summary>
public class SettingsSaveMergeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        return string.Empty;
    }

    [Fact]
    public void TheWindowSave_OnlyAppliesWhatTheFormChanged()
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
        var save = Body(code, "private async Task SaveCoreAsync(");

        // Witness: the save still reads the chat model and the pinned files from the form.
        Assert.Contains("Kept(model", save, StringComparison.Ordinal);
        Assert.Contains("pinnedContextFiles", save, StringComparison.Ordinal);

        Assert.Contains("_config.ApplyChangesFrom(edited, _opened)", save, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(save, @"_config\.\w+\s*=[^=]"));
        Assert.Contains("SnapshotNow()", Body(code, "public InferpalSettingsData("), StringComparison.Ordinal);
    }
}
