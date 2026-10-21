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
    // The built-in tools in the order they were registered: the definitions are sent in it. ⚠ An order of its own, not
    // the dictionary's — the tool list sits near the head of a request, and a server that reuses its cache reads again
    // everything after the first change.
    private readonly List<ITool> _registered = [];
    private readonly InferpalConfig         _config;
    private readonly IApprovalService          _approval;
    private readonly McpToolService            _mcp;
    private readonly FileHistoryService        _fileHistory;
    private readonly OnDemandTools             _onDemand;
    private readonly PackageCheck              _packageCheck;
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

    /// <inheritdoc/>
    public int? WritesInRun => _fileHistory.CurrentRunWriteCount;

    /// <summary>
    /// The debugger surface the two debug tools were built with, or <c>null</c> on a front-end that
    /// has none. Exposed so <c>/debug</c> reports on the very session the model drives rather than
    /// on a second one built beside it.
    /// </summary>
    public IDebugSession? Debug => _debug;

    /// <summary>The MCP servers behind the registry's MCP tools — refreshed when the workspace's repository changes.</summary>
    internal McpToolService Mcp => _mcp;

    /// <summary>The approval pipeline this registry gates its tools with — exposed so that
    /// `/tdd`'s debugger capture asks consent through the same prompt as everything else.</summary>
    public IApprovalService Approval => _approval;

    /// <param name="overlay">The editor's unsaved buffers, for dirty-buffer reads and the writing tools' refusal:
    /// pushed by VS Code, pulled from Visual Studio before every call; null when the editor gives none.</param>
    /// <param name="debug">Debugger surface of the host editor; null when this front-end has none,
    /// in which case the two debug tools are not registered at all — the model is never shown a
    /// tool that can only answer that it is unavailable.</param>
    /// <param name="onDemand">The MCP tools loaded on demand, shared with the registry this one is a sibling of: the
    /// conversation's loaded tools stay loaded in <c>/tdd</c>, and what it loads stays for the conversation.</param>
    /// <param name="packageCheck">Asks the sources of the project whether a package a call adds exists;
    /// <see cref="PackageCheck.Shared"/> when null.</param>
    public ToolRegistry(IEditorSurface editor, IApprovalService approval, InferpalConfig config, ProjectIndexService indexService, IInferenceProvider client, ProjectMapService mapService, McpToolService mcp, DocsIndexService docsIndex, OpenDocumentOverlay? overlay = null, IDebugSession? debug = null, FileHistoryService? fileHistory = null, OnDemandTools? onDemand = null, PackageCheck? packageCheck = null)
    {
        _config      = config;
        _packageCheck = packageCheck ?? PackageCheck.Shared;
        _approval    = approval;
        _mcp         = mcp;
        // The MCP tools a turned-off switch leaves out never reach the split: they weigh nothing in its budget, and
        // tool_search neither names nor loads them.
        _onDemand    = onDemand ?? new OnDemandTools(McpToolsOn, () => WindowInUse?.Invoke() ?? 0, TurnedOffOnDemand);
        _fileHistory = fileHistory ?? new();
        // Outside a git repository the history lives at the workspace root — one folder, the one restore_file searches.
        _fileHistory.WorkspaceRoot ??= () => indexService.RootDir;
        // Read at each snapshot, from the file: a setting saved mid-session — in this editor or the other — decides where
        // the next backup goes.
        _fileHistory.OutsideProject ??= () => config.SharedHistoryOutsideProject;

        _editor       = editor;
        _indexService = indexService;
        _client       = client;
        _mapService   = mapService;
        _docsIndex    = docsIndex;
        _overlay      = overlay;
        // Stopping through the registry — the tool's `stop` and `/debug stop` alike — removes the breakpoints the
        // assistant set; the ones it did not set are never touched.
        _debug        = debug is null ? null : debug as AgentCleaningDebugSession ?? new AgentCleaningDebugSession(debug, new AgentBreakpoints());

        var history  = _fileHistory;
        // The approval service is passed so a build command coming from the workspace's
        // `.inferpal/validators.json` — i.e. written by the repository — is shown to the user
        // before it runs, instead of running by itself after a write.
        var smartFix = new SmartFixValidator(config, () => indexService.RootDir, approval);
        var setDiff  = (DiffInfo? d) => { _pendingDiff = d; };

        Register(new ReadFileTool(() => indexService.RootDir, overlay, history));
        Register(new WriteFileTool(approval, history, () => indexService.RootDir, smartFix, setDiff, overlay));
        Register(new ListFilesTool(() => indexService.RootDir));
        Register(new SearchInFilesTool(() => indexService.RootDir, overlay));
        Register(new RunCommandTool(approval, config, () => indexService.RootDir));
        Register(new ApplyDiffTool(approval, history, () => indexService.RootDir, smartFix, setDiff, overlay) { RenameOffered = RenameOffered });
        Register(new ApplyEditsTool(approval, history, () => indexService.RootDir, smartFix, overlay) { RenameOffered = RenameOffered });
        Register(new RestoreFileTool(approval, history, () => indexService.RootDir, overlay));
        Register(new DeleteFileTool(approval, history, () => indexService.RootDir, overlay));
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
        Register(new RenameSymbolTool(approval, history, () => indexService.RootDir, overlay));
        Register(new SemanticSearchTool(indexService, client, config));
        Register(new SearchDocsTool(docsIndex, client, config));
        Register(new GenerateProjectMapTool(mapService));
        Register(new ReadSkillFileTool(SkillsRoot, config));

        // Registered only when the front-end can actually drive a debugger: a tool whose every
        // answer is "unavailable here" costs prompt tokens on every turn and teaches a small model
        // to keep trying. The step budget is per registry, i.e. per editor session, and is reset by
        // each `start`.
        if (_debug is not null)
        {
            var budget = new DebugStepBudget();
            Register(new DebugControlTool(_debug, approval, budget, () => indexService.RootDir));
            Register(new DebugInspectTool(_debug, () => indexService.RootDir));
            // Same gate, and it was missing here. `get_debugger_state` was registered
            // unconditionally, ten lines above the comment forbidding exactly that: with no
            // debugger of any kind its every answer is "no paused debug session".
            Register(new GetDebuggerStateTool(_debug, () => indexService.RootDir));
        }

        // Last of the built-ins: offered or not, it never moves the definitions sent before it.
        Register(new ToolSearchTool(_onDemand));

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
        new(_editor, approval, _config, _indexService, _client, _mapService, _mcp, _docsIndex, _overlay, debug: null,
            fileHistory: _fileHistory, onDemand: _onDemand, packageCheck: _packageCheck)
        {
            ProjectRootFallback = ProjectRootFallback,
            WindowInUse         = WindowInUse,
        };

    /// <summary>
    /// The context window the next request is sized for (<c>ContextWindowInUse</c> of the front-end), read when the MCP
    /// tools are split between declared and on demand (<see cref="OnDemandTools"/>); unset or 0: the window is unknown.
    /// </summary>
    public Func<int>? WindowInUse { get; set; }

    /// <summary>Forgets the MCP tools loaded by <c>tool_search</c>: the conversation they were loaded for is gone.</summary>
    public void ForgetLoadedTools() => _onDemand.Forget();

    /// <summary>The MCP servers whose tools wait on demand, and how many are not loaded yet (X-Ray says so).</summary>
    public IReadOnlyList<(string Server, int Tools)> ToolsOnDemand => _onDemand.Waiting();

    /// <summary>
    /// The root the skills are read from while nothing is pinned: the editor's project root (Visual Studio: the open
    /// solution's folder, or the open files'). <c>null</c>: the pinned root only (VS Code always pins its folder).
    /// </summary>
    public Func<string?>? ProjectRootFallback { get; set; }

    /// <summary>
    /// Where the skills are read — by <c>read_skill_file</c>, by <c>/skill</c> — the root the system prompt's catalog
    /// reads: the pinned root, else <see cref="ProjectRootFallback"/>.
    /// </summary>
    /// <remarks>⚠ One root for the catalog and the tool: the prompt advertised the repository's skills while the tool,
    /// reading the empty pinned root, found none of them — the model was told to load what could not be loaded.</remarks>
    public string? SkillsRoot() =>
        string.IsNullOrEmpty(_indexService.RootDir) ? ProjectRootFallback?.Invoke() : _indexService.RootDir;

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
                tools.Add(new UserShellTool(name, cmd, _approval, _config, () => _indexService.RootDir));
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

    // Swapped whole: two threads reading the setting never see one's text with the other's switches.
    private sealed record SwitchesMemo(string Text, ToolSwitches Switches);
    private SwitchesMemo? _switches;

    /// <summary>The tools the user turned off, as the setting says now — read again only when its text changes.</summary>
    /// <remarks>⚠ Per registry, never one slot for the process: two agent-protocol threads whose copies of the setting
    /// differ would take turns in it, and each turn would split and weigh every MCP definition again.</remarks>
    private ToolSwitches Switches
    {
        get
        {
            var text = _config.DisabledTools ?? string.Empty;
            var memo = Volatile.Read(ref _switches);
            if (memo is not null && string.Equals(memo.Text, text, StringComparison.Ordinal)) return memo.Switches;
            var switches = ToolSwitches.Parse(text);
            Volatile.Write(ref _switches, new SwitchesMemo(text, switches));
            return switches;
        }
    }

    /// <summary>
    /// Whether <paramref name="tool"/> is turned off — by the user (<see cref="InferpalConfig.DisabledTools"/>) or, for an
    /// MCP tool, by its server's entry (<c>disabledTools</c>). A tool turned off is never offered to the model.
    /// </summary>
    internal bool TurnedOff(ITool tool) => TurnedOff(tool, Switches);

    private static bool TurnedOff(ITool tool, ToolSwitches switches) =>
        tool is McpTool { TurnedOffByEntry: true } || switches.TurnsOff(tool);

    /// <summary>Whether <c>rename_symbol</c> is offered now: a refused hand rename points to it only then.</summary>
    private bool RenameOffered() => _tools.TryGetValue("rename_symbol", out var rename) && rename.IsOffered && !TurnedOff(rename);

    private sealed record McpOnMemo(IReadOnlyList<ITool> Served, ToolSwitches Switches, IReadOnlyList<ITool> On);
    private McpOnMemo? _mcpOn;

    /// <summary>The MCP tools served now that are not turned off — the same list while neither the tools nor the switches
    /// change, since the on-demand split is cached on that reference.</summary>
    private IReadOnlyList<ITool> McpToolsOn()
    {
        var served = _mcp.Tools;
        var switches = Switches;
        var memo = Volatile.Read(ref _mcpOn);
        if (memo is not null && ReferenceEquals(memo.Served, served) && ReferenceEquals(memo.Switches, switches)) return memo.On;
        // Judged with the switches the memo is keyed on: read again, a setting saved in between would be cached under the
        // other text.
        IReadOnlyList<ITool> on = served.Any(t => TurnedOff(t, switches)) ? [.. served.Where(t => !TurnedOff(t, switches))] : served;
        Volatile.Write(ref _mcpOn, new McpOnMemo(served, switches, on));
        return on;
    }

    /// <summary>What a request to load <paramref name="asked"/> (its full or its server's name) answers when the tool
    /// is turned off; <c>null</c> when no tool turned off is named so.</summary>
    private string? TurnedOffOnDemand(string asked)
    {
        var off = _mcp.Tools.OfType<McpTool>().Where(t => (t.Name == asked || t.LocalName == asked) && TurnedOff(t)).ToList();
        return off.Count == 0 ? null : string.Join(" ", off.Select(TurnedOffSentence));
    }

    /// <summary>Why <paramref name="tool"/> is not available — for the model, in English.</summary>
    private string TurnedOffSentence(ITool tool)
    {
        var key = ToolSwitches.Typed(tool);
        return tool is McpTool { TurnedOffByEntry: true } m && !Switches.TurnsOff(tool)
            ? $"'{tool.Name}' is turned off by the entry of the MCP server '{m.ServerName}' (its \"disabledTools\"): it is not "
              + "available. Do the task with the tools you have, or tell the user that it needs this tool."
            : $"'{tool.Name}' is turned off by the user: it is not available. Do the task with the tools you have, or tell the "
              + $"user that it needs this tool (they turn it back on with /tools on {key}).";
    }

    /// <inheritdoc/>
    public string? TurnedOffRefusal(string name)
    {
        var tool = _tools.TryGetValue(name, out var builtIn) ? builtIn
            : UserTools.FirstOrDefault(t => t.Name == name) ?? _mcp.Tools.FirstOrDefault(t => t.Name == name);
        return tool is not null && TurnedOff(tool) ? "Error: " + TurnedOffSentence(tool) + " The call was NOT executed." : null;
    }

    /// <summary>The lines of the setting that name no tool served here — a server stopped, a tool only the other editor has
    /// (its debugger): listed by <c>/tools</c> and the Tools page so they can be turned back on, or they stay unseen.</summary>
    internal IReadOnlyList<string> TurnedOffElsewhere()
    {
        var all = _registered.Concat(UserTools).Concat(_mcp.Tools).ToList();
        return [.. Switches.Entries.Where(e => !all.Any(t => ToolSwitches.KeyOf(t) == e || t.Name == e))];
    }

    /// <summary>The names the tools turned off are turned off by (<see cref="ToolSwitches.KeyOf"/>), in the order a request
    /// would send them — X-Ray's line, without weighing every definition as <see cref="Profile"/> does.</summary>
    internal IReadOnlyList<string> TurnedOffKeys() =>
        [.. _registered.Concat(UserTools).Concat(_mcp.Tools).Where(TurnedOff).Select(ToolSwitches.KeyOf)];

    /// <summary>
    /// Every tool this registry holds, in the order a request sends them, with what happens to it on the next request
    /// and what its definition weighs — what <c>/tools</c> and the Tools page list.
    /// </summary>
    internal IReadOnlyList<ToolProfileEntry> Profile()
    {
        var sent = Definitions.Select(d => d.Function.Name).ToHashSet(StringComparer.Ordinal);
        var waiting = _onDemand.WaitingNames.ToHashSet(StringComparer.Ordinal);
        var switches = Switches;
        ToolProfileEntry Entry(ITool t, ToolSource source)
        {
            var state = switches.TurnsOff(t)                           ? ToolState.OffByUser
                      : t is McpTool { TurnedOffByEntry: true }        ? ToolState.OffByEntry
                      : sent.Contains(t.Name)                          ? ToolState.Sent
                      : waiting.Contains(t.Name)                       ? ToolState.OnDemand
                      :                                                  ToolState.NotOffered;
            var tokens = RequestSize.ToolTokens(
                [new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters))]);
            return new(t, ToolSwitches.KeyOf(t), source, (t as McpTool)?.ServerName, state, tokens);
        }
        return
        [
            .. _registered.Select(t => Entry(t, ToolSource.BuiltIn)),
            .. UserTools.Select(t => Entry(t, ToolSource.Custom)),
            .. _mcp.Tools.Select(t => Entry(t, ToolSource.Mcp)),
        ];
    }

    /// <summary>The MCP tools a request declares (<see cref="OnDemandTools.Declared"/>).</summary>
    /// <remarks>Not rebound to this registry's approval: a definition is a name, a description and a schema, which
    /// rebinding copies unchanged; a call goes through <see cref="McpTools"/>, rebound.</remarks>
    private IEnumerable<ITool> DeclaredMcpTools => _onDemand.Declared();

    /// <inheritdoc/>
    /// <remarks>The tools on demand included: a call written as text names one the model read in <c>tool_search</c>'s
    /// description, and it runs — and loads — like a structured one. The tools turned off too: a call written as text to
    /// one is read as a call and refused with its cause (<see cref="TurnedOffRefusal"/>) — left unread, it would be shown
    /// as the answer, and the model would never learn why nothing ran.</remarks>
    public IEnumerable<string> CallableNames =>
        Definitions.Select(d => d.Function.Name).Concat(_onDemand.WaitingNames)
            .Concat(_registered.Concat(UserTools).Concat(_mcp.Tools).Where(TurnedOff).Select(t => t.Name))
            .Distinct(StringComparer.Ordinal);

    /// <summary>MCP server lines for the support bundle. The chat view-model holds this registry,
    /// not the MCP service (only the settings window does), and the bundle must say the same
    /// thing in both front-ends — so the single reader is delegated from here.</summary>
    public IReadOnlyList<string> DescribeMcpForBundle() => _mcp.DescribeForBundle();

    /// <summary>Every built-in tool this registry holds, offered right now or not — the set the documentation
    /// counts and lists. <see cref="Definitions"/> is what the model is offered on this request.</summary>
    internal IReadOnlyCollection<string> BuiltInNames => _tools.Keys;

    /// <summary>The same set as <see cref="BuiltInNames"/>, with what each declares — what the documentation describes.</summary>
    internal IReadOnlyCollection<ITool> BuiltInTools => _registered;

    public IReadOnlyList<ToolDefinition> Definitions =>
        _registered
            .Where(t => t.IsOffered && !TurnedOff(t))
            .Concat(UserTools.Where(t => !TurnedOff(t)))
            .Concat(DeclaredMcpTools)
            .Select(t => new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters)))
            .ToList();

    /// <summary>Where the shell session's next command starts and what it inherits — what a package a call adds is asked with.</summary>
    private ShellState? ShellStateOf(ITool tool) =>
        (tool as RunCommandTool ?? (_tools.TryGetValue("run_command", out var shell) ? shell as RunCommandTool : null))?.SessionState is var (cwd, env)
            ? new ShellState(cwd, env)
            : null;

    /// <summary>What a call to an invented name answers: the names that exist.</summary>
    /// <remarks>
    /// ⚠ An invented name is recoverable only if the answer says what to call instead. Bare, it is a dead end for a
    /// small model reaching for a name from another assistant (<c>edit_file</c>): it re-reads the file, then says it
    /// changed it — with no write. Only this registry's own list: the decorators (plan mode, background tasks)
    /// answer the names they do not expose before a call reaches here.
    /// </remarks>
    private string UnknownTool(string name) =>
        $"Unknown tool: {name}. The tools you can call are: {string.Join(", ", Definitions.Select(d => d.Function.Name))}.";

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
            return _mcp.DescribeMissingTool(name) ?? UnknownTool(name);
        }

        // An editor that answers on demand (Visual Studio) is asked now which buffers hold unsaved changes: what the
        // tool reads, and what it refuses to write over, is the state of the editor at this call. One that can only be
        // asked about a given file (an Agent Client Protocol client) is asked about the files this call names.
        var named = NamedFiles(args, _indexService.RootDir);
        if (_overlay is not null) await _overlay.RefreshAsync(ct, named);

        // ⚠ A file the workspace keeps out of AI tools (its ignore files: ContentExclusions) is neither read nor changed by
        // any call that names it — built-in, MCP or the user's shell tool — and the refusal says by which file and line:
        // an empty answer would read "this file does not exist".
        if (ExcludedNamedFile(args, _indexService.RootDir) is var (excludedFile, excluded))
        {
            _fileHistory.RecordToolCall(name, ExtractSubject(args), durationMs: 0, error: true);
            return excluded.ForModel(Shown(excludedFile));
        }
        // A package this call adds that no source of the project has — an invented name — is refused before the approval
        // card, and the refusal offers the names that exist. Anything less certain than "absent" lets the call through.
        if (_config.PackageCheckEnabled
            && await _packageCheck.RefusalAsync(name, args, _indexService.RootDir, () => ShellStateOf(tool), ct) is { } absent)
        {
            _fileHistory.RecordToolCall(name, ExtractSubject(args), durationMs: 0, error: true);
            return absent;
        }
        // What a walk of this call skips for the same reason is counted, and said above the result.
        var skipped = Governance.ContentExclusions.BeginTally();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Every write this call backs up is checked when it ends: one that did not land leaves the run.
        using var writes = _fileHistory.TrackCall();
        WorkTreeBracket? watch = null;
        try
        {
            // ⚠ A command, a test run, a user's shell tool, an MCP tool change files WITHOUT a backup of their own: watched
            // here, what they change enters the run like a write — /undo-run gives it back, the result bar counts it.
            // Every call plan mode does not list as read-only, so a tool added tomorrow is watched without being named — but
            // one that backs up its own writes or writes the editor's buffer says so (NeedsWorkTreeWatch), and is spared it.
            if (!PlanModeToolRegistry.IsAllowed(name) && tool.NeedsWorkTreeWatch && _fileHistory.IsTracking)
                WorkTreeBracket.Enter(watch = await WorkTreeBracket.OpenAsync(_indexService.RootDir, UnsavedNow(), ct));
            var result = await tool.ExecuteAsync(args, ct);
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: false);
            // Called by its exact name — read in tool_search's description — a tool on demand that ran is loaded. A call
            // refused before it ran (an excluded file) or that failed loads nothing.
            _onDemand.LoadIfDeferred(name);
            // Above the result: the note qualifies it ("3 files were skipped") before the model reads "no match".
            return skipped.Note() is { Length: > 0 } note ? note + "\n\n" + result : result;
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
            return $"Error: tool '{name}' timed out before it finished ({ex.Message}).";
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // The source-scanning regexes are bounded (RegexBudget): a pathological file — usually
            // generated or minified, one line of megabytes — turns into this instead of a hung turn.
            // Say so explicitly; "The Regex engine has timed out" tells the model nothing actionable.
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: true);
            return $"Error: tool '{name}' gave up: a file in scope is too pathological to parse with the "
                 + "language heuristics (generated or minified?). Narrow the path and retry.";
        }
        catch (Exception ex)
        {
            _fileHistory.RecordToolCall(name, ExtractSubject(args), sw.ElapsedMilliseconds, error: true);
            return ToolFailure.Describe(name, ex);
        }
        finally
        {
            // Stopped or failed, the call may have written halfway: what it changed is recorded all the same, without the
            // cancelled token (each git reading has its own budget).
            if (watch is not null)
            {
                var call = ExtractSubject(args) is { } subject ? $"{name} `{CallLabel(subject)}`" : name;
                // A budget of its own: Stop must not wait on a repository that does not answer.
                using var budget = new CancellationTokenSource(RecordBudget);
                try { await _fileHistory.RecordWatchedCallAsync(watch, writes.Paths, call, budget.Token); }
                catch (Exception ex) { Diagnostics.Swallow("ToolRegistry.RecordWatchedCall", ex); }
                finally { watch.Close(); }
            }
            // ⚠ The project map is cached for two minutes. An agent that writes a file and then looks
            // at the map — the ordinary way to check its own work, and `@tree` reads the same cache —
            // read the map from before, with nothing saying so: the file it had just created was not in
            // the project. Any call that may have changed the workspace drops it; the set that may NOT
            // is plan mode's, so a tool it does not know (a new one, a user shell tool, an MCP tool)
            // costs a rescan, never a stale map. In `finally`: a write that failed halfway wrote halfway.
            // tool_search changes definitions, never the workspace.
            if (!PlanModeToolRegistry.IsAllowed(name) && tool is not ToolSearchTool) _mapService.Invalidate();
        }
    }

    /// <summary>How long recording what a watched call changed may take once the call is over.</summary>
    internal static TimeSpan RecordBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A call's subject as one short line, for <c>/undo-run</c> to name it: a script of forty lines is not a name.</summary>
    /// <remarks>The label goes between backticks: one of its own (PowerShell's escape) would end the code span there.</remarks>
    internal static string CallLabel(string subject)
    {
        var line = subject.Trim().Split('\n', 2)[0].TrimEnd('\r').Trim().Replace('`', '\'');
        return line.Length > 80 ? line[..79] + "…" : line;
    }

    /// <summary>A path as the model names it: relative to the workspace when it is under it.</summary>
    private string Shown(string path) => Governance.ContentExclusions.Shown(path, _indexService.RootDir);

    /// <summary>The files the editor holds unsaved changes for, as the overlay was just refreshed.</summary>
    private IReadOnlyCollection<string>? UnsavedNow() =>
        _overlay is null ? null : [.. _overlay.Paths.Where(_overlay.HasUnsavedChanges)];

    /// <summary>Best-effort human-readable target of a tool call for the run journal:
    /// the first well-known string argument (path, command, query…), <c>null</c> when none.</summary>
    internal static string? ExtractSubject(JsonElement args)
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

    /// <summary>
    /// The existing files a call's arguments name, wherever they sit in the object (<c>path</c>, <c>edits[].path</c>,
    /// a tool's own key): every string value that is a file on disk, resolved against <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// A property, not a list of argument names: a tool added tomorrow, an MCP tool with its own schema, names its files
    /// in keys nobody here knows. Bounded — a text with a line break or of path-defying length is content, not a path.
    /// </remarks>
    internal static IReadOnlyList<string> NamedFiles(JsonElement args, string? root)
    {
        var found = new List<string>();
        var visited = 0;
        void Walk(JsonElement e, int depth)
        {
            if (depth > 6 || ++visited > 200) return;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, depth + 1);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, depth + 1);
                    break;
                case JsonValueKind.String:
                    var s = e.GetString();
                    if (string.IsNullOrWhiteSpace(s) || s.Length > 400 || s.IndexOfAny(['\n', '\r', '\0']) >= 0) return;
                    try
                    {
                        var full = Path.IsPathRooted(s) || string.IsNullOrEmpty(root) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(root, s));
                        if (File.Exists(full)) found.Add(full);
                    }
                    // A string that is no path at all (a query, a regex): not a file the call names.
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
                    break;
            }
        }
        Walk(args, 0);
        return found;
    }

    /// <summary>The arguments that carry TEXT — a search, an edit's lines, a command — never a path: a search for
    /// <c>.env</c> is not a read of <c>.env</c>. Any other string is judged; one forgotten here refuses too much, never
    /// too little. And <c>restore_file</c>'s <c>snapshot</c>: a backup it reads for the person's approval card, never
    /// for the model — the file it restores is its <c>path</c>, judged.</summary>
    private static readonly HashSet<string> TextArguments = new(StringComparer.Ordinal)
    {
        "pattern", "query", "old_content", "new_content", "content", "text", "command", "old_name", "new_name", "expression",
        "snapshot",
    };

    /// <summary>
    /// The first file a call names — on disk, in an unsaved buffer, or about to be created — that the workspace's ignore
    /// files exclude, and why: every string of the arguments, at any depth, resolved as the tools resolve a path
    /// (<see cref="PathSanitizer.Sanitize(string?, string?)"/>: control characters and edge spaces dropped, against the root).
    /// </summary>
    /// <remarks>⚠ Not <see cref="NamedFiles"/>: its walk is budgeted for an editor refresh, and the 60th edit of a batch
    /// is a file this gate must see as well as the first.</remarks>
    internal static (string File, Governance.ContentExclusions.Exclusion Why)? ExcludedNamedFile(JsonElement args, string? root)
    {
        (string, Governance.ContentExclusions.Exclusion)? hit = null;
        void Walk(JsonElement e, string? key, int depth)
        {
            if (hit is not null || depth > 64) return;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name, depth + 1);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, key, depth + 1);
                    break;
                case JsonValueKind.String when key is null || !TextArguments.Contains(key):
                    var s = e.GetString();
                    if (string.IsNullOrWhiteSpace(s) || s.Length > 4096 || s.Contains("://", StringComparison.Ordinal)) return;
                    string full;
                    try { full = PathSanitizer.Sanitize(s, root); }
                    catch (ArgumentException) { return; }   // not a path at all
                    // ⚠ A path that does not exist yet is judged too: a write that CREATES a file where the ignore files
                    // exclude it (`secrets/new.key`, an excluded `.env`) is a change to an excluded file. A folder is
                    // judged by the walk that lists it.
                    if (!Directory.Exists(full) && Governance.ContentExclusions.Of(full) is { } why)
                        hit = (full, why);
                    break;
            }
        }
        Walk(args, null, 0);
        return hit;
    }

    private void Register(ITool tool)
    {
        if (_tools.TryGetValue(tool.Name, out var previous)) _registered[_registered.IndexOf(previous)] = tool;
        else _registered.Add(tool);
        _tools[tool.Name] = tool;
    }

    /// <summary>The background commands <c>run_command</c> started that still run.</summary>
    internal IReadOnlyList<(string Id, string Command)> RunningJobs =>
        _tools.TryGetValue("run_command", out var tool) && tool is RunCommandTool run ? run.RunningJobs : [];

    /// <summary>The editor's unsaved buffers, for a reader outside the tools that must see them as read_file does
    /// (the @folder mention); <c>null</c> when the editor mirrors none.</summary>
    internal OpenDocumentOverlay? Overlay => _overlay;

    /// <summary>The git status tool, for a reader that decides on its structured report (the @diff mention).</summary>
    internal GetGitStatusTool GitStatus => (GetGitStatusTool)_tools["get_git_status"];

    /// <summary>The codebase search, for a reader that decides on its hit count (the @code mention).</summary>
    internal SemanticSearchTool CodeSearch => (SemanticSearchTool)_tools["search_codebase"];

    /// <summary>The project map, for a reader that decides on whether there was a root to map (the @tree mention).</summary>
    internal ProjectMapService ProjectMap => _mapService;

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
