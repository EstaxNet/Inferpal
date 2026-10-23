using System.IO;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Debugging;

/// <summary>"Analyze with Inferpal" on a debugger stop: the question and its attachment, or why there is none.</summary>
/// <param name="Refusal">Why nothing is sent — nothing paused, a debugger that did not answer; <c>null</c> when the
/// question goes.</param>
/// <param name="NothingPaused">The refusal is "nothing paused": what a front-end whose stop comes from elsewhere (Visual
/// Studio's in-process package) decides on, never by reading the sentence back.</param>
internal sealed record StopAnalysis(string Question, string Chip, string Content, string? Refusal = null,
                                    bool NothingPaused = false);

/// <summary>
/// "Analyze with Inferpal" on a debugger stop — Visual Studio's Debug menu and its call stack and variables windows,
/// VS Code's call stack and variables views: the stop as <c>@debugger</c> renders it, then the code around each of the
/// user's frames. One builder for both editors.
/// </summary>
/// <remarks>Asked as a code action, without tools: the model can neither continue, step nor evaluate — nothing runs.
/// So no note on what is left out names a tool to read the rest with, and the attachment is sized for the window the
/// code-actions model loaded (<see cref="CodeExcerpt.BudgetFor"/>): there is no reading the rest later.</remarks>
internal static class DebugStopAnalysis
{
    // Windows around each frame even in a short file: the stack names a frame by its line, and a whole file shown
    // without line numbers makes a model with no tools count to it.
    private static readonly FixPromptBuilder.SiteWords FrameSites =
        new("Code around the frames:", "frame", Reader: null, WholeWhenItFits: false);

    /// <summary>What goes to the chat, the frames' files read as the editor has them (an unsaved buffer's text, else
    /// the disk's).</summary>
    /// <param name="rootDir">The workspace: the stack keeps to the user's frames, as <c>@debugger</c> shows it, and only
    /// files under it are read — the tools reach nothing else, and its ignore files are the only ones known.</param>
    /// <param name="budgetChars">What the answering model's window holds for the attachment
    /// (<see cref="CodeExcerpt.BudgetFor"/>).</param>
    internal static async Task<StopAnalysis> ForAsync(IDebugSession? session, string? rootDir,
        Editor.OpenDocumentOverlay? overlay, int budgetChars, CancellationToken ct)
    {
        DebugStopState? state;
        try { state = await DebuggerStateReader.TryReadStateAsync(session, ct); }
        catch (DebuggerNotAnsweringException ex)
        {
            // A debugger that does not answer is not one that is not paused.
            return new(string.Empty, string.Empty, string.Empty, Strings.DebugNoAnswer(ex.Message));
        }
        if (state is null) return new(string.Empty, string.Empty, string.Empty, Strings.AnalyzeStopNotPaused, NothingPaused: true);

        // The frames the stack shows, in its order: the code of the top one first.
        var shown   = DebugStateFormatter.UserFrames(state.Frames, rootDir).Take(DebugStateFormatter.MaxFrames)
                          .Where(f => !string.IsNullOrEmpty(f.File) && f.Line is > 0)
                          .Select(f => (Path: f.File!, Line: f.Line!.Value))
                          .ToList();
        var inside  = shown.Where(s => Tools.PathSanitizer.IsUnderRoot(Path.GetFullPath(s.Path), rootDir)).ToList();
        var outside = shown.Select(s => s.Path).Except(inside.Select(s => s.Path), PathComparer.Default).Count();
        if (overlay is not null)
            await overlay.RefreshAsync(ct, inside.Select(s => s.Path).Distinct(PathComparer.Default).ToList());

        var parts = new List<string> { DebugStateFormatter.Format(state, rootDir) };
        if (FixPromptBuilder.CodeAround(inside, FixPromptBuilder.AsTheEditorHasIt(overlay), FrameSites) is { Length: > 0 } code)
            parts.Add(code);
        if (outside > 0)
            parts.Add($"({outside} frame file(s) outside the workspace, not read)");

        var excerpt = CodeExcerpt.Of(string.Join("\n\n", parts), budgetChars);
        return new(Strings.AnalyzeStopQuestion, excerpt.Label(Strings.AnalyzeStopChip), excerpt.Text);
    }
}
