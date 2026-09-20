using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for the <c>/snippets</c> command (<c>list</c> / <c>copy</c> / <c>delete</c> /
/// <c>clear</c>), extracted from <c>InferpalToolWindowData</c> so it is unit-testable without VS.
/// </summary>
/// <remarks>
/// The handler orchestrates <see cref="SnippetStore"/> and returns a <see cref="SnippetsCommandResult"/>
/// describing what the caller must do — the message to display and an optional clipboard payload. The
/// actual UI/OS side effects (placing text on the clipboard via an STA thread, showing the info bubble)
/// stay in the VM, which is what makes the decision logic here pure and testable. This is the reference
/// pattern for peeling the remaining <c>Handle*CommandAsync</c> methods off the god-class.
/// </remarks>
internal static class SnippetsCommandHandler
{
    /// <summary>Outcome of a <c>/snippets</c> invocation.</summary>
    /// <param name="Message">Markdown message to show the user.</param>
    /// <param name="CopyToClipboard">When non-null, the caller must place this text on the clipboard.</param>
    internal readonly record struct SnippetsCommandResult(string Message, string? CopyToClipboard = null);

    /// <summary>Parses and executes a <c>/snippets</c> invocation. <paramref name="parts"/> is the
    /// whitespace-split command line (<c>parts[0]</c> is <c>/snippets</c>).</summary>
    public static async Task<SnippetsCommandResult> HandleAsync(string[] parts, CancellationToken ct)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        // Every form binds in its exact shape only: `clear` followed by text emptied the whole library,
        // and an unknown form fell back to the listing, which reads as the deletion having been done.
        // The change is announced only once written: the store swallows the failure and returns false.
        if (sub == "clear" && parts.Length == 2)
        {
            return new(await SnippetStore.ClearAsync(ct) ? Strings.SnippetsCleared : Strings.SnippetsWriteFailed);
        }

        if ((sub == "copy" || sub == "delete") && parts.Length == 3)
        {
            var snippets = await SnippetStore.LoadAllAsync(ct);
            // The listing names each snippet by its id; a number typed by hand is its 1-based position.
            var byId = snippets.FindIndex(s => string.Equals(s.Id, parts[2], StringComparison.OrdinalIgnoreCase));
            int i;
            if (byId >= 0) i = byId;
            else if (int.TryParse(parts[2], out var n)) i = n - 1;
            else return new(Strings.SlashUsage("/snippets [list | copy <n> | delete <n> | clear]"));
            var idx = i + 1;
            if (i < 0 || i >= snippets.Count)
                return new(Strings.SnippetsNoSuch(idx));

            if (sub == "copy")
                return new(Strings.SnippetsCopied(idx), snippets[i].Code);

            return new(await SnippetStore.DeleteAsync(i, ct) ? Strings.SnippetsDeleted(idx) : Strings.SnippetsWriteFailed);
        }

        if (sub != "list" || parts.Length > 2)
            return new(Strings.SlashUsage("/snippets [list | copy <n> | delete <n> | clear]"));

        // ⚠ "No snippets saved yet" and "that file did not open" both arrive here as an empty
        // list, and the first sentence ends by offering the gesture that WRITES — told to someone
        // who has a hundred, it invites them to replace them. The bytes are kept (the store sets an
        // unreadable file aside before overwriting), but a recovery nobody is told about is not one.
        var (all, unreadable) = await SnippetStore.ReadAllAsync(ct);
        if (unreadable) return new(Strings.SnippetsUnreadable(SnippetStore.FilePath));
        return all.Count == 0
            ? new(Strings.SnippetsNone)
            : new(SnippetStore.FormatList(all));
    }
}
