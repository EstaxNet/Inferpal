using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A notice is an assistant bubble on screen and never an answer the model gave — live, a turn keeps ONE answer.
/// Both editors save the thread as shown and rebuild the model's history from it, so every notice (an end notice, a
/// slash command's output, a failed save, a lost connection) came back as another answer. The restore half is in
/// <see cref="SessionManagerTests"/>; these are the two front-ends that mark what they show, read in the source (the
/// view model and the extension are not executable from this suite).
/// </summary>
public class NoticeRestoreTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void InVisualStudio_OnlyTheTurnsAnswer_IsAnAssistantBubble()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root(), "Inferpal", "ToolWindow"), "InferpalToolWindowData*.cs")
                             .ToDictionary(Path.GetFileName, ConventionCoverageTests.CodeOnly);

        var answers = files.Sum(f => Regex.Matches(f.Value, @"ChatMessageItem\.AssistantMsg\(").Count);
        var notices = files.Sum(f => Regex.Matches(f.Value, @"ChatMessageItem\.NoticeMsg\(").Count);
        Assert.True(notices > 10, $"only {notices} notice site(s): the scan is not reading the view model");   // WITNESS

        // The three bubbles of the turn's answer — FinalText, ToolSummary, EmptyFallback — and nothing else.
        Assert.Equal(3, answers);
        var turn = files["InferpalToolWindowData.ChatTurn.cs"];
        foreach (var name in new[] { "finalMsg", "summaryMsg", "emptyMsg" })
            Assert.Contains($"var {name} = ChatMessageItem.AssistantMsg(", turn);
    }

    [Fact]
    public void InVsCode_EveryAssistantEntry_ButTheChatsAnswer_IsANotice()
    {
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(Root(), "vscode", "src", "chatViewProvider.ts")));

        // Each entry up to the brace that closes it — followed by ')' or ';' — not the first '}': the translated
        // sentences carry '{0}'.
        var entries = Regex.Matches(code, @"role: 'assistant',").Select(m =>
        {
            var end = m.Index;
            while (end < code.Length - 1 && !(code[end] == '}' && code[end + 1] is ')' or ';')) end++;
            return code[m.Index..end];
        }).ToList();
        Assert.True(entries.Count >= 6, $"only {entries.Count} assistant entr(y/ies) found: the scan reads nothing");   // WITNESS

        var answers = entries.Where(e => e.Contains("text: finalText")).ToList();
        Assert.Equal(2, answers.Count);                                        // a finished answer, a stopped one
        Assert.All(answers, e => Assert.DoesNotContain("notice", e));
        Assert.All(entries.Except(answers), e => Assert.Contains("notice: true", e));
    }

    [Fact]
    public void BothEditors_MarkANotice_TheWayTheRestoreReadsIt()
    {
        var sessions = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(Root(), "vscode", "src", "chatSessions.ts")));

        Assert.Contains($"export const NOTICE_MARKER = '{SessionManager.NoticeMarker}';", sessions);
        Assert.Contains("toolName: NOTICE_MARKER", sessions);                  // saved
        Assert.Contains("m.toolName === NOTICE_MARKER", sessions);             // and read back, or a re-save loses it
    }
}
