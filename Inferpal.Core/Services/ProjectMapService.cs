using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;

namespace Inferpal.Services;

/// <summary>
/// Generates a compact, agent-readable map of the project's source code:
/// namespace distribution, type inventory, dependency edges, and hotspots.
/// O(N) scan over source files; result is cached per root directory.
/// </summary>
internal sealed class ProjectMapService
{
    private readonly IEditorSurface       _editor;
    private readonly ProjectIndexService? _index;

    // key = root dir, value = cached map
    // The time is kept as a value: written as text in the machine's culture and parsed back, a time separator that is
    // not ':' (Finnish) never parses, and the map is rebuilt on every call.
    private readonly ConcurrentDictionary<string, (DateTime At, string Map)> _cache = new(PathComparer.Default);

    public ProjectMapService(IEditorSurface editor, ProjectIndexService? index = null)
    {
        _editor = editor;
        _index  = index;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Generates (or returns cached) project map for the current solution root.</summary>
    public async Task<string> GenerateMapAsync(CancellationToken ct = default) =>
        await TryGenerateMapAsync(ct) ?? "❌ Could not locate solution or project root.";

    /// <summary>
    /// The project map, or <c>null</c> when there is no solution or project root to map — the state a reader that
    /// decides (the <c>@tree</c> mention) reads, never the sentence the tool serves in its place.
    /// </summary>
    internal async Task<string?> TryGenerateMapAsync(CancellationToken ct = default)
    {
        var root = FindRoot();
        if (root is null)
            return null;

        // Invalidation: re-generate after 120 s (simple TTL via timestamp prefix)
        if (_cache.TryGetValue(root, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 120)
            return cached.Map;

        var map = await BuildMapAsync(root, ct);
        _cache[root] = (DateTime.UtcNow, map);
        return map;
    }

    /// <summary>Clears the cache (e.g., after a new file is added).</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>What each of the two long sections (namespaces, dependencies) may take, so that the whole map fits one
    /// tool result in the context.</summary>
    internal const int SectionBudgetChars = 2_400;

    /// <summary>The source files a map reads at most.</summary>
    /// <remarks>⚠ Reading every file of a repository of 140 000 C# files takes minutes, with the model or the person
    /// waiting, for a map that shows a summary of it. Past the budget the shallowest files are read first (every
    /// project's top files before the deep generated ones), and the header says how many of how many.</remarks>
    internal const int ReadBudgetFiles = 10_000;

    // ── Core builder ──────────────────────────────────────────────────────────

    private static async Task<string> BuildMapAsync(string root, CancellationToken ct) =>
        await BuildMapAsync(root, ReadBudgetFiles, ct);

    internal static async Task<string> BuildMapAsync(string root, int readBudget, CancellationToken ct)
    {
        // ── 1. Scan ───────────────────────────────────────────────────────────
        // Counted here, not by the call: the map is cached, and @tree builds it outside any tool call — its own line says
        // what the workspace's ignore files kept out of it, every time it is shown.
        var walked = Governance.ContentExclusions.Counting(() => EnumerateSourceFiles(root).ToList(), out var ignored);
        var files  = walked.Count <= readBudget ? walked : WorkspaceScan.ShallowestFirst(walked).Take(readBudget).ToList();
        // ⚠ The header below is literally labelled "Scanned: N source files", and N is what the walk
        // TOOK. Three holes never reach that number: the files past the read budget, a file taken and then unreadable
        // (counted here, skipped by the catch below) and a folder the walk never listed or would not follow, whose
        // files are absent from the list altogether. An inventory read as exhaustive is how a model
        // concludes a namespace, a type or a whole layer does not exist in this project.
        var coverage   = new ScanCoverage(walked.Count, files.Count)
                             .WithGap(WorkspaceScan.FirstWalkGap(root, root));
        var unreadable = 0;

        // Per-file parsed data
        var nsFiles      = new Dictionary<string, List<string>>(StringComparer.Ordinal);      // ns → file list
        var typeList     = new List<TypeEntry>();                                               // all types
        var usingsByNs   = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // ns → used ns
        var refCounts    = new Dictionary<string, int>(PathComparer.Default);                  // file → ref count

        // Pass-1 contents kept for pass 2, which would otherwise re-read every file from disk.
        var contents = new Dictionary<string, string>(PathComparer.Default);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The map cites the names a file declares; a legacy-encoded file read as UTF-8 cited a replacement character for each accent.
                var src = await Tools.TextFileEncoding.ReadTextAsync(file, ct);
                contents[file] = src;
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
            // Cancellation must PROPAGATE: swallowed, every remaining read failed instantly and
            // a partial map was returned as if complete.
            catch (OperationCanceledException) { throw; }
            // Counted rather than traced: one ring entry per unreadable file, on every regeneration,
            // is the noise RecordOnce and DroppedLineOnce exist to prevent. The count IS the channel,
            // and it goes where the reader of the map will see it.
            catch { unreadable++; }
        }

        // ── 2. Cross-file ref counts (over the pass-1 contents, no second disk walk) ──
        // Each file is tokenised ONCE into its identifiers, and each identifier is looked up among the stems: every stem
        // tried against every file is files × stems lookups, half an hour on a repository of 140 000 files. A file never
        // counts its own name as a reference; stems that differ only by case are all counted, as identifiers ignore case.
        // A stem spelled like a C# keyword (Class.cs, If.cs) would be "referenced" by every file that writes the keyword —
        // with case ignored the two cannot be told apart —, so it is left out of the hotspots.
        var stemsByWord = refCounts.Keys
            .Where(stem => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(stem.ToLowerInvariant())
                           == Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
            .GroupBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, src) in contents)
        {
            ct.ThrowIfCancellationRequested();
            var own = Path.GetFileNameWithoutExtension(path);
            foreach (var word in IdentifiersOf(src))
                if (!word.Equals(own, StringComparison.OrdinalIgnoreCase) && stemsByWord.TryGetValue(word, out var stems))
                    foreach (var stem in stems) refCounts[stem]++;
        }

        // ── 3. Render ─────────────────────────────────────────────────────────
        var sb = new StringBuilder();

        var bar = new string('═', 66);
        sb.AppendLine(bar);
        sb.AppendLine($"🗺️  PROJECT MAP  —  {Path.GetFileName(root)}");
        sb.AppendLine($"    Root   : {root}");
        sb.AppendLine($"    Scanned: {files.Count} source files  |  {typeList.Count} types  |  {nsFiles.Count} namespaces");
        // ⚠ Said, never left as a zero: a TypeScript or Python workspace would read as an empty project.
        if (files.Count == 0) sb.AppendLine($"    {NoCSharp}");
        // Right under the count it qualifies, not at the far end of a sixty-line report.
        coverage = coverage.WithUnreadable(unreadable);
        if (coverage.IsIncomplete) sb.AppendLine($"    {coverage.Warning().Replace("\n", "\n    ")}");
        if (ignored.Note() is { Length: > 0 } excludedNote) sb.AppendLine($"    {excludedNote}");
        sb.AppendLine(bar);

        // ── Namespace tree ───────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine($"📦 NAMESPACES  ({nsFiles.Count} total)");
        sb.AppendLine(new string('─', 66));

        // ⚠ The map reaches the model as ONE tool result, and the loop cuts a longer one in its middle: on a solution
        // of a hundred namespaces these two sections were forty thousand characters, and the map arrived as its first
        // and last screens. Each keeps its largest namespaces within a budget, in name order, and says what it left.
        string NamespaceLine(string ns, List<string> flist)
        {
            var sample = flist
                .Take(3)
                .Select(Path.GetFileName)
                .Aggregate((a, b) => $"{a}, {b}");
            var more = flist.Count > 3 ? $" +{flist.Count - 3} more" : "";
            return $"  {ns,-42} {flist.Count,3} file(s)   {sample}{more}";
        }

        var shownNs = new HashSet<string>(StringComparer.Ordinal);
        var nsUsed  = 0;
        foreach (var (ns, flist) in nsFiles.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var length = NamespaceLine(ns, flist).Length + 1;
            if (shownNs.Count > 0 && nsUsed + length > SectionBudgetChars) break;
            shownNs.Add(ns);
            nsUsed += length;
        }
        foreach (var (ns, flist) in nsFiles.Where(kv => shownNs.Contains(kv.Key)).OrderBy(kv => kv.Key))
            sb.AppendLine(NamespaceLine(ns, flist));
        if (shownNs.Count < nsFiles.Count)
        {
            var hiddenFiles = nsFiles.Where(kv => !shownNs.Contains(kv.Key)).Sum(kv => kv.Value.Count);
            sb.AppendLine($"  … +{nsFiles.Count - shownNs.Count} smaller namespace(s) not listed ({hiddenFiles} file(s)) — "
                        + "list_files on a folder shows its files");
        }

        // ── Dependency edges ─────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("🔗 DEPENDENCIES  (namespace → imported namespaces)");
        sb.AppendLine(new string('─', 66));

        // Filter to project-internal namespaces only
        var knownNs = new HashSet<string>(nsFiles.Keys, StringComparer.Ordinal);
        const int MaxImportsPerLine = 6;
        var depsUsed = 0;
        var depsLeft = 0;
        // The namespaces listed above, and within the same kind of budget.
        foreach (var (ns, usings) in usingsByNs.Where(kv => shownNs.Contains(kv.Key)).OrderBy(kv => kv.Key))
        {
            var internal_ = usings
                .Where(u => knownNs.Any(k => u.StartsWith(k, StringComparison.Ordinal)))
                .OrderBy(u => u)
                .ToList();
            if (internal_.Count == 0) continue;
            var line = $"  {ns,-42} → {string.Join(", ", internal_.Take(MaxImportsPerLine))}"
                     + (internal_.Count > MaxImportsPerLine ? $" +{internal_.Count - MaxImportsPerLine} more" : "");
            if (depsUsed > 0 && depsUsed + line.Length + 1 > SectionBudgetChars) { depsLeft++; continue; }
            sb.AppendLine(line);
            depsUsed += line.Length + 1;
        }
        if (depsLeft > 0)
            sb.AppendLine($"  … +{depsLeft} namespace(s) not listed — the imports of a file are at its top (read_file)");

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
        RegexOptions.Compiled | RegexOptions.Multiline, RegexBudget.Default);

    // using directives (project-internal will be detected by matching known NS)
    private static readonly Regex _usingRx = new(
        @"(?m)^[ \t]*using\s+([\w][\w\.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline, RegexBudget.Default);

    private static string? ExtractNamespace(string src)
    {
        var m = _nsRx.Match(src);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<TypeEntry> ExtractTypes(string src, string ns, string relFile)
    {
        return [.. CSharpTypeDeclarations.Read(src)
                      .Select(t => new TypeEntry(t.Name, t.Kind, ns, relFile, [.. t.BaseTypes]))];
    }

    private static List<string> ExtractUsings(string src)
    {
        return _usingRx.Matches(src)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    // ── File enumeration ──────────────────────────────────────────────────────

    /// <summary>The identifiers spelled in <paramref name="src"/> (letters, digits, underscore), case-insensitive.</summary>
    private static HashSet<string> IdentifiersOf(string src)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var i = 0; i <= src.Length; i++)
        {
            var inWord = i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_');
            if (inWord && start < 0) start = i;
            else if (!inWord && start >= 0)
            {
                words.Add(src[start..i]);
                start = -1;
            }
        }
        return words;
    }

    /// <summary>What the map says when the root holds no C# file.</summary>
    internal const string NoCSharp = "This map reads C# (.cs) files only, and none was found under the root: it says nothing about code in other languages.";

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        WorkspaceScan.EnumerateFiles(root, "*.cs");

    // ── Root discovery ────────────────────────────────────────────────────────

    private string? FindRoot()
    {
        // 0. Authoritative: the in-process package reports the actually-open solution — mapped to the
        //    folder that holds all of its projects, which is the workspace root (SolutionExtent) — or the folder it has open.
        var active = SolutionExtent.ActiveRoot();
        if (active is not null) return active;

        // Then the workspace root, as is: it IS the project, with or without a solution file.
        // Climbing from it would stop at a PARENT folder's solution — another project's map — and
        // the open files and the current directory below only stand in when no root is known.
        if (_index?.RootDir is { Length: > 0 } root && Directory.Exists(root)) return root;

        // 1. Walk up from each open editor file — reflects the solution the user is actually in,
        //    so it is preferred over CWD (which never follows solution open/close in an OOP host).
        foreach (var p in _editor.GetOpenDocumentPaths())
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir))
            {
                var r = FindSlnDir(dir);
                if (r is not null) return SolutionExtent.RootForDir(r);
            }
        }

        // 2. Last resort: CWD.
        return SolutionExtent.RootForDir(FindSlnDir(Directory.GetCurrentDirectory()));
    }

    private static string? FindSlnDir(string start)
    {
        var dir = start;
        for (int i = 0; i < 8; i++)
        {
            if (SolutionFiles.DirectoryHasSolution(dir))
                return dir;
            foreach (var sub in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
                if (SolutionFiles.DirectoryHasSolution(sub))
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
