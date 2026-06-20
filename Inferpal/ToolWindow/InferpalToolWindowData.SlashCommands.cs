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
    #region Commandes slash — routage & handlers

    // ── Slash commands ─────────────────────────────────────────────────────────
    // Parsing, usage validation, and tool-argument building live in SlashCommandRouter
    // (pure, unit-tested); this VM only executes the resulting action with its services.

    private async Task HandleSlashCommandAsync(string prompt, CancellationToken ct)
    {
        switch (SlashCommandRouter.Route(prompt, GetUserTemplates()))
        {
            case SlashInfoAction info:
                await ShowInfoAsync(info.Message);
                break;

            case SlashToolAction tool:
                await InvokeToolAsync(tool.Tool, tool.Args, ct, attachAs: tool.AttachAs);
                break;

            case SlashPromptAction expanded:
                await SendCoreAsync(expanded.Prompt, oneTimeModel: null, attachments: [], ct: ct, clearPrompt: true);
                break;

            case SlashCodeAction code:
                await RunCodeActionCommandAsync(code.Kind, ct);
                break;

            case SlashDelegatedAction delegated:
                await RunDelegatedCommandAsync(delegated.Id, delegated.Parts, ct);
                break;
        }
    }

    /// <summary>
    /// Runs a code-action slash command.
    /// <para>
    /// <c>/refactor</c>, <c>/fix</c> and <c>/doc</c> <b>apply their result directly to the
    /// document</b> (in place, undoable with Ctrl+Z) — see <see cref="RunInPlaceCodeActionAsync"/>.
    /// <c>/test</c> <b>writes its result to a separate test file</b> (created/opened or extended)
    /// — see <see cref="RunGenerateTestsAsync"/>. <c>/explain</c> and <c>/review</c> stay read-only
    /// and stream their answer into the chat, with the code delivered as an
    /// <see cref="AttachmentItem"/> (file chip).
    /// </para>
    /// </summary>
    private async Task RunCodeActionCommandAsync(SlashCodeActionKind kind, CancellationToken ct)
    {
        if (kind is SlashCodeActionKind.Refactor or SlashCodeActionKind.Fix or SlashCodeActionKind.Doc)
        {
            await RunInPlaceCodeActionAsync(kind, ct);
            return;
        }

        if (kind == SlashCodeActionKind.Test)
        {
            await RunGenerateTestsAsync(ct);
            return;
        }

        var (code, fn, label) = await GetActiveCodeAsync(ct);
        if (string.IsNullOrEmpty(code)) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var prompt = kind switch
        {
            SlashCodeActionKind.Explain => Strings.PromptExplain(fn),
            _                           => Strings.PromptReview(fn),   // Review
        };
        await SendCodeActionAsync(prompt, [new AttachmentItem(label, code, () => {})], ct);
    }

    /// <summary>
    /// Generates unit tests into a <b>separate test file</b> via the shared
    /// <see cref="TestGenerationEdit"/> pipeline: the conventional test path next to the active
    /// document is created and opened, or — if it already exists — extended in place. Reports the
    /// outcome in the chat.
    /// </summary>
    private async Task RunGenerateTestsAsync(CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var model  = string.IsNullOrEmpty(_config.CodeActionsModel) ? _config.DefaultModel : _config.CodeActionsModel;
        var result = await TestGenerationEdit.RunAsync(_vs, view, _client, model, ct);

        await ShowInfoAsync(
            result.NoChange ? Strings.TestsNoChange
            : result.Ok     ? (result.Extended ? Strings.TestsExtended(result.TestFileName) : Strings.TestsGenerated(result.TestFileName))
            :                 Strings.TestsGenerateFailed);
    }

    /// <summary>
    /// Applies a transform action (Refactor / Fix / Add-docs) directly to the active document via
    /// the shared <see cref="InPlaceCodeEdit"/> pipeline: selection if any, otherwise the whole
    /// file. For <c>/doc</c>, the document's semantic context (namespace, type hierarchy, overrides,
    /// interface contracts) is appended to the system prompt as reference.
    /// </summary>
    private async Task RunInPlaceCodeActionAsync(SlashCodeActionKind kind, CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) { await ShowInfoAsync(Strings.SlashNoActiveDocument); return; }

        var (system, instruction) = kind switch
        {
            SlashCodeActionKind.Refactor => (InPlaceCodeActionPrompts.RefactorSystem,  InPlaceCodeActionPrompts.RefactorInstruction),
            SlashCodeActionKind.Fix      => (InPlaceCodeActionPrompts.FixSystem,       InPlaceCodeActionPrompts.FixInstruction),
            _                            => (InPlaceCodeActionPrompts.DocstringSystem, InPlaceCodeActionPrompts.DocstringInstruction),
        };

        if (kind == SlashCodeActionKind.Doc)
        {
            var fullPath     = view.Document.Uri.LocalPath;
            var docText      = view.Document.Text.CopyToString();
            var contextBlock = await DocContextExtractor.BuildContextBlockAsync(fullPath, docText, ct);
            if (!string.IsNullOrWhiteSpace(contextBlock))
                system += "\n\nContext (for reference only — do not output it):\n" + contextBlock;
        }

        var model   = string.IsNullOrEmpty(_config.CodeActionsModel) ? _config.DefaultModel : _config.CodeActionsModel;
        var outcome = await InPlaceCodeEdit.RunAsync(_vs, view, _client, model, system, instruction, ct);

        // The model judged the action a no-op (already clear / correct / documented) — tell the user
        // rather than leaving the chat silent, so the absence of an edit doesn't look like a failure.
        if (outcome == InPlaceEditOutcome.NoChangeNeeded)
            await ShowInfoAsync(kind switch
            {
                SlashCodeActionKind.Refactor => Strings.RefactorNoChange,
                SlashCodeActionKind.Fix      => Strings.FixNoChange,
                _                            => Strings.DocNoChange,
            });
    }

    /// <summary>Executes the stateful commands the router hands back to the VM.</summary>
    private async Task RunDelegatedCommandAsync(SlashCommandId id, string[] parts, CancellationToken ct)
    {
        switch (id)
        {
            case SlashCommandId.Clear:
                await ClearAsync(null, ct);
                break;

            case SlashCommandId.TestBuildBanner:
                // Fires a synthetic BuildFailed event to verify the OOP banner pipeline.
                // If the red banner appears, the OOP side is working correctly.
                // If not, the issue is in the in-process MEF component or signal file.
                _buildMonitor.FireTestBuildFailed();
                await ShowInfoAsync("🔴 Test build-failure signal sent. If the red banner does not appear above, the in-process → OOP pipeline is broken (check signal file: %TEMP%\\Inferpal\\build_signal.json).");
                break;

            case SlashCommandId.Model:
                if (parts.Length < 2) { await ShowInfoAsync(Strings.SlashModelCurrent(_config.DefaultModel)); break; }
                _config.DefaultModel = parts[1];
                _config.Save();
                await RunOnVMContextAsync(() => ActiveModelLabel = parts[1]);
                await ShowInfoAsync(Strings.SlashModelChanged(parts[1]));
                break;

            case SlashCommandId.Tools:
                if (parts.Length < 2 || parts[1] is not ("on" or "off")) { await ShowInfoAsync(Strings.SlashToolsCurrent(_toolsEnabled ? "on" : "off")); break; }
                _toolsEnabled = parts[1] == "on";
                await ShowInfoAsync(Strings.SlashToolsChanged(parts[1]));
                break;

            case SlashCommandId.Export:     await ExportConversationAsync(ct);                            break;
            case SlashCommandId.Context:    await HandleContextCommandAsync(ct);                          break;
            case SlashCommandId.Memory:     await HandleMemoryCommandAsync(ct);                           break;
            case SlashCommandId.Index:      await HandleRagIndexCommandAsync(parts, ct);                  break;
            case SlashCommandId.Commit:     await HandleCommitCommandAsync(ct);                           break;
            case SlashCommandId.CommitExec: await HandleCommitExecAsync(string.Join(" ", parts[1..]), ct); break;
            case SlashCommandId.FixBuild:   await HandleFixBuildCommandAsync(parts, ct);                  break;
            case SlashCommandId.History:    await HandleHistoryCommandAsync(parts, ct);                   break;
            case SlashCommandId.UndoRun:    await HandleUndoRunCommandAsync(parts, ct);                   break;
            case SlashCommandId.PHistory:   await HandlePHistoryCommandAsync(parts, ct);                  break;
            case SlashCommandId.Models:     await HandleModelsCommandAsync(parts, ct);                    break;
            case SlashCommandId.Hardware:   await HandleHardwareCommandAsync(parts, ct);                  break;
            case SlashCommandId.Setup:      await HandleSetupCommandAsync(parts, ct);                    break;
            case SlashCommandId.AgentStep:  await ToggleStepModeAsync();                                  break;
            case SlashCommandId.Plan:       await TogglePlanModeAsync();                                  break;
            case SlashCommandId.Prompts:    await HandlePromptsCommandAsync(parts, ct);                   break;

            case SlashCommandId.Resume:
                if (_stepResume is not null)
                    ResumeStep();
                else
                    await ShowInfoAsync("No agent step is currently paused.");
                break;

            case SlashCommandId.Note:       await HandleNoteCommandAsync(parts, ct);     break;
            case SlashCommandId.Notes:      await HandleNotesCommandAsync(parts, ct);    break;
            case SlashCommandId.Snippets:   await HandleSnippetsCommandAsync(parts, ct); break;
            case SlashCommandId.Template:   await HandleTemplateCommandAsync(parts, ct); break;
            case SlashCommandId.Docs:       await HandleDocsCommandAsync(parts, ct);     break;
            case SlashCommandId.Check:      await HandleCheckCommandAsync(parts, ct);    break;
            case SlashCommandId.Rules:      await HandleRulesCommandAsync(parts, ct);    break;
            case SlashCommandId.Checks:     await HandleChecksCommandAsync(parts, ct);   break;
            case SlashCommandId.Diagnostics: await ShowInfoAsync(Services.Commands.DiagnosticsCommandHandler.Handle(parts)); break;
        }
    }

    /// <summary>
    /// Handles <c>/docs add &lt;url&gt; [title] | list | remove &lt;id&gt; | reindex [id]</c> —
    /// manages external documentation sources crawled and embedded for the <c>search_docs</c> tool.
    /// Crawl/embed runs in the background with progress reported as chat bubbles.
    /// </summary>
    private async Task HandleDocsCommandAsync(string[] parts, CancellationToken ct)
    {
        var sub   = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        var sites = DocSite.Parse(_config.DocSitesJson);

        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 3) { await ShowInfoAsync(Strings.DocsUsage); break; }

                var url = parts[2];
                if (!DocSite.IsValidHttpUrl(url))
                {
                    await ShowInfoAsync(Strings.DocsUsage);
                    break;
                }

                var title = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;
                var site  = DocSite.Create(url, title);

                _config.DocSitesJson = DocSite.Serialize(DocSite.Upsert(sites, site));
                _config.Save();

                await ShowInfoAsync(Strings.DocsAdded(site.Title));

                // Crawl + embed in the background; progress surfaces as info bubbles.
                var progress = new Progress<string>(msg => _ = ShowInfoAsync(msg));
                _ = Task.Run(() => _docsIndex.AddOrReindexAsync(site, progress, CancellationToken.None));
                break;
            }

            case "remove":
            {
                if (parts.Length < 3) { await ShowInfoAsync(Strings.DocsUsage); break; }

                var id      = parts[2].ToLowerInvariant();
                var updated = DocSite.Remove(sites, id);
                if (updated is null) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                _config.DocSitesJson = DocSite.Serialize(updated);
                _config.Save();
                await _docsIndex.RemoveAsync(id, ct);
                await ShowInfoAsync(Strings.DocsRemoved(id));
                break;
            }

            case "reindex":
            {
                var target = parts.Length >= 3
                    ? sites.FirstOrDefault(s => s.Id == parts[2].ToLowerInvariant())
                    : null;
                var toIndex = target is not null ? [target] : sites.ToArray();
                if (toIndex.Length == 0) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                await ShowInfoAsync(Strings.DocsReindexing(target?.Title ?? $"{toIndex.Length}"));
                var progress = new Progress<string>(msg => _ = ShowInfoAsync(msg));
                _ = Task.Run(async () =>
                {
                    foreach (var s in toIndex)
                        await _docsIndex.AddOrReindexAsync(s, progress, CancellationToken.None);
                });
                break;
            }

            case "list":
            default:
            {
                if (sites.Count == 0) { await ShowInfoAsync(Strings.DocsNoSites); break; }

                var stats = _docsIndex.Sites.ToDictionary(
                    s => s.Site.Id, s => (s.PageCount, s.ChunkCount));
                await ShowInfoAsync(DocSite.FormatList(sites, stats));
                break;
            }
        }
    }

    private async Task HandleModelsCommandAsync(string[] parts, CancellationToken ct)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        // Pull owns a live status bubble (insert → update → remove), so it stays in the VM; the rest
        // (list/delete/running) is pure decision logic in ModelsCommandHandler.
        if (sub == "pull")
        {
            if (!_client.Capabilities.ModelManagement) { await ShowInfoAsync(Strings.ModelsBackendUnsupported); return; }
            await PullModelInteractiveAsync(parts, ct);
            return;
        }

        var result = await Services.Commands.ModelsCommandHandler.HandleAsync(_client, parts, ct);
        await ShowInfoAsync(result.Message);
    }

    // /models pull <name> — downloads a model while showing a live status bubble updated per progress line.
    private async Task PullModelInteractiveAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3) { await ShowInfoAsync(Strings.ModelsPullUsage); return; }
        var model     = string.Join(" ", parts[2..]);
        var statusBbl = ChatMessageItem.StatusMsg(Strings.ModelsPulling(model));
        await RunOnVMContextAsync(() => { ApplyItemTheme(statusBbl); Messages.Insert(Messages.Count - 2, statusBbl); });

        var ok = await _client.PullModelAsync(model, status =>
            Post(() => statusBbl.Content = Strings.ModelsPullingStatus(model, status)), ct);

        await RunOnVMContextAsync(() =>
        {
            var idx = Messages.IndexOf(statusBbl);
            if (idx >= 0) Messages.RemoveAt(idx);
        });

        await ShowInfoAsync(ok ? Strings.ModelsPulled(model) : Strings.ModelsPullFailed(model));
    }

    private async Task HandleHardwareCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.HardwareCommandHandler.HandleAsync(_config, _client, parts, ct);

        // The handler never persists config (so tests don't touch %APPDATA%); apply + save here.
        if (result.SetBudgetGb is { } gb)
        {
            _config.VramBudgetGb = gb;
            _config.Save();
        }

        await ShowInfoAsync(result.Message);
    }

    private async Task HandlePHistoryCommandAsync(string[] parts, CancellationToken ct)
    {
        // Read the history snapshot on the VM context, then let the pure handler decide; the VM applies
        // the single resulting effect (fill the prompt, or show a message).
        List<string> history = [];
        await RunOnVMContextAsync(() => history = [.._promptHistory.Entries]);

        var result = Services.Commands.PHistoryCommandHandler.Handle(history, parts);

        if (result.FillPrompt is { } text)
            await RunOnVMContextAsync(() => Prompt = text);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandleNoteCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.NotesCommandHandler.HandleNoteAsync(
            FindProjectRoot(), parts, DateTime.Now, ct);

        if (result.RefreshSystemPrompt)
            // Refresh system prompt so the new note is visible in the current session.
            await RunOnVMContextAsync(() =>
            {
                _baseSystemPrompt = BuildSystemPrompt();
                if (_history.Count > 0 && _history[0].Role == "system")
                    _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
            });

        await ShowInfoAsync(result.Message);
    }

    private async Task HandleNotesCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = await Services.Commands.NotesCommandHandler.HandleNotesAsync(FindProjectRoot(), parts, ct);
        await ShowInfoAsync(result.Message);
    }

    private async Task HandleSnippetsCommandAsync(string[] parts, CancellationToken ct)
    {
        // Pure decision logic lives in SnippetsCommandHandler (unit-tested); the VM only performs the
        // UI/OS side effects it returns — clipboard copy on an STA thread and the info bubble.
        var result = await Services.Commands.SnippetsCommandHandler.HandleAsync(parts, ct);

        if (result.CopyToClipboard is { } code)
        {
            var thread = new Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(string.IsNullOrEmpty(code) ? " " : code); }
                catch { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        await ShowInfoAsync(result.Message);
    }

    private async Task HandleTemplateCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await ShowInfoAsync(SessionManager.FormatTemplateList());
            return;
        }

        var id = parts[1].ToLowerInvariant();
        var tmpl = SessionManager.FindTemplate(id);
        if (tmpl is null)
        {
            await ShowInfoAsync($"Unknown template `{id}`. Type `/template` to see the list.");
            return;
        }

        // Clear the conversation and apply the template
        await ClearAsync(null, ct);

        await RunOnVMContextAsync(() =>
        {
            _activeTemplateSuffix = tmpl.SystemSuffix;
            _baseSystemPrompt     = BuildSystemPrompt();
            _history[0]           = new ChatMessageDto("system", _baseSystemPrompt);
        });

        await ShowInfoAsync(tmpl.Greeting);
    }

    // Config templates first so they shadow a prompt file with the same command name
    // (the router resolves with FirstOrDefault; built-ins always win over both).
    private IReadOnlyList<UserSlashTemplate> GetUserTemplates()
    {
        var fromConfig = SlashCommandRouter.ParseUserTemplates(_config.PromptTemplates);
        var fromFiles  = PromptFilesService.Load(PromptsDir());
        return fromFiles.Count == 0
            ? fromConfig
            : fromConfig.Concat(fromFiles).DistinctBy(t => t.Name).ToList();
    }

    private string PromptsDir() => Path.Combine(FindProjectRoot(), ".inferpal", "prompts");

    #endregion
}
