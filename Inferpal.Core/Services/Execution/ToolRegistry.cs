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

    /// <summary>The approval pipeline this registry gates its tools with — exposed so that
    /// `/tdd`'s debugger capture asks consent through the same prompt as everything else.</summary>
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
        Register(new GetDiagnosticsTool(editor, () => indexService.RootDir));
        Register(new GetActiveDocumentTool(editor));
        Register(new FetchUrlTool(approval));
        Register(new WebSearchTool(approval));
        Register(new GetSolutionInfoTool(editor, () => indexService.RootDir));
        Register(new GetOpenEditorsTool(editor));
        Register(new GetGitStatusTool(editor, () => indexService.RootDir));
        Register(new RunTestsTool(() => indexService.RootDir));
        Register(new InsertAtCursorTool(editor, approval, _fileHistory));
        Register(new ReplaceSelectionTool(editor, approval, _fileHistory));
        Register(new UpdateMemoryTool(editor, approval, _fileHistory, () => indexService.RootDir));
        // trace_dependency / analyze_impact / trace_nexus are unified behind one analyze_code(mode=…)
        // facade to keep the per-request tool list small (the three strategies live inside it).
        Register(new AnalyzeCodeTool(() => indexService.RootDir));
        Register(new RenameSymbolTool(approval, history, () => indexService.RootDir));
        Register(new SemanticSearchTool(indexService, client, config));
        Register(new SearchDocsTool(docsIndex, client, config));
        Register(new GenerateProjectMapTool(mapService));

        // Registered only when the front-end can actually drive a debugger: a tool whose every
        // answer is "unavailable here" costs prompt tokens on every turn and teaches a small model
        // to keep trying. The step budget is per registry, i.e. per editor session, and is reset by
        // each `start`.
        if (debug is not null)
        {
            var budget = new DebugStepBudget();
            Register(new DebugControlTool(debug, approval, budget, () => indexService.RootDir));
            Register(new DebugInspectTool(debug, () => indexService.RootDir));
            // Same gate, and it was missing here. `get_debugger_state` was registered
            // unconditionally, ten lines above the comment forbidding exactly that: with no
            // debugger of any kind its every answer is "no paused debug session".
            Register(new GetDebuggerStateTool(debug, () => indexService.RootDir));
        }

        // ⚠ There is deliberately no sub-agent `delegate` tool, and its absence is a decision, not
        // an omission: built and measured twice, it saved prompt tokens on the main thread and
        // roughly halved the accuracy, because the parent re-explores whatever the sub-agents fail
        // to establish. Do not re-add it (last implementation: commit 60e68a1).
    }

    /// <summary>
    /// The same tool surface, wired to a different <see cref="IApprovalService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by a background task running in proposal mode, whose approval service records every
    /// request and grants none. The service is injected into each tool's constructor, so it cannot
    /// be swapped by a wrapper: a fresh registry is the only construction where no tool holds a
    /// reference to the real prompting service. That is the point, not an inconvenience.
    /// </para>
    /// <para>
    /// The sibling <b>shares this registry's</b> <see cref="FileHistoryService"/>. A fresh one was
    /// harmless while every sibling was proposal-mode and nothing wrote, but <c>/tdd</c> routes real
    /// writes through a sibling: its snapshots then landed in a history nobody could see, and every
    /// <c>/tdd</c> edit escaped <c>/undo-run</c>. Sharing is safe for proposal mode — a recorder
    /// that refuses every write never reaches the snapshot path.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠ The debug surface is <b>not</b> carried over. A background task must not be able to launch
    /// the user's program: starting a session is an execution, and deferring an execution to be
    /// approved later is a blank cheque. The sibling registry therefore has no debug tools at all,
    /// rather than tools whose approval would be recorded as a proposal.
    /// </remarks>
    public ToolRegistry WithApprovalService(IApprovalService approval) =>
        new(_editor, approval, _config, _indexService, _client, _mapService, _mcp, _docsIndex, _overlay, debug: null, fileHistory: _fileHistory);

    /// <summary>
    /// The shell tools the user declared in <c>CustomTools</c>, one <c>name=command</c> per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This property is recomputed on every read of <see cref="Definitions"/></b>, that is at
    /// least three times per request to the model (the client's <c>.Count</c> then its
    /// <c>.ToList()</c>, the set of known names, the orchestrator's two guards). That is why its
    /// rejections go through <see cref="Diagnostics.DroppedLineOnce"/>: said on every pass, they
    /// wipe the diagnostics ring within a few agent turns.
    /// </para>
    /// <para>
    /// ⚠ And two lines cannot claim the same name. The name is <b>normalised</b> (lower-cased,
    /// spaces to underscores), so <c>My Tool=…</c> and <c>my_tool=…</c> are two lines the user reads
    /// as distinct that yield a single name — the same trap <c>McpToolService</c> documents for
    /// <c>my-server</c> and <c>my.server</c>. Without a guard the backend receives two function
    /// definitions with the same name, which is malformed in the OpenAI tool schema, and
    /// <see cref="ExecuteAsync"/> always runs the first: the second command never runs, silently.
    /// </para>
    /// <para>
    /// The second one is <b>dropped</b>, not renamed: unlike an MCP tool, whose name is derived from
    /// its server, here the name is the one the user wrote — exposing a <c>my_tool_2</c> would put a
    /// name in the model's list that appears nowhere in their configuration. Same arbitration as for
    /// a clash with a built-in tool.
    /// </para>
    /// </remarks>
    private IEnumerable<ITool> UserTools
    {
        get
        {
            var tools = new List<ITool>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in (_config.CustomTools ?? string.Empty)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#')) continue;   // '#' prefix = disabled entry

                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    Diagnostics.DroppedLineOnce("CustomTools", "Custom tool ignored (expected name=command)", line, line);
                    continue;
                }
                var name = line[..eq].Trim().ToLowerInvariant().Replace(' ', '_');
                var cmd  = line[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd))
                {
                    Diagnostics.DroppedLineOnce("CustomTools", "Custom tool ignored (empty name or command)", line, line);
                    continue;
                }
                // The most misleading of the three silences: the arbitration is right - a built-in
                // keeps its name - but the user is left watching a tool they declared never being
                // called, and CustomTools is read by the model rather than typed by them: nothing
                // announces it anywhere else.
                if (_tools.ContainsKey(name))
                {
                    Diagnostics.DroppedLineOnce(
                        "CustomTools", $"Custom tool ignored ('{name}' is a built-in tool)", line, line);
                    continue;
                }
                if (!claimed.Add(name))
                {
                    Diagnostics.DroppedLineOnce(
                        "CustomTools",
                        $"Custom tool ignored ('{name}' is already declared by an earlier line, "
                        + "and two tools cannot share a name)", line, line);
                    continue;
                }
                // ⚠ This line is accepted NOW. The duplicate-name rejection above depends on the
                // OTHER lines, not on this one, so its key does not move when the clash clears:
                // remove the earlier declaration and this line works, put it back and the clash
                // returns — silently, for the life of the process, without this.
                Diagnostics.Forget("CustomTools", line);
                tools.Add(new UserShellTool(name, cmd, _approval, _config));
            }

            return tools;
        }
    }

    /// <summary>The MCP tools rebound to <b>this</b> registry's approval pipeline. The shared
    /// <see cref="McpToolService"/> built them with the original service; served raw, a sibling
    /// registry (<see cref="WithApprovalService"/>) would gate every tool it exposes EXCEPT the MCP
    /// ones — the decorator silently not applying to the very tools that can write files.</summary>
    private IEnumerable<ITool> McpTools =>
        _mcp.Tools.Select(t => t is Mcp.McpTool m ? m.WithApproval(_approval) : t);

    /// <summary>MCP server lines for the support bundle. The chat view-model holds this registry,
    /// not the MCP service (only the settings window does), and the bundle must say the same
    /// thing in both front-ends — so the single reader is delegated from here.</summary>
    public IReadOnlyList<string> DescribeMcpForBundle() => _mcp.DescribeForBundle();

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
            // ⚠ "Unknown tool" means "you invented this name". A tool served by an MCP server that
            // went away mid-run was not invented: the model READ it in its own tool list, and the
            // reason it is gone sits one field away from here.
            return _mcp.DescribeMissingTool(name) ?? $"Unknown tool: {name}";
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not the caller's cancellation: the tool's own deadline (an HttpClient timeout, an MCP
            // call budget). An error for whoever asked — never a Stop of the run that called it.
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: true);
            return $"Tool '{name}' timed out before it finished ({ex.Message}).";
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
            return ToolFailure.Describe(name, ex);
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
