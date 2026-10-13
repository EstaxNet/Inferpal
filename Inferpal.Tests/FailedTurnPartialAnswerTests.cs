using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The visible part of a FAILED turn's answer stays on screen as a notice, in both editors: never the model's answer.
/// </summary>
/// <remarks>
/// The two editors disagreed. Visual Studio kept the streamed bubble as an assistant answer: saved as one, a reload
/// handed the model half a reply as what it had said — the rule "a failed turn is never kept as the answer" held for
/// the live history only. VS Code drew it live but left it out of the transcript: gone at the next redraw, never saved.
/// A notice is what both keep (<c>SessionManager.NoticeMarker</c>, <c>notice: true</c>): on screen and in the saved
/// conversation, skipped by the restored history. These rules read the source (the editors need a live host).
/// </remarks>
public sealed class FailedTurnPartialAnswerTests
{
    private static string VsCode(string file) =>
        ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", file));

    [Fact]
    public void VisualStudio_KeepsTheFailedPartAsANotice_OnBothFailurePaths()
    {
        var turn = VsCode("InferpalToolWindowData.ChatTurn.cs");
        // The agent run that came back Failed.
        var failed = turn[turn.IndexOf("if (agentFailed)", StringComparison.Ordinal)..];
        Assert.True(failed.IndexOf("KeepAsNotice(streamingMsg);", StringComparison.Ordinal)
                    < failed.IndexOf("ChatMessageItem.NoticeMsg(agentFinalResponse)", StringComparison.Ordinal));
        Assert.Contains("KeepAsNotice(streamingMsg);", failed[..failed.IndexOf("else switch", StringComparison.Ordinal)], StringComparison.Ordinal);
        // The request that threw.
        Assert.Contains("streamingMsg = FinalizeStreamingBubble(streamingMsg);\n                KeepAsNotice(streamingMsg);\n"
                        + "                var errItem = ChatMessageItem.NoticeMsg(Strings.MsgError(msg));",
                        turn.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);

        var keep = VsCode("InferpalToolWindowData.ToolInvocation.cs");
        Assert.Contains("if (partial is not null) partial.ToolName = Services.Persistence.SessionManager.NoticeMarker;", keep, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_KeepsTheFailedPartAsANotice_OnBothFailurePaths()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var turn = WebviewRebuildTests.Body(provider, "private async chatTurn(");
        // The error the host answered, then the request that failed.
        Assert.Contains("this.keepFailedPartial(finalText);\n        this.append({ role: 'error', text: result.error,",
                        turn.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("this.keepFailedPartial(this.streamText);\n      this.append({ role: 'error', text: message,",
                        turn.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);

        var keep = WebviewRebuildTests.Body(provider, "private keepFailedPartial(");
        Assert.Contains("this.append({ role: 'assistant', text: partial, timestamp: ChatViewProvider.now(), notice: true });", keep, StringComparison.Ordinal);
    }
}
