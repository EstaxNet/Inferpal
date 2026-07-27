using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for the project-notes commands <c>/note</c> (append) and <c>/notes</c>
/// (show / clear), extracted from <c>InferpalToolWindowData</c> so it is unit-testable without VS.
/// </summary>
/// <remarks>
/// The handler orchestrates <see cref="NotesStore"/> and returns a <see cref="NotesCommandResult"/>:
/// the message to display, plus a <see cref="NotesCommandResult.RefreshSystemPrompt"/> flag the VM
/// honors after a successful <c>/note</c> (a new note must become visible in the live system prompt).
/// The project root is resolved by the VM and passed in, keeping this logic free of VS dependencies.
/// Same pattern as <see cref="SnippetsCommandHandler"/>.
/// </remarks>
internal static class NotesCommandHandler
{
    /// <summary>Outcome of a notes command.</summary>
    /// <param name="Message">Markdown message to show the user.</param>
    /// <param name="RefreshSystemPrompt">When true, the VM must rebuild the system prompt so the new
    /// note is reflected in the current session.</param>
    internal readonly record struct NotesCommandResult(string Message, bool RefreshSystemPrompt = false);

    /// <summary>Handles <c>/note &lt;text&gt;</c>: appends a timestamped note. <paramref name="now"/> is
    /// injected so the timestamp is deterministic in tests.</summary>
    public static async Task<NotesCommandResult> HandleNoteAsync(
        string projectRoot, string[] parts, DateTime now, CancellationToken ct)
    {
        if (parts.Length < 2)
            return new(Strings.NoteUsage);

        var text = string.Join(" ", parts[1..]);
        await NotesStore.AppendAsync(projectRoot, text, now, ct);
        return new(Strings.NoteSaved(text), RefreshSystemPrompt: true);
    }

    /// <summary>Handles <c>/notes</c> (show all) and <c>/notes clear</c>.</summary>
    public static async Task<NotesCommandResult> HandleNotesAsync(
        string projectRoot, string[] parts, CancellationToken ct)
    {
        if (parts.Length >= 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            NotesStore.Clear(projectRoot);
            return new(Strings.NotesCleared);
        }

        var content = await NotesStore.ReadAsync(projectRoot, ct);
        if (content is null)
            return new(Strings.NotesNoneYet);
        if (content.Length == 0)
            return new(Strings.NotesEmpty);

        return new($"{Strings.NotesHeading}\n\n{content}");
    }
}
