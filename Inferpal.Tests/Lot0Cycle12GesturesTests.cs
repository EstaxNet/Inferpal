using System.IO;
using System.Linq;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Gestures of the two windows that the suite cannot run: read in their source, each with the behaviour it guards, until
/// the Visual Studio window runs under test.
/// </summary>
public sealed class Lot0Cycle12GesturesTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    private static string Vm(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", file));

    private static string VsCode(string file) => WebviewRebuildTests.TsCode(file);

    /// <summary>Regenerate replays a question, it does not type one. Read, not run: the history keeps a question typed twice in a
    /// row once, so it only shows when the other editor typed in between.</summary>
    [Fact]
    public void Regenerate_IsNotAQuestionTyped()
    {
        var code = Vm("InferpalToolWindowData.ToolInvocation.cs");

        Assert.Equal(2, code.Split("typed: false").Length - 1);   // Continue, Regenerate
    }

    /// <summary>With no solution or folder, the last resort is the process's folder: git would read, or commit to, whatever repository
    /// holds it. Read here, not run: with its guard gone, <c>/commit-exec</c> under a test commits to this repository
    /// (<c>VsWindowRunsUnderTestTests</c> runs the others).</summary>
    [Fact]
    public void EveryGitCommandOfTheWindow_AsksForAWorkspaceFirst()
    {
        var code = Vm("InferpalToolWindowData.PromptHistory.cs");

        Assert.Equal(5, code.Split("if (await GitRootOrSayAsync() is not { } root) return;").Length - 1);
        Assert.DoesNotContain("GitProcess.For(FindProjectRoot())", code);
        Assert.DoesNotContain("GitProcess.ForCommit(FindProjectRoot())", code);
        Assert.Contains("Strings.GitNeedsAWorkspace", code);

        // One reader of "is anything open", and it never falls back on a solution near the process's folder.
        var holder = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Services", "VsIntegration", "VsContextHolder.cs"));
        var gitRoot = holder[holder.IndexOf("public string? GitRoot()", System.StringComparison.Ordinal)..];
        gitRoot = gitRoot[..gitRoot.IndexOf("public string ProjectRoot", System.StringComparison.Ordinal)];
        Assert.Contains("GetDirectoryName(paths[0])", gitRoot);
        Assert.DoesNotContain("GetCurrentDirectory", gitRoot);
        Assert.Contains("_contextHolder.GitRoot()", ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "ReviewCommitCommand.cs")));
        Assert.Contains("ChooseAsync(_vs, _contextHolder.GitRoot(), ct)", Vm("InferpalToolWindowData.Chrome.cs"));
    }

    /// <summary>Said in the thread by each front-end, as ACP does: in /diagnostics alone, a hand edit that breaks the file does nothing, silently.</summary>
    [Fact]
    public void AnUnreadableSettingsFile_IsSaidOnce_InBothWindows()
    {
        var vs = Vm("InferpalToolWindowData.Connection.cs");
        Assert.Contains("if (follow.Unreadable && !_settingsUnreadableSaid) await ShowInfoAsync(Strings.AcpSettingsUnreadable);", vs);
        Assert.Contains("if (!follow.Busy) _settingsUnreadableSaid = follow.Unreadable;", vs);

        var code = VsCode("chatViewProvider.ts");
        Assert.Contains("if (followed.notice && !this.settingsUnreadableSaid)", code);
        Assert.Contains("this.settingsUnreadableSaid = followed.unreadable === true;", code);
    }

    /// <summary>A notification draws no Markdown: the Core's refusals name a model in bold for the chat.</summary>
    [Fact]
    public void ACommitRefusal_ReachesTheNotificationWithoutItsMarkdown()
    {
        Assert.Contains("**model**", Strings.MsgOnlyReasoningFrom("model"));
        Assert.Contains("join('\\n\\n').replace(/\\*\\*/g, '')", VsCode("chatViewProvider.ts"));
    }

    /// <summary>Every colour the settings window computes gives way to Windows' high contrast.</summary>
    [Fact]
    public void TheSettingsWindow_FollowsHighContrast_OnEveryColourItComputes()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "Inferpal", "ToolWindow"), "InferpalSettingsData*.cs").ToList();
        Assert.True(files.Count >= 5, "the settings window's sources are not where this test reads them");

        var lines = files.SelectMany(f => ConventionCoverageTests.CodeOnly(f).Split('\n'))
                         .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"[dD]ark(Theme)?\s*\?\s*""#"))
                         .ToList();
        Assert.True(lines.Count >= 15, $"{lines.Count} computed colours read: the scan lost them");
        Assert.All(lines, l => Assert.True(l.Contains("?.", System.StringComparison.Ordinal) || l.Contains("TextForeground", System.StringComparison.Ordinal) && l.Contains("HighContrastPalette", System.StringComparison.Ordinal),
                                           "a colour high contrast never reaches: " + l.Trim()));
    }

    // ── the exported header ─────────────────────────────

    private static readonly ExportMessage[] Reloaded =
    [
        new("user", "You", "hello", "10:00"),
        new("assistant", "Assistant", "hi", "10:01"),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReloadedThread_DoesNotStateTokensOrADurationItNeverKept(bool asPlainText)
    {
        var document = ConversationExporter.Build(Reloaded, asPlainText, null, null, "today", null);

        Assert.DoesNotContain(Strings.ExportModel, document);
        Assert.DoesNotContain(Strings.ExportTokens, document);
        Assert.DoesNotContain(Strings.ExportDuration, document);
        Assert.Contains(Strings.ExportTurns, document);
    }

    /// <summary>Reference arm: a thread opened here keeps its figures.</summary>
    [Fact]
    public void AThreadOpenedHere_StatesThem()
    {
        var document = ConversationExporter.Build(Reloaded, false, "configured", 1234, "today", "2m 5s");

        Assert.Contains("`configured`", document);
        Assert.Contains(Strings.ExportTokens, document);
        Assert.Contains("2m 5s", document);
    }
}
