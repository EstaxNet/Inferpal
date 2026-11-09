using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tasks;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The modes decide from what a tool CAN do (<see cref="ToolCeilings"/>), one declaration held true to the code, instead of
/// a list of names per mode — a list forgets tomorrow's tool.
/// </summary>
public sealed class ToolCeilingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ceilings-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>Every registered built-in tool is declared, and every declared name is a registered tool.</summary>
    [Fact]
    public void TheTable_AndTheRegisteredTools_AreTheSame()
    {
        var registered = Registry().BuiltInTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(registered.Count >= 30, $"witness: {registered.Count} tools registered");

        Assert.Empty(registered.Where(n => !ToolCeilings.Declared.Contains(n, StringComparer.OrdinalIgnoreCase)));
        Assert.Empty(ToolCeilings.Declared.Where(n => !registered.Contains(n)));
    }

    /// <summary>
    /// ⚠ What sees a ceiling declared too LOW: every call's own effect stays under its tool's ceiling — arguments left empty,
    /// and each value of an <c>action</c> the tool's schema lists.
    /// </summary>
    [Fact]
    public void EveryCallsEffect_StaysUnderItsToolsCeiling()
    {
        var checkedActions = 0;
        foreach (var tool in Registry().BuiltInTools)
        {
            var ceiling = ToolCeilings.Of(tool.Name);
            var variants = new List<JsonElement> { Args(new { }) };
            var schema = JsonSerializer.SerializeToElement(tool.Parameters);
            foreach (var v in ActionValues(schema))
            {
                variants.Add(Args(new Dictionary<string, string> { ["action"] = v }));
                checkedActions++;
            }

            foreach (var args in variants)
            {
                var effect = tool.EffectOf(args);
                Assert.True((effect & ~ceiling) == 0,
                            $"{tool.Name} ({args.GetRawText()}) has effect {effect} beyond its ceiling {ceiling}.");
            }
        }
        Assert.True(checkedActions >= 5, $"witness: {checkedActions} action values read from the schemas");
    }

    /// <summary>The values an <c>action</c> parameter takes, as its schema lists them: an <c>enum</c>, values in quotes
    /// (<c>'poll'</c>), or values separated by <c>|</c>.</summary>
    private static IEnumerable<string> ActionValues(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var props) || !props.TryGetProperty("action", out var action)) yield break;
        if (action.TryGetProperty("enum", out var values))
        {
            foreach (var v in values.EnumerateArray()) yield return v.GetString()!;
            yield break;
        }
        var text = action.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var quoted = System.Text.RegularExpressions.Regex.Matches(text, "'([a-z_]+)'").Select(m => m.Groups[1].Value).ToList();
        if (quoted.Count > 0) { foreach (var q in quoted.Distinct()) yield return q; yield break; }
        if (text.Contains('|'))
            foreach (var part in text.Split('|'))
                if (System.Text.RegularExpressions.Regex.Match(part, "[a-z_]{3,}") is { Success: true } m) yield return m.Value;
    }

    // Each reader's expected answer, written out: the witness that deriving it from the table changes nothing but plan mode.
    private static readonly string[] OldPlanList =
    [
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
        "get_diagnostics", "get_active_document", "get_open_editors", "get_solution_info",
        "get_git_status", "get_debugger_state", "generate_project_map", "analyze_code",
        "web_search", "fetch_url", "read_skill_file",
    ];

    private static readonly string[] OldTaskList =
    [
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
        "get_git_status", "get_solution_info", "generate_project_map", "analyze_code", "read_skill_file",
    ];

    /// <summary>
    /// The readers keep their answer for every registered tool and for a tool nobody declared (an MCP tool): the work-tree
    /// watch, the project map and the cut card read "leaves the files alone" = the old plan list; background tasks = their
    /// old list; the parallel batch = its three reads. Only plan mode changes, and only by get_diagnostics, which builds.
    /// </summary>
    [Fact]
    public void EveryReader_KeepsItsAnswer_ExceptPlanModeForGetDiagnostics()
    {
        var names = Registry().BuiltInTools.Select(t => t.Name).Append("mcp__fs__read").Append("my_shell_tool").ToList();
        foreach (var name in names)
        {
            Assert.Equal(OldPlanList.Contains(name), ToolCeilings.LeavesTrackedFilesAlone(name));
            Assert.Equal(OldTaskList.Contains(name), BackgroundTaskToolRegistry.IsAllowed(name));
            Assert.Equal(name is "read_file" or "list_files" or "search_in_files", ToolCeilings.ConcurrentSafe(name));
            Assert.Equal(OldPlanList.Contains(name) && name != "get_diagnostics", PlanModeToolRegistry.IsAllowed(name));
        }
    }

    /// <summary>
    /// A call written as text (no definition list on the way) to a tool whose OTHER actions write or run is refused in plan
    /// mode and in a background task, even when this call only reads: <c>run_command action:list</c> would read the
    /// person's background jobs.
    /// </summary>
    [Fact]
    public async Task ACallAsTextToAToolThatCanRun_IsRefusedInTheRestrictedModes()
    {
        var registry = Registry();
        var list = Args(new { action = "list" });

        var plan = await new PlanModeToolRegistry(registry).ExecuteAsync("run_command", list, default);
        Assert.Contains("not available in plan mode", plan, StringComparison.Ordinal);

        Assert.False(BackgroundTaskToolRegistry.IsAllowed("run_command"));
        Assert.False(BackgroundTaskToolRegistry.IsAllowed("debug_inspect"));
        Assert.False(PlanModeToolRegistry.IsAllowed("debug_control"));
    }

    /// <summary>An imported custom mode's "read" family holds reads only — with ONE named exception, get_diagnostics, kept
    /// because existing modes count on it. A tool that writes or runs added to the family turns this red.</summary>
    [Fact]
    public void AnImportedModesReadFamily_HoldsReadsOnly_ButTheOneNamedException()
    {
        foreach (var name in Inferpal.Services.Governance.ModeTools.Families["read"].Where(n => n != "get_diagnostics"))
            Assert.True((ToolCeilings.Of(name) & ~ToolEffect.Reads) == 0, $"{name} is in the read family but can {ToolCeilings.Of(name)}.");
    }
}
