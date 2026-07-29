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
}

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
    /// Never throws; returns the <see cref="InPlaceEditOutcome"/>.
    /// </summary>
    public static async Task<InPlaceEditOutcome> RunAsync(
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
        if (string.IsNullOrWhiteSpace(originalCode)) return InPlaceEditOutcome.Failed;

        // Spinner overlay while the model generates.
        InlineEditInputWindow dlg;
        try { dlg = await InlineEditInputWindow.CreateAndShowSpinnerAsync(); }
        catch { return InPlaceEditOutcome.Failed; }

        CodeActionRun run;
        try
        {
            run = await CodeActionPipeline.RunAsync(
                client, model, systemPrompt, instruction,
                docText, sel.Start.Offset, sel.End.Offset, sel.IsEmpty, ct);
        }
        catch (OperationCanceledException)
        {
            return InPlaceEditOutcome.Failed;   // cancelled mid-generation — nothing applied
        }
        finally
        {
            dlg.CloseFromThread();
        }

        if (run.Outcome == CodeActionOutcome.NoChangeNeeded) return InPlaceEditOutcome.NoChangeNeeded;
        if (run.Outcome != CodeActionOutcome.Edited)         return InPlaceEditOutcome.Failed;

        var editRange = new TextRange(
            new TextPosition(view.Document, run.Start),
            new TextPosition(view.Document, run.End));

        // Inline diff preview detour (comfort only — never a control, see ROADMAP design rules):
        // hand the whole-document rewrite to the in-process renderer; if no renderer claims it
        // within the pickup window (view closed, MEF component absent), fall back to the direct
        // apply below — the feature degrades to today's behaviour.
        if ((config?.InlineDiffPreviewEnabled ?? false) && run.NewDocText != docText)
        {
            var filePath = TryGetLocalPath(view);
            if (filePath is not null)
            {
                var requestId = Services.Signals.InlineDiffPreviewSignal.WriteRequest(filePath, docText, run.NewDocText!);
                try
                {
                    if (await Services.Signals.InlineDiffPreviewSignal.WaitForPickupAsync(
                            requestId, TimeSpan.FromSeconds(2), ct))
                        return InPlaceEditOutcome.PreviewShown;
                }
                finally
                {
                    // Claimed requests were consumed by the renderer; unclaimed ones must not
                    // linger and pop up on a later unrelated view of the same file.
                    Services.Signals.InlineDiffPreviewSignal.DiscardRequest();
                }
            }
        }

        try
        {
            await vs.Editor().EditAsync(
                batch =>
                {
                    var doc = view.Document.AsEditable(batch);
                    doc.Replace(editRange, run.EditedCode!);
                },
                ct);
            return InPlaceEditOutcome.Applied;
        }
        catch
        {
            // EditAsync may fail if the document changed between snapshot and apply — user can retry.
            return InPlaceEditOutcome.Failed;
        }
    }

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
