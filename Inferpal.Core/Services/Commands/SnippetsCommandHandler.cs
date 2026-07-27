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

        if (sub == "clear")
        {
            await SnippetStore.ClearAsync(ct);
            return new(Strings.SnippetsCleared);
        }

        if ((sub == "copy" || sub == "delete") && parts.Length >= 3 && int.TryParse(parts[2], out var idx))
        {
            var snippets = await SnippetStore.LoadAllAsync(ct);
            var i = idx - 1; // 1-based display
            if (i < 0 || i >= snippets.Count)
                return new(Strings.SnippetsNoSuch(idx));

            if (sub == "copy")
                return new(Strings.SnippetsCopied(idx), snippets[i].Code);

            await SnippetStore.DeleteAsync(i, ct);
            return new(Strings.SnippetsDeleted(idx));
        }

        // Default: list all snippets.
        var all = await SnippetStore.LoadAllAsync(ct);
        return all.Count == 0
            ? new(Strings.SnippetsNone)
            : new(SnippetStore.FormatList(all));
    }
}
