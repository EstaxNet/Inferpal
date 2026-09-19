using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Governance;

/// <summary>
/// A single project rule loaded from <c>.inferpal/rules/*.md</c>.
/// </summary>
/// <param name="Name">Display name (frontmatter <c>description</c> if present, else the file name).</param>
/// <param name="Body">The rule text (everything after the frontmatter block).</param>
/// <param name="Globs">File globs the rule applies to. Empty ⇒ applies everywhere.</param>
/// <param name="AlwaysApply">When <c>true</c>, the rule is injected regardless of the active file.</param>
internal sealed record ProjectRule(
    string Name, string Body, IReadOnlyList<string> Globs, bool AlwaysApply);

/// <summary>
/// Loads and scopes markdown project rules (Continue-style <c>.continue/rules</c> parity).
/// Pure/static so it is fully unit-testable without VS or the file watcher.
/// </summary>
/// <remarks>
/// Each rule is a markdown file with optional YAML-ish frontmatter:
/// <code>
/// ---
/// description: Naming conventions
/// globs: **/*.cs, src/**
/// alwaysApply: false
/// ---
/// Use PascalCase for public members…
/// </code>
/// A rule with no <c>globs</c> (or <c>alwaysApply: true</c>) is always injected; otherwise it is
/// injected only when the active file matches one of its globs.
/// </remarks>
internal static class RulesService
{
    /// <summary>Reads every <c>*.md</c> rule from <paramref name="rulesDir"/>. Missing dir ⇒ empty.</summary>
    public static IReadOnlyList<ProjectRule> Load(string rulesDir) => Load(rulesDir, out _);

    /// <summary>
    /// Same, reporting the <b>unreadable</b> files. ⚠ A rule that cannot be read stops constraining
    /// the model, and the injection path can say nothing about it — the <c>/rules</c> listing is
    /// what shows it. See <see cref="MarkdownFolder"/>.
    /// </summary>
    public static IReadOnlyList<ProjectRule> Load(string rulesDir, out IReadOnlyList<string> unreadable)
    {
        var rules = new List<ProjectRule>();
        foreach (var (file, text) in MarkdownFolder.ReadAll(rulesDir, "RulesService.Load", out unreadable))
        {
            var (fm, body) = ParseFrontMatter(text);
            if (string.IsNullOrWhiteSpace(body)) continue;

            var globs = fm.TryGetValue("globs", out var g)
                ? g.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            var always = fm.TryGetValue("alwaysApply", out var a)
                         && a.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            var name = fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
                ? d.Trim()
                : Path.GetFileNameWithoutExtension(file);

            rules.Add(new ProjectRule(name, body.Trim(), globs, always));
        }
        return rules;
    }

    /// <summary>
    /// Splits a leading <c>---</c>…<c>---</c> frontmatter block (if any) into a key/value map plus
    /// the remaining body. When no frontmatter is present, the map is empty and the body is the
    /// whole text. Lightweight by design — supports flat <c>key: value</c> lines only.
    /// </summary>
    public static (Dictionary<string, string> FrontMatter, string Body) ParseFrontMatter(string text)
    {
        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text is null) return (fm, string.Empty);

        // Normalize newlines so the fence regex behaves the same on CRLF and LF.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (fm, normalized);

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (fm, normalized);

        // An EMPTY front matter ("---" then "---") puts the closing fence at index 3: Substring(4, -1)
        // threw, and every reader lost all its rules, checks and templates at once.
        var block = end > 4 ? normalized.Substring(4, end - 4) : string.Empty;
        // Body starts after the closing fence line ("\n---" + optional trailing chars up to newline).
        var afterFence = normalized.IndexOf('\n', end + 1);
        var body = afterFence >= 0 ? normalized[(afterFence + 1)..] : string.Empty;

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim();
            var val = trimmed[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0) fm[key] = val;
        }
        return (fm, body);
    }

    /// <summary>
    /// Returns <c>true</c> if the rule should be injected for the given active file (path relative
    /// to the project root, or <c>null</c> when no file is active).
    /// </summary>
    public static bool Matches(ProjectRule rule, string? activeRelPath)
    {
        if (rule.AlwaysApply || rule.Globs.Count == 0) return true;
        if (string.IsNullOrEmpty(activeRelPath)) return false;
        return rule.Globs.Any(glob => GlobMatch(glob, activeRelPath));
    }

    /// <summary>
    /// Matches a glob against a path. Supports <c>**</c> (any depth), <c>*</c> (within a segment)
    /// and <c>?</c>. Paths and globs are normalized to forward slashes. A glob without a slash
    /// (e.g. <c>*.cs</c>) also matches the file name alone, so it works regardless of folder depth.
    /// </summary>
    // Compiled-glob cache. The globs come from .inferpal/rules/*.md — a file that arrives with any
    // clone — and Matches() runs on every system-prompt rebuild (each active-file switch), so
    // recompiling per call is waste. ⚠ The timeout is not: without it a repo-authored pathological
    // glob freezes the prompt build. Same budget IndexExclusions applies to the same dialect.
    private static readonly TimeSpan GlobMatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly object _globCacheLock = new();
    private static readonly Dictionary<string, Regex> _globCache = new(StringComparer.Ordinal);

    private static Regex CompiledGlob(string glob)
    {
        lock (_globCacheLock)
        {
            if (_globCache.TryGetValue(glob, out var cached)) return cached;
            if (_globCache.Count > 256) _globCache.Clear();   // rules churn is tiny; a reset is fine
            var rx = new Regex(GlobToRegex(glob), RegexOptions.None, GlobMatchTimeout);
            _globCache[glob] = rx;
            return rx;
        }
    }

    private static bool SafeIsMatch(Regex rx, string input, string glob)
    {
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException)
        {
            // ⚠ Once per GLOB, not per evaluation: `Matches()` runs on every rebuild of the system
            // prompt, so on every change of active file, and for every glob of every rule.
            // Repeated, the message drowned the ring's 200 entries. And it names the consequence:
            // the rule this glob scopes is not applied.
            Diagnostics.RecordOnce(
                "Rules",
                $"Glob '{glob}' timed out and was treated as no-match: the rule it scopes is not applied.",
                glob);
            return false;   // a glob the engine cannot evaluate must never decide — nor freeze
        }
    }

    public static bool GlobMatch(string glob, string path)
    {
        if (string.IsNullOrEmpty(glob) || string.IsNullOrEmpty(path)) return false;

        var g = glob.Replace('\\', '/').Trim();
        var p = path.Replace('\\', '/').TrimStart('/');

        var rx = CompiledGlob(g);
        if (SafeIsMatch(rx, p, g)) return true;

        // Bare patterns (no path separator) match the file name at any depth.
        if (!g.Contains('/'))
        {
            var name = p[(p.LastIndexOf('/') + 1)..];
            return SafeIsMatch(rx, name, g);
        }
        return false;
    }

    /// <summary>
    /// Translates a glob into a regex pattern. Internal rather than private because the semantic
    /// index reuses it (<see cref="Rag.IndexExclusions"/>) — one glob dialect for the whole
    /// product, and callers whose patterns come from the repository add their own match timeout.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;   // the second '*'
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            // ⚠ `**/` means "zero or more SEGMENTS", not "any characters".
                            // Rendered `.*` with the '/' swallowed, `**/Program.cs` became
                            // `^.*Program\.cs$` — which matches `src/MyProgram.cs`: a rule scoped
                            // to Program.cs also fired on MyProgram.cs, and `**/bin/**` excluded
                            // every `src/mybin/` from the index. The group stays OPTIONAL so that
                            // `**/Foo.cs` still matches `Foo.cs` (zero segments), and it ends with
                            // '/' so the next segment starts on a boundary.
                            sb.Append("(?:.*/)?");
                            i++;   // the '/'
                        }
                        else
                        {
                            sb.Append(".*");   // `**` at the end of a pattern: anything, '/' included
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*"); // * → within a path segment
                    }
                    break;
                case '?': sb.Append("[^/]"); break;
                default:  sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>Renders the matched rules as a system-prompt section. Empty input ⇒ empty string.</summary>
    public static string Render(IEnumerable<ProjectRule> rules)
    {
        var list = rules as IReadOnlyList<ProjectRule> ?? rules.ToList();
        if (list.Count == 0) return string.Empty;

        var sb = new StringBuilder("\n\n## Rules\n");
        foreach (var r in list)
            sb.Append("\n### ").Append(r.Name).Append("\n\n").Append(r.Body).Append('\n');
        return sb.ToString();
    }
}
