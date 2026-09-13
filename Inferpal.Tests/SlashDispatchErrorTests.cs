using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A slash command that throws must not go silent in Visual Studio. The host renders the exception
/// through Strings.MsgError (RunDelegatedSlashAsync); the VM called HandleSlashCommandAsync inside a
/// try/finally with no catch, and no method below it caught either: the failure went into the window's
/// async command and the user saw nothing. The VM cannot be instantiated outside VS: the rule reads the
/// source, without its comments.
/// </summary>
public class SlashDispatchErrorTests
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
    public void AThrowingSlashCommand_IsShownInTheVsChat_LikeTheHostDoes()
    {
        var source = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));

        var start = source.IndexOf("private async Task HandleSlashCommandAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "HandleSlashCommandAsync not found: the rule reads nothing");
        var end  = source.IndexOf("private async Task RunCodeActionCommandAsync(", start, StringComparison.Ordinal);
        var body = source[start..(end > start ? end : source.Length)];

        // Witness: this is the slash command dispatcher.
        Assert.Contains("RunDelegatedCommandAsync(", body, StringComparison.Ordinal);

        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);
        Assert.Contains("Strings.MsgError(", body, StringComparison.Ordinal);
    }
}
