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

        // During a turn, a text is a note for the run (Steer): a /resume typed during a pause must resume before that.
        var resume = turn.IndexOf("if (_stepResume is not null && SlashCommandRouter.IsResume(Prompt))", StringComparison.Ordinal);
        var note   = turn.IndexOf("Steer(text)", StringComparison.Ordinal);
        Assert.True(note > 0, "the note moved — the rule reads nothing");   // WITNESS
        Assert.True(resume > 0 && resume < note, "a /resume typed during a pause becomes a note instead of resuming");
        Assert.Contains("ResumeStep();", turn[resume..note]);
    }

    [Fact]
    public void VsCode_LetsResumeThroughDuringAPause()
    {
        var main = WebviewRebuildTests.TsCode("webview/main.ts");
        var send = WebviewRebuildTests.Body(main, "function send(later = false): void");

        var resume = send.IndexOf("if (busy && stepPausedNow && text.trim().toLowerCase() === '/resume') {", StringComparison.Ordinal);
        var steer  = send.IndexOf("{ type: 'steer', text }", StringComparison.Ordinal);
        Assert.True(steer > 0, "the note a turn reads moved — the rule reads nothing");   // WITNESS
        Assert.True(resume > 0 && resume < steer, "a /resume typed during a pause becomes a note instead of resuming");
        Assert.Contains("post({ type: 'resumeStep' });", send[resume..steer]);

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
