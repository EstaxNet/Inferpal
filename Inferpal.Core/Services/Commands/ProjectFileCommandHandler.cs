using System.IO;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for the read-only project-file commands — <c>/context</c>
/// (<c>.inferpal/context.md</c>) and <c>/memory</c> (<c>.inferpal/memory.md</c>): locate, read,
/// and render a capped preview. Both front-ends carried their own copy of the same three steps.
/// </summary>
internal static class ProjectFileCommandHandler
{
    /// <summary>Longest excerpt echoed into the chat bubble.</summary>
    private const int PreviewChars = 400;

    /// <summary>Reads <c>.inferpal/&lt;fileName&gt;</c> under <paramref name="root"/>.</summary>
    /// <param name="notFound">Localized message builder for a missing file (takes the path).</param>
    /// <param name="loaded">Localized message builder (path, length, preview).</param>
    public static async Task<string> HandleAsync(
        string?              root,
        string               fileName,
        Func<string, string> notFound,
        Func<string, int, string, string> loaded,
        CancellationToken    ct)
    {
        if (string.IsNullOrEmpty(root)) return Strings.SlashContextNoSln;

        var path = Path.Combine(root, ".inferpal", fileName);
        if (!File.Exists(path)) return notFound(path);

        try
        {
            var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            var preview = content.Length > PreviewChars ? content[..PreviewChars] + "…" : content;
            return loaded(path, content.Length, preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"ProjectFileCommandHandler({fileName})", ex);
            return Strings.MsgError(ex.Message);
        }
    }
}
