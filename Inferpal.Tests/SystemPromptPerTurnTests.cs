using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The system prompt carries files that change during a session — pinned files (the setting says
/// "injected into the system prompt before each request"), <c>.inferpal/context.md</c>,
/// <c>memory.md</c>, the rules. The VS Code host rebuilds it for every question; the Visual Studio
/// view model only on a few gestures, so an edited file — by the user, a <c>git pull</c>, or the
/// agent itself — reached the model in its OLD version until one of them happened. The view model
/// is Remote UI and cannot run here: a source scan, with witnesses.
/// </summary>
public class SystemPromptPerTurnTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The code from the rebuild call up to the line that appends the question.</summary>
    private static string Before(string code, string append)
    {
        var at = code.IndexOf(append, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{append}\" is gone: this test would measure nothing.");
        return code[..at];
    }

    [Fact]
    public void BothFrontEnds_RebuildTheSystemPrompt_RightBeforeTheQuestionEntersTheHistory()
    {
        var vm   = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));

        // The last statement before the question is appended must be the rebuild — anywhere earlier
        // in the file would not prove it runs on THIS path.
        foreach (var (code, append) in new[]
                 {
                     (vm,   "_history.Add(new ChatMessageDto(\"user\", historyText));"),
                     (host, "s.History.Add(new ChatMessageDto(\"user\", promptText));"),
                 })
        {
            var head = Before(code, append).TrimEnd();
            Assert.EndsWith(code == vm ? "RefreshSystemPrompt();" : "RefreshSystemPrompt(s);", head, StringComparison.Ordinal);
        }
    }
}
