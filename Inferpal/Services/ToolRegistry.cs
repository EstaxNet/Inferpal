using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Docs;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;

namespace Inferpal.Services;

/// <summary>
/// Registers all available tools and routes execution to the correct <see cref="Tools.ITool"/> implementation.
/// </summary>
/// <remarks>
/// To add a new tool: implement <see cref="Tools.ITool"/> in <c>Services/Tools/</c>
/// and add <c>Register(new MyTool())</c> in the constructor.
/// </remarks>
internal class ToolRegistry : IToolRegistry
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

    public ToolRegistry(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, IApprovalService approval, InferpalConfig config, ProjectIndexService indexService, IInferenceProvider client, ProjectMapService mapService, McpToolService mcp, DocsIndexService docsIndex)
    {
        _config   = config;
        _approval = approval;
        _mcp      = mcp;

        var history  = _fileHistory;
        var smartFix = new SmartFixValidator(config, () => indexService.RootDir);
        var setDiff  = (DiffInfo? d) => { _pendingDiff = d; };

        Register(new ReadFileTool(() => indexService.RootDir));
        Register(new WriteFileTool(approval, history, () => indexService.RootDir, smartFix, setDiff));
        Register(new ListFilesTool(() => indexService.RootDir));
        Register(new SearchInFilesTool(() => indexService.RootDir));
        Register(new RunCommandTool(approval, config, () => indexService.RootDir));
        Register(new ApplyDiffTool(approval, history, () => indexService.RootDir, smartFix, setDiff));
        Register(new ApplyEditsTool(approval, history, () => indexService.RootDir, smartFix));
        Register(new RestoreFileTool(history));
        Register(new DeleteFileTool(approval, history, () => indexService.RootDir));
        Register(new GetDiagnosticsTool());
        Register(new GetActiveDocumentTool(extensibility, contextHolder));
        Register(new FetchUrlTool(approval));
        Register(new WebSearchTool(approval));
        Register(new GetSolutionInfoTool(contextHolder));
        Register(new GetOpenEditorsTool(contextHolder));
        Register(new GetGitStatusTool(contextHolder));
        Register(new GetDebuggerStateTool());
        Register(new RunTestsTool());
        Register(new InsertAtCursorTool(extensibility, contextHolder));
        Register(new ReplaceSelectionTool(extensibility, contextHolder));
        Register(new UpdateMemoryTool(contextHolder));
        // trace_dependency / analyze_impact / trace_nexus are unified behind one analyze_code(mode=…)
        // facade to keep the per-request tool list small (the three strategies live inside it).
        Register(new AnalyzeCodeTool(() => indexService.RootDir));
        Register(new RenameSymbolTool(approval, history, () => indexService.RootDir));
        Register(new SemanticSearchTool(indexService, client, config));
        Register(new SearchDocsTool(docsIndex, client, config));
        Register(new GenerateProjectMapTool(mapService));
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
            return $"Unknown tool: {name}";

        try
        {
            return await tool.ExecuteAsync(args, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool '{name}' error: {ex.Message}";
        }
    }

    private void Register(ITool tool) => _tools[tool.Name] = tool;
}
