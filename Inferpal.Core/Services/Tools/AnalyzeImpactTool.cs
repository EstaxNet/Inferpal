using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Calculates the "blast radius" of modifying a source file:
/// which files, tests, and entry points are affected (direct + transitive).
/// Helps developers assess risk before a refactor.
/// </summary>
internal class AnalyzeImpactTool : ITool
{
    private const int MaxFilesScanned      = 500;
    private const int MaxTransitiveFiles   = 60;
    private const int MaxTransitivePerFile = 8;   // layer-2 entries shown per layer-1 file

    private readonly Func<string?> _getRoot;

    public AnalyzeImpactTool(Func<string?> getRoot) => _getRoot = getRoot;

    // ── ITool ─────────────────────────────────────────────────────────────────

    public string Name => "analyze_impact";

    public string Description =>
        "Calculates the blast radius of changing a source file: " +
        "which files (direct + transitive), tests, and entry points would be affected. " +
        "Use before a refactor to assess risk and surface every consumer of the file's public API. " +
        "Supports C# (namespace + type-name analysis) and other languages (import-path analysis).";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Absolute path to the source file whose impact you want to analyse."
            },
            symbol = new
            {
                type        = "string",
                description = "Focus on a specific public class or interface name exported by the file (optional)."
            },
            depth = new
            {
                type        = "integer",
                description = "Transitive layers to follow: 1 = direct only, 2 = one transitive hop (default), max 3."
            }
        },
        required = new[] { "path" }
    };

    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var filePath = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(filePath, _getRoot());
        if (!File.Exists(filePath))
            return Strings.ToolFileNotFound(filePath);

        var symbol = args.TryGetProperty("symbol", out var sv) ? sv.GetString()?.Trim() : null;
        var depth  = args.TryGetProperty("depth",  out var dv) ? Math.Clamp(dv.GetInt32(), 1, 3) : 2;

        var source   = await File.ReadAllTextAsync(filePath, ct);
        var ext      = Path.GetExtension(filePath).ToLowerInvariant();
        var rootDir  = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);

        // ── 1. Extract public API of the target file ──────────────────────────
        var api = ext is ".cs"
            ? CSharpApiExtractor.Extract(source, filePath)
            : ScriptApiExtractor.Extract(source, filePath, ext);

        // Filter to a specific symbol if requested
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            api = api with
            {
                Types = api.Types
                    .Where(t => t.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };
            if (api.Types.Count == 0)
                return Strings.ImpactSymbolNotFound(symbol!, fileName);
        }

        if (api.Types.Count == 0 && api.ExportedNames.Count == 0)
            return Strings.ImpactNoPublicApi(fileName);

        // ── 2. Scan for direct dependants (Layer 1) ───────────────────────────
        var allFiles  = EnumerateSourceFiles(rootDir, ext).Take(MaxFilesScanned).ToList();
        var layer1    = await ScanDirectDependantsAsync(api, filePath, allFiles, ct);

        // ── 3. Transitive dependants (Layer 2 … depth) ───────────────────────
        var transitive = new List<DependantFile>();
        if (depth >= 2 && layer1.Count > 0)
        {
            var layer1Paths = layer1.Select(d => d.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            transitive = await ScanTransitiveDependantsAsync(layer1, allFiles, layer1Paths, filePath, ct);
        }

        // ── 4. Classify ───────────────────────────────────────────────────────
        var tests        = layer1.Concat(transitive).Where(d => d.Role == FileRole.Test).ToList();
        var entryPoints  = layer1.Concat(transitive).Where(d => d.Role is FileRole.Command or FileRole.Controller or FileRole.EntryPoint).ToList();

        // ── 5. Risk calculation ───────────────────────────────────────────────
        var (riskLevel, riskBullets) = RiskCalculator.Compute(
            api, layer1.Count, transitive.Count, tests.Count, entryPoints.Count);

        // ── 6. Render ─────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine(Strings.ImpactHeader(fileName));
        sb.AppendLine(new string('═', 60));

        // Public API
        sb.AppendLine();
        sb.AppendLine("## Public API exposed");
        if (api.Types.Count > 0)
            foreach (var t in api.Types)
                sb.AppendLine($"  {KindIcon(t.Kind)} {t.Kind,-12} {t.Name}{(t.IsAbstract ? "  *(abstract)*" : "")}");
        if (api.ExportedNames.Count > 0)
            foreach (var n in api.ExportedNames.Take(10))
                sb.AppendLine($"  📤 {n}");
        if (api.Namespace is not null)
            sb.AppendLine($"  📦 namespace `{api.Namespace}`");

        // Layer 1
        sb.AppendLine();
        sb.AppendLine($"## Layer 1 · Direct dependants  ({layer1.Count})");
        if (layer1.Count == 0)
        {
            sb.AppendLine("  *(none found — file may be unused or only referenced dynamically)*");
        }
        else
        {
            foreach (var d in layer1.OrderBy(d => d.Role).ThenBy(d => d.RelPath))
            {
                var icon  = RoleIcon(d.Role);
                var badge = d.Role != FileRole.Source ? $"  `{d.Role}`" : "";
                var refs  = d.ReferencedTypes.Count > 0
                    ? $"  uses: {string.Join(", ", d.ReferencedTypes.Take(3))}"
                    : "";
                var how   = d.DependencyKind != DependencyKind.Uses
                    ? $"  [{d.DependencyKind}]"
                    : "";
                sb.AppendLine($"  {icon} {d.RelPath}{badge}{how}{refs}");
            }
        }

        // Layer 2
        if (depth >= 2)
        {
            sb.AppendLine();
            sb.AppendLine($"## Layer 2 · Transitive dependants  ({transitive.Count})");
            if (transitive.Count == 0)
            {
                sb.AppendLine("  *(none)*");
            }
            else
            {
                foreach (var d in transitive.Take(MaxTransitiveFiles).OrderBy(d => d.RelPath))
                {
                    var icon = RoleIcon(d.Role);
                    var via  = d.ViaFile is not null
                        ? $"  via `{Path.GetFileName(d.ViaFile)}`"
                        : "";
                    sb.AppendLine($"  {icon} {d.RelPath}{via}");
                }
                if (transitive.Count > MaxTransitiveFiles)
                    sb.AppendLine($"  … and {transitive.Count - MaxTransitiveFiles} more");
            }
        }

        // Entry points
        sb.AppendLine();
        sb.AppendLine($"## Entry points / Commands  ({entryPoints.Count})");
        if (entryPoints.Count == 0)
        {
            sb.AppendLine("  *(none detected in blast radius)*");
        }
        else
        {
            foreach (var ep in entryPoints.OrderBy(d => d.RelPath))
                sb.AppendLine($"  {RoleIcon(ep.Role)} {ep.EntryPointName ?? Path.GetFileNameWithoutExtension(ep.RelPath)}   `{ep.RelPath}`");
        }

        // Tests
        sb.AppendLine();
        sb.AppendLine($"## Tests  ({tests.Count})");
        if (tests.Count == 0)
            sb.AppendLine("  ⚠️  No test files detected — refactoring is higher risk");
        else
            foreach (var t in tests.OrderBy(d => d.RelPath))
                sb.AppendLine($"  🧪 {t.RelPath}");

        // Blast radius summary
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(Strings.ImpactFooter(layer1.Count, transitive.Count, tests.Count, entryPoints.Count));
        sb.AppendLine($"**Risk: {riskLevel}**");
        foreach (var bullet in riskBullets)
            sb.AppendLine($"  ↳ {bullet}");

        return sb.ToString().TrimEnd();
    }

    // ── Direct dependant scan ─────────────────────────────────────────────────

    private static async Task<List<DependantFile>> ScanDirectDependantsAsync(
        PublicApi api,
        string    targetFile,
        List<string> candidateFiles,
        CancellationToken ct)
    {
        var results = new List<DependantFile>();
        var rootDir = Path.GetDirectoryName(targetFile)!;
        var isCSharp = Path.GetExtension(targetFile).Equals(".cs", StringComparison.OrdinalIgnoreCase);

        foreach (var file in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Equals(targetFile, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var src = await File.ReadAllTextAsync(file, ct);
                bool referenced;
                List<string> referencedTypes;
                DependencyKind kind;

                if (isCSharp)
                    (referenced, referencedTypes, kind) = CSharpApiExtractor.CheckReference(api, src);
                else
                    (referenced, referencedTypes, kind) = ScriptApiExtractor.CheckReference(api, src, targetFile, file);

                if (!referenced) continue;

                var role           = DependantScanner.ClassifyRole(file, src);
                var entryPointName = DependantScanner.ExtractEntryPointName(file, src);
                var relPath        = Path.GetRelativePath(rootDir, file);

                results.Add(new DependantFile(file, relPath, kind, referencedTypes, role, entryPointName, ViaFile: null));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("AnalyzeImpactTool.ScanFile", ex); }
        }

        return results;
    }

    // ── Transitive dependant scan ─────────────────────────────────────────────

    private static async Task<List<DependantFile>> ScanTransitiveDependantsAsync(
        List<DependantFile> layer1,
        List<string>        allFiles,
        HashSet<string>     layer1Paths,
        string              targetFile,
        CancellationToken   ct)
    {
        var results    = new List<DependantFile>();
        var rootDir    = Path.GetDirectoryName(targetFile)!;
        var alreadySeen = new HashSet<string>(layer1Paths.Concat([targetFile]),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var dep in layer1.Take(MaxTransitivePerFile * 4))
        {
            ct.ThrowIfCancellationRequested();

            // Build a mini-API from the layer-1 file's public type names
            // (we just search for references to the layer-1 filename / class names)
            var depSource   = string.Empty;
            try { depSource = await File.ReadAllTextAsync(dep.FilePath, ct); }
            catch { continue; }

            var depApi = CSharpApiExtractor.Extract(depSource, dep.FilePath);

            foreach (var file in allFiles.Take(MaxTransitiveFiles * 3))
            {
                ct.ThrowIfCancellationRequested();
                if (alreadySeen.Contains(file)) continue;

                try
                {
                    var src = await File.ReadAllTextAsync(file, ct);

                    // Does this file reference the layer-1 file?
                    var (referenced, refTypes, kind) = CSharpApiExtractor.CheckReference(depApi, src);
                    if (!referenced) continue;

                    alreadySeen.Add(file);

                    var role    = DependantScanner.ClassifyRole(file, src);
                    var relPath = Path.GetRelativePath(rootDir, file);

                    results.Add(new DependantFile(file, relPath, kind, refTypes, role,
                        EntryPointName: DependantScanner.ExtractEntryPointName(file, src),
                        ViaFile: dep.RelPath));

                    if (results.Count >= MaxTransitiveFiles) goto Done;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("AnalyzeImpactTool.Transitive", ex); }
            }
        }
        Done:
        return results;
    }

    // ── File enumeration ──────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateSourceFiles(string rootDir, string ext)
    {
        var pattern = ext switch
        {
            ".cs"             => "*.cs",
            ".py"             => "*.py",
            ".js" or ".jsx"   => "*.js",
            ".ts" or ".tsx"   => "*.ts",
            ".java"           => "*.java",
            ".go"             => "*.go",
            _                 => "*" + ext
        };
        try
        {
            return Directory.EnumerateFiles(rootDir, pattern, SearchOption.AllDirectories)
                .Where(f => !IsExcluded(f));
        }
        catch { return []; }
    }

    private static bool IsExcluded(string path) =>
        path.Contains(@"\obj\") || path.Contains(@"\bin\") ||
        path.Contains(@"\.git\") || path.Contains(@"\node_modules\") ||
        path.Contains(@"\dist\") || path.Contains(@"\build\") ||
        path.Contains("/obj/")   || path.Contains("/bin/") ||
        path.Contains("/node_modules/") || path.Contains("/dist/") || path.Contains("/build/");

    // ── Icons ─────────────────────────────────────────────────────────────────

    private static string KindIcon(string kind) => kind switch
    {
        "interface" => "🔶",
        "class"     => "🔷",
        "record"    => "🔷",
        "struct"    => "🔷",
        "enum"      => "📋",
        _           => "▫️"
    };

    private static string RoleIcon(FileRole role) => role switch
    {
        FileRole.Test        => "🧪",
        FileRole.Command     => "⚡",
        FileRole.Controller  => "🌐",
        FileRole.EntryPoint  => "🚀",
        _                    => "📄"
    };

    // ── Inner types ───────────────────────────────────────────────────────────

    private record PublicApi(
        string?      Namespace,
        List<TypeDef> Types,
        List<string> ExportedNames,   // non-C# exported symbols / file stem for imports
        string       FilePath);

    private record TypeDef(string Name, string Kind, bool IsAbstract);

    private record DependantFile(
        string       FilePath,
        string       RelPath,
        DependencyKind DependencyKind,
        List<string> ReferencedTypes,
        FileRole     Role,
        string?      EntryPointName,
        string?      ViaFile);

    private enum DependencyKind { Uses, Instantiates, Inherits, Implements, Injects }
    private enum FileRole        { Source, Test, Command, Controller, EntryPoint }

    // ═════════════════════════════════════════════════════════════════════════
    // C# — public API extractor + reference checker
    // ═════════════════════════════════════════════════════════════════════════

    private static class CSharpApiExtractor
    {
        // File-scoped namespace  (C# 10+):  namespace X.Y.Z;
        // Block-scoped namespace:            namespace X.Y.Z {
        private static readonly Regex _ns = new(
            @"(?m)^\s*namespace\s+([\w\.]+)\s*[;{]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Public type declarations
        private static readonly Regex _typeDecl = new(
            @"(?m)^\s*(?:(?:public|internal)\s+)" +
            @"(?:(abstract|sealed|static|readonly|partial)\s+)*" +
            @"(class|interface|enum|struct|record)\s+([\w]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Checks reference relationships
        private static readonly Regex _inherits     = new(@":\s*[\w,\s<>]*\b([\w]+)\b", RegexOptions.Compiled);
        private static readonly Regex _instantiates = new(@"\bnew\s+([\w]+)\s*[<(]",    RegexOptions.Compiled);
        private static readonly Regex _injectsCtx   = new(@"\((.*?)\)",                   RegexOptions.Compiled);

        public static PublicApi Extract(string source, string filePath)
        {
            // Namespace
            var nsMatch = _ns.Match(source);
            var ns      = nsMatch.Success ? nsMatch.Groups[1].Value : null;

            // Public types
            var types = new List<TypeDef>();
            foreach (Match m in _typeDecl.Matches(source))
            {
                var modifier  = m.Groups[1].Value;
                var kind      = m.Groups[2].Value;
                var name      = m.Groups[3].Value;
                var isAbstract = modifier is "abstract";
                types.Add(new TypeDef(name, kind, isAbstract));
            }

            return new PublicApi(ns, types, [], filePath);
        }

        /// <summary>
        /// Returns (isReferenced, list-of-matched-types, best-relationship-kind).
        /// </summary>
        public static (bool, List<string>, DependencyKind) CheckReference(PublicApi api, string source)
        {
            if (api.Types.Count == 0) return (false, [], DependencyKind.Uses);

            var matched = new List<string>();
            var bestKind = DependencyKind.Uses;

            foreach (var t in api.Types)
            {
                // Word-boundary search — avoids matching "MyOllamaService" as "OllamaService"
                if (!Regex.IsMatch(source, $@"\b{Regex.Escape(t.Name)}\b")) continue;

                matched.Add(t.Name);

                // Determine HOW it's referenced (pick strongest)
                var kind = DetermineKind(source, t.Name);
                if ((int)kind < (int)bestKind) bestKind = kind;  // Implements(0) > Inherits(1) > Instantiates(2) > Injects(3) > Uses(4)
            }

            // Also require that the file's namespace is referenced (or same-namespace sibling)
            // — skipped for files in the same or sub-namespace (they don't need a using)
            // We trust the word-boundary match alone to avoid false positives.

            return (matched.Count > 0, matched, bestKind);
        }

        private static DependencyKind DetermineKind(string source, string typeName)
        {
            var escaped = Regex.Escape(typeName);

            // Inherits/implements  :  class Foo : Bar  or  class Foo : IBar, IFoo
            if (Regex.IsMatch(source, $@":\s*[\w<>\s,]*\b{escaped}\b"))
            {
                // Interface? → Implements
                if (typeName.Length > 1 && typeName[0] == 'I' && char.IsUpper(typeName[1]))
                    return DependencyKind.Implements;
                return DependencyKind.Inherits;
            }

            // Instantiates:  new TypeName(
            if (Regex.IsMatch(source, $@"\bnew\s+{escaped}\s*[<(]"))
                return DependencyKind.Instantiates;

            // Injects (constructor parameter):  TypeName varName
            if (Regex.IsMatch(source, $@"\b{escaped}\s+\w+\b"))
                return DependencyKind.Injects;

            return DependencyKind.Uses;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Scripting languages — import-path + exported-name analysis
    // ═════════════════════════════════════════════════════════════════════════

    private static class ScriptApiExtractor
    {
        private static readonly Regex _jsExport   = new(@"export\s+(?:default\s+)?(?:class|function|const|let|var|async\s+function)\s+([\w]+)", RegexOptions.Compiled);
        private static readonly Regex _pyExport   = new(@"(?m)^(?:def|class)\s+([A-Z][\w]*|[a-z_][\w]*)\s*[:(]", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex _goExport   = new(@"func\s+(?:\([^)]+\)\s+)?([\w]+)\s*\(",                  RegexOptions.Compiled);

        public static PublicApi Extract(string source, string filePath, string ext)
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);

            var exported = ext switch
            {
                ".js" or ".jsx" or ".ts" or ".tsx" =>
                    _jsExport.Matches(source).Cast<Match>().Select(m => m.Groups[1].Value).ToList(),
                ".py" =>
                    _pyExport.Matches(source).Cast<Match>()
                        .Select(m => m.Groups[1].Value)
                        .Where(n => !n.StartsWith('_'))
                        .ToList(),
                ".go" =>
                    _goExport.Matches(source).Cast<Match>()
                        .Select(m => m.Groups[1].Value)
                        .Where(n => char.IsUpper(n[0]))
                        .ToList(),
                _ => new List<string>()
            };

            // Always include the file stem so importers can be found by filename
            if (!exported.Contains(stem, StringComparer.OrdinalIgnoreCase))
                exported.Insert(0, stem);

            return new PublicApi(null, [], exported, filePath);
        }

        public static (bool, List<string>, DependencyKind) CheckReference(
            PublicApi api, string source, string targetFile, string candidateFile)
        {
            var stem    = Path.GetFileNameWithoutExtension(targetFile);
            var escaped = Regex.Escape(stem);

            // Search for import/require with the file name
            bool hasImport =
                Regex.IsMatch(source, $@"['""][^'""]*{escaped}['""]") ||   // JS: from './stem'
                Regex.IsMatch(source, $@"import\s+{escaped}\b") ||         // Python: import stem
                Regex.IsMatch(source, $@"from\s+\w*{escaped}") ||          // Python: from pkg.stem
                Regex.IsMatch(source, $@"import\s+[\w\.]*{escaped}[\w\.]*");    // Java/Go

            if (!hasImport) return (false, [], DependencyKind.Uses);

            // Which exported names are mentioned?
            var matched = api.ExportedNames
                .Where(n => Regex.IsMatch(source, $@"\b{Regex.Escape(n)}\b"))
                .ToList();

            return (true, matched, DependencyKind.Uses);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dependant classification
    // ═════════════════════════════════════════════════════════════════════════

    private static class DependantScanner
    {
        public static FileRole ClassifyRole(string filePath, string source)
        {
            var name = Path.GetFileName(filePath);
            var dir  = filePath.Replace('\\', '/');

            // Test heuristics
            bool isTest =
                name.Contains("test",  StringComparison.OrdinalIgnoreCase) ||
                name.Contains("spec",  StringComparison.OrdinalIgnoreCase) ||
                name.Contains("tests", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains("/test/",  StringComparison.OrdinalIgnoreCase) ||
                dir.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains("/spec/",  StringComparison.OrdinalIgnoreCase) ||
                source.Contains("[Fact]")       || source.Contains("[Test]")       ||
                source.Contains("[TestMethod]") || source.Contains("[Theory]")     ||
                source.Contains("[TestCase]")   || source.Contains("def test_")    ||
                source.Contains("describe(")    || source.Contains("it(\"")        ||
                source.Contains("it('");

            if (isTest) return FileRole.Test;

            // VS extensibility command
            bool isCommand =
                source.Contains("[VisualStudioContribution]") ||
                source.Contains(": Command")                   ||
                source.Contains(": ExtensionPart");

            if (isCommand) return FileRole.Command;

            // ASP.NET Controller / endpoint
            bool isController =
                source.Contains("[ApiController]")   ||
                source.Contains("[Controller]")       ||
                source.Contains(": ControllerBase")  ||
                source.Contains(": Controller")       ||
                source.Contains("app.Map")            ||
                source.Contains("[HttpGet")           ||
                source.Contains("[HttpPost");

            if (isController) return FileRole.Controller;

            // Other entry points
            bool isEntry =
                source.Contains("static void Main(") ||
                source.Contains("static async Task Main(") ||
                source.Contains("WebApplication.Create") ||
                source.Contains("Host.CreateDefaultBuilder");

            if (isEntry) return FileRole.EntryPoint;

            return FileRole.Source;
        }

        // Extract a human-readable entry-point name from VS commands / controllers
        private static readonly Regex _cmdClass = new(
            @"(?:class|record)\s+([\w]+Command|[\w]+Controller|[\w]+Handler|[\w]+Endpoint)\b",
            RegexOptions.Compiled);

        public static string? ExtractEntryPointName(string filePath, string source)
        {
            var m = _cmdClass.Match(source);
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Blast radius risk calculator
    // ═════════════════════════════════════════════════════════════════════════

    private static class RiskCalculator
    {
        public static (string Level, List<string> Bullets) Compute(
            PublicApi api,
            int direct,
            int transitive,
            int tests,
            int entryPoints)
        {
            int total = direct + transitive;

            // Level
            var level = total switch
            {
                0            => "LOW",
                <= 3         => "LOW",
                <= 9         => "MEDIUM",
                <= 19        => "HIGH",
                _            => "CRITICAL"
            };

            // Upgrade for interfaces (widely implemented = higher blast)
            bool hasInterface = api.Types.Any(t => t.Kind == "interface");
            if (hasInterface && level == "MEDIUM") level = "HIGH";
            if (hasInterface && level == "HIGH" && direct >= 5) level = "CRITICAL";

            // Downgrade slightly when good test coverage exists
            if (tests >= 2 && level == "CRITICAL") level = "HIGH";

            // Bullets
            var bullets = new List<string>();

            if (total == 0)
                bullets.Add("No dependants detected — safe to refactor freely");
            else
                bullets.Add($"{direct} direct dependant(s), {transitive} transitive — {total} files total affected");

            if (hasInterface)
            {
                int impls = api.Types.Count(t => t.Kind == "interface");
                bullets.Add($"{impls} public interface(s) — all implementations must stay contract-compatible");
            }

            if (api.Types.Any(t => t.IsAbstract))
                bullets.Add("Abstract type — all subclasses will be affected by signature changes");

            if (entryPoints > 0)
                bullets.Add($"{entryPoints} user-facing entry point(s) in blast radius — end-to-end behaviour may change");

            if (tests == 0 && total > 0)
                bullets.Add("No test files detected — refactoring carries higher regression risk");
            else if (tests > 0)
                bullets.Add($"{tests} test file(s) provide coverage — run them before committing");

            if (direct == 0 && total == 0)
            {
                // Nothing found
            }
            else if (level is "HIGH" or "CRITICAL")
                bullets.Add("Consider creating a feature branch and updating dependants incrementally");

            return (level, bullets);
        }
    }
}
