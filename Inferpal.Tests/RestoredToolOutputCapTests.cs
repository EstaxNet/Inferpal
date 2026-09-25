using Inferpal.Services.Agent;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A reloaded (or branched) conversation is the history the model had LIVE — the question and the answer of each
/// turn. The chat keeps, and a session saves, every tool output WHOLE; the restore folded them back in (capped), so a
/// conversation came back larger than anything the model had: live, a run's tool results do not outlive the run.
/// Measured on real sessions, 1.9× to 8.9× the live history — 31 359 characters against 16 269 for the session
/// Visual Studio reloads each time it opens, a whole default window before the first question.
/// </summary>
public class RestoredToolOutputCapTests
{
    private static readonly string Huge = new('x', AgentOrchestrator.MaxToolResultCharsInContext * 6);

    [Fact]
    public void ARestoredConversation_CarriesNoToolOutput()
    {
        var history = SessionManager.BuildRestoredHistory("system",
        [
            new SavedMessage("user", "read the big file"),
            new SavedMessage("tool", Huge, "read_file"),
            new SavedMessage("assistant", "done"),
        ]);

        Assert.DoesNotContain(history, m => m.Content!.Contains("[Tool result"));
        Assert.True(history.Sum(m => m.Content!.Length) < 200,
                    $"restored history is {history.Sum(m => m.Content!.Length)} characters");
    }

    [Fact]
    public void TheQuestionAndTheAnswer_AreRestoredWhole()
    {
        // Reference arm: what the model had live comes back, untouched.
        var history = SessionManager.BuildRestoredHistory("system",
        [
            new SavedMessage("user", "list"),
            new SavedMessage("tool", "a.cs\nb.cs", "list_files"),
            new SavedMessage("assistant", "Two files: a.cs and b.cs."),
        ]);

        Assert.Equal("list", history[1].Content);
        Assert.Equal("Two files: a.cs and b.cs.", history[2].Content);
    }
}
