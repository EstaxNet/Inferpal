using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services;

/// <summary>
/// Generates a compact, agent-readable map of the project's source code:
/// namespace distribution, type inventory, dependency edges, and hotspots.
/// O(N) scan over source files; result is cached per root directory.
/// </summary>
internal sealed class ProjectMapService
{
    private readonly VsContextHolder _contextHolder;

    // key = root dir, value = cached map
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectMapService(VsContextHolder contextHolder) => _contextHolder = contextHolder;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Generates (or returns cached) project map for the current solution root.</summary>
    public async Task<string> GenerateMapAsync(CancellationToken ct = default)
    {
        var root = FindRoot();
        if (root is null)
            return "❌ Could not locate solution or project root.";

        // Invalidation: re-generate after 120 s (simple TTL via timestamp prefix)
        if (_cache.TryGetValue(root, out var cached))
        {
            var stamp = cached[..24]; // "2026-05-25T03:10:00.000 "
            if (DateTime.TryParse(stamp.TrimEnd(), out var ts) &&
                (DateTime.Now - ts).TotalSeconds < 120)
                return cached[24..]; // strip timestamp, return map
        }

        var map = await BuildMapAsync(root, ct);
        var tagged = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + " " + map;
        _cache[root] = tagged;
        return map;
    }

    /// <summary>Clears the cache (e.g., after a new file is added).</summary>
    public void Invalidate() => _cache.Clear();

    // ── Core builder ──────────────────────────────────────────────────────────

    private static async Task<string> BuildMapAsync(string root, CancellationToken ct)
    {
        // ── 1. Scan ───────────────────────────────────────────────────────────
        var files = EnumerateSourceFiles(root).ToList();

        // Per-file parsed data
        var nsFiles      = new Dictionary<string, List<string>>(StringComparer.Ordinal);      // ns → file list
        var typeList     = new List<TypeEntry>();                                               // all types
        var usingsByNs   = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // ns → used ns
        var refCounts    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);      // file → ref count

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = await File.ReadAllTextAsync(file, ct);
                var rel = Path.GetRelativePath(root, file);
                var ns  = ExtractNamespace(src) ?? "global";

                // Namespace → files
                if (!nsFiles.TryGetValue(ns, out var lst))
                    nsFiles[ns] = lst = [];
                lst.Add(rel);

                // Types in file
                foreach (var t in ExtractTypes(src, ns, rel))
                    typeList.Add(t);

                // Using → dependency edges
                var usings = ExtractUsings(src);
                if (!usingsByNs.TryGetValue(ns, out var used))
                    usingsByNs[ns] = used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in usings) used.Add(u);

                // Reference count (other files referencing this filename stem)
                refCounts.TryAdd(Path.GetFileNameWithoutExtension(file), 0);
            }
            catch { /* skip unreadable files */ }
        }

        // ── 2. Cross-file ref counts ──────────────────────────────────────────
        foreach (var file in files)
        {
            try
            {
                var src = await File.ReadAllTextAsync(file, ct);
                foreach (var stem in refCounts.Keys.ToList())
                    if (src.Contains(stem, StringComparison.OrdinalIgnoreCase))
                        refCounts[stem]++;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("ProjectMapService.RefCount", ex); }
        }

        // ── 3. Render ─────────────────────────────────────────────────────────
        var sb = new StringBuilder();

        var bar = new string('═', 66);
        sb.AppendLine(bar);
        sb.AppendLine($"🗺️  PROJECT MAP  —  {Path.GetFileName(root)}");
        sb.AppendLine($"    Root   : {root}");
        sb.AppendLine($"    Scanned: {files.Count} source files  |  {typeList.Count} types  |  {nsFiles.Count} namespaces");
        sb.AppendLine(bar);

        // ── Namespace tree ───────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine($"📦 NAMESPACES  ({nsFiles.Count} total)");
        sb.AppendLine(new string('─', 66));

        foreach (var (ns, flist) in nsFiles.OrderBy(kv => kv.Key))
        {
            var sample = flist
                .Take(3)
                .Select(Path.GetFileName)
                .Aggregate((a, b) => $"{a}, {b}");
            var more = flist.Count > 3 ? $" +{flist.Count - 3} more" : "";
            sb.AppendLine($"  {ns,-42} {flist.Count,3} file(s)   {sample}{more}");
        }

        // ── Dependency edges ─────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("🔗 DEPENDENCIES  (namespace → imported namespaces)");
        sb.AppendLine(new string('─', 66));

        // Filter to project-internal namespaces only
        var knownNs = new HashSet<string>(nsFiles.Keys, StringComparer.Ordinal);
        foreach (var (ns, usings) in usingsByNs.OrderBy(kv => kv.Key))
        {
            var internal_ = usings
                .Where(u => knownNs.Any(k => u.StartsWith(k, StringComparison.Ordinal)))
                .OrderBy(u => u)
                .ToList();
            if (internal_.Count > 0)
                sb.AppendLine($"  {ns,-42} → {string.Join(", ", internal_)}");
        }

        // ── Type inventory ───────────────────────────────────────────────────
        sb.AppendLine();
        int classes    = typeList.Count(t => t.Kind == "class");
        int interfaces = typeList.Count(t => t.Kind == "interface");
        int records    = typeList.Count(t => t.Kind == "record");
        int enums      = typeList.Count(t => t.Kind == "enum");
        sb.AppendLine($"📐 TYPES  (classes: {classes}  interfaces: {interfaces}  records: {records}  enums: {enums})");
        sb.AppendLine(new string('─', 66));

        // Key interfaces + their implementors
        var ifaceGroups = typeList
            .Where(t => t.Kind == "interface")
            .OrderBy(t => t.Name)
            .ToList();
        if (ifaceGroups.Count > 0)
        {
            sb.AppendLine("  Interfaces:");
            foreach (var iface in ifaceGroups.Take(8))
            {
                var impls = typeList
                    .Where(t => t.Kind is "class" or "record" && t.BaseTypes.Contains(iface.Name))
                    .Select(t => t.Name)
                    .ToList();
                var implStr = impls.Count > 0
                    ? $"  ← {string.Join(", ", impls.Take(4))}{(impls.Count > 4 ? $" +{impls.Count - 4}" : "")}"
                    : "";
                sb.AppendLine($"    {iface.Name,-38} ({iface.Namespace}){implStr}");
            }
            if (ifaceGroups.Count > 8) sb.AppendLine($"    … +{ifaceGroups.Count - 8} more interfaces");
        }

        // ── Hotspots ─────────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("🎯 HOTSPOTS  (most referenced file stems)");
        sb.AppendLine(new string('─', 66));

        var topRefs = refCounts
            .Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToList();
        if (topRefs.Count == 0)
            sb.AppendLine("  (no cross-references detected)");
        else
            foreach (var (stem, count) in topRefs)
                sb.AppendLine($"  {stem,-44} {count,3} refs");

        sb.AppendLine();
        sb.AppendLine(bar);

        return sb.ToString().TrimEnd();
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    // namespace declaration (file-scoped or block-scoped)
    private static readonly Regex _nsRx = new(
        @"(?m)^[ \t]*namespace\s+([\w][\w\.]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // type declarations: class, interface, record, enum, struct
    private static readonly Regex _typeRx = new(
        @"(?m)^[ \t]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|readonly)\s+)*" +
        @"(class|interface|record|enum|struct)\s+([\w]+)" +
        @"(?:\s*<[^{;]*?)?" +                       // optional generics
        @"(?:\s*:\s*([\w,\s<>\[\]\.]+?))?" +        // optional base list
        @"\s*(?:\{|where\b)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // using directives (project-internal will be detected by matching known NS)
    private static readonly Regex _usingRx = new(
        @"(?m)^[ \t]*using\s+([\w][\w\.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string? ExtractNamespace(string src)
    {
        var m = _nsRx.Match(src);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<TypeEntry> ExtractTypes(string src, string ns, string relFile)
    {
        var results = new List<TypeEntry>();
        foreach (Match m in _typeRx.Matches(src))
        {
            var kind  = m.Groups[1].Value;
            var name  = m.Groups[2].Value;
            var bases = m.Groups[3].Value;

            // Extract simple type names from base list (remove generics, whitespace)
            var baseTypes = string.IsNullOrWhiteSpace(bases)
                ? []
                : bases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(b => Regex.Replace(b, @"<.+>", "").Trim())
                       .Where(b => b.Length > 0)
                       .ToList();

            results.Add(new TypeEntry(name, kind, ns, relFile, baseTypes));
        }
        return results;
    }

    private static List<string> ExtractUsings(string src)
    {
        return _usingRx.Matches(src)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    // ── File enumeration ──────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcluded(f));
        }
        catch { return []; }
    }

    private static bool IsExcluded(string path) =>
        path.Contains(@"\obj\")          || path.Contains("/obj/")           ||
        path.Contains(@"\bin\")          || path.Contains("/bin/")           ||
        path.Contains(@"\.git\")         || path.Contains("/.git/")          ||
        path.Contains(@"\node_modules\") || path.Contains("/node_modules/")  ||
        path.Contains(@"\.inferpal\") || path.Contains("/.inferpal/")  ||
        path.Contains(@"\history\")      || path.Contains("/history/");

    // ── Root discovery ────────────────────────────────────────────────────────

    private string? FindRoot()
    {
        // 0. Authoritative: the in-process package reports the actually-open solution.
        var active = ActiveSolutionSignal.TryReadSolutionDir();
        if (active is not null) return active;

        // 1. Walk up from each open editor file — reflects the solution the user is actually in,
        //    so it is preferred over CWD (which never follows solution open/close in an OOP host).
        foreach (var p in _contextHolder.GetOpenPaths())
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir))
            {
                var r = FindSlnDir(dir);
                if (r is not null) return r;
            }
        }

        // 2. Last resort: CWD.
        return FindSlnDir(Directory.GetCurrentDirectory());
    }

    private static string? FindSlnDir(string start)
    {
        var dir = start;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
                return dir;
            foreach (var sub in Directory.GetDirectories(dir))
                if (Directory.GetFiles(sub, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
                    return sub;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private record TypeEntry(
        string       Name,
        string       Kind,
        string       Namespace,
        string       RelFile,
        List<string> BaseTypes);
}
