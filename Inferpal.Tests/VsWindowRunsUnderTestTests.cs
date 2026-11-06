using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
