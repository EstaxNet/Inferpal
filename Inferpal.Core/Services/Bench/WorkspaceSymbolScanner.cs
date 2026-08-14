using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Bench;

/// <summary>
/// One scanned source file, reduced to what the bench needs to build ground truth.
/// </summary>
/// <param name="RelPath">Path relative to the root, forward slashes.</param>
/// <param name="Module">
/// Declared module identity — the C# namespace, or the module path other ecosystems import by.
/// Null when the language has no such notion or it could not be read.
/// </param>
/// <param name="Symbols">Declared top-level type/class names, in declaration order.</param>
/// <param name="Imports">Module identities this file imports (unused by the current bench, kept
/// because the scan produces them for free and a dependency-aware question set will want them).</param>
internal sealed record ScannedFile(
    string RelPath,
    string? Module,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Imports);

/// <summary>
/// Scans a workspace into a <see cref="ScannedFile"/> list: which file declares which symbol, and
/// what it imports. One pass, one read per file, everything capped.
/// </summary>
/// <remarks>
/// <para>
/// Its only consumer is the measurement bench (<see cref="ContextBenchTasks"/>), which needs
/// ground truth — "this type is declared in exactly this file" — to grade a model's navigation.
/// It was written for the repository-map primer (roadmap §12), which was measured and removed;
/// the scanning half survives because grading a bench needs facts about the repository whatever
/// the feature under test.
/// </para>
/// <para>
/// Shares the RAG's notion of what a source file is (<see cref="CodeChunker.SupportedExtensions"/>,
/// 16 extensions) and of what to skip (build output, <c>node_modules</c>, <c>.git</c>,
/// <c>.inferpal</c>), so the bench and the semantic index never disagree about the shape of the
/// repository.
/// </para>
/// <para>
/// <b>Import coverage is partial by language, on purpose.</b> Module identity and imports are
/// extracted for C# (<c>namespace</c> / <c>using</c>) and TS/JS (path-relative
/// <c>import … from</c>); every other supported language contributes its declared symbols but no
/// dependency edges. A deliberate floor, not a silent failure.
/// </para>
/// </remarks>
internal static class WorkspaceSymbolScanner
{
    /// <summary>Files read per scan. A repository larger than this ranks on the sample.</summary>
    public const int MaxFiles = 2_000;

    /// <summary>Symbols kept per file — enough to name a folder, bounded for memory.</summary>
    private const int MaxSymbolsPerFile = 12;

    /// <summary>Extensions whose imports feed the fan-in ranking.</summary>
    private static readonly HashSet<string> TsJsExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx", ".vue" };

    /// <summary>
    /// Scans <paramref name="root"/>. Unreadable files are skipped; the scan never throws.
    /// </summary>
    /// <param name="root">Workspace root.</param>
    /// <param name="ct">Cancellation — the only exception this method lets through.</param>
    /// <returns>Scanned files, plus how many the cap left out.</returns>
    public static async Task<(IReadOnlyList<ScannedFile> Files, ScanCoverage Coverage)> ScanAsync(
        string root, CancellationToken ct = default)
    {
        var all = EnumerateSourceFiles(root);
        var (taken, coverage) = ScanCoverage.Take(all, MaxFiles);

        var result = new List<ScannedFile>(taken.Count);
        foreach (var path in taken)
        {
            ct.ThrowIfCancellationRequested();
            string src;
            try { src = await File.ReadAllTextAsync(path, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow($"WorkspaceSymbolScanner.Read({Path.GetFileName(path)})", ex); continue; }

            var rel = Rel(root, path);
            try { result.Add(Parse(rel, Path.GetExtension(path), src)); }
            catch (RegexMatchTimeoutException ex) { Diagnostics.Swallow($"WorkspaceSymbolScanner.Parse({rel})", ex); }
        }

        return (result, coverage);
    }

    /// <summary>Parses one file into its map entry. Pure — exposed for tests.</summary>
    internal static ScannedFile Parse(string relPath, string extension, string source)
    {
        var symbols = new List<string>();
        var imports = new List<string>();
        string? module = null;

        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            var ns = _csNamespace.Match(source);
            if (ns.Success) module = ns.Groups[1].Value;

            foreach (Match m in _csUsing.Matches(source))
                imports.Add(m.Groups[1].Value);

            foreach (Match m in _csType.Matches(source))
                if (symbols.Count < MaxSymbolsPerFile) symbols.Add(m.Groups[1].Value);
        }
        else if (TsJsExtensions.Contains(extension))
        {
            // TS/JS have no namespace: the module identity IS the path other files import.
            module = StripExtension(relPath);

            foreach (Match m in _tsImport.Matches(source))
            {
                var resolved = ResolveRelativeImport(relPath, m.Groups[1].Value);
                if (resolved is not null) imports.Add(resolved);
            }

            foreach (Match m in _tsExport.Matches(source))
                if (symbols.Count < MaxSymbolsPerFile) symbols.Add(m.Groups[1].Value);
        }
        else
        {
            // Symbols only (see the class remarks): these folders rank on size.
            foreach (Match m in _genericDecl.Matches(source))
                if (symbols.Count < MaxSymbolsPerFile) symbols.Add(m.Groups[1].Value);
        }

        return new ScannedFile(relPath, module, symbols, imports);
    }

    // ── Import resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <c>./x</c> / <c>../y/z</c> against the importing file's folder, into the same
    /// extension-less repo-relative form <see cref="Parse"/> gives a module. Package imports
    /// (<c>vscode</c>, <c>react</c>) return null — they are not folders of this repository.
    /// </summary>
    internal static string? ResolveRelativeImport(string fromRelPath, string import)
    {
        if (!import.StartsWith('.')) return null;

        var segments = new List<string>();
        var dir = fromRelPath.LastIndexOf('/') is var i && i >= 0 ? fromRelPath[..i] : string.Empty;
        if (dir.Length > 0) segments.AddRange(dir.Split('/'));

        foreach (var part in import.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count == 0) return null;   // escapes the repository
                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(part);
                    break;
            }
        }

        return segments.Count == 0 ? null : StripExtension(string.Join('/', segments));
    }

    private static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot   = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    // ── Enumeration ───────────────────────────────────────────────────────────

    private static List<string> EnumerateSourceFiles(string root)
    {
        var result = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!CodeChunker.SupportedExtensions.Contains(Path.GetExtension(path))) continue;
                if (IsExcluded(path)) continue;
                try { if (new FileInfo(path).Length > CodeChunker.MaxFileSizeBytes) continue; }
                catch (Exception ex) { Diagnostics.Swallow("WorkspaceSymbolScanner.FileInfo", ex); continue; }
                result.Add(path);
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("WorkspaceSymbolScanner.Enumerate", ex); }

        // Path order keeps the scanned sample (and therefore the map) stable between runs.
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Same skip list as the semantic index — see <c>ProjectIndexService.IsExcluded</c>.</summary>
    private static readonly string[] ExcludedDirNames =
        ["obj", "bin", ".git", "node_modules", ".vs", "dist", ".inferpal"];

    private static bool IsExcluded(string path)
    {
        foreach (var dir in ExcludedDirNames)
            if (path.Contains($@"\{dir}\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"/{dir}/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    // ── Patterns (all bounded by RegexBudget: they parse whatever is in the workspace) ──

    private static readonly Regex _csNamespace = new(
        @"(?m)^[ \t]*namespace\s+([\w.]+)",
        RegexOptions.Compiled, RegexBudget.Default);

    // `using X;` and `using static X;` — aliases (`using A = B;`) name no module, so they are skipped.
    private static readonly Regex _csUsing = new(
        @"(?m)^[ \t]*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;",
        RegexOptions.Compiled, RegexBudget.Default);

    // `record struct` / `record class` must be matched before bare `record`, or the alternation
    // captures the word "struct" as the type name (it did, and the map said so).
    private static readonly Regex _csType = new(
        @"(?m)^[ \t]*(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|new)\s+)*" +
        @"(?:record\s+(?:struct|class)|class|interface|record|struct|enum)\s+(\w+)",
        RegexOptions.Compiled, RegexBudget.Default);

    private static readonly Regex _tsImport = new(
        @"(?m)^[ \t]*(?:import|export)\b[^'""\n]{0,256}from\s*['""]([^'""\n]{1,256})['""]",
        RegexOptions.Compiled, RegexBudget.Default);

    private static readonly Regex _tsExport = new(
        @"(?m)^[ \t]*export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?" +
        @"(?:class|interface|function|const|type|enum)\s+(\w+)",
        RegexOptions.Compiled, RegexBudget.Default);

    // Top-level declarations, no leading indentation: Python/Go/Rust/Java/C++ nest everything else.
    private static readonly Regex _genericDecl = new(
        @"(?m)^(?:pub\s+|public\s+|private\s+)?(?:final\s+|abstract\s+|static\s+)?" +
        @"(?:class|struct|interface|trait|enum|record|type|def|func|fn)\s+(\w+)",
        RegexOptions.Compiled, RegexBudget.Default);
}
