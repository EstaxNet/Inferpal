using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/history &lt;term&gt;</c> shows three snippets per session and says how many more messages of that session match.
/// </summary>
/// <remarks>
/// The three snippets came with nothing after them: a session where the word appears forty times read as one where it
/// appears three times — the user looking for "the conversation where we talked a lot about X" picked the wrong one.
/// </remarks>
public sealed class HistorySearchMoreMatchesTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-history-more-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ASessionWithMoreMatches_SaysHowManyMore()
    {
        var store = new ConversationStore(_dir);
        var messages = Enumerable.Range(1, 7).Select(i => new SavedMessage("user", $"question {i} about the pricing rule")).ToList();
        await store.SaveAsync("pricing-work", messages, CancellationToken.None);

        var answer = await HistoryCommandHandler.HandleAsync(store, ["/history", "pricing"], Now, CancellationToken.None);

        Assert.Contains("question 3 about the pricing rule", answer);   // witness: the snippets are there
        Assert.Contains($"*{Strings.HistorySearchMoreMatches(4)}*", answer);
    }

    [Fact]
    public async Task ASessionWithThreeMatchesOrFewer_SaysNothingMore()
    {
        // Reference arm: every match is shown, nothing to count.
        var store = new ConversationStore(_dir);
        await store.SaveAsync("short", [new SavedMessage("user", "the pricing rule"), new SavedMessage("assistant", "pricing is fine")],
                              CancellationToken.None);

        var answer = await HistoryCommandHandler.HandleAsync(store, ["/history", "pricing"], Now, CancellationToken.None);

        Assert.Contains("pricing is fine", answer);
        Assert.DoesNotContain(Strings.HistorySearchMoreMatches(0).Replace("0", ""), answer);
    }
}
