using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host.Acp;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Every built-in tool declares what a call does (<see cref="ToolEffect"/>) — a property of the call, not a list of tool
/// names —, and the declaration is checked against the code: a tool that asks for approval declares more than reading,
/// and one that only reads never asks. A tool that does not say (an MCP server's) has every effect.
/// </summary>
public sealed class ToolEffectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "effects-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolEffectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* cleanup */ }
    }

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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void EveryBuiltInTool_DeclaresWhatItDoes()
    {
        var tools = Registry();
        // Every tool the registry holds, offered right now or not.
        var all = tools.BuiltInTools.ToList();
        Assert.True(all.Count >= 30, $"witness: {all.Count} tools registered");

        var undeclared = all.Where(t => t.EffectOf(Args(new { })) == ToolEffect.Unknown).Select(t => t.Name).ToList();

        Assert.Empty(undeclared);
        Assert.Equal(ToolEffect.Unknown, tools.EffectOf("mcp__server__tool", Args(new { })));   // one it does not hold
    }

    [Fact]
    public void TheEffect_IsTheCallsOwn()
    {
        var tools = Registry();
        Assert.Equal(ToolEffect.Reads, tools.EffectOf("read_file", Args(new { path = "a.cs" })));
        Assert.Equal(ToolEffect.Writes, tools.EffectOf("write_file", Args(new { path = "a.cs", content = "x" })));
        Assert.Equal(ToolEffect.Runs, tools.EffectOf("run_command", Args(new { command = "dotnet test" })));
        Assert.Equal(ToolEffect.Reads, tools.EffectOf("run_command", Args(new { action = "poll", id = "1" })));
        Assert.Equal(ToolEffect.Network, tools.EffectOf("fetch_url", Args(new { url = "https://example.org" })));
        Assert.Equal(ToolEffect.Runs, ((ITool)new DebugInspectTool(new NullDebugSession(), new NoopApproval(), () => _root)).EffectOf(Args(new { action = "evaluate", expression = "x" })));
        Assert.Equal(ToolEffect.Reads, ((ITool)new DebugInspectTool(new NullDebugSession(), new NoopApproval(), () => _root)).EffectOf(Args(new { action = "state" })));
        var control = tools.BuiltInTools.FirstOrDefault(t => t.Name == "debug_control");
        if (control is not null)
        {
            Assert.Equal(ToolEffect.Reads, control.EffectOf(Args(new { action = "list_breakpoints" })));
            Assert.Equal(ToolEffect.Runs, control.EffectOf(Args(new { action = "start" })));
        }
    }

    /// <summary>
    /// The declaration is true to the code: a tool's file that asks — the approval card, the editor's write gate, the consent
    /// to build or test the workspace — declares an effect beyond reading, and one whose effect is reading alone never asks;
    /// one that starts a process or evaluates the debuggee's code declares running, one that reaches the network says so.
    /// Inferpal's own git readings (<c>GitProcess</c>) are reads: they run no program of the repository's.
    /// </summary>
    [Fact]
    public void ADeclaredEffect_IsTrueToTheToolsCode()
    {
        var folder = Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Tools");
        var tools = Directory.EnumerateFiles(folder, "*.cs")
            .Select(f => (File: Path.GetFileName(f), Code: ConventionCoverageTests.CodeOnly(f)))
            .Where(t => t.Code.Contains(": ITool", StringComparison.Ordinal))
            .ToList();
        Assert.True(tools.Count >= 25, $"witness: {tools.Count} tool files read");

        var wrong = tools.Where(t => !t.Code.Contains("EffectOf(", StringComparison.Ordinal)).Select(t => $"{t.File}: declares nothing").ToList();
        foreach (var (file, code) in tools)
        {
            // Asking: the approval card, the editor's write gate, or the consent to build or test the workspace.
            var asks = code.Contains("RequestApprovalAsync(", StringComparison.Ordinal)
                    || code.Contains("RequestBatchApprovalAsync(", StringComparison.Ordinal)
                    || code.Contains("EditorWriteGate.AuthorizeAsync(", StringComparison.Ordinal)
                    || code.Contains("_consent.RefusalAsync(", StringComparison.Ordinal);
            bool Says(string effect) => code.Contains("ToolEffect." + effect, StringComparison.Ordinal) || code.Contains("ToolEffect.Unknown", StringComparison.Ordinal);
            var runs = new[] { "ChildProcess.", "new ProcessStartInfo", "ShellSession", "ShellLauncher.", ".EvaluateAsync(" }
                .Any(s => code.Contains(s, StringComparison.Ordinal));
            var network = new[] { "HttpClient", "HttpRequestMessage" }.Any(s => code.Contains(s, StringComparison.Ordinal));
            if (runs && !Says("Runs")) wrong.Add($"{file}: starts a process or evaluates code, does not declare running");
            if (network && !Says("Network")) wrong.Add($"{file}: reaches the network, does not declare it");
            var declaresMore = code.Contains("ToolEffect.Writes", StringComparison.Ordinal)
                            || code.Contains("ToolEffect.Runs", StringComparison.Ordinal)
                            || code.Contains("ToolEffect.Network", StringComparison.Ordinal)
                            || code.Contains("ToolEffect.Unknown", StringComparison.Ordinal);
            if (asks != declaresMore) wrong.Add($"{file}: asks={asks}, declares more than reading={declaresMore}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>An ACP client draws a call by what it declares when the table does not name its tool — never "other" for a
    /// call that said; the table stays finer where it names one (a search, a deletion).</summary>
    [Fact]
    public void AnAcpKind_FollowsTheDeclaredEffect_ASearchOrADeletionExcepted()
    {
        Assert.Equal("execute", AcpToolCalls.Kind("debug_inspect", "runs"));
        Assert.Equal("read", AcpToolCalls.Kind("trace_dependency", "reads"));
        Assert.Equal("edit", AcpToolCalls.Kind("my_tool", "writes"));
        Assert.Equal("fetch", AcpToolCalls.Kind("my_tool", "network"));
        Assert.Equal("other", AcpToolCalls.Kind("mcp__server__tool", "unknown"));
        Assert.Equal("search", AcpToolCalls.Kind("search_in_files", "reads"));   // finer than any effect
        Assert.Equal("delete", AcpToolCalls.Kind("delete_file", "writes"));
        Assert.Equal("read", AcpToolCalls.Kind("run_command", "reads"));         // a job polled: the call's own effect
        Assert.Equal("read", AcpToolCalls.Kind("read_file"));                    // a replay that does not say: the table
        Assert.Equal("execute", AcpToolCalls.Kind("smart_fix_validator"));       // a command asked under another call
    }
}
