using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A rebuilt VS Code webview does not lose what it was showing: an approval card lives exactly as
/// long as the question it asks, the pause banner as long as the pause, and a hidden form keeps what
/// is not saved yet.
/// </summary>
/// <remarks>
/// <para>
/// The webview does not keep cards in the transcript: <c>renderTranscript</c> empties the message
/// list and rebuilds it from the bubbles. Any rehydration while an approval waits (saving the
/// settings, a backend connection edge, a webview reload) therefore wiped the card while the host
/// still waits for its answer — with no timeout: the agent stays blocked on a question nobody can
/// see any more. Saving the settings is the most likely gesture at that moment (adding the
/// <c>allow</c> rule you are being asked about).
/// </para>
/// <para>
/// Reverse half: a turn whose request fails (host crashed, host restarted) leaves a card that
/// answers into the void — the §27.5 ghost card, through the crash path instead of cancellation.
/// </para>
/// <para>
/// ⚠ These rules read the source: the extension has no TypeScript test runner.
/// </para>
/// </remarks>
public class WebviewRebuildTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string TsCode(string relative)
    {
        var path = Path.Combine(RepoRoot(), "vscode", "src",
                                relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"vscode/src/{relative} is gone.");
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    /// <summary>Body of a TypeScript function, by brace matching from its signature.</summary>
    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{signature}\" not found — the rule no longer measures anything.");

        var open  = source.IndexOf('{', at + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }
        Assert.Fail($"body of \"{signature}\" never closes.");
        return string.Empty;
    }

    [Fact]
    public void AHydratedView_ShowsTheApprovalCardsStillWaiting()
    {
        var provider = TsCode("chatViewProvider.ts");
        var webview  = TsCode("webview/main.ts");

        // Witness: rehydration does wipe everything the transcript does not carry.
        Assert.Contains("messagesEl.textContent = ''", Body(webview, "function renderTranscript("),
                        StringComparison.Ordinal);

        var hydrate = Body(provider, "private hydrate(): void");
        Assert.Contains("type: 'hydrate'", hydrate, StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(hydrate, @"this\.pendingApprovals[\s\S]{0,200}?type: 'approval'"),
            "hydrate() does not re-post the cards still waiting: a rehydration during an approval "
            + "wipes the card and leaves the agent waiting for an answer nobody can give.");
    }

    /// <summary>
    /// Same class, another element the transcript does not carry: the step-by-step banner, the only
    /// place that holds the Resume button. Wiped during a pause, it leaves only cancellation.
    /// </summary>
    [Fact]
    public void AHydratedView_ShowsTheStepPauseStillInForce()
    {
        var provider = TsCode("chatViewProvider.ts");

        // Witness: the pause is relayed to the webview, and the Resume button lives in its banner.
        Assert.Contains("type: 'stepPaused'", provider, StringComparison.Ordinal);
        Assert.Contains("type: 'resumeStep'", TsCode("webview/main.ts"), StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(Body(provider, "private hydrate(): void"),
                                  @"this\.stepPaused[\s\S]{0,120}?type: 'stepPaused'"),
            "hydrate() does not re-post the pause banner: a rehydration during a step-by-step pause "
            + "removes the only Resume button.");

        // And the banner does not outlive its turn (cancelled while paused, host crashed): the
        // webview removes it on `stepResumed` only, not on `turnEnded`.
        Assert.True(Regex.IsMatch(Body(provider, "private async chatTurn("),
                                  @"finally[\s\S]*?this\.stepPaused[\s\S]{0,160}?type: 'stepResumed'"),
            "The end of the turn does not retire the pause banner: a turn cancelled during the pause "
            + "leaves a Resume button that resumes nothing.");
    }

    /// <summary>
    /// Same class, in the settings panel: it opens as an editor tab, and VS Code destroys a hidden
    /// tab's webview unless asked to keep it. On return the form reloads from the config and unsaved
    /// edits vanish without a word — clicking Save afterwards writes the old values.
    /// </summary>
    [Fact]
    public void TheSettingsForm_KeepsUnsavedEditsWhileItsTabIsHidden()
    {
        var panel = TsCode("settingsPanel.ts");
        var form  = TsCode("webview/settings.ts");

        // Witness: the form writes only on an explicit Save, so it does hold unsaved state — and it
        // does not keep that state in the webview state itself.
        Assert.Contains("type: 'save'", form, StringComparison.Ordinal);
        Assert.DoesNotContain("setState(", form, StringComparison.Ordinal);

        var created = Regex.Match(panel, @"createWebviewPanel\(([\s\S]*?)\);");
        Assert.True(created.Success, "createWebviewPanel not found — the rule no longer measures anything.");

        Assert.True(Regex.IsMatch(created.Groups[1].Value, @"retainContextWhenHidden:\s*true"),
            "The settings panel does not keep its hidden webview: switching tabs erases unsaved edits.");
    }

    [Fact]
    public void AFailedTurn_RetiresItsApprovalCards()
    {
        var chatTurn = Body(TsCode("chatViewProvider.ts"), "private async chatTurn(");

        // Witness: the turn still has its failure path.
        var catchAt = chatTurn.IndexOf("catch (err)", StringComparison.Ordinal);
        Assert.True(catchAt >= 0, "chatTurn has no catch any more — the rule no longer measures anything.");

        Assert.True(chatTurn[catchAt..].Contains("this.dismissAllPending()", StringComparison.Ordinal),
            "A turn whose request failed (host crashed or restarted) leaves its approval cards "
            + "clickable, while nobody waits for their answer any more.");
    }
}
