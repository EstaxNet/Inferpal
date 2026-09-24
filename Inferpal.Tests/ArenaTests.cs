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

// /arena: pair resolution (auto chat-vs-utility, explicit, fallback to another
// installed model), blind A/B shuffle, vote recording + reveal, standings formatting and store
// round-trip. All ArenaStore-touching tests live in this single class: _fileOverride is a static
// global, and xUnit parallelises across classes, not within one.
public class ArenaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tempFile;

    // A folder of its own, not a loose file in %TEMP%: an unreadable arena.json is COPIED aside
    // before it is overwritten, and that copy is a second file this class has to be able to find
    // (and to take away with it).
    public ArenaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"arena-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _tempFile = Path.Combine(_dir, "arena.json");
        ArenaStore._fileOverride = _tempFile;
    }

    public void Dispose()
    {
        ArenaStore._fileOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
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

    /// <summary>
    /// A server that does not separate reasoning sends it inline, at the head of the content. An answer that never
    /// left its reasoning carried an unclosed tag into the duel, and the chat's reasoning strip — which reads an
    /// unclosed tail as reasoning — hid the second answer with it: a vote between one answer and nothing.
    /// </summary>
    [Fact]
    public async Task ADuel_ShowsBothAnswers_EvenWhenOneNeverLeftItsReasoning()
    {
        int calls = 0;
        var fake = new FakeInferenceProvider
        {
            Installed = [new InstalledModelInfo("big:latest", 1), new InstalledModelInfo("small:latest", 1)],
            OnChatRequest = (_, _, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new ChatTurnResult("<think>\nweighing the options", null, 0, 0)
                : new ChatTurnResult("second answer", null, 0, 0)),
        };

        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "say", "hi"], onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Contains("second answer", Inferpal.Services.Presentation.MarkdownParser.ShownText("assistant", result.Message));
        Assert.DoesNotContain("<think>", result.Message);
    }

    /// <summary>
    /// A vote is a verdict: an answer that stopped at the length limit reads as a curt one, and the voter compared a
    /// fragment with a whole without knowing it. The mark goes under that answer — and only that one.
    /// </summary>
    [Fact]
    public async Task ADuel_MarksTheAnswerThatStoppedAtTheLengthLimit()
    {
        int calls = 0;
        var fake = new FakeInferenceProvider
        {
            Installed = [new InstalledModelInfo("big:latest", 1), new InstalledModelInfo("small:latest", 1)],
            OnChatRequest = (_, _, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new ChatTurnResult("The sky is blue because of Rayl", null, 0, 0, CutAtLimit: true)
                : new ChatTurnResult("Rayleigh scattering.", null, 0, 0)),
        };

        var message = (await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "why", "blue"], onProgress: null, CancellationToken.None, swapOrder: () => false)).Message;

        var mark = message.IndexOf(Strings.ArenaAnswerCut, StringComparison.Ordinal);
        Assert.True(mark > message.IndexOf("because of Rayl", StringComparison.Ordinal), "the cut answer is not marked");
        Assert.True(mark < message.IndexOf("Rayleigh scattering.", StringComparison.Ordinal), "the mark is not under the cut answer");
        Assert.Equal(mark, message.LastIndexOf(Strings.ArenaAnswerCut, StringComparison.Ordinal));   // the whole answer carries none
    }

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

    /// <summary>
    /// Two sizes of one family are two models: <c>/arena qwen3:8b qwen3:32b …</c> resolved both names to
    /// one installed model, and the whole line — model names included — became the prompt of a
    /// chat-vs-utility battle.
    /// </summary>
    [Fact]
    public async Task Battle_ExplicitPair_OfTwoSizesOfOneFamily_ComparesThem()
    {
        var fake = new FakeInferenceProvider
        {
            Installed     = [new InstalledModelInfo("qwen3:32b", 1), new InstalledModelInfo("qwen3:8b", 1)],
            OnChatRequest = (model, messages, tools, onToken) =>
                Task.FromResult(new ChatTurnResult("answer", null, 10, 5)),
        };

        await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "qwen3:8b", "qwen3:32b", "some", "prompt"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Equal(new List<string> { "qwen3:8b", "qwen3:32b" }, fake.ChatModels);
        Assert.Equal("some prompt", (await ArenaStore.LoadAsync()).Pending!.Prompt);
    }

    /// <summary>
    /// One model is one row: the automatic pair records the configured name (<c>big</c>), the explicit form
    /// the installed one (<c>big:latest</c>), and the standings split the same model across two rows —
    /// halving its record and ranking it against itself.
    /// </summary>
    [Fact]
    public void Stats_CountTheImplicitLatestTagAsTheSameModel()
    {
        var battles = new List<ArenaBattle>
        {
            new(DateTime.UtcNow, "p1", "big",        "small", "a"),
            new(DateTime.UtcNow, "p2", "big:latest", "small", "a"),
        };

        var table = ArenaCommandHandler.FormatStats(battles);

        var bigRows = 0;
        foreach (var line in table.Split('\n'))
            if (line.StartsWith("| `big", StringComparison.Ordinal)) bigRows++;
        Assert.Equal(1, bigRows);
        Assert.Contains("| 2 | 2 | 0 | 100 % |", table);
    }

    /// <summary>
    /// Two installed model names and nothing else is the explicit form with its prompt missing, not a
    /// prompt: <c>/arena small big</c> sent the text "small big" to the chat and utility models as a battle.
    /// </summary>
    [Fact]
    public async Task Battle_ExplicitPairWithoutPrompt_ShowsTheUsage()
    {
        var fake = EchoProvider();

        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(chat: "other"), ["/arena", "small", "big"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Equal(Strings.ArenaUsage, result.Message);
        Assert.Empty(fake.ChatModels);
    }

    /// <summary>
    /// One model named twice is not a pair: <c>/arena big big:latest hello</c> fell back to the automatic
    /// battle, with both model names pasted into the prompt.
    /// </summary>
    [Fact]
    public async Task Battle_ExplicitPairOfOneModel_SaysTwoModelsAreNeeded()
    {
        var fake = EchoProvider();

        var result = await ArenaCommandHandler.HandleAsync(
            fake, Config(), ["/arena", "big", "big:latest", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Equal(Strings.ArenaNeedTwoModels, result.Message);
        Assert.Empty(fake.ChatModels);
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

    // The same failure as snippets: the vote answered "vote recorded" without writing anything.
    [Fact]
    public async Task Vote_WhenTheStateCannotBeWritten_SaysSo_AndKeepsTheBattlePending()
    {
        var dir  = Directory.CreateTempSubdirectory("arena-locked-").FullName;
        var path = Path.Combine(dir, "arena.json");
        ArenaStore._fileOverride = path;
        try
        {
            var fake = EchoProvider();
            await ArenaCommandHandler.HandleAsync(
                fake, Config(), ["/arena", "hello"], onProgress: null, CancellationToken.None, swapOrder: () => false);

            string message;
            if (OperatingSystem.IsWindows())
            {
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                message = (await ArenaCommandHandler.HandleAsync(
                    fake, Config(), ["/arena", "b"], onProgress: null, CancellationToken.None)).Message;
            }
            else
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    message = (await ArenaCommandHandler.HandleAsync(
                        fake, Config(), ["/arena", "b"], onProgress: null, CancellationToken.None)).Message;
                }
                finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }

            Assert.Equal(Strings.ArenaVoteNotSaved, message);
            Assert.NotNull((await ArenaStore.LoadAsync()).Pending);

            // Witness: file released, the same vote is recorded.
            Assert.Contains(Strings.ArenaVoteRecordedWin("small"), (await ArenaCommandHandler.HandleAsync(
                fake, Config(), ["/arena", "b"], onProgress: null, CancellationToken.None)).Message);
        }
        finally
        {
            ArenaStore._fileOverride = _tempFile;
            Directory.Delete(dir, recursive: true);
        }
    }

    // A battle whose pending state could not be written made the next vote answer "no pending
    // battle": say so under the battle itself.
    [Fact]
    public async Task ABattleWhosePendingStateCannotBeWritten_WarnsThatAVoteWillNotCount()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"arena_blocker_{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        ArenaStore._fileOverride = Path.Combine(blocker, "arena.json");
        try
        {
            var result = await ArenaCommandHandler.HandleAsync(
                EchoProvider(), Config(), ["/arena", "hello"], onProgress: null, CancellationToken.None, swapOrder: () => false);
            Assert.Contains(Strings.ArenaPendingNotSaved, result.Message);
        }
        finally
        {
            ArenaStore._fileOverride = _tempFile;
            File.Delete(blocker);
        }
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

    // ── Votes: what cannot be recomputed is kept ───────────────────────────────

    /// <summary>A document that parses and is refused by the shape check — the truncated-write shape.</summary>
    private const string NullBattles = """{ "Battles": null, "Pending": null }""";

    private static string? AsideOf(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".unreadable-*")
                 .FirstOrDefault();

    /// <summary>
    /// A vote is not recomputable state: an arena file that will not load is set aside, not replaced.
    /// </summary>
    /// <remarks>
    /// ⚠ The flag alone is not the fix. The shape check ("a hand-edited or truncated file can
    /// deserialise into a state whose list is null") lived on <c>LoadAsync</c> only, so the two
    /// halves disagreed on what "readable" means: such a document is refused when loading and
    /// counted as readable when saving, hence never set aside. One predicate, given at
    /// construction, is read by both.
    /// </remarks>
    [Fact]
    public async Task AnArenaFileRefusedByItsShapeCheck_IsSetAside_BeforeTheNextSaveOverwritesIt()
    {
        File.WriteAllText(_tempFile, NullBattles);

        // WITNESS: the load really refuses it — otherwise this measures a file that was fine.
        Assert.Empty((await ArenaStore.LoadAsync()).Battles);

        Assert.True(await ArenaStore.SaveAsync(new ArenaSavedState(
            [new ArenaBattle(DateTime.UtcNow, "p", "a", "b", "a")], null)));

        var aside = AsideOf(_tempFile);
        Assert.NotNull(aside);
        Assert.Equal(NullBattles, File.ReadAllText(aside!));
        Assert.Single((await ArenaStore.LoadAsync()).Battles);
        try { File.Delete(aside!); } catch { }
    }

    /// <summary>The other half: setting a readable file aside on every save means nothing at all.</summary>
    [Fact]
    public async Task AReadableArenaFile_IsNotSetAside()
    {
        await ArenaStore.SaveAsync(new ArenaSavedState([], null));
        await ArenaStore.SaveAsync(new ArenaSavedState(
            [new ArenaBattle(DateTime.UtcNow, "p", "a", "b", "tie")], null));

        Assert.Null(AsideOf(_tempFile));
    }

    // ── The bytes are kept, and the user is told ───────────────────────────────

    /// <summary>
    /// Keeping the bytes was half the repair. The other half is that every <c>/arena</c> answer
    /// built from the fallback is a sentence about the USER — "no vote recorded yet", "no battle
    /// awaiting a vote" — made from a fact about one file, and the copy sitting next to it has a
    /// name nobody would ever go looking for.
    /// </summary>
    private async Task TearTheVoteLogAsync()
    {
        await ArenaStore.SaveAsync(new ArenaSavedState(
            [new ArenaBattle(DateTime.UtcNow, "MARKER-PROMPT", "big", "small", "a")],
            new ArenaPending(DateTime.UtcNow, "a pending prompt", "big", "small")));

        var whole = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("MARKER-PROMPT", whole);            // WITNESS: it really was written
        await File.WriteAllTextAsync(_tempFile, whole[..(whole.Length / 2)]);
        Assert.True((await ArenaStore.ReadAsync()).Unreadable);  // WITNESS: and it really is torn
    }

    [Fact]
    public async Task Stats_WhenTheFileDidNotOpen_NamesIt_InsteadOfSayingNoVotes()
    {
        await TearTheVoteLogAsync();

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "stats"], onProgress: null, CancellationToken.None);

        Assert.Equal(Strings.ArenaUnreadable(ArenaStore.FilePath), result.Message);
        Assert.DoesNotContain(Strings.ArenaNoStats, result.Message);
    }

    /// <summary>Reference arm: a user who really has never voted still gets the ordinary sentence,
    /// or the notice is noise on every first run.</summary>
    [Fact]
    public async Task Stats_WithNoFileAtAll_StillSaysNoVotesYet()
    {
        Assert.False(File.Exists(_tempFile));

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "stats"], onProgress: null, CancellationToken.None);

        Assert.Equal(Strings.ArenaNoStats, result.Message);
    }

    [Fact]
    public async Task Vote_WhenTheFileDidNotOpen_SaysTheVoteWasNotRecorded_AndWhy()
    {
        await TearTheVoteLogAsync();

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "a"], onProgress: null, CancellationToken.None);

        Assert.Contains(Strings.ArenaVoteNotRead, result.Message);
        Assert.Contains(ArenaStore.FilePath, result.Message);
        // "No battle awaiting a vote" describes the user's state, not the file's.
        Assert.DoesNotContain(Strings.ArenaNoPending, result.Message);
    }

    /// <summary>Reference arm: with a readable file and nothing pending, the ordinary sentence.</summary>
    [Fact]
    public async Task Vote_WithAReadableFileAndNothingPending_StillSaysNoBattle()
    {
        await ArenaStore.SaveAsync(new ArenaSavedState([], null));

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "tie"], onProgress: null, CancellationToken.None);

        Assert.Equal(Strings.ArenaNoPending, result.Message);
    }

    /// <summary>
    /// The battle that follows is the write that replaces the log, so it is the last moment anyone
    /// can be told. From the next standings on, the answer is "1 battle" and nothing says why.
    /// </summary>
    [Fact]
    public async Task ANewBattle_OverAnUnreadableLog_SaysTheOldVotesWereSetAside()
    {
        await TearTheVoteLogAsync();

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Contains(Strings.ArenaVotePrompt, result.Message);   // the battle itself still ran
        Assert.Contains(Strings.ArenaUnreadable(ArenaStore.FilePath), result.Message);
        Assert.NotNull(AsideOf(_tempFile));                         // and the sentence is true
    }

    /// <summary>Reference arm: the ordinary battle says none of it.</summary>
    [Fact]
    public async Task ANewBattle_OverAReadableLog_SaysNothingAboutTheFile()
    {
        await ArenaStore.SaveAsync(new ArenaSavedState([], null));

        var result = await ArenaCommandHandler.HandleAsync(
            EchoProvider(), Config(), ["/arena", "hello"],
            onProgress: null, CancellationToken.None, swapOrder: () => false);

        Assert.Contains(Strings.ArenaVotePrompt, result.Message);
        Assert.DoesNotContain(ArenaStore.FilePath, result.Message);
    }
}
