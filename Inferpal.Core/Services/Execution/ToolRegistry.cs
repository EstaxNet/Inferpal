using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>
/// Registers all available tools and routes execution to the correct <see cref="Tools.ITool"/> implementation.
/// </summary>
/// <remarks>
/// To add a new tool: implement <see cref="Tools.ITool"/> in <c>Services/Tools/</c>
/// and add <c>Register(new MyTool())</c> in the constructor.
/// </remarks>
internal class ToolRegistry : IToolRegistry, IDisposable
{
    private readonly Dictionary<string, ITool> _tools = [];
    private readonly InferpalConfig         _config;
    private readonly IApprovalService          _approval;
    private readonly McpToolService            _mcp;
    private readonly FileHistoryService        _fileHistory;
    private DiffInfo? _pendingDiff;

    // Kept so a sibling registry can be built with a different approval service — see
    // WithApprovalService. Storing the composition is cheaper than threading a factory through
    // every front-end that owns a registry.
    private readonly IEditorSurface       _editor;
    private readonly ProjectIndexService  _indexService;
    private readonly IInferenceProvider   _client;
    private readonly ProjectMapService    _mapService;
    private readonly DocsIndexService     _docsIndex;
    private readonly OpenDocumentOverlay? _overlay;
    private readonly IDebugSession?       _debug;

    public DiffInfo? ConsumeDiff() { var d = _pendingDiff; _pendingDiff = null; return d; }

    /// <summary>File snapshot/restore service, exposed so the VM can begin a change-tracking run
    /// (<see cref="FileHistoryService.BeginRun"/>) and run <c>/undo-run</c>.</summary>
    public FileHistoryService History => _fileHistory;

    /// <summary>
    /// The debugger surface the two debug tools were built with, or <c>null</c> on a front-end that
    /// has none. Exposed so <c>/debug</c> reports on the very session the model drives rather than
    /// on a second one built beside it.
    /// </summary>
    public IDebugSession? Debug => _debug;

    /// <summary>The approval pipeline this registry gates its tools with — exposed so §25's
    /// `/tdd` capture asks consent through the same prompt as everything else.</summary>
    public IApprovalService Approval => _approval;

    /// <param name="overlay">Open-document mirror for dirty-buffer reads;
    /// null when the editor feeds no overlay (VS in-proc today).</param>
    /// <param name="debug">Debugger surface of the host editor; null when this front-end has none,
    /// in which case the two debug tools are not registered at all — the model is never shown a
    /// tool that can only answer that it is unavailable.</param>
    public ToolRegistry(IEditorSurface editor, IApprovalService approval, InferpalConfig config, ProjectIndexService indexService, IInferenceProvider client, ProjectMapService mapService, McpToolService mcp, DocsIndexService docsIndex, OpenDocumentOverlay? overlay = null, IDebugSession? debug = null, FileHistoryService? fileHistory = null)
    {
        _config      = config;
        _approval    = approval;
        _mcp         = mcp;
        _fileHistory = fileHistory ?? new();

        _editor       = editor;
        _indexService = indexService;
        _client       = client;
        _mapService   = mapService;
        _docsIndex    = docsIndex;
        _overlay      = overlay;
        _debug        = debug;

        var history  = _fileHistory;
        // The approval service is passed so a build command coming from the workspace's
        // `.inferpal/validators.json` — i.e. written by the repository — is shown to the user
        // before it runs, instead of running by itself after a write.
        var smartFix = new SmartFixValidator(config, () => indexService.RootDir, approval);
        var setDiff  = (DiffInfo? d) => { _pendingDiff = d; };

        Register(new ReadFileTool(() => indexService.RootDir, overlay));
        Register(new WriteFileTool(approval, history, () => indexService.RootDir, smartFix, setDiff));
        Register(new ListFilesTool(() => indexService.RootDir));
        Register(new SearchInFilesTool(() => indexService.RootDir));
        Register(new RunCommandTool(approval, config, () => indexService.RootDir));
        Register(new ApplyDiffTool(approval, history, () => indexService.RootDir, smartFix, setDiff));
        Register(new ApplyEditsTool(approval, history, () => indexService.RootDir, smartFix));
        Register(new RestoreFileTool(approval, history, () => indexService.RootDir));
        Register(new DeleteFileTool(approval, history, () => indexService.RootDir));
        Register(new GetDiagnosticsTool(editor));
        Register(new GetActiveDocumentTool(editor));
        Register(new FetchUrlTool(approval));
        Register(new WebSearchTool(approval));
        Register(new GetSolutionInfoTool(editor));
        Register(new GetOpenEditorsTool(editor));
        Register(new GetGitStatusTool(editor, () => indexService.RootDir));
        Register(new GetDebuggerStateTool());
        Register(new RunTestsTool());
        Register(new InsertAtCursorTool(editor));
        Register(new ReplaceSelectionTool(editor));
        Register(new UpdateMemoryTool(editor));
        // trace_dependency / analyze_impact / trace_nexus are unified behind one analyze_code(mode=…)
        // facade to keep the per-request tool list small (the three strategies live inside it).
        Register(new AnalyzeCodeTool(() => indexService.RootDir));
        Register(new RenameSymbolTool(approval, history, () => indexService.RootDir));
        Register(new SemanticSearchTool(indexService, client, config));
        Register(new SearchDocsTool(docsIndex, client, config));
        Register(new GenerateProjectMapTool(mapService));

        // Roadmap §21. Registered only when the front-end can actually drive a debugger: a tool
        // whose every answer is "unavailable here" costs prompt tokens on every turn and teaches a
        // small model to keep trying. The step budget is per registry, i.e. per editor session, and
        // is reset by each `start`.
        if (debug is not null)
        {
            var budget = new DebugStepBudget();
            Register(new DebugControlTool(debug, approval, budget, () => indexService.RootDir));
            Register(new DebugInspectTool(debug, () => indexService.RootDir));
        }

        // ⚠ No `delegate` tool here, and this one is closed rather than merely absent. It was built
        // and measured twice against gates registered before the code was written:
        //   §11 (2026-07-31) — 91 % of the main thread's prompt tokens saved, accuracy halved
        //                      (4/12 → 2/12). Cut; the redesign was scoped as §20.
        //   §20 (2026-08-02) — one sub-agent per target, explicit budget, audited citations. On a
        //                      valid reference arm (58,3 %, inside the 40-80 % window): 7/36 against
        //                      21/36, and only 13,9 % of tokens saved because the parent re-explored
        //                      what the sub-agents failed to establish.
        // The §20 gate said in advance that a second failure closes the track for good. It did.
        // Do not re-add this tool. Recoverable at commit 60e68a1 if the dossier ever needs reading.
    }

    /// <summary>
    /// The same tool surface, wired to a different <see cref="IApprovalService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by a background task running in proposal mode (roadmap §18), whose approval service
    /// records every request and grants none. The service is injected into each tool's constructor,
    /// so it cannot be swapped by a wrapper: a fresh registry is the only construction where no tool
    /// holds a reference to the real prompting service. That is the point — not an inconvenience.
    /// </para>
    /// <para>
    /// The sibling <b>shares this registry's</b> <see cref="FileHistoryService"/>. It used to get a
    /// fresh, runless one — true no-op while every sibling was proposal-mode (nothing wrote), but
    /// since §25 routes <c>/tdd</c>'s REAL writes through a sibling, those snapshots landed in a
    /// history nobody could see and every <c>/tdd</c> edit silently escaped <c>/undo-run</c>
    /// (pre-1.6.0 architecture review, §1.5). Sharing is safe for proposal mode: a recorder that refuses every
    /// write never reaches the snapshot path.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠ The debug surface is <b>not</b> carried over. A background task must not be able to launch
    /// the user's program: starting a session is an execution, and deferring an execution to be
    /// approved later is the blank cheque §9 refused. The sibling registry therefore has no debug
    /// tools at all, rather than tools whose approval would be recorded as a proposal.
    /// </remarks>
    public ToolRegistry WithApprovalService(IApprovalService approval) =>
        new(_editor, approval, _config, _indexService, _client, _mapService, _mcp, _docsIndex, _overlay, debug: null, fileHistory: _fileHistory);

    private IEnumerable<ITool> UserTools =>
        (_config.CustomTools ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))   // '#' prefix = disabled entry
            .Select(line =>
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) return null;
                var name = line[..eq].Trim().ToLowerInvariant().Replace(' ', '_');
                var cmd  = line[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd)) return null;
                if (_tools.ContainsKey(name)) return null; // built-in tools take priority
                return (ITool)new UserShellTool(name, cmd, _approval, _config);
            })
            .Where(t => t is not null)!;

    /// <summary>The MCP tools rebound to <b>this</b> registry's approval pipeline. The shared
    /// <see cref="McpToolService"/> built them with the original service; served raw, a sibling
    /// registry (<see cref="WithApprovalService"/>) would gate every tool it exposes EXCEPT the
    /// MCP ones — the decorator (e.g. §25's TestFileWriteGuard) silently not applying to the very
    /// tools that can write files (pre-1.6.0 architecture review, §1.5).</summary>
    private IEnumerable<ITool> McpTools =>
        _mcp.Tools.Select(t => t is Mcp.McpTool m ? m.WithApproval(_approval) : t);

    public IReadOnlyList<ToolDefinition> Definitions =>
        _tools.Values
            .Concat(UserTools)
            .Concat(McpTools)
            .Select(t => new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters)))
            .ToList();

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            tool = UserTools.FirstOrDefault(t => t.Name == name)
                ?? McpTools.FirstOrDefault(t => t.Name == name);

        if (tool is null)
        {
            _fileHistory.RecordToolCall(name, subject: null, durationMs: 0, error: true);
            return $"Unknown tool: {name}";
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // The source-scanning regexes are bounded (RegexBudget): a pathological file — usually
            // generated or minified, one line of megabytes — turns into this instead of a hung turn.
            // Say so explicitly; "The Regex engine has timed out" tells the model nothing actionable.
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: true);
            return $"Tool '{name}' gave up: a file in scope is too pathological to parse with the "
                 + "language heuristics (generated or minified?). Narrow the path and retry.";
        }
        catch (Exception ex)
        {
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: true);
            return $"Tool '{name}' error: {ex.Message}";
        }
    }

    /// <summary>Best-effort human-readable target of a tool call for the run journal:
    /// the first well-known string argument (path, command, query…), <c>null</c> when none.</summary>
    private static string? ExtractSubject(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in (string[])["path", "file_path", "command", "url", "query", "target", "name"])
        {
            if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// Disposes the tools that own OS resources (today: <c>run_command</c> and its detached
    /// background jobs). Called when the editor session ends — a child process spawned with
    /// <c>UseShellExecute=false</c> does not die with its parent.
    /// </summary>
    public void Dispose()
    {
        foreach (var tool in _tools.Values.OfType<IDisposable>())
        {
            try { tool.Dispose(); }
            catch (Exception ex) { Diagnostics.Swallow($"ToolRegistry.Dispose({tool.GetType().Name})", ex); }
        }
    }
}
