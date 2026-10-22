using System.IO;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Review a commit…" in Visual Studio: the recent commits read once — nothing typed ever reaches git's command line —,
/// filtered as the person types, the typed text a line of its own when it can name a revision (VS Code's picker rule),
/// and the pick reviewed by <c>/check</c> in the chat.
/// </summary>
public class ReviewCommitsTests
{
    private const char F = '\x1f';
    private const char Rs = '\x1e';

    private static string Log(params (string Hash, string Subject, string Author, string When)[] commits) =>
        string.Concat(commits.Select(c => $"{c.Hash}{F}{c.Hash[..7]}{F}{c.Subject}{F}{c.Author}{F}{c.When}{Rs}\n"));

    [Fact]
    public void TheLog_IsReadCommitByCommit_AndALineThatDoesNotReadIsLeftOut()
    {
        var output = Log(("c10e0c25a1b2", "Apply the discount before the tax", "Dams", "2026-10-22T10:00:00+02:00"))
                   + $"broken{F}line{Rs}\n"
                   + Log(("7684325dead0", "Cart: a Reset button | and its test", "Alex", "2026-10-21T09:30:00+02:00"));

        var commits = ReviewCommits.Parse(output);

        Assert.Equal(["c10e0c25a1b2", "7684325dead0"], commits.Select(c => c.Hash));
        Assert.Equal("c10e0c2", commits[0].ShortHash);
        Assert.Equal("Cart: a Reset button | and its test", commits[1].Subject);
        Assert.Equal("Alex", commits[1].Author);
    }

    [Fact]
    public void TheList_IsFilteredByAHashThatStartsWithTheText_OrASubjectThatHoldsIt()
    {
        var commits = Enumerable.Range(0, 80)
            .Select(i => new CommitEntry($"{i:x4}abcdef", $"{i:x4}abc", i == 70 ? "Cart: discount" : $"Change {i}", "Dams", DateTimeOffset.Now))
            .ToList();

        Assert.Equal(ReviewCommits.Shown, ReviewCommits.Filter(commits, "").Count);                       // the most recent
        Assert.Equal("Cart: discount", Assert.Single(ReviewCommits.Filter(commits, "DISCOUNT")).Subject); // past the 50 shown
        Assert.Equal("0046abcdef", Assert.Single(ReviewCommits.Filter(commits, "0046")).Hash);
        Assert.Empty(ReviewCommits.Filter(commits, "nothing like it"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("fix the cart", null)]       // words of a subject, searched: no revision holds a space
    [InlineData("init", null)]               // /check init writes an example check, it reviews nothing
    [InlineData("Cart:", null)]              // no revision /check would take: the subjects are searched only
    [InlineData("release/1.8", "release/1.8")]
    [InlineData(" main..feature ", "main..feature")]
    public void TheTypedText_IsALineOfItsOwn_WhenItCanNameARevision(string typed, string? revision) =>
        Assert.Equal(revision, ReviewCommits.TypedRevision(typed));

    /// <summary>The one git call: the log, the arguments fixed — never what is typed —, signatures off (gpg's lines would
    /// land in the records); git's refusal named, no list.</summary>
    [Fact]
    public async Task TheCommits_AreReadOnce_AndARefusalIsSaid()
    {
        var asked = new List<string>();
        GitRunner refusing = (args, _) => { asked.Add(args); return Task.FromResult(("fatal: not a git repository", 128)); };

        var (commits, failure) = await ReviewCommits.ReadAsync(refusing, CancellationToken.None);

        Assert.Empty(commits);
        Assert.NotNull(failure);
        Assert.Equal($"-c log.showSignature=false log -n {ReviewCommits.Searched} --format=%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1e", Assert.Single(asked));

        GitRunner answering = (_, _) => Task.FromResult((Log(("c10e0c25a1b2", "Apply the discount", "Dams", "2026-10-22T10:00:00+02:00")), 0));
        var (read, none) = await ReviewCommits.ReadAsync(answering, CancellationToken.None);
        Assert.Null(none);
        Assert.Equal("Apply the discount", Assert.Single(read).Subject);
    }

    // ── Visual Studio: the command, the dialog, the menu ──────────────────────

    private static string Code(params string[] parts) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]));

    /// <summary>The command reads the commits of the repository the chat's <c>/check</c> reads — one root reader for
    /// both —, and reviews the pick with <c>/check</c> in the chat, as typing it; a dialog closed or cancelled reviews
    /// nothing.</summary>
    [Fact]
    public void VisualStudiosCommand_ReviewsThePickWithCheck()
    {
        var command = Code("Inferpal", "Commands", "ReviewCommitCommand.cs");
        Assert.Contains("await Services.Commands.ReviewCommits.ReadAsync(GitProcess.For(_contextHolder.ProjectRoot()), ct)", command, StringComparison.Ordinal);
        Assert.Contains("private string FindProjectRoot(IReadOnlyList<string>? openPaths = null) => _contextHolder.ProjectRoot(openPaths);",
                        Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"), StringComparison.Ordinal);
        Assert.Contains("if (chosen is null) return;", command, StringComparison.Ordinal);
        Assert.Contains("_contextHolder.SetPendingPrompt($\"/check {chosen}\");", command, StringComparison.Ordinal);
        Assert.Contains("MenuChild.Command<ReviewCommitCommand>()", Code("Inferpal", "Commands", "InferpalMenu.cs"), StringComparison.Ordinal);

        // The dialog: the typed text first when it names a revision, then the commits it finds; the first line selected.
        var data = Code("Inferpal", "ToolWindow", "ReviewCommitDialogData.cs");
        Assert.Contains("if (Services.Commands.ReviewCommits.TypedRevision(_filter) is { } typed)", data, StringComparison.Ordinal);
        Assert.Contains("foreach (var c in Services.Commands.ReviewCommits.Filter(_commits, _filter))", data, StringComparison.Ordinal);
        Assert.Contains("SelectLocked(Rows.FirstOrDefault());", data, StringComparison.Ordinal);
        // The keyboard: the search box has it as the dialog opens, ↓/↑ move the selection from it, Enter reviews it; the
        // lines are no tab stops (Enter on one would click it, not review it).
        var xaml = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "ReviewCommitDialogControl.xaml"));
        Assert.Contains("FocusManager.FocusedElement=\"{Binding ElementName=Search}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<KeyBinding Key=\"Down\" Command=\"{Binding NextCommand}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<KeyBinding Key=\"Up\"   Command=\"{Binding PreviousCommand}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectCommand}\" Focusable=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
        // A click on a line a keystroke has replaced selects nothing; git refused, nothing is offered.
        Assert.Contains("if (Rows.Contains(row)) SelectLocked(row);", data, StringComparison.Ordinal);
        Assert.Contains("if (!HasFailure)", data, StringComparison.Ordinal);
    }
}
