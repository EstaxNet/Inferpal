using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// During a step pause, a typed <c>/resume</c> resumes the agent — the remedy the pause notice names — in both editors.
/// </summary>
/// <remarks>
/// ⚠ "Click ▶ Resume (or type /resume)": in Visual Studio, Enter during a turn is Stop, so typing it CANCELLED the paused
/// run; in VS Code the composer ignores a send during a turn, so nothing happened at all.
/// </remarks>
public class ResumeDuringPauseTests
{
    [Theory]
    [InlineData("/resume", true)]
    [InlineData("  /RESUME \n", true)]
    [InlineData("/resume now", false)]   // reference arms: anything else is not a resume
    [InlineData("resume", false)]
    [InlineData("", false)]
    public void OnlyResumeItself_IsAResume(string prompt, bool expected) =>
        Assert.Equal(expected, SlashCommandRouter.IsResume(prompt));

    [Fact]
    public void VisualStudio_ResumesBeforeItWouldStop()
    {
        var turn = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));

        var resume = turn.IndexOf("if (_stepResume is not null && SlashCommandRouter.IsResume(Prompt))", StringComparison.Ordinal);
        var stop   = turn.IndexOf("_currentCts?.Cancel();", StringComparison.Ordinal);
        Assert.True(stop > 0, "Stop moved — the rule reads nothing");   // WITNESS
        Assert.True(resume > 0 && resume < stop, "a /resume typed during a pause stops the run");
        Assert.Contains("ResumeStep();", turn[resume..stop]);
    }

    [Fact]
    public void VsCode_LetsResumeThroughDuringAPause()
    {
        var main = WebviewRebuildTests.TsCode("webview/main.ts");
        var send = WebviewRebuildTests.Body(main, "function send(): void");

        var resume = send.IndexOf("if (busy && stepPausedNow && text.trim().toLowerCase() === '/resume') {", StringComparison.Ordinal);
        var refuse = send.IndexOf("if (!text.trim() || busy) {", StringComparison.Ordinal);
        Assert.True(refuse > 0, "the busy guard moved — the rule reads nothing");   // WITNESS
        Assert.True(resume > 0 && resume < refuse, "a /resume typed during a pause is dropped with every other send");
        Assert.Contains("post({ type: 'resumeStep' });", send[resume..refuse]);

        // The flag follows the pause: set by it, cleared when it is resumed and when the turn ends.
        string CaseHead(string label)
        {
            var at = main.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at > 0, $"{label} moved — the rule reads nothing");
            return main[at..Math.Min(main.Length, at + label.Length + 60)];
        }
        Assert.Contains("stepPausedNow = true;", CaseHead("case 'stepPaused': {"));
        Assert.Contains("stepPausedNow = false;", CaseHead("case 'stepResumed':"));
        Assert.Contains("stepPausedNow = false;", CaseHead("case 'turnEnded': {"));
    }
}
