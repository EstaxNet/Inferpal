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
    #region Helpers UI : budget, défilement, contexte VM, thème

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
        ContextBudgetTooltip = budget.Tooltip;
        HasContextBudget     = true;
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
            catch { }
        }, null);

    // Awaitable: ensures the action runs with SynchronizationContext.Current = our context.
    // Because NonConcurrentSynchronizationContext is FIFO, awaiting this after RunAgentAsync
    // guarantees all prior Post() calls (onToken etc.) have already completed.
    private Task RunOnVMContextAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
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
        catch { }
    }

    private void ApplyThemeColors(bool isDark)
    {
        _isDark          = isDark;
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
    }

    private void UpdateMessageBubbles()
    {
        foreach (var m in Messages)
            ApplyItemTheme(m);
    }

    #endregion
}
