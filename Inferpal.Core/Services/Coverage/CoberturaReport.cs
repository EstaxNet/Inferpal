using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Inferpal.Services.Coverage;

/// <summary>
/// One Cobertura report — what every coverage tool of every ecosystem writes (coverlet, <c>dotnet-coverage</c>,
/// coverage.py, istanbul for jest and vitest, c8) — read once, the same way: the line hits of each source file of the
/// workspace, and the line spans of its methods when the tool names them.
/// </summary>
/// <remarks>⚠ Test files, generated files and third-party code are left out: a target in them would ask the model to test
/// the tests, or code nobody here writes.</remarks>
internal sealed class CoberturaReport
{
    /// <summary>By file, relative to the workspace root with forward slashes: line → hits.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Files { get; }

    /// <summary>By file: the methods the tool names, as (name, first line, last line) of their reported lines. Empty for a
    /// tool that names none (coverage.py).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Name, int First, int Last)>> Methods { get; }

    private CoberturaReport(Dictionary<string, Dictionary<int, int>> files,
                            Dictionary<string, List<(string Name, int First, int Last)>> methods)
    {
        Files   = files.ToDictionary(f => f.Key, f => (IReadOnlyDictionary<int, int>)f.Value, StringComparer.Ordinal);
        Methods = methods.ToDictionary(m => m.Key, m => (IReadOnlyList<(string, int, int)>)m.Value, StringComparer.Ordinal);
    }

    // ⚠ "Tests.cs" by its capital: the pattern is otherwise case-blind, and Latest.cs or Contest.cs would be no target.
    private static readonly Regex TestPath = new(
        @"(^|/)(tests?|__tests__|spec|specs)(/|$)|[._-](tests?|spec)\.[a-z]+$|(^|/)test_[^/]*\.py$|(?-i:Tests?)\.cs$|(^|/)[^/]+\.Tests?(\.|/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    // What C# compiles a method's body into: an async or iterator state machine (class "<Name>d__3", method MoveNext), a
    // lambda or a local function ("<Name>b__0_0", "<Name>g__Local|1_0").
    private static readonly Regex CompiledMember = new(@"^<(\w+)>", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex Generated = new(
        @"(\.g\.cs|\.Designer\.cs|AssemblyInfo\.cs|\.min\.js)$|(^|/)(obj|bin|node_modules|dist|build|ThirdParty|third_party|_?vendor(ed)?)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Whether <paramref name="relative"/> is a source file a target may be taken from — not a test, not
    /// generated, not third-party.</summary>
    public static bool IsSource(string relative) =>
        !RegexBudget.IsMatch(TestPath, relative) && !RegexBudget.IsMatch(Generated, relative);

    /// <summary>The report at <paramref name="xmlPath"/>; <c>null</c> when it is missing or cannot be read.</summary>
    public static CoberturaReport? Read(string xmlPath, string root)
    {
        try
        {
            if (!File.Exists(xmlPath)) return null;
            return Parse(File.ReadAllText(xmlPath), root, File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Diagnostics.Swallow("CoberturaReport.Read", ex);
            return null;
        }
    }

    /// <param name="exists">Whether a candidate path is a file: a class's file name is relative to one of the report's
    /// <c>source</c> folders, and only the one where the file is says which.</param>
    internal static CoberturaReport Parse(string xml, string root, Func<string, bool> exists)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var doc     = XDocument.Load(reader);
        var sources = doc.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
        var files   = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var methods = new Dictionary<string, List<(string, int, int)>>(StringComparer.Ordinal);
        var fullRoot = Path.GetFullPath(root);
        foreach (var cls in doc.Descendants("class"))
        {
            var name = ((string?)cls.Attribute("filename") ?? string.Empty).Replace('\\', Path.DirectorySeparatorChar)
                                                                            .Replace('/', Path.DirectorySeparatorChar);
            if (name.Length == 0) continue;
            var full = Locate(name, sources, fullRoot, exists);
            if (!Tools.PathSanitizer.IsUnderRoot(full, fullRoot)) continue;
            var relative = Path.GetRelativePath(fullRoot, full).Replace('\\', '/');
            if (!IsSource(relative)) continue;

            if (!files.TryGetValue(relative, out var lines)) files[relative] = lines = [];
            // The lines of the class itself, not those its methods repeat.
            foreach (var line in cls.Element("lines")?.Elements("line") ?? [])
                Add(lines, line);
            foreach (var method in cls.Element("methods")?.Elements("method") ?? [])
            {
                var numbers = method.Descendants("line").Select(l => (int?)l.Attribute("number") ?? 0).Where(n => n > 0).ToList();
                foreach (var line in method.Descendants("line")) Add(lines, line);
                if (numbers.Count == 0) continue;
                if (!methods.TryGetValue(relative, out var spans)) methods[relative] = spans = [];
                spans.Add((SourceName((string?)method.Attribute("name") ?? string.Empty, (string?)cls.Attribute("name") ?? string.Empty),
                           numbers.Min(), numbers.Max()));
            }
        }
        return new CoberturaReport(files, methods.ToDictionary(m => m.Key, m => Joined(m.Value), StringComparer.Ordinal));
    }

    /// <summary>The name a method has in its source: an async method's state machine (<c>MoveNext</c> of
    /// <c>&lt;SaveAsync&gt;d__3</c>), a lambda or a local function (<c>&lt;Save&gt;b__0_0</c>) are the method they were
    /// written in.</summary>
    internal static string SourceName(string method, string className)
    {
        if (RegexBudget.IsMatch(CompiledMember, method)) return CompiledMember.Match(method).Groups[1].Value;
        var nested = className.LastIndexOfAny(['/', '+']) is var cut and >= 0 ? className[(cut + 1)..] : className;
        return method == "MoveNext" && RegexBudget.IsMatch(CompiledMember, nested)
            ? CompiledMember.Match(nested).Groups[1].Value : method;
    }

    /// <summary>The spans of one file, those of one name that touch joined: an async method is its stub and its state
    /// machine, one target rather than two over the same lines.</summary>
    private static List<(string Name, int First, int Last)> Joined(List<(string Name, int First, int Last)> spans)
    {
        var joined = new List<(string Name, int First, int Last)>();
        foreach (var group in spans.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            (string Name, int First, int Last)? open = null;
            foreach (var span in group.OrderBy(s => s.First))
            {
                // Two lines apart at most: a stub's signature, then its body; overloads further apart stay apart.
                if (open is { } o && span.First <= o.Last + 2) open = (o.Name, o.First, Math.Max(o.Last, span.Last));
                else { if (open is { } done) joined.Add(done); open = span; }
            }
            if (open is { } last) joined.Add(last);
        }
        return joined;
    }

    private static void Add(Dictionary<int, int> lines, XElement line)
    {
        var number = (int?)line.Attribute("number") ?? 0;
        if (number <= 0) return;
        var hits = (long?)line.Attribute("hits") ?? 0;
        lines[number] = Math.Max(lines.GetValueOrDefault(number), (int)Math.Min(hits, int.MaxValue));
    }

    private static string Locate(string name, List<string> sources, string root, Func<string, bool> exists)
    {
        if (Path.IsPathFullyQualified(name)) return Path.GetFullPath(name);
        foreach (var source in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(Path.IsPathFullyQualified(source) ? source : Path.Combine(root, source), name));
            if (exists(candidate)) return candidate;
        }
        return Path.GetFullPath(Path.Combine(root, name));
    }

    /// <summary>Several reports — one per test project of a solution — as one: a line is as covered as its best report
    /// says.</summary>
    public static CoberturaReport Merge(IEnumerable<CoberturaReport> reports)
    {
        var files   = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var methods = new Dictionary<string, List<(string Name, int First, int Last)>>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            foreach (var (file, lines) in report.Files)
            {
                if (!files.TryGetValue(file, out var merged)) files[file] = merged = [];
                foreach (var (line, hits) in lines) merged[line] = Math.Max(merged.GetValueOrDefault(line), hits);
            }
            foreach (var (file, spans) in report.Methods)
            {
                if (!methods.TryGetValue(file, out var merged)) methods[file] = merged = [];
                foreach (var span in spans)
                    if (!merged.Contains(span)) merged.Add(span);
            }
        }
        return new CoberturaReport(files, methods);
    }

    /// <summary>Covered and total lines of <paramref name="files"/> (relative paths); files the report does not list count
    /// nothing.</summary>
    public (int Covered, int Total) Of(IEnumerable<string> files)
    {
        int covered = 0, total = 0;
        foreach (var file in files)
        {
            if (!Files.TryGetValue(file, out var lines)) continue;
            total   += lines.Count;
            covered += lines.Values.Count(h => h > 0);
        }
        return (covered, total);
    }
}
