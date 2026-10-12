using System.IO;
using System.Text;

namespace Inferpal.Services.Persistence;

/// <summary>
/// Loads reusable prompt files (Continue-style <c>.prompt</c> parity) from
/// <c>.inferpal/prompts/*.md</c>. Each file becomes a user slash command: the file
/// name is the command (<c>review-security.md</c> → <c>/review-security</c>), the body
/// (after optional frontmatter) is the prompt text — <c>{args}</c> expands like config
/// templates. The optional frontmatter <c>description:</c> becomes the autocomplete hint.
/// </summary>
/// <remarks>
/// Built-in commands always win (the router matches them before user templates), and
/// config templates shadow a prompt file with the same name (the VM concatenates
/// config-first and the router resolves with FirstOrDefault). Results are cached for a
/// few seconds because autocomplete reloads on every keystroke.
/// </remarks>
internal static class PromptFilesService
{
    private const long CacheTtlMs = 3000;

    private static readonly object _gate = new();
    // Per folder: the project's and the machine's are both read on every keystroke of the autocomplete — one slot would
    // evict the other at each call.
    private static readonly Dictionary<string, (long At, IReadOnlyList<UserSlashTemplate> Templates)> _cache =
        new(StringComparer.Ordinal);

    /// <param name="source">How a command's <c>Source</c> names its folder (<c>/prompts</c> shows it).</param>
    public static IReadOnlyList<UserSlashTemplate> Load(string promptsDir, string? source = null)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var key = promptsDir + "|" + source;
            if (_cache.TryGetValue(key, out var cached) && now - cached.At < CacheTtlMs)
                return cached.Templates;

            var templates = LoadUncached(promptsDir, out _, source);
            _cache[key] = (now, templates);
            return templates;
        }
    }

    /// <summary>Drops the cache (after <c>/prompts init</c>, an import, and between tests).</summary>
    internal static void InvalidateCache()
    {
        lock (_gate) _cache.Clear();
        RepoCommandFiles.InvalidateCache();
    }

    internal static IReadOnlyList<UserSlashTemplate> LoadUncached(string promptsDir) =>
        LoadUncached(promptsDir, out _);

    /// <summary>Same, reporting the unreadable files. See <see cref="MarkdownFolder"/>.</summary>
    /// <param name="source">The folder as a command's <c>Source</c> names it; <c>.inferpal/prompts/</c> by default.</param>
    internal static IReadOnlyList<UserSlashTemplate> LoadUncached(
        string promptsDir, out IReadOnlyList<string> unreadable, string? source = null)
    {
        var result = new List<UserSlashTemplate>();
        foreach (var (file, text) in Governance.MarkdownFolder.ReadAll(
                     promptsDir, "PromptFilesService.Load", out unreadable))
        {
            var (fm, body) = RulesService.ParseFrontMatter(text);
            body = body.Trim();
            if (body.Length == 0) continue;

            var hint = fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
                ? d.Trim()
                : null;
            result.Add(new UserSlashTemplate(
                "/" + CommandName(Path.GetFileNameWithoutExtension(file)), body, hint,
                Source: (source ?? ".inferpal/prompts/") + Path.GetFileName(file)));
        }
        return result;
    }

    /// <summary>File name → command token: lower-cased, spaces/underscores collapsed to '-'.</summary>
    internal static string CommandName(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName.Trim().ToLowerInvariant())
            sb.Append(c is ' ' or '_' ? '-' : c);
        return sb.ToString();
    }
}
