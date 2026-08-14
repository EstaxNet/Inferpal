using System.IO;
using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

/// <summary>
/// VS side of the test-generation code action (<c>/test</c> and the <c>Add unit tests</c>
/// context-menu command). Unlike <see cref="InPlaceCodeEdit"/>, the generated code does NOT
/// belong to the active document — it goes into a <b>separate test file</b>.
/// <para>
/// What to write and where is decided by <see cref="Services.CodeActions.TestGenerationPlanner"/>
/// (Core), so the headless host answers <c>/test</c> with the same behaviour; what stays here is
/// the part that is genuinely VS: the spinner, and applying the result as an <b>undoable</b> edit
/// (Ctrl+Z) when the test file already exists.
/// </para>
/// Never throws; returns a <see cref="Result"/> describing what happened.
/// </summary>
internal static class TestGenerationEdit
{
    /// <summary>
    /// <paramref name="Extended"/> is true when an existing test file was augmented rather than created.
    /// <paramref name="NoChange"/> is true when the model judged there were no useful tests to add
    /// (trivial code, or an existing file that already covers every meaningful case) — nothing was written.
    /// </summary>
    public sealed record Result(bool Ok, string TestFileName, bool Extended, bool NoChange = false);

    public static async Task<Result> RunAsync(
        VisualStudioExtensibility vs,
        ITextViewSnapshot         sourceView,
        IInferenceProvider        client,
        string                    model,
        CancellationToken         ct)
    {
        var sourcePath = sourceView.Document.Uri.LocalPath;

        var sel        = sourceView.Selection;
        var sourceCode = !sel.IsEmpty
            ? sel.Extent.CopyToString()
            : sourceView.Document.Text.CopyToString();

        // Spinner overlay while the model generates.
        InlineEditInputWindow dlg;
        try { dlg = await InlineEditInputWindow.CreateAndShowSpinnerAsync(); }
        catch { return new Result(false, string.Empty, false); }

        TestGenerationPlan plan;
        try
        {
            plan = await Services.CodeActions.TestGenerationPlanner.PlanAsync(
                client, model, sourcePath, sourceCode, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new Result(false, string.Empty, false); }
        finally { dlg.CloseFromThread(); }

        if (plan.NoChange) return new Result(false, plan.TestFileName, plan.Extended, NoChange: true);
        if (!plan.Ok)      return new Result(false, plan.TestFileName, plan.Extended);

        try
        {
            if (!plan.Extended)
            {
                // Brand-new file: write it to disk, then open it in the editor.
                var dir = Path.GetDirectoryName(plan.TestPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(plan.TestPath, plan.Content, ct);
                await vs.Documents().OpenTextDocumentAsync(new Uri(plan.TestPath), ct);
            }
            else
            {
                // Existing file: open it and replace its whole content via an undoable edit.
                var doc      = await vs.Documents().OpenTextDocumentAsync(new Uri(plan.TestPath), ct);
                var fullText = doc.Text.CopyToString();
                var range    = new TextRange(
                    new TextPosition(doc, 0),
                    new TextPosition(doc, fullText.Length));
                await vs.Editor().EditAsync(batch =>
                {
                    var editable = doc.AsEditable(batch);
                    editable.Replace(range, plan.Content);
                }, ct);
            }

            return new Result(true, plan.TestFileName, plan.Extended);
        }
        catch
        {
            return new Result(false, plan.TestFileName, plan.Extended);
        }
    }
}
