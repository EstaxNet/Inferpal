using System.IO;
using System.Linq;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A saved conversation that cannot be read must be <b>named</b>, never dropped from the answer.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>ConversationStore.ListWithPreviewAsync</c> and <c>SearchAsync</c> both wrapped their
/// per-session read in <c>catch { Diagnostics.Swallow(…) }</c> and moved on. The ring buffer is not
/// where the user is looking: <c>/history</c> then answers <c>Strings.HistoryNoSessions</c> — "no
/// saved sessions" — to someone who has ten, or shows five of eight with nothing saying so, and
/// <c>/history &lt;term&gt;</c> answers <c>HistoryNoResults</c> for a word sitting in the one file it
/// could not open.
/// </para>
/// <para>
/// ⚠ <b>The repository already holds the rule, one folder away.</b>
/// <c>ChecksService.Load(dir, out var unreadable)</c> exists so <c>/check</c> can say it:
/// <i>"Checks that cannot be read are named on every answer that depends on them: dropped silently,
/// the diff would be reviewed against fewer checks than the user wrote."</i> The same sentence is
/// true of a conversation, which is worth more and cannot be rewritten by hand.
/// </para>
/// <para>
/// Two shapes, both real: a file that will not parse (an interrupted write on a full disk, a sync
/// client that merged two versions) and one that will not open (locked by the other front-end, by a
/// backup agent, by an antivirus). Both are exercised here — the second only where the platform
/// enforces it, which its own witness checks.
/// </para>
/// </remarks>
public sealed class SessionListingSilenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"sessions-{Guid.NewGuid():N}");

    /// <summary>
    /// ⚠ A folder of this instance's own, passed to the store — never
    /// <c>ConversationStore.OverrideDirForTests</c>. That static belongs to the whole suite, and
    /// repointing it from here made four unrelated classes read the wrong folder while this one ran.
    /// </summary>
    private ConversationStore Store => new(_dir);

    public SessionListingSilenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task WriteSessionAsync(string name, params string[] userMessages)
    {
        var store = Store;
        await store.SaveAsync(
            name,
            userMessages.Select(m => new SavedMessage("user", m, null, DateTime.UtcNow.ToString("s"))),
            CancellationToken.None);
    }

    /// <summary>A session file that exists and will not parse.</summary>
    private void WriteCorruptSession(string name) =>
        File.WriteAllText(Path.Combine(_dir, $"{name}.json"), "{ \"SavedAt\": ");

    private Task<string> HistoryAsync(params string[] parts) =>
        HistoryCommandHandler.HandleAsync(new ConversationStore(_dir), parts, DateTime.UtcNow, CancellationToken.None);

    // ── /history (the listing) ────────────────────────────────────────────────

    [Fact]
    public async Task History_WithAnUnreadableSession_NamesIt_InsteadOfShorteningTheListInSilence()
    {
        await WriteSessionAsync("alpha", "hello from alpha");
        WriteCorruptSession("beta");

        var answer = await HistoryAsync("/history");

        Assert.Contains("alpha", answer, StringComparison.Ordinal);    // witness: the listing really ran
        Assert.Contains("beta", answer, StringComparison.Ordinal);
        Assert.Contains(Strings.SessionsUnreadableListed(1, "beta"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_WhenEverySessionIsUnreadable_DoesNotAnswer_NoSavedSessions()
    {
        // ⚠ The worst shape, and the one a ring-buffer trace cannot soften: the user is told their
        // conversations do not exist.
        WriteCorruptSession("alpha");
        WriteCorruptSession("beta");

        var answer = await HistoryAsync("/history");

        // Listing order, which ConversationStore sorts by name descending (session names start
        // with a date, so that is "most recent first").
        Assert.Contains(Strings.SessionsUnreadableListed(2, "beta, alpha"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_WithEverythingReadable_SaysNothingAboutUnreadableSessions()
    {
        // NEGATIVE WITNESS: without it, a build that always appended the notice would pass the two
        // tests above while measuring nothing.
        await WriteSessionAsync("alpha", "hello from alpha");

        var answer = await HistoryAsync("/history");

        Assert.Contains("alpha", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(NoticePrefix(Strings.SessionsUnreadableListed(1, Sentinel)), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_WithNoSessionsAtAll_StillSaysThereAreNone()
    {
        // REFERENCE ARM: an empty folder is a genuine "no saved sessions", and it must stay that.
        var answer = await HistoryAsync("/history");

        Assert.Equal(Strings.HistoryNoSessions, answer);
    }

    // ── /history <term> (the search) ──────────────────────────────────────────

    [Fact]
    public async Task HistorySearch_WithAnUnreadableSession_SaysSo_InsteadOfAnswering_NoResults()
    {
        WriteCorruptSession("beta");

        var answer = await HistoryAsync("/history", "needle");

        Assert.Contains(Strings.SessionsUnreadableSearched(1, "beta"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistorySearch_WithAMatchAndAnUnreadableSession_SaysBoth()
    {
        // The half that is easy to forget: a search that DID find something is just as partial.
        await WriteSessionAsync("alpha", "the needle is here");
        WriteCorruptSession("beta");

        var answer = await HistoryAsync("/history", "needle");

        Assert.Contains("alpha", answer, StringComparison.Ordinal);
        Assert.Contains(Strings.SessionsUnreadableSearched(1, "beta"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistorySearch_WithEverythingReadable_SaysNothingAboutUnreadableSessions()
    {
        await WriteSessionAsync("alpha", "the needle is here");

        var answer = await HistoryAsync("/history", "haystack");

        Assert.Equal(Strings.HistoryNoResults("haystack"), answer);
    }

    // ── The other shape: a file that will not OPEN ────────────────────────────

    [Fact]
    public async Task History_WithASessionLockedByAnotherProcess_NamesItToo()
    {
        await WriteSessionAsync("alpha", "hello from alpha");
        await WriteSessionAsync("locked", "hello from locked");

        // FileShare.None is this repository's own witness for "unreadable": .NET turns it into an
        // flock on Unix, so one gesture covers the four CI legs.
        using (var hold = new FileStream(Path.Combine(_dir, "locked.json"),
                                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Witness: the lock really discriminates. Without it this test is green on a platform
            // that does not enforce it, and measures nothing.
            Assert.ThrowsAny<IOException>(() =>
                new FileStream(Path.Combine(_dir, "locked.json"), FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete).Dispose());

            var answer = await HistoryAsync("/history");

            Assert.Contains("alpha", answer, StringComparison.Ordinal);
            Assert.Contains(Strings.SessionsUnreadableListed(1, "locked"), answer, StringComparison.Ordinal);
        }
    }

    // ── The expensive half: a name believed free ──────────────────────────────

    private static List<SavedMessage> TwoTurns() =>
    [
        new("user", "first question", null, "2026-09-18T10:00:00"),
        new("assistant", "first answer", null, "2026-09-18T10:00:01"),
        new("user", "second question", null, "2026-09-18T10:01:00"),
        new("assistant", "second answer", null, "2026-09-18T10:01:01"),
    ];

    [Fact]
    public async Task Branch_NeverTakesTheNameOfASessionItCouldNotRead()
    {
        // ⚠ The silence is not only cosmetic here. `/branch` picks the first FREE `<base>__bN`, and
        // "free" was decided from the list the failed reads had already been dropped from — so the
        // new branch takes the unreadable session's name and the next save overwrites it. The very
        // cycle `AppDataJsonFile.preserveUnreadable` was written for: unreadable ⇒ absent ⇒
        // overwritten.
        await WriteSessionAsync("alpha", "hello from alpha");
        WriteCorruptSession("alpha__b2");

        var scan = await Store.ListWithPreviewAsync(CancellationToken.None);
        var plan = BranchManager.Plan(TwoTurns(), 1, "alpha", scan, DateTime.Now);

        Assert.NotNull(plan);
        Assert.NotEqual("alpha__b2", plan!.BranchName);
    }

    [Fact]
    public async Task Branch_StillTakesTheFirstFreeName_WhenNothingIsUnreadable()
    {
        // REFERENCE ARM: without it, a fix that simply skipped `__b2` forever would pass the test
        // above while breaking the naming everyone else gets.
        await WriteSessionAsync("alpha", "hello from alpha");

        var scan = await Store.ListWithPreviewAsync(CancellationToken.None);
        var plan = BranchManager.Plan(TwoTurns(), 1, "alpha", scan, DateTime.Now);

        Assert.NotNull(plan);
        Assert.Equal("alpha__b2", plan!.BranchName);
    }

    /// <summary>The localized notice up to where the session names start.</summary>
    private static string NoticePrefix(string formatted) => formatted.Split(Sentinel, StringSplitOptions.None)[0];

    /// <summary>A placeholder no localized sentence can contain, so the template can be cut
    /// where its arguments start. Written as an escape, never as a raw byte in the source.</summary>
    private const string Sentinel = "\u0001";
}
