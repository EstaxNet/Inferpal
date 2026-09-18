using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The product PROMISES the conversation comes back — it restores it at opening — and the three
/// paths that keep that promise failed into a log.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The dearest one is not persistence, it is AMNESIA.</b> In VS Code the host is a separate
/// process: when it restarts (a setting changed, a crash, "Restart Host") it comes back with an
/// EMPTY history while the thread still shows the conversation. <c>onHostReady</c> exists only to
/// repair that — it saves what is on screen then loads it back so the host rebuilds its history —
/// and its own comment names the cost:
/// <i>« the next question went out with no context, and the model "forgot" what is on screen »</i>.
/// Its failure fell straight back into that very fault, while writing one line to the output
/// channel. ⚠ And there are <b>two doors</b>: the exception, and the reload that comes back empty
/// with nothing thrown (<c>HostServer</c> returns <c>null</c> when the auto-save does not belong to
/// this workspace) — the second did not even write its log line.
/// </para>
/// <para>
/// ⚠ <b>And the per-turn auto-save is mute in BOTH front-ends</b> (<c>Session.AutoSave</c> in the
/// ring on the VS side, <c>[chat] auto-save failed</c> in the channel on the VS Code side). There
/// the transcript is still on screen — so the notice is said <b>once</b>, and re-arms on the first
/// save that works again: repeating it every turn is the noise <c>RecordOnce</c> exists to avoid.
/// </para>
/// <para>
/// ⚠ <b>What this test can and cannot do.</b> None of the paths is runnable here (a Remote UI
/// view-model on one side, the extension host on the other): this is a <b>source scan</b>, like
/// <see cref="ArchiveFailureSilenceTests"/>, whose class this defect belongs to — that fix closed
/// the archive of <c>/clear</c>, these three doors stayed open. Each half carries its witness.
/// </para>
/// </remarks>
public sealed class ConversationPersistenceSilenceTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ChatViewProvider =>
        Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts");

    /// <summary>The body of a TypeScript method, braces matched, comments neutralised.</summary>
    /// <remarks>
    /// Comments are stripped first: a scan that reads raw text finds its pattern in the prose that
    /// <i>documents</i> the defect, and goes green on a file that still has it.
    /// </remarks>
    internal static string TsMethodBody(string filePath, string signature)
    {
        var text = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(filePath));

        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found: this test would have measured nothing.");

        var open = text.IndexOf('{', start);
        Assert.True(open > 0, $"'{signature}' has no body.");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }
        Assert.Fail($"'{signature}' is not brace-balanced: the scan cannot trust what it read.");
        return string.Empty;
    }

    /// <summary>`t(` alone is two characters that match inside other identifiers — the CALL is matched.</summary>
    private static readonly Regex Translated = new(@"\bt\(\s*['""`]");

    // ── VS Code: the host restarted, we hand the conversation back ────────────

    [Fact]
    public void VsCode_SaysSo_WhenTheRestartedHostCannotBeGivenTheConversationBack()
    {
        var body = TsMethodBody(ChatViewProvider, "async onHostReady(");

        // WITNESS: this really is the rebuild — it saves the screen then loads it back.
        Assert.Contains("sessionSave('last_session'", body, StringComparison.Ordinal);
        Assert.Contains("sessionLoad('last_session'", body, StringComparison.Ordinal);

        // The sentence goes into the THREAD, where the user is looking at the conversation the
        // model has just lost — not into the output channel, which they will never open.
        Assert.Contains("this.append(", body, StringComparison.Ordinal);
        Assert.Matches(Translated, body);
    }

    [Fact]
    public void VsCode_TheRebuildNotice_CoversTheDoorThatThrowsNOTHING()
    {
        // A reload that comes back EMPTY is not an exception: `HostServer` returns null when the
        // auto-save does not belong to this workspace. That door went through no catch, so neither a
        // notice nor even a log line. The reason must therefore be decided BEFORE the catch and not
        // inside it — signature: a reason variable assigned outside the catch block.
        var body  = TsMethodBody(ChatViewProvider, "async onHostReady(");
        var start = body.IndexOf("sessionLoad('last_session'", StringComparison.Ordinal);
        Assert.True(start > 0, "the reload is gone: this test measured nothing.");

        var untilCatch = body.IndexOf("} catch", start, StringComparison.Ordinal);
        Assert.True(untilCatch > start, "the rebuild's try/catch is gone: worth checking.");

        // Entre le rechargement et le catch, la branche « revenu vide » doit nommer sa raison.
        var successPath = body[start..untilCatch];
        Assert.Matches(Translated, successPath);
    }

    // ── VS Code: the auto-save of every turn ─────────────────────────────────

    [Fact]
    public void VsCode_SaysSo_WhenTheTurnCannotBeAutoSaved()
    {
        var body = TsMethodBody(ChatViewProvider, "private autoSaveLast(");

        // WITNESS: this really is the turn's auto-save.
        Assert.Contains("sessionSave('last_session'", body, StringComparison.Ordinal);

        Assert.Contains("this.append(", body, StringComparison.Ordinal);
        Assert.Matches(Translated, body);
    }

    [Fact]
    public void VsCode_TheAutoSaveNotice_IsSaidOnceAndRearmedWhenASaveWorksAgain()
    {
        // One notice per failed turn buries the thread: the auto-save runs on EVERY turn, and its
        // causes last (a full disk, a read-only %AppData%). Said once — and re-armed as soon as a
        // save works again, otherwise "once" becomes "once in the life of the view".
        var body = TsMethodBody(ChatViewProvider, "private autoSaveLast(");

        Assert.Contains("autoSaveFailureTold = true", body, StringComparison.Ordinal);
        Assert.Contains("autoSaveFailureTold = false", body, StringComparison.Ordinal);
    }

    // ── Visual Studio: the same auto-save, the same deafness ──────────────────

    [Fact]
    public void VisualStudio_SaysSo_WhenTheTurnCannotBeAutoSaved()
    {
        var method = VmMethod("AutoSaveAsync");

        var catches = method.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        Assert.True(catches.Count > 0, "the auto-save catches nothing any more: check what replaced it.");

        var caught = string.Join("\n", catches.Select(c => c.Block.ToString()));
        Assert.Contains("SessionAutoSaveFailed", caught, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_TheAutoSaveNotice_IsSaidOnceAndRearmedWhenASaveWorksAgain()
    {
        var method = VmMethod("AutoSaveAsync");
        var body   = method.Body?.ToString() ?? string.Empty;

        Assert.Contains("_autoSaveFailureTold = true", body, StringComparison.Ordinal);
        Assert.Contains("_autoSaveFailureTold = false", body, StringComparison.Ordinal);
    }

    private static MethodDeclarationSyntax VmMethod(string name)
    {
        var path = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.PendingPrompt.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .SingleOrDefault(m => m.Identifier.Text == name);
        // WITNESS: the method this test is about still exists, under this name, in this file.
        Assert.True(method is not null, $"{name} was not found: this test would have measured nothing.");
        return method!;
    }

    // ── The sentence itself ───────────────────────────────────────────────────

    [Fact]
    public void TheAutoSaveNotice_NamesTheConversationAndTheCause()
    {
        var notice = Strings.SessionAutoSaveFailed("disk full");

        Assert.Contains("disk full", notice, StringComparison.Ordinal);
        // A notice that does not say what is at stake sends the reader looking for a setting.
        Assert.NotEqual("disk full", notice);
    }
}
