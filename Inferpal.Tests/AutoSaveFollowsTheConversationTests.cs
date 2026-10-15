using System.IO;
using Inferpal.Services.Persistence;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: a conversation is written for the workspace it belongs to, not for the solution open at the time of
/// writing.
/// </summary>
/// <remarks>
/// Visual Studio keeps the conversation on screen when another solution is opened. Its auto-save, its archive at
/// <c>/clear</c> and its branches were written for the root of the moment: a conversation begun in Shop went into
/// Billing's slot — Billing's own last conversation overwritten, never archived — and <c>/clear</c> emptied Billing's slot
/// while the conversation just discarded came back at Shop's next start.
/// </remarks>
public sealed class AutoSaveFollowsTheConversationTests : IDisposable
{
    private readonly string _dir   = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"autosave-conv-{Guid.NewGuid():N}");
    private readonly string _shop  = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Shop");
    private readonly string _bill  = Path.Combine(Path.GetTempPath(), "inferpal-tests", "Billing");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static List<SavedMessage> Turn(string question) =>
        [new("user", question), new("assistant", "answer to " + question)];

    private async Task<string?> FirstQuestion(ConversationStore store, string? root) =>
        (await store.LoadAutoSaveAsync(root, CancellationToken.None)) is { Messages.Count: > 0 } s ? s.Messages[0].Content : null;

    [Fact]
    public async Task AConversationRestoredInOneSolution_IsSavedThere_AfterAnotherSolutionIsOpened()
    {
        var store = new ConversationStore(_dir, ConversationStore.VisualStudioSlot);
        await store.AutoSaveAsync(Turn("Billing's last conversation"), CancellationToken.None, _bill);

        var root = new ConversationRoot();
        root.Restored(_shop);                                  // the window opened on Shop and restored its conversation
        // Billing is opened; the conversation stays on screen and the next turn ends.
        await store.AutoSaveAsync(Turn("still about Shop"), CancellationToken.None, root.For(_bill));

        Assert.Equal("still about Shop", await FirstQuestion(store, _shop));
        Assert.Equal("Billing's last conversation", await FirstQuestion(store, _bill));
    }

    [Fact]
    public void AConversationNeverSaved_BelongsWhereItIsFirstSaved_AndStaysThere()
    {
        var root = new ConversationRoot();
        Assert.False(root.IsBound);                            // in no slot: discarding it empties none

        Assert.Equal(_shop, root.For(_shop));
        Assert.True(root.IsBound);
        Assert.Equal(_shop, root.For(_bill));                  // a solution switch does not move it

        root.Discarded();                                      // /clear: the next conversation starts in Billing
        Assert.False(root.IsBound);
        Assert.Equal(_bill, root.For(_bill));
    }

    [Fact]
    public void AConversationRestoredWithoutAWorkspace_StaysRootless_WhenASolutionIsOpened()
    {
        var root = new ConversationRoot();
        root.Restored(null);                                   // a window without a solution restored its own slot

        Assert.True(root.IsBound);
        Assert.Null(root.For(_bill));                          // Billing's slot is not this conversation's
    }

    /// <summary>Every write of the conversation in the adapter goes through its root — a call that passes the root of
    /// the moment is the defect.</summary>
    [Fact]
    public void EveryConversationWriteOfTheVisualStudioAdapter_GoesThroughTheConversationsRoot()
    {
        string[] writes = ["SaveAsync", "AutoSaveAsync", "ForgetAutoSaveAsync", "ForgetAutoSaveOrSayAsync"];
        var calls = ConventionCoverageTests.ViewModelSources()
            .SelectMany(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f).GetRoot()
                                             .DescendantNodes().OfType<InvocationExpressionSyntax>()
                                             .Select(i => (File: Path.GetFileName(f), Call: i)))
            .Where(c => c.Call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "_store" } } m
                     && writes.Contains(m.Name.Identifier.Text))
            .ToList();

        // Witness: the archive of /clear, the auto-save, the forget of /clear and the two files of /branch.
        Assert.True(calls.Count >= 5, $"only {calls.Count} session writes found in the adapter: the scan reads nothing any more.");
        var offenders = calls.Where(c => c.Call.ArgumentList.Arguments.Any(a => a.ToString().Contains("RootDir", StringComparison.Ordinal)))
                             .Select(c => $"{c.File}: {c.Call}")
                             .ToList();
        Assert.True(offenders.Count == 0,
                    "a session is written for the solution open now, not the conversation's (ConversationRoot):\n"
                    + string.Join("\n", offenders));

        // …and the conversation the window loads is bound to the workspace it was loaded in.
        var load = ConventionCoverageTests.CodeOnly(
            ConventionCoverageTests.ViewModelSources().Single(f => f.EndsWith("InferpalToolWindowData.Connection.cs", StringComparison.Ordinal)));
        Assert.Contains("_conversationRoot.Restored(here)", load, StringComparison.Ordinal);
        Assert.Contains("_conversationRoot.Discarded()", load, StringComparison.Ordinal);
    }
}
