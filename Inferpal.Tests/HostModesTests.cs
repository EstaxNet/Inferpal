using System.IO;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A mode the person picks in VS Code or an ACP client, held by the host: its instructions in the system message, its
/// tools only, its effort, its model when the server has one — said once when it has none —, kept with the
/// conversation, left by Plan, and said when its file is gone.
/// </summary>
public partial class HostServerTests
{
    private const string ReviewMode = """
        ---
        name: Review
        description: Reads the change and comments; never writes
        model: [gpt-5, qwen3.8:27b]
        tools: [read, search]
        reasoning-effort: high
        ---
        REVIEW-INSTRUCTIONS: comment on the change, never edit a file.
        """;

    private static void WriteMode(Harness h, string fileName, string text)
    {
        var dir = Path.Combine(h.RootDir, ".inferpal", "modes");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), text);
    }

    private static Task<T> Call<T>(Harness h, string method, object args) =>
        h.Client.InvokeWithParameterObjectAsync<T>(method, args).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

    /// <summary>
    /// No mode here: the selector still has its words — where a mode goes, and the page that says how to write one —
    /// since the segment shows anyway. One mode written, the tooltip counts it.
    /// </summary>
    [Fact]
    public async Task ModeList_WithNoModeHere_SaysWhereOneGoes_AndLinksThePageOnWritingOne()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var none = (await Call<ModeListResult>(h, "mode/list", new { })).Labels!;
        Assert.Equal(Services.Presentation.ModePresenter.None, none.None);
        Assert.Equal(Services.Presentation.ModePresenter.None, none.SegmentTip);
        Assert.Contains(Services.Governance.ChatModes.UserDir, none.None, StringComparison.Ordinal);   // the person's own folder
        Assert.Equal(Localization.Strings.ModesWriteOne, none.WriteOne);
        Assert.Equal(Services.DocsLinks.Modes, none.WriteOneUrl);

        WriteMode(h, "review.md", ReviewMode);
        var one = (await Call<ModeListResult>(h, "mode/list", new { })).Labels!;
        Assert.Equal(Localization.Strings.ModesSegmentTip(1), one.SegmentTip);
    }

    [Fact]
    public async Task AModeHeld_SendsItsInstructionsItsToolsItsEffort_AndItsModelTheServerHas()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        h.Fake.ModelNames = ["devstral-small-2", "qwen3.8:27b"];
        h.Fake.RunAgentThroughChat = false;   // the run itself records its effort
        IToolRegistry? offered = null;
        h.Fake.OnAgentRun = (history, tools, _, _) =>
        {
            offered = tools;
            return Task.FromResult(new AgentResult("Two points.", [], history));
        };
        await h.InitializeAsync();

        var list = await Call<ModeListResult>(h, "mode/list", new { });
        Assert.Contains(list.Modes, m => m.Name == "Review" && m.Source == ".inferpal/modes/review.md" && !m.MayWrite);
        var set = await Call<ModeSetResult>(h, "mode/set", new { name = "review" });
        Assert.Equal("Review", set.Mode!.Name);
        Assert.Equal("qwen3.8:27b", set.Model);   // the first of its models the server has

        var sent = await Call<ChatSendResult>(h, "chat/send", new { prompt = "review it", agentMode = false, model = "devstral-small-2" });

        Assert.Null(sent.Error);
        var (model, history) = Assert.Single(h.Fake.AgentRuns);
        Assert.Equal("qwen3.8:27b", model);
        Assert.Contains("## Mode: Review", history[0].Content, StringComparison.Ordinal);
        Assert.Contains("REVIEW-INSTRUCTIONS", history[0].Content, StringComparison.Ordinal);
        Assert.Contains(offered!.Definitions, d => d.Function.Name == "read_file");
        Assert.DoesNotContain(offered.Definitions, d => d.Function.Name is "write_file" or "run_command");
        Assert.Equal(ReasoningEffort.High, h.Fake.Efforts[^1]);
        Assert.Equal("Review", (await Call<ModeListResult>(h, "mode/list", new { })).Current);
    }

    /// <summary>A code action runs without the held mode — its instructions too, not only its tools and model; the next
    /// question has them again.</summary>
    [Fact]
    public async Task ACodeAction_IsSentWithoutTheHeldModesInstructions()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        h.Fake.ModelNames = ["devstral-small-2"];
        string? codeActionSystem = null;
        h.Fake.OnChatRequest = (_, messages, _, _) =>
        {
            codeActionSystem ??= messages[0].Content;
            return Task.FromResult(new ChatTurnResult("explained", null, 1, 1));
        };
        h.Fake.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("ok", [], history));
        await h.InitializeAsync();
        await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });

        await Call<ChatSendResult>(h, "chat/send", new { prompt = "Explain the following code", model = "devstral-small-2", codeAction = true });
        await Call<ChatSendResult>(h, "chat/send", new { prompt = "review it", agentMode = false, model = "devstral-small-2" });

        Assert.NotNull(codeActionSystem);
        Assert.DoesNotContain("REVIEW-INSTRUCTIONS", codeActionSystem, StringComparison.Ordinal);
        Assert.Contains("REVIEW-INSTRUCTIONS", h.Fake.AgentRuns[^1].History[0].Content, StringComparison.Ordinal);   // witness
    }

    [Fact]
    public async Task AModeWhoseModelTheServerLacks_IsSaidOnce_AndTheChatModelAnswers()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        h.Fake.ModelNames = ["devstral-small-2"];
        h.Fake.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("ok", [], history));
        await h.InitializeAsync();
        await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });

        await Call<ChatSendResult>(h, "chat/send", new { prompt = "one", agentMode = false, model = "devstral-small-2" });
        await Call<ChatSendResult>(h, "chat/send", new { prompt = "two", agentMode = false, model = "devstral-small-2" });
        await Call<string[]>(h, "models/list", new { });   // drains the notifications sent before it

        Assert.Equal(2, h.Fake.AgentRuns.Count);
        Assert.All(h.Fake.AgentRuns, r => Assert.Equal("devstral-small-2", r.Model));
        Assert.Single(h.Target.HostNotices, n => n.Contains("gpt-5, qwen3.8:27b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnknownMode_IsRefused_AndPlanLeavesTheModeHeld()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        await h.InitializeAsync();

        var refused = await Call<ModeSetResult>(h, "mode/set", new { name = "Nope" });
        Assert.Null(refused.Mode);
        Assert.Contains("Nope", refused.Refused, StringComparison.Ordinal);
        Assert.Null((await Call<ModeListResult>(h, "mode/list", new { })).Current);

        await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });
        await Call<bool>(h, "plan/mode", new { enabled = true });
        Assert.Null((await Call<ModeListResult>(h, "mode/list", new { })).Current);
        Assert.DoesNotContain("## Mode: Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);

        // Typed, /plan leaves the mode too: a second /plan comes back to Chat, never to the mode.
        await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });
        await Call<SlashCommandResult>(h, "command/slash", new { text = "/plan" });
        await Call<SlashCommandResult>(h, "command/slash", new { text = "/plan" });
        Assert.Null((await Call<ModeListResult>(h, "mode/list", new { })).Current);
    }

    [Fact]
    public async Task AConversationKeepsItsMode_AndAModeWhoseFileIsGone_IsSaidAndLeft()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        h.Fake.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("ok", [], history));
        await h.InitializeAsync();
        await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });
        var name = $"test-host-mode-{Guid.NewGuid():N}";
        try
        {
            await Call<object?>(h, "session/save", new
            {
                name,
                messages = new object[] { new { role = "user", content = "hello" }, new { role = "assistant", content = "hi" } },
            });
            await Call<ModeSetResult>(h, "mode/set", new { name = (string?)null });

            var loaded = await Call<SessionLoadResult?>(h, "session/load", new { name });
            Assert.Equal("Review", loaded!.Mode);
            Assert.Contains("## Mode: Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);

            File.Delete(Path.Combine(h.RootDir, ".inferpal", "modes", "review.md"));
            await Call<ChatSendResult>(h, "chat/send", new { prompt = "still there?", agentMode = false });
            await Call<string[]>(h, "models/list", new { });

            Assert.Contains(h.Target.HostNotices, n => n == Localization.Strings.ModeGone("Review"));
            Assert.DoesNotContain("## Mode:", h.Fake.AgentRuns[^1].History[0].Content, StringComparison.Ordinal);

            // Loaded again with its file gone: the conversation comes back without it, said — never a failed load.
            var again = await Call<SessionLoadResult?>(h, "session/load", new { name });
            Assert.NotNull(again);
            Assert.Null(again!.Mode);
            await Call<string[]>(h, "models/list", new { });
            Assert.Equal(2, h.Target.HostNotices.Count(n => n == Localization.Strings.ModeGone("Review")));
        }
        finally
        {
            await Call<bool>(h, "session/delete", new { name });
        }
    }

    [Fact]
    public async Task TheSelectorsList_SaysEachModesModelAndFile_InTheSelectorsWords()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        h.Fake.ModelNames = ["devstral-small-2", "qwen3.8:27b"];
        await h.InitializeAsync();

        var list = await Call<ModeListResult>(h, "mode/list", new { models = true });
        var review = Assert.Single(list.Modes, m => m.Name == "Review");
        Assert.Equal($"qwen3.8:27b · {Localization.Strings.ModeReadsOnly} · .inferpal/modes/review.md", review.Summary);
        Assert.Equal(Localization.Strings.ModesSegment, list.Labels!.Segment);
        Assert.Equal(Localization.Strings.ModesFooter, list.Labels.Footer);

        // Without the server's list, the conversation's model: one question to the server only when the selector asks.
        var plain = await Call<ModeListResult>(h, "mode/list", new { });
        Assert.StartsWith(Localization.Strings.ModePickerChatModel, plain.Modes.Single(m => m.Name == "Review").Summary,
                          StringComparison.Ordinal);

        var set = await Call<ModeSetResult>(h, "mode/set", new { name = "Review" });
        Assert.Equal(Localization.Strings.ModeHeldLine("Review", Localization.Strings.ModeReadsOnly), set.Mode!.HeldLine);
    }

    [Fact]
    public async Task TheModesCommand_ListsEachModeAndWhatWasSetAside()
    {
        using var h = CreateHarness();
        WriteMode(h, "review.md", ReviewMode);
        WriteMode(h, "chat.md", "---\nname: Chat\n---\nWould hide Inferpal's own.\n");
        await h.InitializeAsync();

        var result = await Call<SlashCommandResult>(h, "command/slash", new { text = "/modes" });

        Assert.Contains("**Review**", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("`.inferpal/modes/review.md`", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("read, search", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("`.inferpal/modes/chat.md`", result.Markdown, StringComparison.Ordinal);
    }
}
