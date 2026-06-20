using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Traces the call graph of a source file: what each method/function calls,
/// and optionally who calls it, with cross-file resolution up to a configurable depth.
/// Uses syntax-level analysis for C# and regex for other languages.
/// </summary>
internal class TraceDependencyTool : ITool
{
    private const int MaxAllowedDepth   = 3;
    private const int MaxCallsPerMethod = 30;
    private const int MaxFilesScanned   = 400;

    private readonly Func<string?> _getRoot;

    public TraceDependencyTool(Func<string?> getRoot) => _getRoot = getRoot;

    // ── ITool ─────────────────────────────────────────────────────────────────

    public string Name => "trace_dependency";

    public string Description =>
        "Traces the call graph from a source file: which methods are defined, what each " +
        "calls (callees), and optionally who calls them (callers). Cross-file resolution " +
        "up to 'depth' hops. Supports C# (syntax analysis) and other languages (regex). " +
        "Use 'symbol' to focus on a single method/function.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Absolute path to the source file to analyse."
            },
            symbol = new
            {
                type        = "string",
                description = "Method or function name to focus on. Omit to analyse all symbols in the file."
            },
            depth = new
            {
                type        = "integer",
                description = "Cross-file recursion depth: 0 = current file only, 1 = one hop (default), up to 3."
            },
            direction = new
            {
                type        = "string",
                description = "'callees' (default) — what does this call; 'callers' — who calls this; 'both'."
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

        var symbol    = args.TryGetProperty("symbol",    out var sv) ? sv.GetString()?.Trim() : null;
        var depth     = args.TryGetProperty("depth",     out var dv) ? Math.Clamp(dv.GetInt32(), 0, MaxAllowedDepth) : 1;
        var direction = args.TryGetProperty("direction", out var di) ? di.GetString() ?? "callees" : "callees";

        var source = await File.ReadAllTextAsync(filePath, ct);
        var ext    = Path.GetExtension(filePath).ToLowerInvariant();

        // ── Parse file ────────────────────────────────────────────────────────
        var methods = ParseMethods(source, ext, filePath);

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            methods = methods
                .Where(m => m.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (methods.Count == 0)
                return Strings.TraceDepsSymbolNotFound(symbol!, Path.GetFileName(filePath));
        }

        if (methods.Count == 0)
            return Strings.TraceDepsNoMethods(Path.GetFileName(filePath));

        // ── Build cross-file index ────────────────────────────────────────────
        var rootDir = Path.GetDirectoryName(filePath)!;
        DefinitionIndex? index = null;
        if (depth > 0)
            index = await BuildIndexAsync(rootDir, ext, ct);

        // ── Render ────────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine(Strings.TraceDepsHeader(Path.GetFileName(filePath)));
        sb.AppendLine(new string('═', 60));

        bool showCallers = direction is "callers" or "both";
        bool showCallees = direction is "callees" or "both";

        if (showCallers)
        {
            sb.AppendLine();
            sb.AppendLine("### Callers");
            sb.AppendLine();
            AppendCallers(sb, methods, rootDir, ext, filePath, ct);
        }

        if (showCallees)
        {
            if (showCallers) sb.AppendLine();
            sb.AppendLine("### Callees");
            sb.AppendLine();

            foreach (var m in methods)
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine($"▶ **{m.Name}**{m.Signature}  *(line {m.Line})*");
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { m.Name };
                AppendCalleeTree(sb, m, filePath, index, depth, 1, visited, "  ");
                sb.AppendLine();
            }
        }

        // ── Summary ───────────────────────────────────────────────────────────
        var allCallees = methods.SelectMany(m => m.Calls)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
        int resolved = index is null ? 0
                     : allCallees.Count(c => index.TryFind(c, out _));

        sb.AppendLine("---");
        sb.AppendLine(Strings.TraceDepsFooter(methods.Count, allCallees.Count, resolved));

        return sb.ToString().TrimEnd();
    }

    // ── Callees tree ──────────────────────────────────────────────────────────

    private static void AppendCalleeTree(
        StringBuilder    sb,
        MethodInfo       method,
        string           baseFile,
        DefinitionIndex? index,
        int              maxDepth,
        int              depth,
        HashSet<string>  visited,
        string           indent)
    {
        if (method.Calls.Count == 0)
        {
            sb.AppendLine($"{indent}*(no outgoing calls detected)*");
            return;
        }

        for (int i = 0; i < method.Calls.Count; i++)
        {
            bool   isLast     = i == method.Calls.Count - 1;
            string conn       = isLast ? "└── " : "├── ";
            string childIndent = indent + (isLast ? "    " : "│   ");
            var    call       = method.Calls[i];

            // Resolve to definition
            MethodInfo? callee = null;
            string loc;
            if (index is not null && index.TryFind(call, out var def))
            {
                callee = def;
                if (def.FilePath == baseFile)
                    loc = $"[this file:{def.Line}]";
                else
                {
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(baseFile)!, def.FilePath);
                    loc = $"[{rel}:{def.Line}]";
                }
            }
            else
            {
                loc = "[external]";
            }

            bool cyclic = visited.Contains(call);
            sb.AppendLine(cyclic
                ? $"{indent}{conn}{call}()  {loc}  ↩ cyclic"
                : $"{indent}{conn}{call}()  {loc}");

            if (!cyclic && callee is not null && depth < maxDepth)
            {
                visited.Add(call);
                AppendCalleeTree(sb, callee, baseFile, index, maxDepth, depth + 1, visited, childIndent);
                visited.Remove(call);
            }
        }
    }

    // ── Callers ───────────────────────────────────────────────────────────────

    private static void AppendCallers(
        StringBuilder    sb,
        List<MethodInfo> targets,
        string           rootDir,
        string           ext,
        string           targetFile,
        CancellationToken ct)
    {
        var targetNames = targets.Select(m => m.Name)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // key=target method, value=list of callers
        var callerMap = new Dictionary<string, List<(string caller, string relFile, int line)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateSourceFiles(rootDir, ext).Take(MaxFilesScanned))
        {
            ct.ThrowIfCancellationRequested();
            if (file == targetFile) continue;
            try
            {
                var src      = File.ReadAllText(file);
                var fext     = Path.GetExtension(file).ToLowerInvariant();
                var fileMths = ParseMethods(src, fext, file);
                var relPath  = Path.GetRelativePath(rootDir, file);

                foreach (var fm in fileMths)
                    foreach (var call in fm.Calls)
                        if (targetNames.Contains(call))
                        {
                            if (!callerMap.TryGetValue(call, out var lst))
                                callerMap[call] = lst = [];
                            lst.Add((fm.Name, relPath, fm.Line));
                        }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("TraceDependencyTool.BuildCallerMap", ex); }
        }

        foreach (var t in targets)
        {
            sb.AppendLine($"▶ **{t.Name}**() ← called by:");
            if (callerMap.TryGetValue(t.Name, out var callers) && callers.Count > 0)
                foreach (var (caller, relFile, line) in callers.OrderBy(x => x.relFile).ThenBy(x => x.line))
                    sb.AppendLine($"  • {caller}()  [{relFile}:{line}]");
            else
                sb.AppendLine("  *(no callers found in scanned files)*");
            sb.AppendLine();
        }
    }

    // ── Index building ────────────────────────────────────────────────────────

    private static async Task<DefinitionIndex> BuildIndexAsync(string rootDir, string ext, CancellationToken ct)
    {
        var index = new DefinitionIndex();
        foreach (var file in EnumerateSourceFiles(rootDir, ext).Take(MaxFilesScanned))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src     = await File.ReadAllTextAsync(file, ct);
                var fext    = Path.GetExtension(file).ToLowerInvariant();
                var methods = ParseMethods(src, fext, file);
                foreach (var m in methods)
                    index.TryAdd(m.Name, m);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("TraceDependencyTool.IndexMethods", ex); }
        }
        return index;
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
            ".rs"             => "*.rs",
            ".rb"             => "*.rb",
            ".php"            => "*.php",
            ".cpp" or ".cxx"  => "*.cpp",
            ".c"              => "*.c",
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
        path.Contains(@"\obj\")          ||
        path.Contains(@"\bin\")          ||
        path.Contains(@"\.git\")         ||
        path.Contains(@"\node_modules\") ||
        path.Contains(@"\dist\")         ||
        path.Contains(@"\build\")        ||
        path.Contains(@"\.generated\")   ||
        // Linux-style separators too (cross-platform safety)
        path.Contains("/obj/")           ||
        path.Contains("/bin/")           ||
        path.Contains("/node_modules/")  ||
        path.Contains("/dist/")          ||
        path.Contains("/build/");

    // ── Language dispatch ─────────────────────────────────────────────────────

    private static List<MethodInfo> ParseMethods(string source, string ext, string filePath)
    {
        var raw = ext switch
        {
            ".cs"                     => CSharpAnalyzer.Parse(source),
            ".py"                     => LangAnalyzer.ParsePython(source),
            ".js" or ".jsx"
                or ".ts" or ".tsx"    => LangAnalyzer.ParseJavaScript(source),
            ".java"                   => LangAnalyzer.ParseJava(source),
            ".go"                     => LangAnalyzer.ParseGo(source),
            _                         => LangAnalyzer.ParseGeneric(source)
        };

        // Stamp file path into every record
        return raw.Select(m => m with { FilePath = filePath }).ToList();
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private record MethodInfo(
        string       Name,
        string       Signature,
        int          Line,
        List<string> Calls,
        string       FilePath = "");

    private sealed class DefinitionIndex
    {
        private readonly Dictionary<string, MethodInfo> _defs =
            new(StringComparer.OrdinalIgnoreCase);

        public void TryAdd(string name, MethodInfo info) =>
            _defs.TryAdd(name, info);

        public bool TryFind(string name,
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MethodInfo def) =>
            _defs.TryGetValue(name, out def);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // C# — syntax-level analyser (no Roslyn compiler required)
    // ═════════════════════════════════════════════════════════════════════════

    private static class CSharpAnalyzer
    {
        // Matches method and constructor declarations, including:
        //   • public/private/protected/…  modifiers
        //   • async, static, override, virtual, sealed, abstract, partial, extern, unsafe, new
        //   • return type (void, Task, Task<T>, IEnumerable<…>, etc.)
        //   • method name (captured)
        //   • optional type parameters <T, …>
        //   • parameter list (simplified — handles most real-world cases)
        //   • optional where clause
        //   • opening { or => (expression body)
        private static readonly Regex _decl = new(
            @"(?m)^[ \t]*" +
            @"(?:(?:public|private|protected|internal|static|async|override|virtual" +
            @"|sealed|abstract|partial|extern|unsafe|new|readonly|required)\s+)*" +
            @"(?:[\w<>?,\[\]\.\s]+?\s+)" +     // return type (non-greedy)
            @"([\w]+)\s*" +                     // ① method name
            @"(?:<[^>]*>)?\s*" +               // optional type params
            @"\([^)]{0,512}\)\s*" +            // params (≤512 chars to avoid catastrophic backtracking)
            @"(?:where\s+[^{;]+?)?\s*" +       // optional where clause
            @"(?:\{|=>)",                       // body opens
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Matches invocation-like expressions: identifier( or identifier<T>(
        private static readonly Regex _call = new(
            @"\b([A-Za-z_][\w]*)\s*(?:<[^>()]{0,80}>)?\s*\(",
            RegexOptions.Compiled);

        // C# keywords and common non-method identifiers to suppress
        private static readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase)
        {
            // Control flow
            "if", "else", "while", "for", "foreach", "do", "switch", "case", "default",
            "return", "break", "continue", "goto", "throw", "try", "catch", "finally",
            // Type / declaration
            "using", "lock", "fixed", "unsafe", "checked", "unchecked",
            "typeof", "sizeof", "nameof", "default", "new", "stackalloc",
            "delegate", "event", "static", "abstract", "virtual", "override",
            "sealed", "partial", "extern", "async", "await", "yield", "in", "out", "ref",
            "with", "init", "get", "set", "add", "remove", "value", "void",
            "where", "select", "from", "group", "join", "let", "orderby",
            // Literals
            "true", "false", "null", "base", "this",
            // Common types used in new T(...)
            "string", "int", "long", "short", "byte", "uint", "ulong", "ushort", "sbyte",
            "float", "double", "decimal", "bool", "char", "object", "dynamic",
            "var", "params", "readonly",
            // Generic container types (usually appear as new T<…>())
            "List", "Dictionary", "HashSet", "Queue", "Stack", "LinkedList", "SortedList",
            "SortedDictionary", "SortedSet", "IList", "ICollection", "IEnumerable",
            "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary",
            "Array", "Span", "Memory", "ReadOnlySpan", "ReadOnlyMemory",
            "Task", "ValueTask", "CancellationToken", "CancellationTokenSource",
            "Regex", "Match", "StringBuilder", "StringReader", "StringWriter",
            "StreamReader", "StreamWriter", "BinaryReader", "BinaryWriter",
            "Exception", "ArgumentException", "InvalidOperationException",
            "NullReferenceException", "NotImplementedException",
            "Tuple", "ValueTuple", "Nullable",
            "Action", "Func", "Predicate",
            "Console", "Math", "Convert", "String", "Enum", "GC",
            "Activator", "Environment", "Path", "File", "Directory",
            // Attribute syntax
            "Obsolete", "Serializable", "NotNull", "MaybeNull", "CallerMemberName",
            // LINQ
            "Where", "Select", "OrderBy", "GroupBy", "ThenBy",
            "FirstOrDefault", "SingleOrDefault", "ToList", "ToArray", "ToDictionary",
            "Count", "Any", "All", "Max", "Min", "Sum", "Average",
            // Roslyn / Syntax names that appear frequently in this project
            "Assert", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone",
        };

        public static List<MethodInfo> Parse(string source)
        {
            var methods = new List<MethodInfo>();
            var matchList = _decl.Matches(source).Cast<Match>().ToList();

            for (int i = 0; i < matchList.Count; i++)
            {
                var m    = matchList[i];
                var name = m.Groups[1].Value;

                // Skip if the captured name is a keyword or very short
                if (_skip.Contains(name) || name.Length <= 1) continue;

                int line = CountNewlines(source, m.Index) + 1;
                var sig  = ParseSignature(m.Value);

                // Body: from this match's start to the next match's start (rough approximation)
                int bodyStart = m.Index + m.Length - 1;
                int bodyEnd   = i + 1 < matchList.Count
                    ? matchList[i + 1].Index
                    : source.Length;

                var body  = source[bodyStart..bodyEnd];
                var calls = ExtractCalls(body, name);

                methods.Add(new MethodInfo(name, sig, line, calls));
            }

            return methods;
        }

        private static string ParseSignature(string matchText)
        {
            int ps = matchText.IndexOf('(');
            int pe = matchText.LastIndexOf(')');
            if (ps < 0 || pe <= ps) return "()";

            var paramStr = matchText[(ps + 1)..pe].Trim();
            if (string.IsNullOrWhiteSpace(paramStr)) return "()";

            var types = paramStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p =>
                {
                    var tokens = p.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    return tokens.Length > 0 ? tokens[0] : "?";
                });
            return $"({string.Join(", ", types)})";
        }

        private static List<string> ExtractCalls(string body, string ownerName)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in _call.Matches(body))
            {
                var n = m.Groups[1].Value;
                if (!_skip.Contains(n) && n != ownerName && n.Length > 1)
                    set.Add(n);
                if (set.Count >= MaxCallsPerMethod) break;
            }

            return [.. set];
        }

        private static int CountNewlines(string s, int upTo)
        {
            int count = 0;
            for (int i = 0; i < upTo && i < s.Length; i++)
                if (s[i] == '\n') count++;
            return count;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Other languages — regex-based analysers
    // ═════════════════════════════════════════════════════════════════════════

    private static class LangAnalyzer
    {
        // ── Python ────────────────────────────────────────────────────────────

        private static readonly Regex _pyDecl = new(
            @"(?m)^([ \t]*)(?:async\s+)?def\s+([\w]+)\s*\(([^)]{0,256})\)\s*(?:->.*?)?\s*:",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex _pyCall = new(
            @"\b([a-z_][\w]*)\s*\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> _pyKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "elif", "while", "for", "in", "not", "and", "or", "is",
            "return", "yield", "raise", "try", "except", "finally", "with", "as",
            "import", "from", "class", "def", "lambda", "pass", "break", "continue",
            "assert", "del", "global", "nonlocal", "async", "await",
            "print", "range", "len", "type", "isinstance", "issubclass",
            "list", "dict", "set", "tuple", "str", "int", "float", "bool",
            "super", "object", "property", "staticmethod", "classmethod",
        };

        public static List<MethodInfo> ParsePython(string source)
        {
            var lines     = source.Split('\n');
            var methods   = new List<MethodInfo>();
            var matchList = _pyDecl.Matches(source).Cast<Match>().ToList();

            for (int i = 0; i < matchList.Count; i++)
            {
                var m      = matchList[i];
                var indent = m.Groups[1].Value.Length;
                var name   = m.Groups[2].Value;
                var paramStr = m.Groups[3].Value.Trim();
                int lineNo = CountNewlines(source, m.Index) + 1;
                var sig    = string.IsNullOrEmpty(paramStr) ? "()" : $"({paramStr[..Math.Min(40, paramStr.Length)]})";

                // Body: lines with greater indentation than this def, until next same-level def
                int bodyStart = m.Index + m.Length;
                int bodyEnd   = i + 1 < matchList.Count ? matchList[i + 1].Index : source.Length;
                var body      = source[bodyStart..bodyEnd];

                var calls = new HashSet<string>();
                foreach (Match c in _pyCall.Matches(body))
                {
                    var n = c.Groups[1].Value;
                    if (!_pyKeywords.Contains(n) && n != name && n.Length > 1)
                        calls.Add(n);
                    if (calls.Count >= MaxCallsPerMethod) break;
                }

                methods.Add(new MethodInfo(name, sig, lineNo, [.. calls]));
            }

            return methods;
        }

        // ── JavaScript / TypeScript ───────────────────────────────────────────

        private static readonly Regex _jsDecl = new(
            @"(?m)(?:" +
            // function declaration: function name(
            @"(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*([\w]+)\s*\(" +
            @"|" +
            // arrow / expression: const/let/var name = async? ( or name =>
            @"(?:const|let|var|public|private|protected|static)\s+([\w]+)\s*=\s*(?:async\s+)?\(?[^=]*\)?\s*=>" +
            @"|" +
            // class method: methodName( { or async methodName(
            @"(?:async\s+)?(?:get\s+|set\s+)?([\w]+)\s*\([^)]{0,256}\)\s*\{" +
            @")",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex _jsCall = new(
            @"\b([a-zA-Z_$][\w$]*)\s*\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> _jsKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "while", "for", "of", "in", "do", "switch", "case", "default",
            "return", "break", "continue", "throw", "try", "catch", "finally",
            "new", "delete", "typeof", "instanceof", "void", "yield", "await",
            "function", "class", "extends", "super", "import", "export",
            "const", "let", "var", "this", "true", "false", "null", "undefined",
            "async", "static", "get", "set",
            "console", "Math", "Array", "Object", "String", "Number", "Boolean",
            "Promise", "setTimeout", "setInterval", "clearTimeout", "clearInterval",
            "JSON", "Date", "RegExp", "Error", "Symbol", "Map", "Set", "WeakMap",
            "require", "module", "exports", "process", "Buffer",
        };

        public static List<MethodInfo> ParseJavaScript(string source)
        {
            var methods   = new List<MethodInfo>();
            var matchList = _jsDecl.Matches(source).Cast<Match>().ToList();

            for (int i = 0; i < matchList.Count; i++)
            {
                var m    = matchList[i];
                // First non-empty group is the name
                var name = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value
                         : m.Groups[2].Value.Length > 0 ? m.Groups[2].Value
                         : m.Groups[3].Value;
                if (string.IsNullOrEmpty(name) || _jsKeywords.Contains(name)) continue;

                int lineNo = CountNewlines(source, m.Index) + 1;

                int bodyStart = m.Index + m.Length;
                int bodyEnd   = i + 1 < matchList.Count ? matchList[i + 1].Index : source.Length;
                var body      = source[bodyStart..bodyEnd];

                var calls = new HashSet<string>();
                foreach (Match c in _jsCall.Matches(body))
                {
                    var n = c.Groups[1].Value;
                    if (!_jsKeywords.Contains(n) && n != name && n.Length > 1)
                        calls.Add(n);
                    if (calls.Count >= MaxCallsPerMethod) break;
                }

                methods.Add(new MethodInfo(name, "()", lineNo, [.. calls]));
            }

            return methods;
        }

        // ── Java ──────────────────────────────────────────────────────────────

        private static readonly Regex _javaDecl = new(
            @"(?m)^[ \t]*" +
            @"(?:(?:public|private|protected|static|final|abstract|synchronized|native|strictfp|default)\s+)*" +
            @"(?:<[^>]*>\s+)?" +              // optional generic return type
            @"(?:[\w<>?,\[\]\.]+\s+)" +       // return type
            @"([\w]+)\s*\([^)]{0,256}\)\s*" + // ① name
            @"(?:throws\s+[\w,\s]+)?\s*" +    // throws clause
            @"\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static List<MethodInfo> ParseJava(string source)
            => ParseWithRegex(source, _javaDecl, _jsCall, _jsKeywords, groupIdx: 1);

        // ── Go ────────────────────────────────────────────────────────────────

        private static readonly Regex _goDecl = new(
            @"(?m)^func\s+(?:\([^)]+\)\s+)?([\w]+)\s*\([^)]{0,256}\)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex _goCall = new(
            @"\b([a-zA-Z_][\w]*)\s*\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> _goKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "for", "range", "switch", "case", "default", "select",
            "return", "break", "continue", "goto", "fallthrough",
            "defer", "go", "chan", "func", "type", "struct", "interface", "map",
            "new", "make", "append", "len", "cap", "copy", "delete", "close",
            "panic", "recover", "print", "println", "error", "nil", "true", "false",
        };

        public static List<MethodInfo> ParseGo(string source)
            => ParseWithRegex(source, _goDecl, _goCall, _goKeywords, groupIdx: 1);

        // ── Generic fallback ──────────────────────────────────────────────────

        private static readonly Regex _genericDecl = new(
            @"(?m)(?:function|def|func|sub|void|int|string|bool)\s+([\w]+)\s*\(",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly Regex _genericCall = new(
            @"\b([A-Za-z_][\w]*)\s*\(",
            RegexOptions.Compiled);

        public static List<MethodInfo> ParseGeneric(string source)
            => ParseWithRegex(source, _genericDecl, _genericCall, new HashSet<string>(), groupIdx: 1);

        // ── Shared helper ─────────────────────────────────────────────────────

        private static List<MethodInfo> ParseWithRegex(
            string source,
            Regex  declRegex,
            Regex  callRegex,
            HashSet<string> skipSet,
            int groupIdx)
        {
            var methods   = new List<MethodInfo>();
            var matchList = declRegex.Matches(source).Cast<Match>().ToList();

            for (int i = 0; i < matchList.Count; i++)
            {
                var m    = matchList[i];
                var name = m.Groups[groupIdx].Value;
                if (string.IsNullOrEmpty(name) || skipSet.Contains(name)) continue;

                int lineNo = CountNewlines(source, m.Index) + 1;

                int bodyStart = m.Index + m.Length;
                int bodyEnd   = i + 1 < matchList.Count ? matchList[i + 1].Index : source.Length;
                var body      = source[bodyStart..bodyEnd];

                var calls = new HashSet<string>();
                foreach (Match c in callRegex.Matches(body))
                {
                    var n = c.Groups[1].Value;
                    if (!skipSet.Contains(n) && n != name && n.Length > 1)
                        calls.Add(n);
                    if (calls.Count >= MaxCallsPerMethod) break;
                }

                methods.Add(new MethodInfo(name, "()", lineNo, [.. calls]));
            }

            return methods;
        }

        private static int CountNewlines(string s, int upTo)
        {
            int count = 0;
            for (int i = 0; i < upTo && i < s.Length; i++)
                if (s[i] == '\n') count++;
            return count;
        }
    }
}
