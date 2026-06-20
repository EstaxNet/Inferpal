using System.IO;
using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

/// <summary>
/// Pipeline for the test-generation code action (<c>/test</c> and the <c>Add unit tests</c>
/// context-menu command). Unlike <see cref="InPlaceCodeEdit"/>, the generated code does NOT
/// belong to the active document — it goes into a <b>separate test file</b>:
/// <list type="number">
///   <item>Resolve the conventional test path next to the source (<see cref="TestFilePathResolver"/>).</item>
///   <item>Generate the tests (model, tools off) from the selection — or the whole file.</item>
///   <item>New file → write it and open it. Existing file → open it and replace its content via an
///         undoable edit (Ctrl+Z), the model having been given the current tests to preserve.</item>
/// </list>
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
        var sourceName = Path.GetFileName(sourcePath);

        var sel        = sourceView.Selection;
        var sourceCode = !sel.IsEmpty
            ? sel.Extent.CopyToString()
            : sourceView.Document.Text.CopyToString();
        if (string.IsNullOrWhiteSpace(sourceCode))
            return new Result(false, string.Empty, false);

        var testPath = TestFilePathResolver.Resolve(sourcePath);
        var testName = Path.GetFileName(testPath);

        // Read any existing test file so the model can extend it instead of clobbering it.
        string? existing = null;
        if (File.Exists(testPath))
        {
            try { existing = await File.ReadAllTextAsync(testPath, ct); }
            catch { existing = null; }
        }
        var extend = !string.IsNullOrWhiteSpace(existing);

        var system = extend ? TestGenerationPrompts.ExtendFileSystem : TestGenerationPrompts.NewFileSystem;
        var user   = extend
            ? $"Existing test file ({testName}):\n\n{existing}\n\nSource under test ({sourceName}):\n\n{sourceCode}"
            : $"{TestGenerationPrompts.Instruction}\n\nSource file: {sourceName}\n\n{sourceCode}";

        var messages = new List<ChatMessageDto>
        {
            new("system", system),
            new("user",   user),
        };

        // Spinner overlay while the model generates.
        InlineEditInputWindow dlg;
        try { dlg = await InlineEditInputWindow.CreateAndShowSpinnerAsync(); }
        catch { return new Result(false, testName, extend); }

        ChatTurnResult result;
        try
        {
            result = await client.SendChatAsync(
                model, messages, EmptyToolRegistry.Instance, onToken: null, ct, TaskComplexity.Quick);
        }
        catch
        {
            dlg.CloseFromThread();
            return new Result(false, testName, extend);
        }
        finally
        {
            dlg.CloseFromThread();
        }

        var content = InlineEditResponse.Clean(result.TextContent);

        // The model signalled there is nothing worth testing / nothing left to add — write nothing.
        if (CodeActionSentinel.IsNoChange(content))
            return new Result(false, testName, extend, NoChange: true);

        if (string.IsNullOrWhiteSpace(content))
            return new Result(false, testName, extend);

        try
        {
            if (!extend)
            {
                // Brand-new file: write it to disk, then open it in the editor.
                var dir = Path.GetDirectoryName(testPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(testPath, content, ct);
                await vs.Documents().OpenTextDocumentAsync(new Uri(testPath), ct);
            }
            else
            {
                // Existing file: open it and replace its whole content via an undoable edit.
                var doc      = await vs.Documents().OpenTextDocumentAsync(new Uri(testPath), ct);
                var fullText = doc.Text.CopyToString();
                var range    = new TextRange(
                    new TextPosition(doc, 0),
                    new TextPosition(doc, fullText.Length));
                await vs.Editor().EditAsync(batch =>
                {
                    var editable = doc.AsEditable(batch);
                    editable.Replace(range, content);
                }, ct);
            }

            return new Result(true, testName, extend);
        }
        catch
        {
            return new Result(false, testName, extend);
        }
    }
}
