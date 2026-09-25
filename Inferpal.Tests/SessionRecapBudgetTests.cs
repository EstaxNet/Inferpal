using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The session recap (every <c>oodaTurnThreshold</c> turns, on by default) must fit the window of the model that writes
/// it. It sent the whole history — the chat's system prompt included — to the utility model, whose window a server
/// chooses per model: LM Studio refused it (no recap, ever, and only a /diagnostics line to say so); Ollama cut its head,
/// the system prompt that carries the previous recap first, so "Goal" was rewritten from the recent turns alone.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public async Task TheSessionRecapRequest_FitsTheUtilityModelsWindow()
    {
        using var h = CreateHarness(cfg =>
        {
            cfg.OodaTurnThreshold = 1;
            cfg.ContextWindowSize = 32_768;
            cfg.UtilityModel      = "utility-small";
        });
        h.Fake.LoadedContextWindow = 4_096;
        var recapChars = -1;
        h.Fake.OnChatRequest = (_, messages, _, _) =>
        {
            if (messages[^1].Content == Strings.OodaSummarizePrompt)
            {
                recapChars = messages.Sum(m => (m.Content ?? string.Empty).Length);
                return Task.FromResult(new ChatTurnResult("Goal: ship. Progress: parser. Open: tests.", null, 1, 1));
            }
            return Task.FromResult(new ChatTurnResult("answer " + new string('a', 60_000), null, 1, 1));
        };
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(recapChars >= 0, "the session recap was never requested");                    // witness
        Assert.True(recapChars <= 4_096 * 4, $"the recap request carried {recapChars} characters into 4 096 tokens");
        Assert.Contains("utility-small", h.Fake.LoadedContextQueries);
    }
}

public class SessionRecapRequestTests
{
    private static List<ChatMessageDto> History(int turns, int answerChars)
    {
        var h = new List<ChatMessageDto> { new("system", "CHAT SYSTEM PROMPT " + new string('s', 20_000)) };
        for (var i = 0; i < turns; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i} " + new string('a', answerChars)));
        }
        return h;
    }

    [Fact]
    public void TheRequest_EndsOnTheRecapInstruction_AndAlternatesRoles()
    {
        var request = SessionRecap.BuildRequest(History(3, 10), previousRecap: null, budgetChars: int.MaxValue);

        Assert.Equal(["system", "user"], request.Messages.Select(m => m.Role));
        Assert.Equal(Strings.OodaSummarizePrompt, request.Messages[^1].Content);
        Assert.Contains("question 0", request.Messages[0].Content);
        Assert.DoesNotContain("CHAT SYSTEM PROMPT", request.Messages[0].Content);
        Assert.Equal(0, request.Omitted);
    }

    [Fact]
    public void ThePreviousRecap_IsCarried_SoTheGoalSurvivesTheTurnsThatDidNotFit()
    {
        var request = SessionRecap.BuildRequest(History(30, 3_000), previousRecap: "Goal: migrate the parser.",
                                                budgetChars: 16_384);

        Assert.Contains("Goal: migrate the parser.", request.Messages[0].Content);
        Assert.Contains("question 29", request.Messages[0].Content);
        Assert.DoesNotContain("question 0\n", request.Messages[0].Content!.Replace("\r\n", "\n"));
        Assert.True(request.Omitted > 0);
        Assert.True(request.Messages.Sum(m => m.Content!.Length) <= 16_384 + Strings.OodaSummarizePrompt.Length + 400);
    }
}
