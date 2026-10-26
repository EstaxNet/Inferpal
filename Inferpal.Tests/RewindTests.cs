using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Persistence;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Going back to a question with its files: the runs that followed it are undone, newest first — all of them or none —
/// and only the runs of this conversation.
/// </summary>
/// <remarks>⚠ <c>/branch</c> went back in the conversation only: the files the following runs changed stayed changed, and
/// a saved conversation did not even know which runs followed a question (an hour shown without a date, and the
/// workspace's runs include the other editor's).</remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class RewindTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-rewind").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-rewind-key").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static string Text(string path) => File.ReadAllText(path);

    /// <summary>A closed run that writes each (file, content) pair.</summary>
    private static async Task<string> RunAsync(FileHistoryService history, params (string Path, string Content)[] writes)
    {
        history.BeginRun();
        foreach (var (path, content) in writes)
        {
            Assert.True((await history.BackUpBeforeChangeAsync(path, CancellationToken.None)).Item1);
            await File.WriteAllTextAsync(path, content);
        }
        history.EndRun();
        return history.Runs.OrderByDescending(r => r.Id, StringComparer.Ordinal).First().Id;
    }

    /// <summary>Three questions; the runs of the 2nd and 3rd edit A.cs one after the other, the 3rd creates New.cs.</summary>
    private async Task<(FileHistoryService History, List<SavedMessage> Messages)> AConversationAsync()
    {
        File.WriteAllText(PathOf("A.cs"), "v1");
        var history = Service();
        var r1 = await RunAsync(history, (PathOf("B.cs"), "b by run 1"));
        var r2 = await RunAsync(history, (PathOf("A.cs"), "v2"));
        var r3 = await RunAsync(history, (PathOf("A.cs"), "v3"), (PathOf("New.cs"), "created by run 3"));
        return (history, [
            new("user", "Add B"), new("assistant", "Added.", Run: r1),
            new("user", "Change A"), new("assistant", "Changed.", Run: r2),
            new("user", "Change A again"), new("assistant", "Done.", Run: r3),
        ]);
    }

    [Fact]
    public async Task GoingBack_UndoesTheRunsThatFollowedTheQuestion_NewestFirst_ByteForByte()
    {
        var (history, messages) = await AConversationAsync();

        var outcome = await Rewind.ApplyAsync(history, messages, 2, null);

        Assert.Empty(outcome!.Failed);
        Assert.Equal("v1", Text(PathOf("A.cs")));                    // through v3 → v2 → v1
        Assert.False(File.Exists(PathOf("New.cs")));
        Assert.Equal("b by run 1", Text(PathOf("B.cs")));            // the run before the question stays
        Assert.Equal((1, 1), (outcome.PutBack, outcome.Deleted));   // A.cs once, though two runs gave it back
    }

    [Fact]
    public async Task ThePlan_NamesThisConversationsRuns_NotTheOtherEditors()
    {
        var (history, messages) = await AConversationAsync();
        var other = Service();
        await RunAsync(other, (PathOf("Other.cs"), "the other editor's"));

        var plan = Rewind.Plan(history, messages, 2)!;

        Assert.Equal(RewindRefusal.None, plan.Refusal);
        Assert.Equal(2, plan.Runs.Count);
        Assert.DoesNotContain(plan.Files, f => f.Path == PathOf("Other.cs"));
        Assert.Contains(plan.Files, f => f.Path == PathOf("New.cs") && f.Kind == RewindFileKind.Deleted);
        Assert.Contains(plan.Files, f => f.Path == PathOf("A.cs") && f.Kind == RewindFileKind.PutBack);
        // The run it leaves is said: "the runs that followed it are undone" read as all of them.
        Assert.Equal(1, plan.OtherRuns);
        Assert.Contains(Strings.RewindOtherRunsLeft(1), RewindCard.Build(plan).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileChangedAfterTheRuns_RefusesEverything_AndNoByteIsTouched()
    {
        var (history, messages) = await AConversationAsync();
        await File.WriteAllTextAsync(PathOf("A.cs"), "edited by hand");

        var outcome = await Rewind.ApplyAsync(history, messages, 2, null);

        Assert.Equal(RewindRefusal.ChangedSince, outcome!.Plan.Refusal);
        Assert.Equal(PathOf("A.cs"), outcome.Plan.RefusedPath);
        Assert.Equal("edited by hand", Text(PathOf("A.cs")));
        Assert.True(File.Exists(PathOf("New.cs")));                  // nothing undone, not even the file that could be
    }

    /// <summary>A run of another conversation BETWEEN two of the series: the newest one's end still matches, but undoing
    /// the older one would overwrite what that run wrote — refused before a byte moves, never stopped half-way.</summary>
    [Fact]
    public async Task ARunOfAnotherConversationInBetween_RefusesEverything()
    {
        File.WriteAllText(PathOf("A.cs"), "v1");
        var history = Service();
        var mine = await RunAsync(history, (PathOf("A.cs"), "v2"));
        await RunAsync(Service(), (PathOf("A.cs"), "by the other editor"));
        var later = await RunAsync(history, (PathOf("A.cs"), "v3"));
        List<SavedMessage> messages =
        [
            new("user", "Change A"), new("assistant", "Changed.", Run: mine),
            new("user", "Change A again"), new("assistant", "Done.", Run: later),
        ];

        var outcome = await Rewind.ApplyAsync(history, messages, 1, null);

        Assert.Equal(RewindRefusal.ChangedBetween, outcome!.Plan.Refusal);
        Assert.Equal("v3", Text(PathOf("A.cs")));                    // nothing undone, not even the newest run
    }

    [Fact]
    public async Task ARunNoLongerKept_RefusesEverything()
    {
        var (history, messages) = await AConversationAsync();
        messages.Add(new SavedMessage("assistant", "A run long gone.", Run: "19990101000000000_gone-0001"));

        var plan = Rewind.Plan(history, messages, 2)!;

        Assert.Equal(RewindRefusal.NotKept, plan.Refusal);
    }

    [Fact]
    public async Task AQuestionWithNoRunAfterIt_TouchesNoFile()
    {
        var (history, messages) = await AConversationAsync();
        messages.Add(new SavedMessage("user", "Explain it"));
        messages.Add(new SavedMessage("assistant", "It does this."));

        var plan = Rewind.Plan(history, messages, 4)!;

        Assert.Empty(plan.Runs);
        Assert.Equal(Strings.RewindLeadNoRun, RewindCard.Build(plan).Body);
    }

    /// <summary>A file kept from a run's review stays as it is — even where an older run of the series changed it too.</summary>
    [Fact]
    public async Task AFileKeptFromAReview_IsLeftAsItIs_AndTheRestGoesBack()
    {
        var (history, messages) = await AConversationAsync();
        var newest = history.Runs.OrderByDescending(r => r.Id, StringComparer.Ordinal).First();
        history.KeepFile(newest, PathOf("A.cs"));

        var outcome = await Rewind.ApplyAsync(history, messages, 2, null);

        Assert.Empty(outcome!.Failed);
        Assert.Contains(outcome.Plan.Files, f => f.Path == PathOf("A.cs") && f.Kind == RewindFileKind.Kept);
        Assert.Equal("v3", Text(PathOf("A.cs")));
        Assert.False(File.Exists(PathOf("New.cs")));                  // WITNESS: the rest did go back
    }

    [Fact]
    public void GoingBackToTheFirstQuestion_KeepsWhatCameBeforeIt()
    {
        List<SavedMessage> messages = [new("assistant", "Welcome."), new("user", "First"), new("assistant", "Answer")];

        var before = BranchManager.TruncateAtTurn(messages, 0)!;

        Assert.Equal("Welcome.", Assert.Single(before).Content);
    }

    /// <summary>A hand edit between two runs of the series: undoing the newer one would give it back, the older one would
    /// then refuse the file — a half-undone state. Refused before a byte moves.</summary>
    [Fact]
    public async Task AFileEditedByHandBetweenTwoOfTheRuns_RefusesEverything()
    {
        File.WriteAllText(PathOf("A.cs"), "v1");
        var history = Service();
        var first = await RunAsync(history, (PathOf("A.cs"), "v2"), (PathOf("B.cs"), "b by the first run"));
        await File.WriteAllTextAsync(PathOf("A.cs"), "v2 edited by hand");
        var second = await RunAsync(history, (PathOf("A.cs"), "v3"));
        List<SavedMessage> messages =
        [
            new("user", "Change A"), new("assistant", "Changed.", Run: first),
            new("user", "Again"), new("assistant", "Done.", Run: second),
        ];

        var outcome = await Rewind.ApplyAsync(history, messages, 1, null);

        Assert.Equal(RewindRefusal.ChangedBetween, outcome!.Plan.Refusal);
        Assert.Equal("v3", Text(PathOf("A.cs")));
        Assert.Equal("b by the first run", Text(PathOf("B.cs")));
    }

    /// <summary>A run of another conversation that changed a file only a LATER run of the series touches: undoing that
    /// later run puts back what the other run wrote, nothing is lost — not refused.</summary>
    [Fact]
    public async Task ARunOfAnotherConversation_OnAFileTheFirstRunNeverTouched_DoesNotRefuse()
    {
        File.WriteAllText(PathOf("X.cs"), "x0");
        var history = Service();
        var first = await RunAsync(history, (PathOf("Y.cs"), "y by the first run"));
        await RunAsync(Service(), (PathOf("X.cs"), "x by the other editor"));
        var later = await RunAsync(history, (PathOf("X.cs"), "x by the later run"));
        List<SavedMessage> messages =
        [
            new("user", "Make Y"), new("assistant", "Made.", Run: first),
            new("user", "Change X"), new("assistant", "Changed.", Run: later),
        ];

        var outcome = await Rewind.ApplyAsync(history, messages, 1, null);

        Assert.Equal(RewindRefusal.None, outcome!.Plan.Refusal);
        Assert.Empty(outcome.Failed);
        Assert.Equal("x by the other editor", Text(PathOf("X.cs")));
        Assert.False(File.Exists(PathOf("Y.cs")));
    }

    /// <summary>Kept in an OLDER run's review: left as it is, and said so — never listed as deleted while it stays.</summary>
    [Fact]
    public async Task AFileKeptInAnOlderRunsReview_IsListedKept_AndStays()
    {
        var (history, messages) = await AConversationAsync();
        var middle = history.Runs.OrderBy(r => r.Id, StringComparer.Ordinal).ElementAt(1);   // the run that made A.cs v2
        history.KeepFile(middle, PathOf("A.cs"));

        var outcome = await Rewind.ApplyAsync(history, messages, 2, null);

        Assert.Contains(outcome!.Plan.Files, f => f.Path == PathOf("A.cs") && f.Kind == RewindFileKind.Kept);
        Assert.Empty(outcome.Failed);
        Assert.Equal("v3", Text(PathOf("A.cs")));
    }

    /// <summary>The card showed other runs than those that follow the question now: nothing is undone.</summary>
    [Fact]
    public async Task RunsOtherThanTheCardShowed_AreNotUndone()
    {
        var (history, messages) = await AConversationAsync();

        var outcome = await Rewind.ApplyAsync(history, messages, 2, ["not-the-runs-it-showed"]);

        Assert.Equal(RewindRefusal.Stale, outcome!.Plan.Refusal);
        Assert.Equal("v3", Text(PathOf("A.cs")));
    }

    /// <summary>The card is the screen's: never saved with the conversation, like the review card.</summary>
    [Fact]
    public void TheRewindCard_IsNeverSaved()
    {
        var snapshot = SessionManager.BuildSnapshot(new (string, string, string, string, string)[]
        {
            ("user", "Change A", "", "", ""), ("rewind", "", "", "", ""), ("assistant", "Changed.", "", "", ""),
        });

        Assert.Equal(["user", "assistant"], snapshot.Select(m => m.Role));
    }

    /// <summary>The question comes back as typed: what went with it is named, not attached again.</summary>
    [Fact]
    public void WhatWentWithTheQuestion_IsSaidNotAttachedAgain()
    {
        var saved = Inferpal.Services.Agent.ChatTurnPolicy.BuildBubbleText("Fix this", ["Foo.cs"]);

        Assert.Equal("Fix this", RewindCard.Typed(saved).Text);
        Assert.EndsWith(Strings.RewindAttachmentsNotAgain("Foo.cs"), RewindCard.Done(null, 2, saved), StringComparison.Ordinal);
    }

    /// <summary>The result bar is the screen's, but its run is saved with the message before it; a session written
    /// before the field existed reads as it was.</summary>
    [Fact]
    public async Task TheRunATurnMade_IsSavedWithItsMessage_AndReadBack()
    {
        var snapshot = SessionManager.BuildSnapshot(new (string, string, string, string, string)[]
        {
            ("user", "Change A", "", "", ""), ("assistant", "Changed.", "", "", ""), ("result", "", "", "", "run-7"),
        });
        var store = new ConversationStore(Path.Combine(_dir, "sessions"));

        await store.SaveAsync("s", snapshot, CancellationToken.None, workspaceRoot: _dir);
        var read = (await store.LoadAsync("s", CancellationToken.None))!.Messages;

        Assert.Equal(2, read.Count);
        Assert.Equal("run-7", read[1].Run);
        Assert.Null(read[0].Run);
    }
}
