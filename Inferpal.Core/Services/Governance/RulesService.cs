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
    public static IReadOnlyList<ProjectRule> Load(string rulesDir)
    {
        if (string.IsNullOrEmpty(rulesDir) || !Directory.Exists(rulesDir))
            return [];

        var rules = new List<ProjectRule>();
        foreach (var file in Directory.EnumerateFiles(rulesDir, "*.md", SearchOption.TopDirectoryOnly)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch { continue; }

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

        var block = normalized.Substring(4, end - 4);
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
    public static bool GlobMatch(string glob, string path)
    {
        if (string.IsNullOrEmpty(glob) || string.IsNullOrEmpty(path)) return false;

        var g = glob.Replace('\\', '/').Trim();
        var p = path.Replace('\\', '/').TrimStart('/');

        var rx = GlobToRegex(g);
        if (Regex.IsMatch(p, rx)) return true;

        // Bare patterns (no path separator) match the file name at any depth.
        if (!g.Contains('/'))
        {
            var name = p[(p.LastIndexOf('/') + 1)..];
            return Regex.IsMatch(name, rx);
        }
        return false;
    }

    private static string GlobToRegex(string glob)
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
                        sb.Append(".*");   // ** → any depth (including '/')
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') i++; // swallow trailing slash of **/
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
