using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Xunit;

namespace Inferpal.Tests;

public partial class HostServerTests
{
    /// <summary>A compaction summary that reasons sends its progress to the editor's step line (<c>chat/step</c>), not only
    /// "Compacting context…" once for the whole summary (<see cref="SummaryProgressTests"/>).</summary>
    [Fact]
    public async Task AtTheHost_TheEditorsStepLine_FollowsTheSummary()
    {
        using var h = CreateHarness(cfg =>
        {
            cfg.ContextWindowSize = 8_192; cfg.CompactionEnabled = true; cfg.ContextWindowKeepTurns = 1;
        });
        await h.InitializeAsync();
        var words = string.Join(' ', Enumerable.Range(0, 1_500).Select(i => $"word{i}"));
        h.Fake.DriveThinking = think => think(new string('r', 400));
        h.Fake.OnChatRequest = (_, messages, _, _) => Task.FromResult(new ChatTurnResult(
            messages[^1].Content?.Contains("long", StringComparison.Ordinal) == true ? "done " + words : "done", null, 3, 5));

        for (var i = 0; i < 6 && !h.Target.ToolNotes.Any(n => n.Name == "context_compact"); i++)
        {
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = $"long question {i}", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            // Notifications are one-way: a round-trip request guarantees they were dispatched.
            await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }

        Assert.Contains(h.Target.ToolNotes, n => n.Name == "context_compact");   // witness: the history was compacted
        Assert.Contains(h.Target.Steps, s => SummaryProgressTests.IsProgress(s, 100));
    }
}
