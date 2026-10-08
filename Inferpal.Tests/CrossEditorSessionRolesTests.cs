using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A session written by one editor and reloaded in the other comes back whole: every role is shown, and the next save
/// writes it back as it was read.
/// </summary>
/// <remarks>
/// VS Code saves a failed turn, a "Cancelled.", an unreachable backend as <c>error</c>, which Visual Studio has no
/// bubble for: restored, it was there and invisible. Visual Studio saves its plan card as <c>plan</c>, which VS Code
/// dropped on loading — and the next save of the conversation erased it from the file. The adapters' types cannot be
/// built here (no Visual Studio SDK, no VS Code), so their halves are read in their sources, without comments.
/// </remarks>
public sealed class CrossEditorSessionRolesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Ts(string file) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(Root(), "vscode", "src", file)));

    /// <summary>The roles a VS Code transcript entry can have — what its session save writes.</summary>
    private static IReadOnlyList<string> VsCodeSavedRoles()
    {
        var union = Regex.Match(Ts("webviewMessages.ts"), @"interface WvTranscriptItem \{\s*role: ([^;]+);");
        Assert.True(union.Success, "the transcript item's role union was not found");
        // `status` is a live line on both sides: neither editor restores it as a message.
        return [.. Regex.Matches(union.Groups[1].Value, "'([a-z]+)'").Select(m => m.Groups[1].Value).Where(r => r != "status")];
    }

    /// <summary>The roles VS Code's restore draws as themselves (the explicit branches of <c>toTranscript</c>).</summary>
    private static IReadOnlyList<string> VsCodeDrawnRoles()
    {
        var restore = Ts("chatSessions.ts");
        var start   = restore.IndexOf("export function toTranscript", StringComparison.Ordinal);
        Assert.True(start >= 0, "toTranscript was not found");
        return [.. Regex.Matches(restore[start..], @"m\.role === '([a-z]+)'").Select(m => m.Groups[1].Value).Distinct()];
    }

    [Fact]
    public void ARoleOnlyVsCodeDraws_IsShownInVisualStudio_AsANotice_AndSavedBackAsRead()
    {
        var foreign = VsCodeSavedRoles().Where(r => !SessionManager.VisualStudioBubbleRoles.Contains(r)).ToList();
        Assert.Contains("error", foreign);                                   // witness: the union is read

        var item = ConventionCoverageTests.CodeOnly(Path.Combine(Root(), "Inferpal", "ToolWindow", "ChatMessageItem.cs"));
        Assert.Contains("if (!Services.Persistence.SessionManager.VisualStudioBubbleRoles.Contains(role))", item, StringComparison.Ordinal);
        Assert.Contains("var notice = NoticeMsg(content);", item, StringComparison.Ordinal);
        Assert.Contains("notice._foreign  = new SavedMessage(role, content,", item, StringComparison.Ordinal);
        Assert.Contains("_foreign is { } f ? (f.Role, f.Content,", item, StringComparison.Ordinal);

        // Every save of the window writes what a bubble was read as: the three snapshot sites.
        var vm = Directory.EnumerateFiles(Path.Combine(Root(), "Inferpal", "ToolWindow"), "InferpalToolWindowData*.cs")
                          .Select(ConventionCoverageTests.CodeOnly).ToList();
        Assert.Equal(3, vm.Sum(code => Regex.Matches(code, @"SessionManager\.BuildSnapshot\(\s*Messages\.Select\(m => m\.Saved\)\)").Count));
        Assert.DoesNotContain(vm, code => code.Contains("(m.Role, m.Content, m.ToolName, m.Timestamp)", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoleOnlyVisualStudioDraws_IsKeptByVsCode_AndWrittenBack()
    {
        var drawn = VsCodeDrawnRoles();
        Assert.Contains("assistant", drawn);                                 // witness: the branches are read
        var foreign = SessionManager.VisualStudioBubbleRoles.Where(r => !drawn.Contains(r)).ToList();
        Assert.Equal(["plan"], foreign);

        var restore = Ts("chatSessions.ts");
        // Read: every other role but the live status line becomes a notice that carries the saved message…
        Assert.Contains("} else if (m.role !== 'status') {", restore, StringComparison.Ordinal);
        Assert.Contains("notice: true, timestamp: m.timestamp ?? undefined, savedAs: m", restore, StringComparison.Ordinal);
        // …written back first, before the rule that would rewrite it as a notice.
        Assert.Matches(@"transcript\.map\(\(item\) =>\s*item\.savedAs\s*\?\s*item\.savedAs", restore);
    }

    [Fact]
    public void AForeignRole_NeverReachesTheModel()
    {
        var history = SessionManager.BuildRestoredHistory("system",
            [new SavedMessage("user", "q"), new SavedMessage("error", "Cannot reach LM Studio"), new SavedMessage("plan", "- [ ] step")]);

        Assert.Equal(["system", "user"], history.Select(m => m.Role));
    }
}
