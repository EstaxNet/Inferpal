using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The number of messages <c>/history</c> and VS Code's session picker give a session counts its conversation — the
/// questions and the answers — not the tool steps and notices saved with it.
/// </summary>
/// <remarks>
/// It was the length of the saved transcript: two questions answered after thirty tool steps read "34 messages", and
/// VS Code's picker printed "34 msg · from X @ turn 2", two units on one line.
/// </remarks>
public sealed class SessionMessageCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"msgcount-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ASessionCountsItsQuestionsAndAnswers_NotItsToolStepsOrNotices()
    {
        var store = new ConversationStore(_dir);
        await store.SaveAsync("agent-session",
        [
            new("user", "fix the build"),
            new("tool", "{...}", "read_file"), new("tool", "{...}", "apply_diff"), new("tool", "{...}", "get_diagnostics"),
            new("assistant", "Fixed: the missing using."),
            new("assistant", "⚠ The run stopped at its iteration limit.", SessionManager.NoticeMarker),
            new("user", "/diagnostics", SessionManager.NoticeMarker),
            new("user", "and the tests?"),
            new("tool", "{...}", "run_tests"),
            new("assistant", "All 12 pass."),
        ], CancellationToken.None);

        var summary = Assert.Single((await store.ListWithPreviewAsync(CancellationToken.None)).Items);

        Assert.Equal(4, summary.MessageCount);
    }

    [Fact]
    public async Task AConversationWithoutTools_CountsEveryMessage()
    {
        // Reference arm.
        var store = new ConversationStore(_dir);
        await store.SaveAsync("chat", [new("user", "q1"), new("assistant", "a1"), new("user", "q2"), new("assistant", "a2")],
                              CancellationToken.None);

        Assert.Equal(4, Assert.Single((await store.ListWithPreviewAsync(CancellationToken.None)).Items).MessageCount);
    }
}
