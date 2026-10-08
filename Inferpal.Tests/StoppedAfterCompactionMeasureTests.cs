using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code host: a turn STOPPED after its compaction leaves the conversation measured.
/// </summary>
/// <remarks>
/// The compaction set the measure to 0 and only a turn that ran to its end wrote a new one: Stop in that window left 0,
/// the measure of a first turn, which the next check reads as "nothing to decide" — the next question went out with no
/// context check, whatever it attached, and the ring showed the tool definitions alone. The Visual Studio window
/// measures the history right after its compaction.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task ChatSend_StoppedAfterACompaction_LeavesTheConversationMeasured()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var session = h.Server.CurrentSession!;
        session.ToolsEnabled             = false;
        session.Config.ContextWindowSize = 4096;
        session.Config.CompactionEnabled = false;      // a truncation: no summary request to script
        session.Config.ContextWindowKeepTurns = 1;

        var words   = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"word{i}"));
        var history = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 6; i++)
        {
            history.Add(new ChatMessageDto("user", $"question {i} {words}"));
            history.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        session.History = history;                     // measured by the setter: over the trigger

        h.Fake.OnChat = (_, _) => throw new OperationCanceledException();   // the user pressed Stop

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "and now?", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(result.Cancelled);
        Assert.Contains(session.History, m => m.Content?.StartsWith("[Context Note]", StringComparison.Ordinal) == true);  // witness
        Assert.Equal(AgentOrchestrator.EstimateTokens(session.History), session.LastPromptTokens);
        Assert.True(result.NextTurnTokens >= session.LastPromptTokens, $"ring: {result.NextTurnTokens}");
    }
}
