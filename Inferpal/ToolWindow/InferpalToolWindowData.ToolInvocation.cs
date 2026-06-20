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
    #region Invocation d'outils & export

    private async Task InvokeToolAsync(string toolName, object argsObj, CancellationToken ct, string? attachAs = null)
    {
        string    result;
        DiffInfo? diff = null;
        try
        {
            var json = JsonSerializer.Serialize(argsObj);
            var args = JsonDocument.Parse(json).RootElement.Clone();
            result   = await _tools.ExecuteAsync(toolName, args, ct);
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
                var proposal = ChatMessageItem.AssistantMsg(Strings.BuildFailedProposal(errorCount));
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
    /// visually empty (returns <c>null</c>; see <see cref="Services.Agent.ChatTurnPolicy.IsVisiblyEmpty"/>
    /// and the empty-bubble bug history) or themes it and returns it. Replaces the
    /// triple guard that used to be copy-pasted at every stream completion/cancel/error.
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

    private void MarkRegeneratable(ChatMessageItem msg)
    {
        if (_lastRegenerableMsg is not null)
            _lastRegenerableMsg.IsRegeneratable = false;
        _lastRegenerableMsg = msg;
        msg.InitRegenerateCallback(() => Post(() => _ = RegenerateAsync()));
    }

    private async Task RegenerateAsync()
    {
        if (_currentCts is not null) return;

        // Find the last user message in the visible list (before the two anchors at the end)
        ChatMessageItem? lastUserItem = null;
        var lastUserIdx = -1;
        for (var i = Messages.Count - 3; i >= 0; i--)
        {
            if (Messages[i].Role == "user") { lastUserItem = Messages[i]; lastUserIdx = i; break; }
        }
        if (lastUserItem is null) return;

        var userText = lastUserItem.Content;

        // Remove everything from the user message onward (keep only what came before)
        while (Messages.Count - 2 > lastUserIdx)
            Messages.RemoveAt(Messages.Count - 3);

        // Forget the current regenerate handle — the new response will set a fresh one
        if (_lastRegenerableMsg is not null)
        {
            _lastRegenerableMsg.IsRegeneratable = false;
            _lastRegenerableMsg = null;
        }

        // Roll _history back to just before the last user turn (_history[0] is always the system prompt)
        for (var i = _history.Count - 1; i >= 1; i--)
        {
            if (_history[i].Role == "user") { _history.RemoveRange(i, _history.Count - i); break; }
        }

        await SendCoreAsync(userText, oneTimeModel: null, attachments: [], ct: CancellationToken.None, clearPrompt: false);
    }

    private async Task RestoreAllFilesAsync(List<string> paths)
    {
        foreach (var path in paths)
            await InvokeToolAsync("restore_file", new { path }, CancellationToken.None);
    }

    private Task ShowToolResultAsync(string toolName, string result) =>
        RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.ToolMsg(toolName, result, expanded: true);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });

    private Task ShowInfoAsync(string markdown) =>
        RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.AssistantMsg(markdown);
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
                .Select(m => new Services.Persistence.ExportMessage(m.Role, m.Label, m.Content, m.Timestamp))
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

        string? filePath = await ShowSaveFileDialogAsync();
        if (filePath is null) return;

        try
        {
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

    private static Task<string?> ShowSaveFileDialogAsync()
    {
        var tcs    = new TaskCompletionSource<string?>();
        var thread = new System.Threading.Thread(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Export Conversation",
                Filter     = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
                DefaultExt = ".md",
                FileName   = $"conversation_{DateTime.Now:yyyy-MM-dd_HHmm}",
            };
            tcs.SetResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // EmptyToolRegistry is now internal and lives in Inferpal.Services (see EmptyToolRegistry.cs).

    #endregion
}
