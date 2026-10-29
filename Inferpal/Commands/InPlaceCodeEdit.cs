using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

/// <summary>Outcome of an in-place code edit.</summary>
internal enum InPlaceEditOutcome
{
    /// <summary>The model produced a rewrite and it was applied to the document.</summary>
    Applied,
    /// <summary>The model judged the action a no-op (already good) — nothing was changed.</summary>
    NoChangeNeeded,
    /// <summary>The edit could not be produced or applied (no code, model/edit error).</summary>
    Failed,
    /// <summary>The rewrite was handed to the in-editor inline diff preview — the user decides
    /// per hunk there; nothing has been applied yet.</summary>
    PreviewShown,
    /// <summary>The editor refused the edit: the document changed or was closed while the model was working.</summary>
    DocumentChanged,
    /// <summary>The document is excluded by the workspace's ignore files: nothing was sent to the model, and the
    /// result's detail is the sentence that says so.</summary>
    Excluded,
}

/// <summary>Outcome of an in-place code edit plus, on <see cref="InPlaceEditOutcome.Failed"/>,
/// the underlying error message (network/model) so callers can show the cause.</summary>
internal readonly record struct InPlaceEditResult(InPlaceEditOutcome Outcome, string? FailureDetail = null);

/// <summary>
/// VS side of the code actions that <b>apply their result directly to the document</b>
/// (Refactor / Fix / Add-docs), used both by the editor context-menu commands
/// (<see cref="InPlaceCodeActionBase"/>) and the equivalent chat slash commands.
/// The model step is the shared <see cref="CodeActionPipeline"/>; this wrapper adds the
/// spinner, the inline diff preview detour and the actual <c>EditAsync</c> apply.
/// </summary>
internal static class InPlaceCodeEdit
{
    /// <summary>
    /// Runs the full pipeline: spinner → <see cref="CodeActionPipeline.RunAsync"/> → inline diff
    /// preview detour (config-gated) → replace the range via <c>EditAsync</c> (undoable with Ctrl+Z).
    /// Never throws; returns the <see cref="InPlaceEditResult"/>.
    /// </summary>
    public static async Task<InPlaceEditResult> RunAsync(
        VisualStudioExtensibility vs,
        ITextViewSnapshot         view,
        IInferenceProvider        client,
        string                    model,
        string                    systemPrompt,
        string                    instruction,
        CancellationToken         ct,
        Inferpal.Config.InferpalConfig? config = null)
    {
        var docText = view.Document.Text.CopyToString();
        var sel     = view.Selection;

        // No code to work on (empty file / whitespace selection) — fail before the spinner.
        var (originalCode, _, _, _) = CodeActionPipeline.ResolveTarget(
            docText, sel.Start.Offset, sel.End.Offset, sel.IsEmpty);
        if (string.IsNullOrWhiteSpace(originalCode)) return new(InPlaceEditOutcome.Failed);
        // Excluded by the workspace's ignore files: the document never reaches the model (the slash commands and the
        // context menu both come here).
        WorkspaceExclusions.Follow();
        if (Services.Governance.ContentExclusions.NoticeFor(view.Document.Uri.LocalPath, null) is { } excluded)
            return new(InPlaceEditOutcome.Excluded, excluded);

        // Spinner overlay while the model generates.
        InlineEditInputWindow dlg;
        try { dlg = await InlineEditInputWindow.CreateAndShowSpinnerAsync(); }
        catch (Exception ex)
        {
            // The spinner is a plain WPF window on its own STA thread — if it fails to open
            // the model was never even called; name the cause instead of the generic verdict.
            Services.Diagnostics.Swallow("InPlaceCodeEdit.Spinner", ex);
            return new(InPlaceEditOutcome.Failed, ex.Message);
        }

        CodeActionRun run;
        // Closing the spinner cancels the generation, or the edit lands anyway once the model answers.
        using (var generation = CancellationTokenSource.CreateLinkedTokenSource(ct, dlg.CancelledByUser))
        {
            try
            {
                // The model's wait in the spinner window while the server reads the prompt.
                using (ModelWaitScope.Open(dlg.ShowWait))
                    run = await CodeActionPipeline.RunAsync(
                        client, model, systemPrompt, instruction,
                        docText, sel.Start.Offset, sel.End.Offset, sel.IsEmpty, generation.Token,
                        path: view.Document.Uri.LocalPath);
            }
            catch (OperationCanceledException)
            {
                // Cancelled mid-generation (user or VS command deadline) — nothing applied. Carry the
                // cause: an unexplained cancellation otherwise surfaces as the bare generic failure.
                return new(InPlaceEditOutcome.Failed, Strings.MsgCancelled);
            }
            finally
            {
                dlg.CloseFromThread();
            }
        }
        // The pipeline may turn a cancellation into a failed run rather than throw.
        if (dlg.CancelledByUser.IsCancellationRequested) return new(InPlaceEditOutcome.Failed, Strings.MsgCancelled);

        if (run.Outcome == CodeActionOutcome.NoChangeNeeded) return new(InPlaceEditOutcome.NoChangeNeeded);
        if (run.Outcome != CodeActionOutcome.Edited)         return new(InPlaceEditOutcome.Failed, run.FailureDetail);

        var editRange = new TextRange(
            new TextPosition(view.Document, run.Start),
            new TextPosition(view.Document, run.End));

        if (await TryPreviewAsync(config, view, docText, run.NewDocText!, ct))
            return new(InPlaceEditOutcome.PreviewShown);

        try
        {
            var response = await vs.Editor().EditAsync(
                batch =>
                {
                    var doc = view.Document.AsEditable(batch);
                    doc.Replace(editRange, run.EditedCode!);
                },
                ct);
            return new(Services.VsIntegration.VsEditResult.Applied(response)
                ? InPlaceEditOutcome.Applied
                : InPlaceEditOutcome.DocumentChanged);
        }
        catch (Exception ex)
        {
            // EditAsync may fail if the document changed between snapshot and apply — user can retry.
            return new(InPlaceEditOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The inline diff preview detour (comfort only — never a control), for every edit the model writes into the active
    /// document: when the setting is on, the whole-document rewrite goes to the in-process renderer, where the user keeps
    /// or rejects each change. <c>false</c> when the setting is off, nothing changes, or no renderer claims the request
    /// within the pickup window (view closed, MEF component absent): the caller applies directly, as without the preview.
    /// </summary>
    /// <param name="docText">The document the rewrite was computed from: the renderer refuses it once the file drifted.</param>
    internal static async Task<bool> TryPreviewAsync(
        Inferpal.Config.InferpalConfig? config, ITextViewSnapshot view, string docText, string newDocText, CancellationToken ct)
    {
        if (!(config?.InlineDiffPreviewEnabled ?? false) || newDocText == docText) return false;
        var filePath = TryGetLocalPath(view);
        if (filePath is null) return false;

        // The sentences leave WITH the request: the in-proc renderer has no Strings
        // (net472, no reference to the Core) and without them its three failure exits are
        // mute - a tick that does nothing, a keystroke that throws the rewrite away.
        var notices = new Services.Signals.InlineDiffNotices(
            Strings.InlineDiffPreviewAbandoned,
            Strings.InlineDiffPreviewDrifted,
            Strings.InlineDiffPreviewApplyFailed);
        var requestId = Services.Signals.InlineDiffPreviewSignal.WriteRequest(filePath, docText, newDocText, notices);
        try
        {
            return await Services.Signals.InlineDiffPreviewSignal.WaitForPickupAsync(requestId, TimeSpan.FromSeconds(2), ct);
        }
        finally
        {
            // Claimed requests were consumed by the renderer; unclaimed ones must not
            // linger and pop up on a later unrelated view of the same file.
            Services.Signals.InlineDiffPreviewSignal.DiscardRequest();
        }
    }

    /// <summary>Failure message for the user: the generic verdict, plus the underlying error when known.</summary>
    internal static string FailureMessage(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? Strings.CodeActionFailed
            : Strings.CodeActionFailed + "\n\n" + detail;

    /// <summary>Local file path of the document, or null (untitled / non-file buffers).</summary>
    private static string? TryGetLocalPath(ITextViewSnapshot view)
    {
        try
        {
            var path = view.Document.Uri.LocalPath;
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFullPath(path);
        }
        catch { return null; }
    }
}
