using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Presentation;

namespace Inferpal.Services.CodeActions;

/// <summary>The Explorer's "Explain": the question and its attachment, or why there is none.</summary>
/// <param name="Refusal">Why nothing is sent — the refusal <c>@file</c> or <c>@folder</c> would give, a binary file, a file
/// that could not be read; <c>null</c> when the question goes.</param>
internal sealed record ExplainAsk(string Question, string Chip, string Content, string? Refusal = null);

/// <summary>
/// "Explain" on an item of the Explorer — Visual Studio's Solution Explorer, VS Code's file explorer: a file as the editor
/// has it, or a folder as <c>@folder</c> attaches it (the listing and a sample of the bodies, each cut said). One builder
/// for both editors.
/// </summary>
/// <remarks>Asked as a code action, without tools — as Explain from the editor's menu — so the attachment is sized for
/// the window the code-actions model loaded (<see cref="CodeExcerpt.BudgetFor"/>).</remarks>
internal static class ExplorerExplain
{
    /// <param name="path">The item, as the editor names it: Visual Studio's project node is its folder
    /// (<see cref="ItemOf"/>, applied by that adapter), a file in VS Code's explorer is that file.</param>
    internal static async Task<ExplainAsk> ForAsync(string path, string? rootDir, Editor.OpenDocumentOverlay? overlay,
                                                    int budgetChars, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // A notice, never a question, when there is no folder to walk — the refusal @folder gives.
                var folder = MentionController.FolderPath(path, rootDir);
                if (MentionController.FolderRefusal(folder, rootDir) is { } why) return Refused(why);
                if (overlay is not null) await overlay.RefreshAsync(ct);
                // The budget given to the walk, never cut after: its notes on what is missing come below the bodies.
                var listing = await Task.Run(() => MentionController.BuildFolderContext(folder, ct, overlay, budgetChars), ct);
                var name    = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                return new(Strings.ExplainFolderQuestion(name), "📁 " + name, listing);
            }

            // Excluded by the workspace's ignore files: the notice @file gives, never the content.
            if (MentionController.FileRefusal(path, rootDir) is { } excluded) return Refused(excluded);
            if (MentionController.BinaryRefusal(path) is { } binary) return Refused(binary);
            if (overlay is not null) await overlay.RefreshAsync(ct, [path]);
            if (await MentionController.FileMentionTextAsync(path, overlay, ct) is not { } text) return Refused(Strings.AttachFileTooLarge);
            var fileName = Path.GetFileName(path);
            var excerpt  = CodeExcerpt.Of(text, budgetChars);
            return new(Strings.ExplainFileQuestion(fileName), excerpt.Label(fileName), excerpt.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A project item missing on disk, a locked file: said, as @file says it.
            return Refused(Strings.AttachError(ex.Message));
        }
    }

    /// <summary>What a Solution Explorer node stands for: a project file (<c>*.csproj</c>, <c>*.vcxproj</c>…) or a solution
    /// is its folder; any path without a trailing separator.</summary>
    internal static string ItemOf(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        if (Directory.Exists(trimmed)) return trimmed;
        return Path.GetExtension(trimmed).EndsWith("proj", StringComparison.OrdinalIgnoreCase) || SolutionFiles.IsSolution(trimmed)
            ? Path.GetDirectoryName(trimmed) ?? trimmed
            : trimmed;
    }

    private static ExplainAsk Refused(string why) => new(string.Empty, string.Empty, string.Empty, why);
}
