using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>What Visual Studio's chat window does, by running it (<see cref="VsWindowHarness"/>).</summary>
[Collection(SignalCollection.Name)]
public sealed class VsWindowRunsUnderTestTests
{
    [Fact]
    public async Task AQuestion_GoesThroughTheWindow_AndItsAnswerIsShown()
    {
        using var vs = new VsWindowHarness(new FakeInferenceProvider { ChatResult = new ChatTurnResult("THE ANSWER", null, 0, 0) });

        await vs.AskAsync("the question");

        Assert.Contains(vs.Window.Messages, m => m.Role == "assistant" && m.Content.Contains("THE ANSWER", StringComparison.Ordinal));
        Assert.Contains(vs.Window.Messages, m => m.Role == "user" && m.Content.Contains("the question", StringComparison.Ordinal));
        Assert.Equal(string.Empty, vs.Window.Prompt);
        Assert.False(vs.Window.IsLoading);
    }

    /// <summary>An agent run's reasoning shows in the status line as the generic indicator, never as its text: a tail cut
    /// mid-word that quotes the question back reads as the model's answer landing there.</summary>
    [Fact]
    public async Task AnAgentRunsReasoning_ShowsAsThinking_NeverAsItsText()
    {
        var fake = new FakeInferenceProvider { ChatResult = new ChatTurnResult("THE ANSWER", null, 0, 0) };
        fake.DriveThinking = emit => emit("back and tell me what it contains");
        using var vs = new VsWindowHarness(fake, config: new InferpalConfig { DefaultModel = "m", AgentModeEnabled = true });
        var steps = new List<string>();
        vs.Window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vs.Window.CurrentStep)) lock (steps) steps.Add(vs.Window.CurrentStep);
        };

        await vs.AskAsync("the question");

        lock (steps)
        {
            Assert.Contains("\U0001F4AD " + Strings.StatusThinking, steps);   // witness: the reasoning reached the status line
            Assert.DoesNotContain(steps, s => s.Contains("tell me", StringComparison.Ordinal));
        }
    }

    /// <summary>The whole way: a write asks on a card in the conversation, Enter answers it, the file is written, the turn ends.</summary>
    [Fact]
    public async Task AWrite_AsksOnItsCard_AndEnterLetsItThrough()
    {
        string? said = null;
        var fake = new FakeInferenceProvider();
        using var vs = new VsWindowHarness(fake);
        var file = Path.Combine(vs.Root, "new.txt");
        fake.OnAgentRun = async (history, tools, _, _) =>
        {
            said = await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path = file, content = "written\n" }), CancellationToken.None);
            return new AgentResult("Done.", [], history);
        };

        var turn = vs.AskAsync("write the file");
        await VsWindowHarness.UntilAsync(() => said is not null || vs.Window.Messages.Any(m => m.Role == "approval"));

        Assert.Null(said);                 // refused before the card, it would have said so
        Assert.False(File.Exists(file));   // the card holds it
        await vs.AskAsync(string.Empty);   // Enter on an empty prompt answers the card
        await turn.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("written\n", File.ReadAllText(file));
        Assert.False(vs.Window.IsLoading);
    }

    /// <summary>A forced card in the chat does not offer "Always" — a command whose text hides what it runs; an ordinary
    /// command's card does (reference arm).</summary>
    [Theory]
    [InlineData("Invoke-Expression $x", false)]
    [InlineData("echo hi", true)]
    public async Task ACommandsCard_OffersAlways_OnlyWhenItIsNotForced(string command, bool offers)
    {
        string? said = null;
        var fake = new FakeInferenceProvider();
        using var vs = new VsWindowHarness(fake);
        fake.OnAgentRun = async (history, tools, _, _) =>
        {
            said = await tools.ExecuteAsync("run_command", JsonSerializer.SerializeToElement(new { command }), CancellationToken.None);
            return new AgentResult("Done.", [], history);
        };

        var turn = vs.AskAsync("run it");
        await VsWindowHarness.UntilAsync(() => said is not null || vs.Window.Messages.Any(m => m.Role == "approval"));

        Assert.Equal(offers, vs.Window.Messages.Single(m => m.Role == "approval").OffersAlways);
        await vs.AskAsync(string.Empty);
        await turn.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>With nothing open, the last resort is the process's folder: git would read the repository around it. Under a
    /// test that is this very repository, with its solution near: the case itself.</summary>
    /// <remarks>⚠ What only reads. With its guard gone, <c>/commit-exec</c> run here commits to this repository:
    /// <c>Lot0Cycle12GesturesTests</c> reads it instead.</remarks>
    [Theory]
    [InlineData("/check")]
    [InlineData("/commit")]
    [InlineData("/pr-description")]
    [InlineData("/onboard")]
    public async Task AGitCommand_WithNothingOpen_AsksForAWorkspace(string command)
    {
        using var vs = new VsWindowHarness(folderOpen: false);

        await vs.AskAsync(command);
        await VsWindowHarness.UntilAsync(() => vs.Window.Messages.Any(m => m.Content.Contains(Strings.GitNeedsAWorkspace, StringComparison.Ordinal)));

        Assert.Empty(vs.Fake.AgentRuns);
    }

    /// <summary>The servers a pull changed under a turn: a refresh tears every one down, so it waits for the turn to end. A
    /// background task holds the lease here, as it does while the chat stays free for a command.</summary>
    [Fact]
    public async Task TheRepositorysMcpFiles_ChangedUnderARunningTurn_AreFollowedOnceItEnds()
    {
        using var vs = new VsWindowHarness(config: new InferpalConfig { DefaultModel = "m", McpEnabled = true });
        var file = Path.Combine(vs.Root, ".mcp.json");
        File.WriteAllText(file, "{\"mcpServers\":{\"a\":{\"command\":\"a\"}}}");
        await vs.Tools.Mcp.RefreshAsync();
        File.WriteAllText(file, "{\"mcpServers\":{\"b\":{\"command\":\"b\"}}}");
        Assert.True(vs.Tools.Mcp.RepositoryFilesChanged());

        using (GpuScheduler.AcquireChatLease())
        {
            await vs.AskAsync("/help");
            Assert.True(vs.Tools.Mcp.RepositoryFilesChanged());   // not read again: the refresh is waiting
        }

        await VsWindowHarness.UntilAsync(() => !vs.Tools.Mcp.RepositoryFilesChanged());
    }

    /// <summary>The agent loop reads these settings: what the other editor saved waits for the turn, and the next gesture takes it.</summary>
    [Fact]
    public async Task ASettingSavedElsewhere_WaitsForTheTurnToEnd()
    {
        var previous = InferpalConfig.OverridePathForTests;
        var path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"vm-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        InferpalConfig.OverridePathForTests = path;
        try
        {
            new InferpalConfig { DefaultModel = "m", PermissionRules = "" }.Save();
            var config = InferpalConfig.LoadOwnCopy();
            using var vs = new VsWindowHarness(config: config);
            var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(path))!;
            other.PermissionRules = "deny run_command ^rm";
            File.WriteAllText(path, JsonSerializer.Serialize(other));

            using (GpuScheduler.AcquireChatLease())
            {
                await vs.AskAsync("/help");
                Assert.Equal(string.Empty, config.PermissionRules);
            }

            await vs.AskAsync("/help");
            Assert.Equal("deny run_command ^rm", config.PermissionRules);
        }
        finally
        {
            InferpalConfig.OverridePathForTests = previous;
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>A window built before its solution restores the slot from its heartbeat: a slot it cannot read does not end the
    /// heartbeat, or the window would stop saying the server is gone.</summary>
    [Fact]
    public async Task ADamagedSlot_ReachedLate_LeavesTheHeartbeatRunning()
    {
        using var vs = new VsWindowHarness(folderOpen: false);
        var slot = new ConversationStore(slotOwner: ConversationStore.VisualStudioSlot).AutoSaveFile(vs.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        File.WriteAllText(slot, "{ damaged");
        ActiveSolutionSignal.WriteFolder(vs.Root);   // the solution, opened after the window

        await VsWindowHarness.UntilAsync(() => vs.Fake.ConnectionChecks > 0);
    }

    /// <summary>The last question of the conversation restored is left unanswered by the closing that cut its run. A notice
    /// after it is not an answer: the line that says so looks past it.</summary>
    [Fact]
    public async Task AQuestionTheClosingCut_SaysNoAnswerWillFollow_PastANotice()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-vm").FullName;
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, "a0\n");
        var closed = new FileHistoryService { WorkspaceRoot = () => root, Editor = ConversationStore.VisualStudioSlot };
        closed.BeginRun();
        await closed.BackUpBeforeChangeAsync(file, CancellationToken.None);
        File.WriteAllText(file, "a1\n");
        closed.CutCurrentRun();
        closed.EndRun();   // the turn's own end, as the editor closes
        await new ConversationStore(slotOwner: ConversationStore.VisualStudioSlot).AutoSaveAsync(
            [new SavedMessage("user", "the question", null), new SavedMessage("assistant", "said at the start", SessionManager.NoticeMarker)],
            CancellationToken.None, root);

        using var vs = new VsWindowHarness(root: root);

        await VsWindowHarness.UntilAsync(() => vs.Window.Messages.Any(m => m.Content.Contains(Strings.AwayNoAnswer("Visual Studio"), StringComparison.Ordinal)));
    }

    /// <summary>Reference arm: with a folder open, the same command reaches git, and says what git answered there.</summary>
    [Fact]
    public async Task AGitCommand_WithAFolderOpen_GoesOn()
    {
        using var vs = new VsWindowHarness();
        var git = GitProcess.For(vs.Root);
        await git("init -q -b topic", CancellationToken.None);
        await git("-c user.name=t -c user.email=t@t.invalid commit -q --allow-empty -m first", CancellationToken.None);

        await vs.AskAsync("/pr-description");

        await VsWindowHarness.UntilAsync(() => vs.Window.Messages.Any(m => m.Content.Contains(Strings.PrNoBase, StringComparison.Ordinal)));
    }
}
