using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Prompting;

/// <summary>
/// Extracts semantic context (namespace, type hierarchy, overrides, interface contracts)
/// from a source file to enrich the <c>/doc</c> slash-command prompt.
/// </summary>
/// <remarks>
/// Supports C# (regex-based), TypeScript/JavaScript, Python, Go, and Rust (language hint only).
/// All members are static; no DI required.
/// </remarks>
internal static class DocContextExtractor
{
    private const int MaxInterfaceFileBytes = 20_000;
    private const int MaxInterfaceFiles     = 6;

    // ── Public entry point ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a compact markdown block describing the file's semantic context.
    /// Returns an empty string for unsupported or unrecognised file types.
    /// </summary>
    /// <param name="filePath">Absolute path to the source file being documented.</param>
    /// <param name="content">Full text content of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> BuildContextBlockAsync(
        string filePath, string content, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs"                              => await BuildCSharpContextAsync(filePath, content, ct),
            ".ts" or ".tsx"                    => BuildLanguageHint("TypeScript", "JSDoc (`/** @param {Type} name - desc  @returns {Type} desc */`)"),
            ".js" or ".jsx" or ".mjs" or ".cjs"=> BuildLanguageHint("JavaScript", "JSDoc (`/** @param {Type} name - desc  @returns {Type} desc */`)"),
            ".py"                              => BuildLanguageHint("Python",     "Google-style docstrings (`\"\"\"Summary.\\n\\nArgs:\\n    x (type): ...\\nReturns:\\n    type: ...\\n\"\"\"`)"),
            ".go"                              => BuildLanguageHint("Go",         "GoDoc comments (`// FuncName does ...` — no blank line between comment and declaration)"),
            ".rs"                              => BuildLanguageHint("Rust",       "Rust doc comments (`/// Summary.\\n///\\n/// # Arguments\\n/// * \\`name\\` - ...\\n/// # Returns`)"),
            _                                  => string.Empty,
        };
    }

    // ── C# context ─────────────────────────────────────────────────────────────

    private static async Task<string> BuildCSharpContextAsync(
        string filePath, string content, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Semantic context (C# — use XML doc comments: `///`)");

        // 1. Namespace
        var ns = ExtractNamespace(content);
        if (ns is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Namespace:** `{ns}`");
        }

        // 2. Types defined in this file
        var types = ExtractTypes(content);
        if (types.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Types defined in this file:**");
            foreach (var t in types)
            {
                var bases = t.BaseTypes.Count > 0
                    ? $" : {string.Join(", ", t.BaseTypes)}"
                    : "";
                sb.AppendLine($"- `{t.Kind} {t.Name}{bases}`");
            }
        }

        // 3. Override members
        var overrides = ExtractOverrideMembers(content);
        if (overrides.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Override members** — prefer `<inheritdoc/>` unless adding extra remarks:");
            foreach (var m in overrides)
                sb.AppendLine($"- `{m}`");
        }

        // 4. Explicit interface implementations
        var explicitImpls = ExtractExplicitInterfaceImpls(content);
        if (explicitImpls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Explicit interface implementations** — use `<inheritdoc cref=\"IFoo.Member\"/>`:");
            foreach (var m in explicitImpls)
                sb.AppendLine($"- `{m}`");
        }

        // 5. Collect all interface names referenced (base list + explicit impls)
        var interfaceNames = types
            .SelectMany(t => t.BaseTypes)
            .Concat(explicitImpls.Select(e => e.Split('.')[0]))
            .Where(n => n.Length > 1 && n[0] == 'I' && char.IsUpper(n[1]))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (interfaceNames.Count > 0)
        {
            var contracts = await LoadInterfaceContractsAsync(filePath, interfaceNames, ct);
            if (contracts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Interface contracts** (use `<inheritdoc cref=\"IFoo.Member\"/>` for explicit impls):");
                foreach (var (ifaceName, members) in contracts)
                {
                    sb.AppendLine($"- `{ifaceName}`:");
                    foreach (var mem in members.Take(12))
                    {
                        var docHint = mem.Summary is not null ? $" — *{TruncateSummary(mem.Summary, 80)}*" : "";
                        sb.AppendLine($"  - `{mem.Signature}`{docHint}");
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Generic language hint ──────────────────────────────────────────────────

    private static string BuildLanguageHint(string lang, string docStyle) =>
        $"## Semantic context ({lang})\n\n**Doc style:** {docStyle}";

    // ── Regex patterns ─────────────────────────────────────────────────────────

    // namespace declaration — file-scoped (`namespace X.Y;`) or block-scoped (`namespace X.Y {`)
    private static readonly Regex _nsRx = new(
        @"(?m)^[ \t]*namespace\s+([\w][\w\.]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // type declarations: class, interface, record, enum, struct (with optional modifiers + generics + base list)
    private static readonly Regex _typeRx = new(
        @"(?m)^[ \t]*(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|new)\s+)*" +
        @"(class|interface|record|enum|struct)\s+([\w]+)" +
        @"(?:\s*<[^{;]*?)?" +                            // optional generics
        @"(?:\s*:\s*([\w,\s<>\[\]\.]+?))?" +             // optional base list
        @"\s*(?:\{|where\b|;)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // members with `override` modifier (method / property / indexer)
    private static readonly Regex _overrideRx = new(
        @"(?m)^[ \t]*(?:(?:public|protected|internal|private|static|async|sealed|new|unsafe|partial|readonly|virtual|abstract)\s+)*" +
        @"override\s+" +
        @"(?:async\s+|static\s+)*(?:[\w<>\[\]?,\.\s]+?\s+)" +
        @"([\w]+)\s*[\(<\[]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // explicit interface implementations: `ReturnType IFoo.Member(...)`
    // Heuristic: the interface qualifier starts with 'I' followed by uppercase.
    private static readonly Regex _explicitImplRx = new(
        @"(?m)^[ \t]*(?:(?:public|protected|internal|private|static|async|unsafe|readonly|new)\s+)*" +
        @"(?:[\w<>\[\]?,\.\s]+?\s+)" +
        @"(I[A-Z]\w*)\.([\w]+)\s*[\(<]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // ── Parsers ────────────────────────────────────────────────────────────────

    private static string? ExtractNamespace(string src)
    {
        var m = _nsRx.Match(src);
        return m.Success ? m.Groups[1].Value : null;
    }

    private sealed record TypeInfo(string Kind, string Name, List<string> BaseTypes);

    private static List<TypeInfo> ExtractTypes(string src)
    {
        var results = new List<TypeInfo>();
        foreach (Match m in _typeRx.Matches(src))
        {
            var kind      = m.Groups[1].Value;
            var name      = m.Groups[2].Value;
            var rawBases  = m.Groups[3].Value;

            var baseTypes = string.IsNullOrWhiteSpace(rawBases)
                ? new List<string>()
                : rawBases
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(b => Regex.Replace(b, @"<.+?>", "").Trim())
                    .Where(b => b.Length > 0 && !string.Equals(b, "where", StringComparison.Ordinal))
                    .ToList();

            results.Add(new TypeInfo(kind, name, baseTypes));
        }
        return results;
    }

    private static List<string> ExtractOverrideMembers(string src)
    {
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();

        foreach (Match m in _overrideRx.Matches(src))
        {
            var name = m.Groups[1].Value;
            if (seen.Add(name))
                results.Add(name + "(…)");
        }
        return results;
    }

    private static List<string> ExtractExplicitInterfaceImpls(string src)
    {
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();

        foreach (Match m in _explicitImplRx.Matches(src))
        {
            var iface  = m.Groups[1].Value;
            var member = m.Groups[2].Value;
            var key    = $"{iface}.{member}";
            if (seen.Add(key))
                results.Add(key);
        }
        return results;
    }

    // ── Interface contract loader ──────────────────────────────────────────────

    private sealed record InterfaceMember(string Signature, string? Summary);

    /// <summary>
    /// Scans sibling <c>I*.cs</c> files (current dir + one level up) to find
    /// interface definitions and extract their member signatures + XML doc summaries.
    /// </summary>
    private static async Task<Dictionary<string, List<InterfaceMember>>> LoadInterfaceContractsAsync(
        string sourceFile, List<string> interfaceNames, CancellationToken ct)
    {
        var result = new Dictionary<string, List<InterfaceMember>>(StringComparer.Ordinal);

        var dir = Path.GetDirectoryName(sourceFile);
        if (dir is null) return result;

        // Search current dir, then parent dir (handles Services/I*.cs patterns)
        var searchDirs = new List<string> { dir };
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent is not null) searchDirs.Add(parent);

        int filesScanned = 0;

        foreach (var searchDir in searchDirs)
        {
            if (filesScanned >= MaxInterfaceFiles) break;

            IEnumerable<string> candidates;
            try { candidates = Directory.EnumerateFiles(searchDir, "I*.cs", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var candidate in candidates)
            {
                if (filesScanned >= MaxInterfaceFiles) break;
                ct.ThrowIfCancellationRequested();

                // Only load files likely to contain one of our target interfaces
                var stem = Path.GetFileNameWithoutExtension(candidate);
                bool isCandidate = interfaceNames.Any(n =>
                    n.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                    stem.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!isCandidate) continue;

                try
                {
                    if (new FileInfo(candidate).Length > MaxInterfaceFileBytes) continue;

                    var src = await File.ReadAllTextAsync(candidate, ct);
                    filesScanned++;

                    foreach (var ifaceName in interfaceNames)
                    {
                        if (result.ContainsKey(ifaceName)) continue;
                        if (!src.Contains($"interface {ifaceName}", StringComparison.Ordinal)) continue;

                        var members = ParseInterfaceMembers(src, ifaceName);
                        if (members.Count > 0)
                            result[ifaceName] = members;
                    }
                }
                catch { /* skip unreadable / parse errors */ }
            }
        }

        return result;
    }

    // ── Interface body parser ──────────────────────────────────────────────────

    private static readonly Regex _xmlSummaryInlineRx = new(
        @"<summary>\s*(.*?)\s*</summary>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Extracts member signatures and their XML <c>&lt;summary&gt;</c> doc from an
    /// interface body, using a simple line-by-line state machine.
    /// </summary>
    private static List<InterfaceMember> ParseInterfaceMembers(string src, string ifaceName)
    {
        // Locate the interface declaration
        var ifaceIdx = src.IndexOf($"interface {ifaceName}", StringComparison.Ordinal);
        if (ifaceIdx < 0) return [];

        // Find opening brace (accounts for base-interface list)
        var braceOpen = src.IndexOf('{', ifaceIdx);
        if (braceOpen < 0) return [];

        // Find matching closing brace via depth counter
        int depth = 1, pos = braceOpen + 1;
        while (pos < src.Length && depth > 0)
        {
            char c = src[pos++];
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }

        var body = src.Substring(braceOpen + 1, pos - braceOpen - 2);
        var members = new List<InterfaceMember>();

        // State: accumulate `///` lines, then emit a member when we hit a signature line
        var docAccum = new StringBuilder();
        string? pendingSummary = null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                docAccum.AppendLine(line);
                // Try to capture inline summary
                var m = _xmlSummaryInlineRx.Match(docAccum.ToString());
                if (m.Success)
                {
                    pendingSummary = Regex.Replace(m.Groups[1].Value, @"\s*///\s*", " ").Trim();
                    if (pendingSummary.Length == 0) pendingSummary = null;
                }
            }
            else if (line.StartsWith("[", StringComparison.Ordinal) ||
                     line.Length == 0 ||
                     line.StartsWith("//", StringComparison.Ordinal))
            {
                // Attribute, blank, or non-doc comment — don't reset doc yet
            }
            else if (line.Contains('(') || line.Contains("{ get") || line.Contains("{ set") ||
                     (line.EndsWith(';') && line.Contains(' ')))
            {
                // Looks like a member signature — clean it up
                var sig = line
                    .TrimEnd('{', ';', ' ')
                    .Replace("{ get; set; }", "{ get; set; }")
                    .Trim();

                if (sig.Length > 0 && sig.Length < 150)
                {
                    members.Add(new InterfaceMember(sig, pendingSummary));
                }

                // Reset state
                pendingSummary = null;
                docAccum.Clear();
            }
            else
            {
                // Non-signature, non-doc line — reset
                pendingSummary = null;
                docAccum.Clear();
            }
        }

        return members;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string TruncateSummary(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";
}
