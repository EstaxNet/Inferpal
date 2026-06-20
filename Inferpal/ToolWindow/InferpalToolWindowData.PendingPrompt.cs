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
    #region Prompt en attente, fichier actif & sessions

    // ── Pending prompt from editor context menu ────────────────────────────────

    private void OnPendingPromptAvailable(object? sender, EventArgs e) => ConsumePendingPrompt();

    private void OnActiveFileChanged(object? sender, string filePath)
    {
        _activeFilePath = filePath;

        // Rebuild the system prompt when either persona auto-switching is on (existing behaviour)
        // or glob-scoped project rules exist — both depend on the active file. If neither applies,
        // the prompt is identical, so skip the rebuild.
        var language = _config.PersonaAutoSwitch ? DetectLanguage(filePath) : null;
        if (string.IsNullOrEmpty(language) && !HasGlobScopedRules()) return;

        _baseSystemPrompt = BuildSystemPrompt(language);
        Post(() =>
        {
            if (_history.Count > 0 && _history[0].Role == "system")
                _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
        });
    }

    // True if any project rule is glob-scoped (not alwaysApply / not unscoped), meaning the
    // injected rule set can change with the active file and the prompt must be rebuilt on switch.
    private bool HasGlobScopedRules()
    {
        var dir = FindProjectRoot();
        var rulesDir = Path.Combine(dir, ".inferpal", "rules");
        return RulesService.Load(rulesDir).Any(r => !r.AlwaysApply && r.Globs.Count > 0);
    }

    private void ConsumePendingPrompt()
    {
        var p            = _contextHolder.ConsumePendingPrompt();
        var m            = _contextHolder.ConsumePendingModel();
        var attachLabel  = _contextHolder.ConsumePendingAttachLabel();
        var attachCode   = _contextHolder.ConsumePendingAttachContent();
        if (p is null) return;

        List<AttachmentItem> atts = [];
        if (!string.IsNullOrEmpty(attachLabel) && !string.IsNullOrEmpty(attachCode))
            atts.Add(new AttachmentItem(attachLabel, attachCode, () => {}));

        // Auto-send the code action directly on the VM context.
        // The model is captured in the closure — no volatile field or
        // cross-thread race condition possible.
        // clearPrompt: false so the user's current draft is preserved.
        // Performance Shield: cancel any in-flight request on the VM context
        // before starting, so rapid successive code actions don't pile up.
        Post(() =>
        {
            _currentCts?.Cancel();
            _ = SendCoreAsync(p, m, atts, CancellationToken.None, clearPrompt: false);
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SaveNamedSessionAsync(string firstUserContent, List<SavedMessage> snapshot)
    {
        try
        {
            var title = await GenerateSessionTitleAsync(firstUserContent);
            await _store.SaveAsync(SessionManager.SessionFileName(DateTime.Now, title), snapshot, CancellationToken.None);
            await RunOnVMContextAsync(RefreshSessionsList);
        }
        catch { }
    }

    private async Task<string> GenerateSessionTitleAsync(string firstUserContent)
    {
        var fallback = SessionManager.MakeSnippet(firstUserContent);
        if (string.IsNullOrWhiteSpace(firstUserContent)) return fallback;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history:
                [
                    new("system", SessionManager.TitleSystemPrompt),
                    new("user",   firstUserContent.Length > 400 ? firstUserContent[..400] : firstUserContent)
                ],
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      cts.Token);

            return SessionManager.SanitizeTitle(result.FinalResponse, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private async Task AutoSaveAsync()
    {
        try
        {
            List<SavedMessage> snapshot = [];
            await RunOnVMContextAsync(() =>
            {
                snapshot = SessionManager.BuildSnapshot(
                    Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
            });
            await _store.AutoSaveAsync(snapshot, CancellationToken.None);
        }
        catch { }
    }

    private async Task DeleteSessionAsync(object? _, CancellationToken ct)
    {
        var name = SelectedSession;
        if (string.IsNullOrEmpty(name)) return;

        var confirmed = await _vs.Shell().ShowPromptAsync(
            Strings.DeleteSessionConfirm(name), PromptOptions.OKCancel, ct);
        if (!confirmed) return;

        _store.Delete(name);
        await RunOnVMContextAsync(() =>
        {
            RefreshSessionsList();
            SelectedSession = string.Empty;
        });
    }

    private void RefreshSessionsList()
    {
        RecentSessions.Clear();
        foreach (var s in _store.ListSessions().Where(s => s != "last_session"))
            RecentSessions.Add(s);
    }

    // ── 30-fps UI throttle ─────────────────────────────────────────────────────

    // Batches incoming tokens and flushes them to the UI at ~30 fps (32 ms).
    // Prevents flooding the UI thread when the model streams tokens rapidly.
    #endregion
}
