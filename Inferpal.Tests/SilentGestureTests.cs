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

    /// <summary>
    /// Every file that receives a gesture from a webview — derived, never listed.
    /// </summary>
    /// <remarks>
    /// ⚠ The discriminator is <c>onDidReceiveMessage</c>, never a name: named, the rule covers the
    /// view it happens to be written on and leaves the settings panel — the other half of the
    /// product's own UI — outside, where a ↻ button whose failure reaches the output channel alone
    /// sits eight lines under a remark spelling out that exact cost for the branch beside it. A
    /// panel added tomorrow inherits the rule.
    /// </remarks>
    private static IEnumerable<(string Name, string Text)> GestureSources()
    {
        var dir   = Path.Combine(RepoRoot(), "vscode", "src");
        var found = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.ts", SearchOption.TopDirectoryOnly))
        {
            // The extension decides, not the pattern: on Windows a `*.ts` filter also answers with
            // `.tsx` files through 8.3 short-name matching — the shape that made a `*.sln` search
            // return a `.slnx` here.
            if (!string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(path);
            if (!text.Contains("onDidReceiveMessage", StringComparison.Ordinal)) continue;
            found++;
            yield return (Path.GetFileName(path), text);
        }

        // WITNESS: the chat view and the settings panel both receive gestures, so a lower count
        // means the discriminator stopped finding one — a renamed folder, another way of
        // subscribing — and the scan sweeps it no more, green.
        Assert.True(found >= 2,
            $"Only {found} webview host(s) discovered under vscode/src: the scan no longer reads them all.");
    }

    /// <summary>The bare guard, as it reads when nothing is said.</summary>
    private const string BareGuard = "if (!host?.isRunning) {";

    /// <summary>
    /// Paths whose bare host guard may stay silent.
    /// </summary>
    /// <remarks>
    /// ⚠ The discriminator is <b>whether a person is waiting for an answer</b>, not whether the code
    /// started by itself. <c>onHostReady</c> and <c>pollBackendStatus</c> run unprompted, so a toast
    /// there is noise. <c>configSaved</c> does NOT: it runs because the user pressed Save — it is
    /// exempt only because the settings panel <b>is already answering that same person</b>
    /// (<see cref="TheSettingsPanel_NamesWhatFailed_RatherThanBlamingTheBackend"/>).
    /// ⚠ Calling it "background" is what hid its three silent catches from the rule above for so
    /// long: the wrong reason for a right exemption still costs, because the next reader applies
    /// the reason and not the entry.
    /// </remarks>
    private static readonly string[] BackgroundPaths = ["onHostReady", "configSaved", "pollBackendStatus"];

    [Fact]
    public void EveryGestureThatNeedsTheHost_SaysWhenItCannotRun()
    {
        // WITNESS: the funnel exists, and the file really is the one that carries these gestures.
        Assert.Contains("private hostForGesture()", Source());
        Assert.Contains("case 'xrayToggle'", Source());
        Assert.Contains("case 'pinActive'", Source());

        var seen = 0;
        foreach (var (file, src) in GestureSources())
        foreach (Match m in Regex.Matches(src, Regex.Escape(BareGuard), RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            seen++;
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
                        $"{file}: gesture '{name}' returns without saying why the host could not serve it");
        }

        // WITNESS: the guard's spelling is what the scan recognises — reworded, it would judge
        // nothing while staying green.
        Assert.True(seen >= 8, $"Only {seen} host guard(s) read across the webview hosts: the pattern is dead.");
    }

    [Fact]
    public void AGestureThatFailed_ReachesTheUser_NotOnlyTheOutputChannel()
    {
        var src = Source();

        // The gestures whose failure used to stop at this.log(...). Named, because "is this a
        // gesture?" is not a syntactic question — the same reason the editor-surface rule is an
        // assertion rather than a scan.
        // ⚠ The last three are `configSaved`'s, and they were invisible because TWO rules agreed on
        // the same misclassification: this list did not name them, and `BackgroundPaths` below
        // called that method a path "that runs without anyone asking" — while its own doc says
        // "called after the settings panel SAVED". Two rules agreeing is not two checks.
        foreach (var what in new[] { "cancel", "resumeStep", "xray/panel",
                                     "model pick → host config", "agent mode → host config",
                                     "branch", "branch command", "branch switch",
                                     "model setting", "agent mode setting", "settings reload" })
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

    /// <summary>The settings panel's ↻ button, and the probe beside it.</summary>
    /// <remarks>
    /// An assertion by name rather than a rule: "does this catch belong to a gesture?" is not a
    /// syntactic question, and the sibling catches of the same handler degrade on purpose — an
    /// unreachable backend does not throw, it answers an empty model list through the success
    /// path, and the popup is what names that.
    /// </remarks>
    [Fact]
    public void TheSettingsPanel_NamesWhatFailed_RatherThanBlamingTheBackend()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "settingsPanel.ts"));

        var refresh = Regex.Match(src, @"case 'refreshModels': \{[\s\S]*?\n      \}",
                                  RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(refresh.Success, "the refreshModels case was not found — the assertion reads nothing");

        // ⚠ The subject is the CATCH, not the case: the no-host branch at the top of the same case
        // already posts an error, and an assertion on the whole case is green while the catch says
        // nothing — measured, on the very defect this test exists for.
        var caught = Regex.Match(refresh.Value, @"catch \(err\) \{[\s\S]*?\n        \}",
                                 RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(caught.Success, "the refreshModels catch was not found — the assertion reads nothing");
        Assert.Contains("models/list failed", caught.Value);   // the cause still reaches the channel
        Assert.Contains("this.post(", caught.Value);           // and the clicker hears about it

        // ⚠ `ok: false` alone renders as "Backend unreachable": the wrong cause when the host is
        // what is gone, and the user goes to check a server that is answering. Both of the probe's
        // branches reach it — the missing host, and the RPC that threw (an unreachable backend does
        // not throw, it answers `ok: false` through the success path).
        var probe = Regex.Match(src, @"case 'testConnection': \{[\s\S]*?\n      \}",
                                RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(probe.Success, "the testConnection case was not found");
        Assert.Contains("hostUnavailableMessage()", probe.Value);

        var probeCaught = Regex.Match(probe.Value, @"catch \(err\) \{[\s\S]*?\n        \}",
                                      RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(probeCaught.Success, "the testConnection catch was not found — the assertion reads nothing");
        Assert.Contains("hostErrorText(err)", probeCaught.Value);
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
