using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region UI helpers: budget, scrolling, view-model context, theme

    // ── Context budget ─────────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the context-window fill indicator from <see cref="_lastPromptTokens"/>
    /// and <see cref="InferpalConfig.ContextWindowSize"/>. Must be called on the VM thread.
    /// </summary>
    private void UpdateContextBudget()
    {
        var budget = Services.Presentation.ContextBudgetGauge.Compute(_lastPromptTokens, _config.ContextWindowSize);
        if (budget is null)
        {
            HasContextBudget = false;
            return;
        }

        ContextFillPercent   = budget.FillPercent;
        ContextBudgetColor   = budget.Color;
        // Numeric readout + the "click to open the X-Ray panel" affordance (the bar is a button).
        ContextBudgetTooltip = budget.Tooltip + "\n" + Strings.TooltipXrayGauge;
        HasContextBudget     = true;
    }

    /// <summary>
    /// Resets what was counting the conversation being replaced: the session token total (shown
    /// <b>and</b> exported), the previous turn's prompt size — the measurement the pre-send context
    /// check decides on — and the gauge derived from it. Called by <b>both</b> paths that replace
    /// the conversation, <c>/clear</c> and restoring a session or a branch. VM context.
    /// </summary>
    /// <remarks>
    /// ⚠ Only <c>/clear</c> did it. Loading a session therefore kept the counters of the
    /// conversation just left: the header and the <b>exported</b> conversation announced a token
    /// total belonging to ANOTHER conversation, and above all <see cref="ContextManager"/> decided
    /// on a foreign measurement — either compacting a short conversation just opened (a model call
    /// for nothing, turns thrown away), or failing to bound a long one and letting the backend cut
    /// off its head, system prompt included, without anything saying so. The VS Code host has held
    /// this property since it existed (<c>HostSession.History</c> zeroes <c>LastPromptTokens</c> on
    /// assignment, with that same justification word for word): it was the <b>main</b> front-end
    /// that lacked it.
    ///
    /// ⚠ The build banner is not part of this: it describes the <b>solution</b>, not the
    /// conversation. <c>/clear</c> dismisses it because that is a reset gesture; a reloaded session
    /// keeps it because the build is still broken.
    /// </remarks>
    private void ResetTurnAccounting()
    {
        _sessionTokens     = 0;
        _lastPromptTokens  = 0;
        TokenInfo          = string.Empty;
        ContextFillPercent = 0;
        ContextBudgetColor = "#606060";
        UpdateContextBudget();   // → HasContextBudget = false until a prompt has been measured
    }

    // ── Scroll helpers ─────────────────────────────────────────────────────────

    // Alternates between two invisible anchor items so SelectedItem always changes.
    // VS Remote UI batches PropertyChanged; the null→item trick collapses to one value.
    // With two anchors each call sends a genuinely different object.
    // ⚠ Selection alone NEVER scrolls a WPF ListBox (no BringIntoView code path exists in
    // Selector/ListBox/ListBoxItem — verified against dotnet/wpf sources). The actual
    // scrolling happens in-process: ChatAutoScroller (GhostText) class-handles
    // ListBoxItem.Selected on the tagged chat list and calls BringIntoView, and follows
    // content growth during streaming. This method is therefore only the cross-process
    // "scroll to bottom" signal; without the in-proc side it is a no-op.
    private void ScrollToBottom()
    {
        if (Messages.Count < 2) return;
        _scrollToggle = !_scrollToggle;
        var target = _scrollToggle ? _anchor0 : _anchor1;
        Post(() => ScrollTarget = target);
    }

    // ── VM context helpers ─────────────────────────────────────────────────────

    // Fire-and-forget: used for onToken/onStep callbacks during streaming.
    private void Post(Action action) =>
        SynchronizationContext.Post(_ =>
        {
            try { action(); }
            catch (Exception ex) { Diagnostics.Swallow("UiHelpers.Post", ex); }
        }, null);

    // Awaitable: ensures the action runs with SynchronizationContext.Current = our context.
    // Because NonConcurrentSynchronizationContext is FIFO, awaiting this after RunAgentAsync
    // guarantees all prior Post() calls (onToken etc.) have already completed.
    /// <summary>
    /// Cancels the turn in flight (if any) and waits for it to unwind, so the caller may replace
    /// the conversation. No-op when nothing is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the wait, and not just a cancel.</b> Loading a session, <c>/clear</c> and
    /// <c>/branch</c> all replace <c>_history</c> and refill <c>Messages</c>. Done under a running
    /// agent loop, that is the race the host side closed with its turn slot (revue pré-1.6.0,
    /// §2.6) and that this front-end — the primary one — never had: the loop holds the OLD list, so
    /// its answer is appended to the freshly restored conversation when it lands, and the render
    /// pass looks for a streaming bubble that <c>Messages.Clear()</c> has already removed
    /// (<c>IndexOf</c> = -1 ⇒ an insert at -1, caught as a bare error bubble in the wrong
    /// conversation).
    /// </para>
    /// <para>
    /// Same shape as <c>RunPendingTurnAsync</c>, which already does this for code actions: cancel,
    /// await the turn's own completion signal, with a belt so a turn that never finalises cannot
    /// make the session panel permanently dead.
    /// </para>
    /// </remarks>
    private async Task SettleCurrentTurnAsync()
    {
        Task? previousTurn = null;
        await RunOnVMContextAsync(() =>
        {
            if (!IsLoading) return;
            previousTurn = _turnDone?.Task;
            _currentCts?.Cancel();
        });

        if (previousTurn is null) return;
        // VSTHRD003 as in RunPendingTurnAsync: the TCS completes on this VM's
        // NonConcurrentSynchronizationContext in an out-of-process host — no JTF to deadlock on.
#pragma warning disable VSTHRD003
        await Task.WhenAny(previousTurn, Task.Delay(TimeSpan.FromSeconds(15)));
#pragma warning restore VSTHRD003
    }

    private Task RunOnVMContextAsync(Action action)
    {
        // RunContinuationsAsynchronously is load-bearing: without it, SetResult runs the awaiting
        // caller's continuation INLINE on the VM pump's worker — so all the "off-context" code
        // after each `await RunOnVMContextAsync(...)` (history building, clipboard Join…) executed
        // on the pump, behind which the streaming Post()s pile up (pre-1.6.0 architecture review, §2.4).
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext.Post(_ =>
        {
            try   { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    // ── Theme ──────────────────────────────────────────────────────────────────

    private async Task InitThemeAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            _themeSubscription = await extensibility.Settings().SubscribeAsync(
                ColorThemeId,
                CancellationToken.None,
                value => Post(() =>
                    ApplyThemeColors(VsThemeDetector.IsDark(value.ValueOrDefault(string.Empty)))));
        }
        catch (Exception ex) { Diagnostics.Swallow("UiHelpers.Theme", ex); }
    }

    private void ApplyThemeColors(bool isDark)
    {
        _isDark          = isDark;
        VsThemeDetector.CurrentIsDark = isDark;
        var p            = ThemePalette.For(isDark);
        ThemeWindowBg    = p.WindowBg;
        ThemeText        = p.Text;
        ThemeSubtleText  = p.SubtleText;
        ThemeCodeBg      = p.CodeBg;
        ThemeCodeText    = p.CodeText;
        ThemeCodeBorder  = p.CodeBorder;
        ThemeBorder      = p.Border;
        ThemeSessionBg   = p.SessionBg;
        ThemePanelBg     = p.PanelBg;
        ThemeInputBg     = p.InputBg;
        ThemeInputBorder = p.InputBorder;
        ThemeHoverBg     = p.HoverBg;
        UpdateMessageBubbles();
    }

    /// <summary>
    /// Themes a chat item and inserts it just before the two scroll anchors — the only correct way
    /// to add a bubble. Returns it, so a caller that needs the reference keeps one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The funnel exists because the two steps kept coming apart.</b> Fifteen sites built the
    /// item <em>inline inside</em> the <c>Messages.Insert(…)</c> call, which makes theming
    /// impossible — there is no reference to theme — so those bubbles rendered with default colours
    /// under a dark theme. The pre-1.6.0 review had already fixed four of them one by one, in
    /// <c>ChatTurn</c>, and the comment it left there ("every other insertion themes; these four
    /// didn't") was true of that file only.
    /// </para>
    /// <para>
    /// Must run on the VM context, like every <c>Messages</c> mutation.
    /// </para>
    /// </remarks>
    private ChatMessageItem InsertThemed(ChatMessageItem item)
    {
        ApplyItemTheme(item);
        Messages.Insert(Messages.Count - 2, item);
        return item;
    }

    private void ApplyItemTheme(ChatMessageItem item)
    {
        var p = ThemePalette.For(_isDark);

        item.BubbleBackground = p.BubbleBackground(item.Role);
        item.ThemeText        = p.Text;
        item.ThemeSubtleText  = p.BubbleSubtleText;
        item.ThemeToolText    = p.BubbleToolText;
        item.ThemeCodeText    = p.CodeText;
        item.ThemeCodeBg      = p.CodeBg;
        item.ThemeCodeBorder  = p.CodeBorder;

        foreach (var b in item.Blocks)
        {
            b.ThemeText       = item.ThemeText;
            b.ThemeCodeText   = item.ThemeCodeText;
            b.ThemeCodeBg     = item.ThemeCodeBg;
            b.ThemeCodeBorder = item.ThemeCodeBorder;
            foreach (var run in b.Inlines)
                run.Foreground = run.IsCode ? item.ThemeCodeText : item.ThemeText;
        }

        // Diff viewer: green-background additions / red-background deletions per the active theme.
        DiffLine.ApplyTheme(item.DiffLines, p);
    }

    private void UpdateMessageBubbles()
    {
        foreach (var m in Messages)
            ApplyItemTheme(m);
    }

    #endregion
}
