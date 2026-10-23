using System.IO;
using Inferpal.Localization;

namespace Inferpal.Services.CodeActions;

/// <summary>"Fix with Inferpal" on problems chosen in an editor's list: the question, its attachment, and why there is
/// nothing to send. One decision for Visual Studio's Error List and VS Code's quick fix.</summary>
internal static class ProblemsFix
{
    /// <summary>
    /// Why the Error List's command sends nothing, or <c>null</c> when it has problems to fix. <paramref name="answer"/>
    /// <c>null</c>: the in-process half did not answer — not loaded (<paramref name="inProcLoaded"/> <c>false</c>), or
    /// past the budget.
    /// </summary>
    internal static string? Refusal(ErrorListAnswer? answer, bool? inProcLoaded) =>
        answer is null                ? (inProcLoaded == false ? Strings.FixProblemsNoInProc : Strings.FixProblemsNoAnswer)
        : answer.Failure is { } why   ? Strings.FixProblemsUnreadable(why)
        : answer.Problems.Count == 0  ? Strings.FixProblemsNoSelection
        : null;

    /// <summary>
    /// Why the agent could fix none of <paramref name="problems"/>, or <c>null</c> when it could fix one: each names a
    /// file outside <paramref name="root"/> (the tools write under it only) or one the workspace's ignore files exclude
    /// (never read nor written). Said before the question goes: sent, the turn ends on refused edits.
    /// </summary>
    internal static string? NothingEditable(IReadOnlyList<ProblemEntry> problems, string? root)
    {
        // A problem with no file (a command-line error) may be fixed anywhere: the agent decides where.
        if (problems.Count == 0 || problems.Any(p => string.IsNullOrEmpty(p.File))) return null;
        var files = problems.Select(p => p.File!).Distinct(PathComparer.Default).ToList();
        if (files.Any(f => Governance.ContentExclusions.Of(f) is null && Tools.PathSanitizer.IsUnderRoot(Path.GetFullPath(f), root)))
            return null;
        return files.Select(f => Governance.ContentExclusions.NoticeFor(f, root)).FirstOrDefault(n => n is not null)
            ?? Strings.FixProblemsOutside;
    }

    /// <summary>
    /// What goes to the chat, the files read as the editor has them: an unsaved buffer's text, else the disk's.
    /// </summary>
    /// <remarks>⚠ A problem an editor reports in a buffer that is not saved points at the BUFFER's lines: read from disk,
    /// the code around it is another code, or none.</remarks>
    internal static async Task<(string Question, string Chip, string Content)> ForAsync(
        IReadOnlyList<ProblemEntry> problems, Editor.OpenDocumentOverlay? overlay, CancellationToken ct)
    {
        if (overlay is not null)
            await overlay.RefreshAsync(ct, problems.Select(p => p.File).OfType<string>().Distinct().ToList());
        return For(problems, path => overlay is not null && overlay.TryGetUnsaved(path, out var buffered)
            ? buffered
            : FixPromptBuilder.ReadFromDisk(path));
    }

    /// <summary>What goes to the chat: the question, the attachment's label, and the attachment — each problem as a build
    /// prints it and the code around it (<see cref="FixPromptBuilder.ForProblems(IReadOnlyList{ProblemEntry}, Func{string, string?})"/>).</summary>
    /// <remarks>Asked with the chat's tools: fixing is editing, under the usual approvals.</remarks>
    internal static (string Question, string Chip, string Content) For(IReadOnlyList<ProblemEntry> problems,
                                                                       Func<string, string?> tryReadFile) =>
        (Strings.FixProblemsQuestion(problems.Count), Strings.FixProblemsChip(problems.Count),
         FixPromptBuilder.ForProblems(problems, tryReadFile));
}
