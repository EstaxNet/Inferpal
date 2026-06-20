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

        Assert.Equal(Strings.PHistoryNoEntry(9), result.Message);
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
}
