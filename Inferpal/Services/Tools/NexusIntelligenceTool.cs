using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

/// <summary>
/// Maps cross-language dependency bridges between C# and TypeScript/JavaScript:
/// REST API routes, Blazor JS interop calls, and SignalR hub connections.
/// Answers "which TS code calls this C# endpoint?" and vice versa.
/// </summary>
internal sealed class NexusIntelligenceTool : ITool
{
    private const int MaxFilesScanned = 500;

    private readonly Func<string?> _getRoot;

    public NexusIntelligenceTool(Func<string?> getRoot) => _getRoot = getRoot;

    public string Name => "trace_nexus";

    public string Description =>
        "Maps cross-language dependency bridges between C# and TypeScript/JavaScript across the project. " +
        "Detects REST API endpoints (attribute + minimal-API routing) with their TypeScript callers (fetch/axios/http), " +
        "Blazor JS interop (InvokeAsync ↔ JS function definitions), and SignalR hubs (C# methods ↔ TS invoke/on). " +
        "Use 'focus' to narrow to a specific route, function, or hub method name.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            root = new
            {
                type        = "string",
                description = "Root directory to scan. Defaults to the solution root when omitted."
            },
            focus = new
            {
                type        = "string",
                description = "Optional: filter results to a specific route path, function name, or hub method (case-insensitive partial match)."
            },
            bridges = new
            {
                type        = "string",
                description = "Which bridges to include: 'rest' | 'interop' | 'signalr' | 'all' (default)."
            }
        }
    };

    // ── Entry point ────────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root   = PathSanitizer.Sanitize(
                         (args.TryGetProperty("root",  out var rv) ? rv.GetString() : null)
                         ?? _getRoot());
        // Keep scanning inside the workspace — the LLM must not point this at arbitrary disk paths.
        PathSanitizer.AssertUnderRoot(root, _getRoot());
        var focus  = args.TryGetProperty("focus", out var fv)  ? fv.GetString()?.Trim().ToLowerInvariant() : null;
        // 'bridges' selects which bridge kinds to scan. (Renamed from 'mode' so it no longer
        // collides with analyze_code's own 'mode' strategy selector, which forwards the same args.)
        var mode   = args.TryGetProperty("bridges", out var mv) ? mv.GetString()?.ToLowerInvariant() : "all";

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return $"Directory not found: '{root}'. Provide a valid 'root' parameter.";

        bool doRest     = mode is "all" or "rest";
        bool doInterop  = mode is "all" or "interop";
        bool doSignalR  = mode is "all" or "signalr";

        // ── Scan source files ──────────────────────────────────────────────────
        var csFiles = EnumerateFiles(root, "*.cs").ToList();
        var tsFiles = EnumerateFiles(root, "*.ts").Concat(EnumerateFiles(root, "*.tsx"))
                          .Concat(EnumerateFiles(root, "*.js")).Concat(EnumerateFiles(root, "*.jsx"))
                          .ToList();

        var csBridges = new CsBridges();
        var tsBridges = new TsBridges();

        foreach (var f in csFiles.Take(MaxFilesScanned))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = await File.ReadAllTextAsync(f, ct);
                if (doRest)    ScanCsRest(src, f, root, csBridges);
                if (doInterop) ScanCsInterop(src, f, root, csBridges);
                if (doSignalR) ScanCsSignalR(src, f, root, csBridges);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("NexusIntelligenceTool.ScanCs", ex); }
        }

        foreach (var f in tsFiles.Take(MaxFilesScanned))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = await File.ReadAllTextAsync(f, ct);
                if (doRest)    ScanTsRest(src, f, root, tsBridges);
                if (doInterop) ScanTsInterop(src, f, root, tsBridges);
                if (doSignalR) ScanTsSignalR(src, f, root, tsBridges);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("NexusIntelligenceTool.ScanTs", ex); }
        }

        // ── Render ─────────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("## Nexus Intelligence — C# ↔ TypeScript cross-language graph");
        sb.AppendLine($"*Scanned {csFiles.Count} C# files · {tsFiles.Count} TS/JS files*");
        sb.AppendLine();

        int linkCount = 0;

        if (doRest)
            linkCount += RenderRest(sb, csBridges.Endpoints, tsBridges.RestCallers, focus);

        if (doInterop)
            linkCount += RenderInterop(sb, csBridges.InteropCalls, tsBridges.JsFuncDefs, focus);

        if (doSignalR)
            linkCount += RenderSignalR(sb, csBridges.HubMethods, tsBridges.SignalRCalls, focus);

        if (linkCount == 0 && focus is not null)
            sb.AppendLine($"*No cross-language bridges found matching `{focus}`.*");
        else if (linkCount == 0)
            sb.AppendLine("*No cross-language bridges detected. This project may not use REST/interop/SignalR, or the patterns were not recognized.*");

        return sb.ToString().TrimEnd();
    }

    // ── Renderers ──────────────────────────────────────────────────────────────

    private static int RenderRest(
        StringBuilder sb,
        List<RestEndpoint> endpoints,
        List<RestCaller>   callers,
        string? focus)
    {
        var visible = endpoints
            .Where(e => focus is null || e.Route.Contains(focus, StringComparison.OrdinalIgnoreCase)
                                     || e.MethodName.Contains(focus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Route)
            .ToList();

        if (visible.Count == 0) return 0;

        sb.AppendLine("### REST Endpoints  (C# → TypeScript callers)");
        sb.AppendLine(new string('─', 60));

        int links = 0;
        foreach (var ep in visible)
        {
            var verb = ep.Verb.PadRight(7);
            sb.AppendLine($"  {verb} `{ep.Route}`");
            sb.AppendLine($"    ← C# `{ep.RelFile}:{ep.Line}`" +
                          (ep.MethodName.Length > 0 ? $"  `{ep.MethodName}()`" : string.Empty));

            var matched = callers
                .Where(c => RouteMatches(ep.Route, c.Route))
                .ToList();

            if (matched.Count > 0)
            {
                foreach (var m in matched)
                    sb.AppendLine($"    ↔  {m.Verb} `{m.Route}`  TS: `{m.RelFile}:{m.Line}`");
                links += matched.Count;
            }
            else
            {
                sb.AppendLine("    ↔  *(no TypeScript callers found)*");
            }
            sb.AppendLine();
        }

        // Orphaned TS callers (no matching C# endpoint found)
        var orphans = callers
            .Where(c => focus is null || c.Route.Contains(focus, StringComparison.OrdinalIgnoreCase))
            .Where(c => !endpoints.Any(e => RouteMatches(e.Route, c.Route)))
            .ToList();

        if (orphans.Count > 0)
        {
            sb.AppendLine("  *TS callers with no matching C# endpoint:*");
            foreach (var o in orphans.Take(10))
                sb.AppendLine($"    • {o.Verb.PadRight(7)} `{o.Route}`  — `{o.RelFile}:{o.Line}`");
            sb.AppendLine();
        }

        return links;
    }

    private static int RenderInterop(
        StringBuilder sb,
        List<InteropCall> calls,
        List<JsFuncDef>   defs,
        string? focus)
    {
        var visible = calls
            .Where(c => focus is null || c.FuncName.Contains(focus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.FuncName)
            .ToList();

        if (visible.Count == 0) return 0;

        sb.AppendLine("### JS Interop  (C# InvokeAsync ↔ TypeScript/JS functions)");
        sb.AppendLine(new string('─', 60));

        int links = 0;
        foreach (var call in visible)
        {
            sb.AppendLine($"  `{call.FuncName}()`");
            sb.AppendLine($"    ← C# `{call.RelFile}:{call.Line}`  InvokeAsync");

            var matched = defs
                .Where(d => d.FuncName.Equals(call.FuncName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count > 0)
            {
                foreach (var d in matched)
                    sb.AppendLine($"    ↔  TS/JS: `{d.RelFile}:{d.Line}`  function {d.FuncName}()");
                links += matched.Count;
            }
            else
            {
                sb.AppendLine("    ↔  *(no JS definition found)*");
            }
            sb.AppendLine();
        }

        return links;
    }

    private static int RenderSignalR(
        StringBuilder sb,
        List<HubMethod>    methods,
        List<SignalRCall>  srCalls,
        string? focus)
    {
        var visible = methods
            .Where(m => focus is null || m.Name.Contains(focus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name)
            .ToList();

        if (visible.Count == 0 && srCalls.Count == 0) return 0;

        sb.AppendLine("### SignalR Hubs  (C# Hub methods ↔ TypeScript invoke/on)");
        sb.AppendLine(new string('─', 60));

        int links = 0;
        foreach (var m in visible)
        {
            var dir = m.Direction == "send" ? "Clients.SendAsync →" : "Hub method ←";
            sb.AppendLine($"  `{m.Name}()`  [{dir}]");
            sb.AppendLine($"    ← C# `{m.RelFile}:{m.Line}`  {m.HubClass}");

            var matched = srCalls
                .Where(c => c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count > 0)
            {
                foreach (var c in matched)
                    sb.AppendLine($"    ↔  TS: `{c.RelFile}:{c.Line}`  .{c.Direction}(\"{c.Name}\")");
                links += matched.Count;
            }
            else
            {
                sb.AppendLine("    ↔  *(no TypeScript counterpart found)*");
            }
            sb.AppendLine();
        }

        // Orphaned TS SignalR calls
        var orphans = srCalls
            .Where(c => focus is null || c.Name.Contains(focus, StringComparison.OrdinalIgnoreCase))
            .Where(c => !methods.Any(m => m.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (orphans.Count > 0)
        {
            sb.AppendLine("  *TS SignalR calls with no matching C# hub method:*");
            foreach (var o in orphans.Take(10))
                sb.AppendLine($"    • .{o.Direction}(\"{o.Name}\")  — `{o.RelFile}:{o.Line}`");
            sb.AppendLine();
        }

        return links;
    }

    // ── C# scanners ────────────────────────────────────────────────────────────

    // [HttpGet("/route")] / [HttpPost] / [Route("...")]
    private static readonly Regex _csHttpAttr = new(
        @"\[\s*(?:Http(Get|Post|Put|Delete|Patch)|Route)\s*(?:\(\s*""([^""]*)""\s*\))?\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // app.MapGet("route", ...) / MapPost / ...
    private static readonly Regex _csMapRoute = new(
        @"\.Map(Get|Post|Put|Delete|Patch)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // method name from declaration after Http* attribute
    private static readonly Regex _csMethName = new(
        @"(?:public|private|protected|internal)[^(]+?\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static void ScanCsRest(string src, string file, string root, CsBridges out_)
    {
        var lines = src.Split('\n');
        string? pendingVerb  = null;
        string? pendingRoute = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var attrMatch = _csHttpAttr.Match(line);
            if (attrMatch.Success)
            {
                pendingVerb  = attrMatch.Groups[1].Success ? attrMatch.Groups[1].Value.ToUpper() : "ANY";
                pendingRoute = attrMatch.Groups[2].Success ? attrMatch.Groups[2].Value : null;
                continue;
            }

            var mapMatch = _csMapRoute.Match(line);
            if (mapMatch.Success)
            {
                out_.Endpoints.Add(new RestEndpoint(
                    mapMatch.Groups[1].Value.ToUpper(),
                    mapMatch.Groups[2].Value,
                    Path.GetRelativePath(root, file), i + 1, string.Empty));
                continue;
            }

            if (pendingVerb is not null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("[")) continue; // stacked attributes

                var mthMatch = _csMethName.Match(line);
                if (mthMatch.Success)
                {
                    out_.Endpoints.Add(new RestEndpoint(
                        pendingVerb,
                        pendingRoute ?? string.Empty,
                        Path.GetRelativePath(root, file), i + 1,
                        mthMatch.Groups[1].Value));
                }
                pendingVerb  = null;
                pendingRoute = null;
            }
        }
    }

    private static readonly Regex _csJsInvoke = new(
        @"Invoke(?:Async|VoidAsync)\s*(?:<[^>]+>)?\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static void ScanCsInterop(string src, string file, string root, CsBridges out_)
    {
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in _csJsInvoke.Matches(lines[i]))
                out_.InteropCalls.Add(new InteropCall(
                    m.Groups[1].Value, Path.GetRelativePath(root, file), i + 1));
        }
    }

    private static readonly Regex _csHubClass  = new(@"class\s+(\w+)\s*:\s*Hub(?:<[^>]+>)?", RegexOptions.Compiled);
    private static readonly Regex _csHubMethod = new(
        @"(?:public|protected)\s+(?:(?:async|override|virtual|Task|ValueTask)\s+)*(?:Task<[^>]+>|Task|ValueTask|void)\s+(\w+)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex _csSendAsync = new(
        @"\.SendAsync\s*\(\s*""([^""]+)""", RegexOptions.Compiled);

    private static void ScanCsSignalR(string src, string file, string root, CsBridges out_)
    {
        // Find hub classes
        var hubMatch = _csHubClass.Match(src);
        if (!hubMatch.Success) return;

        var hubClass = hubMatch.Groups[1].Value;
        var lines    = src.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var m = _csHubMethod.Match(lines[i]);
            if (m.Success)
                out_.HubMethods.Add(new HubMethod(
                    m.Groups[1].Value, "receive",
                    Path.GetRelativePath(root, file), i + 1, hubClass));

            var s = _csSendAsync.Match(lines[i]);
            if (s.Success)
                out_.HubMethods.Add(new HubMethod(
                    s.Groups[1].Value, "send",
                    Path.GetRelativePath(root, file), i + 1, hubClass));
        }
    }

    // ── TS/JS scanners ─────────────────────────────────────────────────────────

    private static readonly Regex _tsFetch = new(
        @"\bfetch\s*\(\s*[""'`]([^""'`$][^""'`]*)[""'`]",
        RegexOptions.Compiled);

    private static readonly Regex _tsAxios = new(
        @"\baxios\.(get|post|put|delete|patch)\s*(?:<[^>]+>)?\s*\(\s*[""'`]([^""'`$][^""'`]*)[""'`]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _tsHttp = new(
        @"\bhttp\.(get|post|put|delete|patch)\s*(?:<[^>]+>)?\s*\(\s*[""'`]([^""'`$][^""'`]*)[""'`]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ScanTsRest(string src, string file, string root, TsBridges out_)
    {
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var rel = Path.GetRelativePath(root, file);
            foreach (Match m in _tsFetch.Matches(lines[i]))
                out_.RestCallers.Add(new RestCaller("FETCH", m.Groups[1].Value, rel, i + 1));

            foreach (Match m in _tsAxios.Matches(lines[i]))
                out_.RestCallers.Add(new RestCaller(m.Groups[1].Value.ToUpper(), m.Groups[2].Value, rel, i + 1));

            foreach (Match m in _tsHttp.Matches(lines[i]))
                out_.RestCallers.Add(new RestCaller(m.Groups[1].Value.ToUpper(), m.Groups[2].Value, rel, i + 1));
        }
    }

    // function name() / export function name() / window.name = / const name = () =>
    private static readonly Regex _tsFuncDef = new(
        @"(?:(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(\w+)\s*\(" +
        @"|window\.(\w+)\s*=" +
        @"|(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?\(?[^=)]*\)?\s*=>)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static void ScanTsInterop(string src, string file, string root, TsBridges out_)
    {
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in _tsFuncDef.Matches(lines[i]))
            {
                var name = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value
                         : m.Groups[2].Value.Length > 0 ? m.Groups[2].Value
                         : m.Groups[3].Value;
                if (!string.IsNullOrEmpty(name))
                    out_.JsFuncDefs.Add(new JsFuncDef(name, Path.GetRelativePath(root, file), i + 1));
            }
        }
    }

    private static readonly Regex _tsSrInvoke = new(
        @"\b\w*[Cc]onnection\b.*?\.invoke\s*\(\s*[""']([^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex _tsSrOn = new(
        @"\b\w*[Cc]onnection\b.*?\.on\s*\(\s*[""']([^""']+)[""']",
        RegexOptions.Compiled);

    private static void ScanTsSignalR(string src, string file, string root, TsBridges out_)
    {
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var rel = Path.GetRelativePath(root, file);
            foreach (Match m in _tsSrInvoke.Matches(lines[i]))
                out_.SignalRCalls.Add(new SignalRCall(m.Groups[1].Value, "invoke", rel, i + 1));
            foreach (Match m in _tsSrOn.Matches(lines[i]))
                out_.SignalRCalls.Add(new SignalRCall(m.Groups[1].Value, "on", rel, i + 1));
        }
    }

    // ── Route matching ─────────────────────────────────────────────────────────

    private static bool RouteMatches(string csRoute, string tsRoute)
    {
        if (string.IsNullOrEmpty(csRoute) || string.IsNullOrEmpty(tsRoute)) return false;

        // Normalize: strip leading slash, lowercase
        static string Norm(string r) => r.TrimStart('/').ToLowerInvariant();

        // Replace {param} with wildcard segment for comparison
        static string Parameterize(string r) =>
            Regex.Replace(Norm(r), @"\{[^}]+\}", "*");

        var csNorm = Parameterize(csRoute);
        var tsNorm = Parameterize(tsRoute);

        if (csNorm == tsNorm) return true;

        // Allow trailing wildcard match: /api/users matches /api/users/
        return csNorm.TrimEnd('/') == tsNorm.TrimEnd('/');
    }

    // ── File enumeration ───────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(f => !IsExcluded(f));
        }
        catch { return []; }
    }

    private static bool IsExcluded(string path) =>
        path.Contains(@"\obj\")          || path.Contains("/obj/")          ||
        path.Contains(@"\bin\")          || path.Contains("/bin/")          ||
        path.Contains(@"\.git\")         || path.Contains("/.git/")         ||
        path.Contains(@"\node_modules\") || path.Contains("/node_modules/") ||
        path.Contains(@"\dist\")         || path.Contains("/dist/")         ||
        path.Contains(@"\.vs\")          || path.Contains("/.vs/");

    // ── Bridge data containers ─────────────────────────────────────────────────

    private sealed class CsBridges
    {
        public readonly List<RestEndpoint> Endpoints    = [];
        public readonly List<InteropCall>  InteropCalls = [];
        public readonly List<HubMethod>    HubMethods   = [];
    }

    private sealed class TsBridges
    {
        public readonly List<RestCaller>  RestCallers  = [];
        public readonly List<JsFuncDef>   JsFuncDefs   = [];
        public readonly List<SignalRCall>  SignalRCalls = [];
    }

    // ── Bridge records ─────────────────────────────────────────────────────────

    private record RestEndpoint(string Verb, string Route, string RelFile, int Line, string MethodName);
    private record RestCaller(string Verb, string Route, string RelFile, int Line);
    private record InteropCall(string FuncName, string RelFile, int Line);
    private record JsFuncDef(string FuncName, string RelFile, int Line);
    private record HubMethod(string Name, string Direction, string RelFile, int Line, string HubClass);
    private record SignalRCall(string Name, string Direction, string RelFile, int Line);
}
