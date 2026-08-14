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
    private readonly FileHistoryService        _fileHistory = new();
    private DiffInfo? _pendingDiff;

    public DiffInfo? ConsumeDiff() { var d = _pendingDiff; _pendingDiff = null; return d; }

    /// <summary>File snapshot/restore service, exposed so the VM can begin a change-tracking run
    /// (<see cref="FileHistoryService.BeginRun"/>) and run <c>/undo-run</c>.</summary>
    public FileHistoryService History => _fileHistory;

    /// <param name="overlay">Open-document mirror for dirty-buffer reads;
    /// null when the editor feeds no overlay (VS in-proc today).</param>
    public ToolRegistry(IEditorSurface editor, IApprovalService approval, InferpalConfig config, ProjectIndexService indexService, IInferenceProvider client, ProjectMapService mapService, McpToolService mcp, DocsIndexService docsIndex, OpenDocumentOverlay? overlay = null)
    {
        _config   = config;
        _approval = approval;
        _mcp      = mcp;

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
        Register(new GetGitStatusTool(editor));
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

        // ⚠ No `delegate` tool here. It was built and measured (roadmap §11) and removed on
        // 2026-07-31: it saved 91 % of the main thread's prompt tokens but halved accuracy
        // (4/12 → 2/12), and the pre-registered gate makes degraded accuracy fatal whatever the
        // saving. Do not re-add it without the redesign and the new gate described in §20.
    }

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

    public IReadOnlyList<ToolDefinition> Definitions =>
        _tools.Values
            .Concat(UserTools)
            .Concat(_mcp.Tools)
            .Select(t => new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters)))
            .ToList();

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            tool = UserTools.FirstOrDefault(t => t.Name == name)
                ?? _mcp.Tools.FirstOrDefault(t => t.Name == name);

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
