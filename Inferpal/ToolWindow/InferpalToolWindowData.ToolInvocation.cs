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
    #region Tool invocation & export

    private async Task InvokeToolAsync(string toolName, object argsObj, CancellationToken ct, string? attachAs = null)
    {
        string    result;
        DiffInfo? diff = null;
        try
        {
            var json = JsonSerializer.Serialize(argsObj);
            var args = JsonDocument.Parse(json).RootElement.Clone();
            result   = SlashToolAction.Shown(await _tools.ExecuteAsync(toolName, args, ct));
            diff     = _tools.ConsumeDiff();
        }
        catch (Exception ex)
        {
            result = Strings.MsgError(ex.Message);
        }

        if (attachAs is not null)
        {
            await RunOnVMContextAsync(() => AddAttachment(attachAs, result));
            return;
        }

        var capturedDiff = diff;
        await RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.ToolMsg(toolName, result, expanded: true);
            if (capturedDiff is not null)
                item.InitDiff(capturedDiff);
            ApplyItemTheme(item);
            bool buildFailed = toolName == GetDiagnosticsTool.ToolName && GetDiagnosticsTool.OutputHasErrors(result);
            if (buildFailed)
                item.InitFixCallback(result, rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
            Messages.Insert(Messages.Count - 2, item);

            // When the build fails, insert a visible assistant bubble that proposes to fix it.
            // The button pre-fills "/fix-build" so the user just presses Enter to start the
            // automated fix loop, rather than having to discover the button inside the tool bubble.
            if (buildFailed)
            {
                var errorCount = result
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => GetDiagnosticsTool.ErrorLineRegex.IsMatch(l));
                var proposal = ChatMessageItem.NoticeMsg(Strings.BuildFailedProposal(errorCount));
                proposal.InitFixCallback(result, _ => Post(() => Prompt = "/fix-build"));
                ApplyItemTheme(proposal);
                Messages.Insert(Messages.Count - 2, proposal);
            }

            ScrollToBottom();
        });
    }

    /// <summary>
    /// Finalizes a streaming bubble (must run on the VM context): stops streaming —
    /// which triggers ParseMarkdown — then either discards the bubble when it is
    /// visually empty (returns <c>null</c>; see <see cref="Services.Agent.ChatTurnPolicy.IsVisiblyEmpty"/>)
    /// or themes it and returns it. The one place that guard lives, rather than copy-pasted at
    /// every stream completion/cancel/error.
    /// </summary>
    private ChatMessageItem? FinalizeStreamingBubble(ChatMessageItem? item)
    {
        if (item is null) return null;
        item.IsStreaming = false;
        if (Services.Agent.ChatTurnPolicy.IsVisiblyEmpty(item.Content))
        {
            var idx = Messages.IndexOf(item);
            if (idx >= 0) Messages.RemoveAt(idx);
            return null;
        }
        ApplyItemTheme(item);
        return item;
    }

    /// <summary>
    /// The visible part of a FAILED turn's answer stays on screen as a notice: what the model wrote before the failure,
    /// never its answer. Kept as an answer, it was saved as one, and a reload handed the model half a reply as what it
    /// had said; VS Code keeps it the same way.
    /// </summary>
    private static void KeepAsNotice(ChatMessageItem? partial)
    {
        if (partial is not null) partial.ToolName = Services.Persistence.SessionManager.NoticeMarker;
    }

    private void MarkRegeneratable(ChatMessageItem msg)
    {
        if (_lastRegenerableMsg is not null)
            _lastRegenerableMsg.IsRegeneratable = false;
        _lastRegenerableMsg = msg;
        msg.InitRegenerateCallback(() => Post(() => _ = RegenerateAsync()));
    }

    /// <summary>Offers Continue on this end notice — and takes it off the one before.</summary>
    private void MarkContinuable(ChatMessageItem notice)
    {
        ForgetContinuable();
        _lastContinuableMsg = notice;
        notice.InitContinueCallback(() => Post(() => _ = ContinueAsync()));
    }

    /// <summary>A turn starts, or the conversation is replaced: no end notice offers Continue any more.</summary>
    private void ForgetContinuable()
    {
        if (_lastContinuableMsg is not null) _lastContinuableMsg.IsContinuable = false;
        _lastContinuableMsg = null;
    }

    /// <summary>
    /// Continue: the question "ask it to continue" would type, sent as one — by the funnel of every typed question, so the
    /// backend-down refusal, the turn's claim and the session save hold for it as for the others.
    /// </summary>
    /// <remarks>⚠ Not a resumed run: a new turn on the conversation as it stands, which the stable start of the request
    /// lets the server answer without reading it again.</remarks>
    private async Task ContinueAsync()
    {
        var claim = 0;
        string? oneTimeModel = null;
        await RunOnVMContextAsync(() =>
        {
            if (_sendStarting || IsLoading || _currentCts is not null || _lastContinuableMsg is null) return;
            // A code action's answer continues as one — its model, without tools — as Regenerate replays it.
            oneTimeModel = _lastSent?.Model;
            if (!_isBackendReachable)
            {
                InsertThemed(ChatMessageItem.NoticeMsg(UnreachableText()));
                ScrollToBottom();
                return;
            }
            ForgetContinuable();
            _sendStarting = true;
            claim = ++_sendClaim;
        });
        if (claim == 0) return;
        try
        {
            await SendCoreAsync(Strings.ContinueQuestion, oneTimeModel, attachments: [], CancellationToken.None,
                                clearPrompt: false, typed: false);
        }
        finally
        {
            await RunOnVMContextAsync(() => { if (_sendClaim == claim) _sendStarting = false; });
        }
    }

    private async Task RegenerateAsync()
    {
        // Full turn guard, not just _currentCts: between SendAsync's claim and SendCoreAsync taking
        // the turn, only _sendStarting says a turn is starting — a regenerate click there truncated
        // Messages/_history under it. Runs on the VM context (invoked via Post), so reading the claim
        // here is safe.
        if (_sendStarting || IsLoading || _currentCts is not null) return;

        // Checked BEFORE anything is removed: with the backend down, the resend below stops at
        // SendCoreAsync's pre-flight, and the question it was about to resend was already gone.
        if (!_isBackendReachable)
        {
            InsertThemed(ChatMessageItem.NoticeMsg(UnreachableText()));
            ScrollToBottom();
            return;
        }

        // Find the last user message in the visible list (before the two anchors at the end)
        ChatMessageItem? lastUserItem = null;
        var lastUserIdx = -1;
        for (var i = Messages.Count - 3; i >= 0; i--)
        {
            // A note typed during the run is not the question: the question is above it.
            if (Messages[i].Role == "user" && !Messages[i].IsNote) { lastUserItem = Messages[i]; lastUserIdx = i; break; }
        }
        if (lastUserItem is null) return;
        // The notes its run read are part of the question in the history: asked again, it carries them.
        List<string> notes = [..Messages.Skip(lastUserIdx + 1).Where(m => m.IsNote && m.NoteRead).Select(m => m.Content)];

        // ⚠ What the question was sent WITH, when the bubble is that turn's: resent from its words alone, a /explain or a
        // context-menu action lost its code and became an ordinary agent turn (agent model, tools, approvals), and a
        // question lost the file attached to it. A bubble from a restored session has only its words.
        var (userText, oneTimeModel, attachments) = _lastSent is { } sent && ReferenceEquals(sent.Question, lastUserItem)
            ? (sent.Text, sent.Model, sent.Attachments)
            : (lastUserItem.Content, null, []);

        // Remove everything from the user message onward (keep only what came before)
        while (Messages.Count - 2 > lastUserIdx)
            Messages.RemoveAt(Messages.Count - 3);

        // Forget the current regenerate handle — the new response will set a fresh one
        if (_lastRegenerableMsg is not null)
        {
            _lastRegenerableMsg.IsRegeneratable = false;
            _lastRegenerableMsg = null;
        }

        // Roll _history back to just before the last user turn (_history[0] is always the system prompt) — only when that
        // turn IS this bubble's: a question stopped during its context build never entered the history, and the last
        // user turn there is the one before, which the screen still shows. A restored bubble's question is in it.
        var inHistory = !(_lastSent is { } last && ReferenceEquals(last.Question, lastUserItem))
                     || ReferenceEquals(_questionInHistory, lastUserItem);
        if (inHistory)
        {
            for (var i = _history.Count - 1; i >= 1; i--)
            {
                if (_history[i].Role == "user") { _history.RemoveRange(i, _history.Count - i); break; }
            }
        }
        // The workspace block rode with the question taken back: the question asked again carries it again.
        if (!Services.Prompting.WorkspaceContext.IsIn(_history)) _workspaceContextInjected = false;

        await SendCoreAsync(userText, oneTimeModel, attachments, ct: CancellationToken.None, clearPrompt: false, notes: notes);
    }

    private Task ShowInfoAsync(string markdown) =>
        RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.NoticeMsg(markdown);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
        });

    private Task ExportAsync(object? _, CancellationToken ct) => ExportConversationAsync(ct);

    private async Task ExportConversationAsync(CancellationToken ct)
    {
        List<Services.Persistence.ExportMessage> snapshot = [];
        await RunOnVMContextAsync(() =>
        {
            snapshot = Messages
                .Where(m => m.Role is "user" or "assistant" or "tool")
                // A note is not a turn of its own: the export counts the questions.
                .Select(m => new Services.Persistence.ExportMessage(m.Role, m.Label, m.Content, m.Timestamp, IsNotice: m.IsNote))
                .ToList();
        });

        if (snapshot.Count == 0)
        {
            await ShowInfoAsync(Strings.ExportNoMessages);
            return;
        }

        // Capture session stats on VM context
        int    sessionTokens = 0;
        string modelName     = string.Empty;
        await RunOnVMContextAsync(() =>
        {
            sessionTokens = _sessionTokens;
            modelName     = _config.DefaultModel;
        });

        // ⚠ Opening the dialog is INSIDE the try: outside it, its failure escaped
        // Strings.ExportFailed and rose into the command machinery, where nobody renders it.
        try
        {
            string? filePath = await ShowSaveFileDialogAsync();
            if (filePath is null) return;

            var isTxt    = Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
            var date     = DateTime.Now.ToString("f", CultureInfo.CurrentCulture);
            var duration = _sessionStartTime.HasValue
                ? (DateTime.Now - _sessionStartTime.Value) : (TimeSpan?)null;

            var document = Services.Persistence.ConversationExporter.Build(
                snapshot, isTxt, modelName, sessionTokens, date,
                Services.Persistence.ConversationExporter.FormatDuration(duration));

            await File.WriteAllTextAsync(filePath, document, System.Text.Encoding.UTF8, ct);
            await ShowInfoAsync(Strings.ExportSuccess(Path.GetFileName(filePath)));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await ShowInfoAsync(Strings.ExportFailed(m));
        }
    }

    private static Task<string?> ShowSaveFileDialogAsync() =>
        StaDialog.RunAsync(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = Strings.DialogExportTitle,
                Filter     = Strings.DialogExportFilter,
                DefaultExt = ".md",
                FileName   = $"conversation_{DateTime.Now:yyyy-MM-dd_HHmm}",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }, "ExportDialog");

    // EmptyToolRegistry is now internal and lives in Inferpal.Services (see EmptyToolRegistry.cs).

    #endregion
}
