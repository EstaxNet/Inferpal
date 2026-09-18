using System.IO;
using System.Linq;
using Inferpal.Localization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Clearing the chat <b>archives</b> the conversation you are leaving. When that archive fails, the
/// conversation is gone — and both front-ends only wrote a line to a log.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The save is deliberately fire-and-forget (naming the session costs a model call, and
/// <c>/clear</c> must not wait 15 s for it), so by the time it fails the transcript is already
/// cleared: <b>the failure arrives after the only moment the user could have copied anything.</b>
/// Visual Studio swallowed it into <c>/diagnostics</c> (<c>Session.SaveNamed</c>), VS Code into its
/// output channel (<c>[chat] archive failed</c>) — neither is where someone who just pressed "new
/// conversation" is looking.
/// </para>
/// <para>
/// ⚠ <b>And the rule is written twenty lines below the silence.</b> <c>saveSessionCommand</c>, in the
/// same file as <c>archiveConversation</c>, carries: <i>"a palette command that does NOTHING is
/// indistinguishable from one that failed: with no host, we say so"</i>. The explicit save says
/// it; the implicit one, the only one that can lose something, did not.
/// </para>
/// <para>
/// ⚠ <b>What this test can and cannot do.</b> Neither archive path is reachable from here — one is a
/// Remote UI view-model, the other TypeScript in the extension host — so this is a <b>source scan</b>,
/// like <c>SessionNameCollisionTests.BothFrontEnds_ArchiveUnderAFreeName</c> next door and like
/// <c>InProcContractTests</c>. It asserts the shape, not the pixels; each half carries a witness so a
/// renamed method or a moved file fails loudly instead of passing empty.
/// </para>
/// </remarks>
public sealed class ArchiveFailureSilenceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── Visual Studio ─────────────────────────────────────────────────────────

    [Fact]
    public void VisualStudio_SaysSo_WhenTheArchiveOfTheClearedConversationFails()
    {
        var path = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.PendingPrompt.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .SingleOrDefault(m => m.Identifier.Text == "SaveNamedSessionAsync");
        // WITNESS: the method this test is about still exists under this name, in this file.
        Assert.True(method is not null,
            "SaveNamedSessionAsync was not found: this test would have measured nothing.");

        var catches = method!.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        Assert.True(catches.Count > 0, "the archive no longer catches anything: check what replaced it.");

        // The failure must reach the conversation, not only the diagnostics ring.
        var body = string.Join("\n", catches.Select(c => c.Block.ToString()));
        Assert.Contains("SessionArchiveFailed", body, StringComparison.Ordinal);
    }

    // ── VS Code ───────────────────────────────────────────────────────────────

    [Fact]
    public void VsCode_SaysSo_WhenTheArchiveOfTheClearedConversationFails()
    {
        var body = ArchiveConversationBody();

        // WITNESS: this really is the archive — it asks for the generated name and saves under it.
        Assert.Contains("sessionTitle(", body, StringComparison.Ordinal);
        Assert.Contains("sessionSave(", body, StringComparison.Ordinal);
        Assert.Contains("catch", body, StringComparison.Ordinal);

        // A log line is not a message. The user must see it where they are: the window.
        Assert.Contains("showWarningMessage", body, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_ArchiveNotice_IsTranslatable()
    {
        // The sentence goes through the extension's own l10n channel, like every other string the
        // webview and the window show — a hard-coded one is English in the nine other locales.
        // ⚠ `t(` alone is not a witness — two characters that match inside other identifiers. The
        // call is matched instead: the helper's name, then its first argument's quote, whatever the
        // formatter did with the line break between them.
        var body = ArchiveConversationBody();
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"\bt\(\s*['""`]"), body);
    }

    /// <summary>The body of <c>archiveConversation</c>, comments neutralised.</summary>
    /// <remarks>
    /// One reader for both scans of this file: the brace matching and the comment neutralisation
    /// live in <see cref="ConversationPersistenceSilenceTests.TsMethodBody"/>, which measures the
    /// same class of defect two doors down.
    /// </remarks>
    private static string ArchiveConversationBody() =>
        ConversationPersistenceSilenceTests.TsMethodBody(
            Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts"),
            "private archiveConversation(");

    // ── The sentence itself ───────────────────────────────────────────────────

    [Fact]
    public void TheNotice_NamesTheConversationAndTheCause()
    {
        var notice = Strings.SessionArchiveFailed("disk full");

        Assert.Contains("disk full", notice, StringComparison.Ordinal);
        // A notice that does not say what was lost sends the reader looking for a setting.
        Assert.NotEqual("disk full", notice);
    }
}
