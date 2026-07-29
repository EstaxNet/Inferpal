using System.Net.Http;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// Portable model step of the in-place code actions (Refactor / Fix / Add-docs), shared by
// the VS adapter and the host's headless `codeAction/run`. ResolveTarget's own cases are
// covered in InlineEditResponseTests — here we lock the run outcomes.
public class CodeActionPipelineTests
{
    private static FakeInferenceProvider ProviderReplying(string reply) =>
        new() { ChatResult = new ChatTurnResult(reply, null, 0, 0) };

    private static Task<CodeActionRun> RunAsync(FakeInferenceProvider provider, string docText,
                                                int selStart = 0, int selEnd = 0, bool selectionEmpty = true) =>
        CodeActionPipeline.RunAsync(provider, "m", "system", "instruction",
                                    docText, selStart, selEnd, selectionEmpty, CancellationToken.None);

    [Fact]
    public async Task Run_WholeFileRewrite_ReturnsEditedWithFullRange()
    {
        var run = await RunAsync(ProviderReplying("int y = 2;"), "int x = 1;");

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("int y = 2;", run.EditedCode);
        Assert.Equal(0, run.Start);
        Assert.Equal(10, run.End);
        Assert.False(run.HasSelection);
        Assert.Equal("int y = 2;", run.NewDocText);
    }

    [Fact]
    public async Task Run_Selection_ReplacesOnlyTheRangeInNewDocText()
    {
        // doc: "aa\nbb\ncc" — selection covers "bb" ([3,5)).
        var run = await RunAsync(ProviderReplying("BB"), "aa\nbb\ncc", 3, 5, selectionEmpty: false);

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.True(run.HasSelection);
        Assert.Equal("aa\nBB\ncc", run.NewDocText);
    }

    [Fact]
    public async Task Run_FencedReply_IsCleanedBeforeUse()
    {
        var run = await RunAsync(ProviderReplying("```csharp\nint y = 2;\n```"), "int x = 1;");

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("int y = 2;", run.EditedCode);
    }

    [Fact]
    public async Task Run_SentinelReply_ReturnsNoChangeNeeded()
    {
        var run = await RunAsync(ProviderReplying(CodeActionSentinel.Token), "int x = 1;");

        Assert.Equal(CodeActionOutcome.NoChangeNeeded, run.Outcome);
        Assert.Null(run.NewDocText);
    }

    [Fact]
    public async Task Run_EmptyDocument_Fails()
    {
        var run = await RunAsync(ProviderReplying("whatever"), "   \n  ");

        Assert.Equal(CodeActionOutcome.Failed, run.Outcome);
    }

    [Fact]
    public async Task Run_EmptyModelReply_Fails()
    {
        var run = await RunAsync(ProviderReplying(""), "int x = 1;");

        Assert.Equal(CodeActionOutcome.Failed, run.Outcome);
    }

    [Fact]
    public async Task Run_ProviderThrows_ReturnsFailedNotThrow()
    {
        var provider = new FakeInferenceProvider
        {
            OnChat = (_, _) => throw new HttpRequestException("backend down"),
        };

        var run = await RunAsync(provider, "int x = 1;");

        Assert.Equal(CodeActionOutcome.Failed, run.Outcome);
    }

    [Fact]
    public async Task Run_Cancellation_Propagates()
    {
        var provider = new FakeInferenceProvider
        {
            OnChat = (_, ct) => Task.FromCanceled<ChatTurnResult>(new CancellationToken(true)),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(provider, "int x = 1;"));
    }

    [Fact]
    public async Task Run_SelectionReply_IsReindentedToTheOriginalBase()
    {
        // Selection "    return 1;" (indented 4) — model replies at column 0; the reindenter
        // must re-anchor the snippet so the document keeps its shape.
        const string doc = "void M()\n{\n    return 1;\n}";
        var start = doc.IndexOf("    return 1;", StringComparison.Ordinal);
        var end   = start + "    return 1;".Length;

        var run = await RunAsync(ProviderReplying("return 2;"), doc, start, end, selectionEmpty: false);

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("void M()\n{\n    return 2;\n}", run.NewDocText);
    }
}
