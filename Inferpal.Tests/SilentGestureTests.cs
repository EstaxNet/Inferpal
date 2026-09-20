using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A VS Code gesture that needs the host says when it cannot run — it never returns in silence.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The rule is the one <c>chatViewProvider.ts</c> states for its own mentions, and it held for
/// exactly one of its readers. Measured on the file: <b>eight</b> gestures left on a bare
/// <c>if (!host?.isRunning) return;</c> — the X-Ray panel and its toggles, pin and unpin, the file
/// search behind an <c>@</c> mention, the model picker and the agent-mode switch — and <b>six</b>
/// whose failure only reached the output channel. The model picker is the one that lies rather than
/// stalls: the webview shows the model the user chose while Inferpal answers with the previous one,
/// which is the cost that method's own remark already states for the branch it does cover.
/// </para>
/// <para>
/// ⚠ A source scan with witnesses, like <c>ArchiveFailureSilenceTests</c>: these paths need a VS
/// Code extension host, so the suite cannot execute them. Background work — bootstrap, polling,
/// pushing state after a change nobody asked for — keeps the bare test on purpose, and is named
/// here rather than left to a reader's judgement.
/// </para>
/// </remarks>
public sealed class SilentGestureTests
{
    private static string Source() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts"));

    /// <summary>The bare guard, as it reads when nothing is said.</summary>
    private const string BareGuard = "if (!host?.isRunning) {";

    /// <summary>The three paths that run without anyone asking, where a toast would be noise.</summary>
    private static readonly string[] BackgroundPaths = ["onHostReady", "configSaved", "pollBackendStatus"];

    [Fact]
    public void EveryGestureThatNeedsTheHost_SaysWhenItCannotRun()
    {
        var src = Source();

        // WITNESS: the funnel exists, and the file really is the one that carries these gestures.
        Assert.Contains("private hostForGesture()", src);
        Assert.Contains("case 'xrayToggle'", src);
        Assert.Contains("case 'pinActive'", src);

        var bare = Regex.Matches(src, Regex.Escape(BareGuard), RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.NotEmpty(bare);   // witness: the pattern still exists in this file at all

        foreach (Match m in bare)
        {
            var before = src[..m.Index];
            // ⚠ The method pattern must not require `private`: `onHostReady` and
            // `resetConversation` are public, and matching only the private ones attributes their
            // guard to the method above — the scan then names the wrong site, which is worse than
            // naming none.
            var owner  = Regex.Matches(before, @"(?:case '(?<c>[a-zA-Z]+)':|^  (?:private |public )?(?:async )?(?<m>[A-Za-z0-9_]+)\()",
                                       RegexOptions.Multiline, TimeSpan.FromSeconds(5));
            var last   = owner.Count > 0 ? owner[^1] : null;
            var name   = last?.Groups["c"].Success == true ? last.Groups["c"].Value
                       : last?.Groups["m"].Value ?? "?";

            // The guard may stay bare in background work, and in the one per-keystroke path, which
            // says it once instead (a toast per keystroke is the noise that stops being read).
            var allowed = Array.IndexOf(BackgroundPaths, name) >= 0 || name == "mentionSearch";
            // ⚠ Wide enough to clear a comment: the branch that says it may explain WHY first, and
            // a window cut to the guard itself reports a site that does speak as if it did not.
            var body    = src.Substring(m.Index, Math.Min(700, src.Length - m.Index));

            Assert.True(allowed || body.Contains("sayOnce") || body.Contains("hostUnavailable"),
                        $"gesture '{name}' returns without saying why the host could not serve it");
        }
    }

    [Fact]
    public void AGestureThatFailed_ReachesTheUser_NotOnlyTheOutputChannel()
    {
        var src = Source();

        // The gestures whose failure used to stop at this.log(...). Named, because "is this a
        // gesture?" is not a syntactic question — the same reason the editor-surface rule is an
        // assertion rather than a scan.
        foreach (var what in new[] { "cancel", "resumeStep", "xray/panel",
                                     "model pick → host config", "agent mode → host config",
                                     "branch", "branch command", "branch switch" })
        {
            Assert.Contains($"this.gestureFailed('{what}', err)", src);
        }

        // WITNESS: the funnel shows something, rather than logging under another name.
        var funnel = Regex.Match(src, @"private gestureFailed\([\s\S]{0,400}?\n  \}",
                                 RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(funnel.Success, "gestureFailed was not found — the scan would pass on nothing");
        Assert.Contains("showWarningMessage", funnel.Value);
        Assert.Contains("this.log(", funnel.Value);   // the log line is kept, not replaced
    }

    /// <summary>The per-keystroke path says it once, and re-arms when a search works again.</summary>
    [Fact]
    public void TheSearchBehindAMention_SaysItOnce_AndRearms()
    {
        var src = Source();

        var block = Regex.Match(src, @"case 'mentionSearch': \{[\s\S]*?\n      \}",
                                RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(block.Success, "the mentionSearch case was not found");

        Assert.Contains("this.sayOnce('mentionSearch'", block.Value);
        Assert.Contains("this.saidOnce.delete('mentionSearch')", block.Value);   // re-armed on success
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "vscode", "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
