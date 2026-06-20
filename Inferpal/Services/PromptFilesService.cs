using System.IO;
using System.Text;

namespace Inferpal.Services;

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
    private static string? _cachedDir;
    private static long    _cachedAt;
    private static IReadOnlyList<UserSlashTemplate> _cachedResult = [];

    public static IReadOnlyList<UserSlashTemplate> Load(string promptsDir)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (promptsDir == _cachedDir && now - _cachedAt < CacheTtlMs)
                return _cachedResult;

            _cachedDir    = promptsDir;
            _cachedAt     = now;
            _cachedResult = LoadUncached(promptsDir);
            return _cachedResult;
        }
    }

    /// <summary>Drops the cache (after <c>/prompts init</c>, and between tests).</summary>
    internal static void InvalidateCache()
    {
        lock (_gate) _cachedDir = null;
    }

    internal static IReadOnlyList<UserSlashTemplate> LoadUncached(string promptsDir)
    {
        if (string.IsNullOrEmpty(promptsDir) || !Directory.Exists(promptsDir))
            return [];

        var result = new List<UserSlashTemplate>();
        foreach (var file in Directory.EnumerateFiles(promptsDir, "*.md", SearchOption.TopDirectoryOnly)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch { continue; }

            var (fm, body) = RulesService.ParseFrontMatter(text);
            body = body.Trim();
            if (body.Length == 0) continue;

            var hint = fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
                ? d.Trim()
                : null;
            result.Add(new UserSlashTemplate(
                "/" + CommandName(Path.GetFileNameWithoutExtension(file)), body, hint));
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
