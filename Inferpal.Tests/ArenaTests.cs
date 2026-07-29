using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Arena;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// /arena (ROADMAP 1.3.0 §6): pair resolution (auto chat-vs-utility, explicit, fallback to another
// installed model), blind A/B shuffle, vote recording + reveal, standings formatting and store
// round-trip. All ArenaStore-touching tests live in this single class: _fileOverride is a static
// global, and xUnit parallelises across classes, not within one.
public class ArenaTests : IDisposable
{
    private readonly string _tempFile;

    public ArenaTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"inferpal-arena-{Guid.NewGuid():N}.json");
        ArenaStore._fileOverride = _tempFile;
    }

    public void Dispose()
    {
        ArenaStore._fileOverride = null;
        try { File.Delete(_tempFile); } catch { }
    }

    private static InferpalConfig Config(string chat = "big", string utility = "small") =>
        new() { DefaultModel = chat, UtilityModel = utility };

    /// <summary>Two installed models; answers are numbered by call order and never contain the
    /// model name — so the blind-labelling assertions can't pass by accident.</summary>
    private static FakeInferenceProvider EchoProvider()
    {
        int calls = 0;
        return new()
        {
            Installed = [new InstalledModelInfo("big:latest", 1), new InstalledModelInfo("small:latest", 1)],
            OnChatRequest = (model, messages, tools, onToken) =>
                Task.FromResult(new ChatTurnResult($"answer-{Interlocked.Increment(ref calls)}", null, 10, 5)),
        };
    }

    // ── Battles ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Battle_AutoPair_UsesChatAndUtilityModels_AndStoresPendingMapping()
    {
        var fake = EchoProvider();
        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "why", "is", "the", "sky", "blue"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Equal(new List<string> { "big", "small" }, fake.ChatModels);
        Assert.Contains(Strings.ArenaVotePrompt, result.Message);
        Assert.DoesNotContain("big", result.Message);      // blind: identities never shown pre-vote
        Assert.Contains("> why is the sky blue", result.Message);

        var state = await ArenaStore.LoadAsync();
        Assert.NotNull(state.Pending);
        Assert.Equal("big",   state.Pending!.ModelA);
        Assert.Equal("small", state.Pending.ModelB);
        Assert.Equal("why is the sky blue", state.Pending.Prompt);
    }

    [Fact]
    public async Task Battle_SwapOrder_ReversesBlindMapping()
    {
        var fake = EchoProvider();
        await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => true);

        var state = await ArenaStore.LoadAsync();
        Assert.Equal("small", state.Pending!.ModelA);
        Assert.Equal("big",   state.Pending.ModelB);
    }

    [Fact]
    public async Task Battle_ExplicitPair_WinsOverConfig_TagTolerant()
    {
        var fake = EchoProvider();
        await ArenaCommandHandler.HandleAsync(
            fake, Config(chat: "other"), ["/arena", "small", "big", "some", "prompt"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        // Tokens matched the installed names (tag-tolerant), prompt = the remainder.
        Assert.Equal(new List<string> { "small:latest", "big:latest" }, fake.ChatModels);
        Assert.Equal("some prompt", (await ArenaStore.LoadAsync()).Pending!.Prompt);
    }

    [Fact]
    public async Task Battle_NoUtilityModel_FallsBackToAnotherInstalledModel()
    {
        var fake = EchoProvider();
        await ArenaCommandHandler.HandleAsync(
            fake, Config(chat: "big", utility: ""), ["/arena", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Equal(new List<string> { "big", "small:latest" }, fake.ChatModels);
    }

    [Fact]
    public async Task Battle_NoDistinctPair_ExplainsInsteadOfRunning()
    {
        var fake = new FakeInferenceProvider
        {
            Installed = [new InstalledModelInfo("big:latest", 1)],
        };
        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(chat: "big", utility: ""), ["/arena", "hello"],
            onProgress: null, CancellationToken.None);

        Assert.Equal(Strings.ArenaNeedTwoModels, result.Message);
        Assert.Empty(fake.ChatModels);
    }

    [Fact]
    public async Task Battle_ProviderFailure_ReportsError_NotThrow()
    {
        var fake = EchoProvider();
        fake.OnChatRequest = (_, _, _, _) => throw new InvalidOperationException("boom");

        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "hello"], onProgress: null, CancellationToken.None);

        Assert.Equal(Strings.ArenaFailed("boom"), result.Message);
        Assert.Null((await ArenaStore.LoadAsync()).Pending);
    }

    [Fact]
    public async Task Usage_ShownWithoutArguments()
    {
        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena"], onProgress: null, CancellationToken.None);
        Assert.Equal(Strings.ArenaUsage, result.Message);
    }

    // ── Votes ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vote_RecordsBattle_RevealsModels_AndClearsPending()
    {
        var fake = EchoProvider();
        await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "b"], onProgress: null, CancellationToken.None);

        Assert.Contains(Strings.ArenaReveal("big", "small"), result.Message);
        Assert.Contains(Strings.ArenaVoteRecordedWin("small"), result.Message);

        var state = await ArenaStore.LoadAsync();
        Assert.Null(state.Pending);
        var battle = Assert.Single(state.Battles);
        Assert.Equal(("big", "small", "b"), (battle.ModelA, battle.ModelB, battle.Vote));
    }

    [Fact]
    public async Task Vote_WithoutPendingBattle_Explains()
    {
        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "a"], onProgress: null, CancellationToken.None);
        Assert.Equal(Strings.ArenaNoPending, result.Message);
    }

    // ── Standings ──────────────────────────────────────────────────────────────

    [Fact]
    public void FormatStats_AggregatesWinsTiesAndRate_SortedByWins()
    {
        var now = DateTime.UtcNow;
        var stats = ArenaCommandHandler.FormatStats(
        [
            new ArenaBattle(now, "p", "big", "small", "a"),
            new ArenaBattle(now, "p", "big", "small", "a"),
            new ArenaBattle(now, "p", "small", "big", "b"),
            new ArenaBattle(now, "p", "big", "small", "tie"),
        ]);

        // big: 4 battles, 3 wins, 1 tie (75 %) — listed before small (4 battles, 0 wins).
        Assert.Contains("| `big` | 4 | 3 | 1 | 75 % |", stats);
        Assert.Contains("| `small` | 4 | 0 | 1 | 0 % |", stats);
        Assert.True(stats.IndexOf("`big`", StringComparison.Ordinal)
                  < stats.IndexOf("`small`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stats_WithoutBattles_Explains()
    {
        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "stats"], onProgress: null, CancellationToken.None);
        Assert.Equal(Strings.ArenaNoStats, result.Message);
    }

    // ── Store ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_RoundTrips_AndTreatsMissingFileAsEmpty()
    {
        var empty = await ArenaStore.LoadAsync();
        Assert.Empty(empty.Battles);
        Assert.Null(empty.Pending);

        var state = new ArenaSavedState(
            [new ArenaBattle(new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc), "p", "x", "y", "tie")],
            new ArenaPending(new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc), "q", "x", "y"));
        await ArenaStore.SaveAsync(state);

        var loaded = await ArenaStore.LoadAsync();
        Assert.Equal(state.Battles, loaded.Battles);
        Assert.Equal(state.Pending, loaded.Pending);
    }
}
