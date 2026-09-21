using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class PHistoryCommandHandlerTests
{
    // Oldest-first, matching _promptHistory.Entries.
    private static readonly string[] Sample = ["fix the parser", "add tests", "refactor cache"];

    private static string[] Cmd(params string[] args) => ["/phistory", .. args];

    [Fact]
    public void Use_ValidIndex_FillsPromptWithEntry()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("use", "2"));

        Assert.Equal("add tests", result.FillPrompt);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Use_OutOfRange_ReturnsMessageNoFill()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("use", "9"));

        Assert.Equal(Strings.PHistoryNoEntry("9"), result.Message);
        Assert.Null(result.FillPrompt);
    }

    [Fact]
    public void List_Empty_ReturnsEmptyNotice()
    {
        var result = PHistoryCommandHandler.Handle([], Cmd());

        Assert.Equal(Strings.PHistoryEmpty, result.Message);
        Assert.Null(result.FillPrompt);
    }

    [Fact]
    public void List_NoTerm_ReturnsAllEntriesMostRecentFirst()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd());

        Assert.NotNull(result.Message);
        Assert.Contains(Strings.PHistoryListHeader, result.Message);
        Assert.Contains("refactor cache", result.Message);
        // Most recent first: entry #3 appears before #1 in the listing.
        Assert.True(result.Message!.IndexOf("refactor cache", StringComparison.Ordinal)
                  < result.Message.IndexOf("fix the parser", StringComparison.Ordinal));
    }

    [Fact]
    public void List_WithMatchingTerm_FiltersEntries()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("test"));

        Assert.NotNull(result.Message);
        Assert.Contains("add tests", result.Message);
        Assert.DoesNotContain("refactor cache", result.Message);
    }

    [Fact]
    public void List_WithNonMatchingTerm_ReturnsNoMatchNotice()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("zzz"));

        Assert.Equal(Strings.PHistoryNoMatch("zzz"), result.Message);
        Assert.Null(result.FillPrompt);
    }

    [Fact]
    public void Use_TheCommandCopiedFromAFullListing_StillFillsThatEntry_AfterTheTypedCommandIsRecorded()
    {
        // VS Code records the typed command before routing it: on a full history, the append evicts the oldest entry and shifts every position.
        var history = Enumerable.Range(1, 50).Select(n => $"p{n}").ToList();
        var listing = PHistoryCommandHandler.Handle(history, Cmd()).Message!;
        var line    = listing.Split('\n').Single(l => l.Contains(" p12  ", StringComparison.Ordinal));
        var typed   = line[(line.IndexOf("`/phistory use ", StringComparison.Ordinal) + 1)..line.LastIndexOf('`')];
        Assert.Equal("p12", PHistoryCommandHandler.Handle(history, typed.Split(' ')).FillPrompt);

        Inferpal.Services.Agent.ChatTurnPolicy.AppendPromptHistory(history, typed, 50);

        Assert.Equal("p12", PHistoryCommandHandler.Handle(history, typed.Split(' ')).FillPrompt);
    }

    [Fact]
    public void Use_AKeyThatNoLongerExists_SaysSo()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("use", "0badc0de"));

        Assert.Null(result.FillPrompt);
        Assert.Contains("0badc0de", result.Message);
    }

    // ── A history that did not open is not an empty history ────────────────────

    private const string TornPath = @"C:\Users\x\AppData\Roaming\Inferpal\prompt_history.json";

    /// <summary>
    /// The window reads this file once, while it is built; from then on it holds an empty list and
    /// every sentence below is a statement about the USER. The bytes are kept — the store sets an
    /// unreadable file aside before the next prompt overwrites it — but a recovery nobody is told
    /// about is not one.
    /// </summary>
    [Fact]
    public void List_WhenTheFileDidNotOpen_NamesIt_InsteadOfSayingEmpty()
    {
        var result = PHistoryCommandHandler.Handle([], Cmd(), TornPath);

        Assert.Equal(Strings.PHistoryUnreadable(TornPath), result.Message);
        Assert.NotEqual(Strings.PHistoryEmpty, result.Message);
    }

    /// <summary>
    /// The prompts typed in THIS session are real history and still list: the notice rides above
    /// them. Replacing the listing would hide what the user does have.
    /// </summary>
    [Fact]
    public void List_WhenTheFileDidNotOpen_StillListsThisSessionsPrompts()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd(), TornPath);

        Assert.Contains(Strings.PHistoryUnreadable(TornPath), result.Message);
        Assert.Contains("refactor cache", result.Message);
    }

    /// <summary>"No entry matching" is "not found"; with half the history missing the true answer
    /// is "not searched" — the same two sentences <c>/history</c> had to separate.</summary>
    [Fact]
    public void Search_WhenTheFileDidNotOpen_DoesNotSayNotFoundOnItsOwn()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("zzz"), TornPath);

        Assert.Contains(Strings.PHistoryUnreadable(TornPath), result.Message);
        Assert.Contains(Strings.PHistoryNoMatch("zzz"), result.Message);
    }

    [Fact]
    public void Use_AnEntryThatIsMissing_WhenTheFileDidNotOpen_SaysWhy()
    {
        var result = PHistoryCommandHandler.Handle(Sample, Cmd("use", "9"), TornPath);

        Assert.Null(result.FillPrompt);
        Assert.Contains(Strings.PHistoryUnreadable(TornPath), result.Message);
    }

    /// <summary>Reference arm: a readable history says none of it, or the notice becomes the noise
    /// everyone stops reading.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("zzz")]
    [InlineData("use 9")]
    public void AReadableHistory_SaysNothingAboutTheFile(string args)
    {
        var parts  = args.Length == 0 ? Cmd() : Cmd(args.Split(' '));
        var result = PHistoryCommandHandler.Handle(Sample, parts);

        Assert.DoesNotContain("prompt_history.json", result.Message ?? string.Empty);
    }
}
