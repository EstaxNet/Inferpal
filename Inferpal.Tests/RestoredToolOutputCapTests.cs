using Inferpal.Services.Agent;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A tool result enters the live context cut to <see cref="AgentOrchestrator.MaxToolResultCharsInContext"/>,
/// while the chat keeps — and saves — the WHOLE output. Reloading a session (or branching one) rebuilt the
/// history from what was saved, so a conversation in which the agent read a large file or ran a verbose
/// command came back many times larger than anything the model had seen: the first question after the
/// reload overflowed the context window, and nothing trims a restored turn before it is sent.
/// </summary>
public class RestoredToolOutputCapTests
{
    private static readonly string Huge = new('x', AgentOrchestrator.MaxToolResultCharsInContext * 6);

    [Fact]
    public void ARestoredToolResult_IsNoLargerThanTheModelSawItLive()
    {
        var history = SessionManager.BuildRestoredHistory("system",
        [
            new SavedMessage("user", "read the big file"),
            new SavedMessage("tool", Huge, "read_file"),
            new SavedMessage("assistant", "done"),
        ]);

        var turn = Assert.Single(history, m => m.Content!.Contains("[Tool result — read_file]"));
        Assert.True(turn.Content!.Length <= "read the big file".Length + AgentOrchestrator.MaxToolResultCharsInContext + 400,
            $"restored turn is {turn.Content.Length} characters");
        Assert.Contains("truncated", turn.Content);                 // the cut is said, as it was live
    }

    [Fact]
    public void ASmallToolResult_IsRestoredWhole()
    {
        // Reference arm.
        var history = SessionManager.BuildRestoredHistory("system",
        [
            new SavedMessage("user", "list"),
            new SavedMessage("tool", "a.cs\nb.cs", "list_files"),
        ]);

        Assert.Contains("a.cs\nb.cs", history[^1].Content);
        Assert.DoesNotContain("truncated", history[^1].Content);
    }
}
