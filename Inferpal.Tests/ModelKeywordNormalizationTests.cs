using System.IO;
using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a keyword argument written by the MODEL does when it is not written exactly.
/// </summary>
/// <remarks>
/// <para>
/// The keyword rule of <see cref="ConventionCoverageTests"/> holds the <i>read</i> — that the value goes
/// through <c>ToolArgs.Keyword</c>. These tests hold the <i>answer</i>, which is the half a scan
/// cannot see: three tools compared a raw <c>GetString()</c> against lower-case literals, so
/// <c>"Callers"</c>, <c>" all"</c> and <c>"Replace"</c> matched nothing and the tool carried on as
/// if the argument had been absent.
/// </para>
/// <para>
/// None of the three threw. Each returned something the model has no way to doubt: a call-graph
/// report with <b>neither</b> section, a whole-workspace scan reporting no cross-language bridges,
/// and an <b>append</b> where an overwrite was asked for. The second half of the repair is that an
/// unrecognised keyword is now <b>named</b> — the shape <c>analyze_code</c>, <c>debug_control</c>
/// and <c>run_tests</c> already had.
/// </para>
/// </remarks>
public class ModelKeywordNormalizationTests
{
    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private const string Sample =
        "public class Sample\n{\n    public void DoWork()\n    {\n        Helper();\n    }\n\n"
      + "    public void Helper()\n    {\n    }\n}\n";

    // ── trace_dependency: 'direction' ─────────────────────────────────────────

    [Theory]
    [InlineData("Callers",  "### Callers")]
    [InlineData(" callers ", "### Callers")]
    [InlineData("CALLEES",  "### Callees")]
    [InlineData("Both",     "### Callers")]
    public async Task Direction_IsMatchedWhateverTheCaseAndSpacing(string direction, string expected)
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-dir").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        await File.WriteAllTextAsync(file, Sample);
        try
        {
            var tool   = new TraceDependencyTool(() => dir);
            var result = await tool.ExecuteAsync(Args(new { path = file, direction }), CancellationToken.None);

            Assert.Contains(expected, result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task UnknownDirection_IsNamed_NotRenderedAsAnEmptyReport()
    {
        // The defect this pins: neither literal matched, so both sections were skipped and the
        // report came out well-formed and empty. The model reads "no callers", not "bad argument".
        var dir  = Directory.CreateTempSubdirectory("inferpal-dir").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        await File.WriteAllTextAsync(file, Sample);
        try
        {
            var tool   = new TraceDependencyTool(() => dir);
            var result = await tool.ExecuteAsync(Args(new { path = file, direction = "sideways" }),
                                                 CancellationToken.None);

            Assert.Contains("Unknown direction", result, StringComparison.Ordinal);
            Assert.DoesNotContain("### Callers", result, StringComparison.Ordinal);
            Assert.DoesNotContain("### Callees", result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── trace_nexus: 'bridges' ────────────────────────────────────────────────

    private const string RestSection    = "### REST Endpoints";
    private const string InteropSection = "### JS Interop";

    /// <summary>A workspace holding exactly one REST bridge and one interop bridge.</summary>
    private static async Task<string> NexusWorkspaceAsync()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-nexus").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "Api.cs"),
            "public static class Api\n{\n    public static void Map(WebApplication app)\n"
          + "    {\n        app.MapGet(\"/api/items\", () => 1);\n    }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "Interop.cs"),
            "public class Widget\n{\n    async Task Go(IJSRuntime js)\n"
          + "    {\n        await js.InvokeAsync<string>(\"showWidget\");\n    }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "client.ts"),
            "const r = await fetch('/api/items');\nexport function showWidget() { }\n");
        return dir;
    }

    // ⚠ The assertion has to DISCRIMINATE. My first version only checked "no refusal message",
    // and the sabotage left it GREEN: removing the guard also removes the message it looks for, so
    // it passed on a tool that had lit no scan at all. It now requires the SECTION to be rendered.
    [Fact]
    public async Task BridgesAll_SurvivesTheSpacesAndScansEveryKind()
    {
        var dir = await NexusWorkspaceAsync();
        try
        {
            var tool   = new NexusIntelligenceTool(() => dir);
            var result = await tool.ExecuteAsync(Args(new { root = dir, bridges = " ALL " }), CancellationToken.None);

            Assert.Contains(RestSection,    result, StringComparison.Ordinal);
            Assert.Contains(InteropSection, result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(" rest ",    RestSection,    InteropSection)]
    [InlineData(" INTEROP ", InteropSection, RestSection)]
    public async Task BridgesFilter_IsHonouredWhateverTheCaseAndSpacing(
        string bridges, string rendered, string absent)
    {
        var dir = await NexusWorkspaceAsync();
        try
        {
            var tool   = new NexusIntelligenceTool(() => dir);
            var result = await tool.ExecuteAsync(Args(new { root = dir, bridges }), CancellationToken.None);

            Assert.Contains(rendered, result, StringComparison.Ordinal);
            // The other half: the filter was actually HONOURED, not merely everything switched on.
            Assert.DoesNotContain(absent, result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task UnknownBridges_IsNamed_InsteadOfScanningEverythingToReportNothing()
    {
        // The most expensive silence of the three: an unrecognised filter lit none of the three
        // scans, and the tool still walked every .cs and .ts of the workspace to conclude there
        // are no bridges between the languages.
        var dir = Directory.CreateTempSubdirectory("inferpal-nexus").FullName;
        try
        {
            var tool   = new NexusIntelligenceTool(() => dir);
            var result = await tool.ExecuteAsync(Args(new { root = dir, bridges = "http" }),
                                                 CancellationToken.None);

            Assert.Contains("Unknown bridges", result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// <c>update_memory</c>'s half, kept apart because it moves the process's current directory —
/// <c>FindProjectRoot</c> walks up from it.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class UpdateMemoryKeywordTests
{
    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private sealed class StubEditor(string path) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(path, string.Empty));
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(path);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(new EditorEditResult(path, ReplacedSelection: true));
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class CountingApproval(bool answer) : IApprovalService
    {
        public int Calls { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private static async Task<T> InWorkspaceAsync<T>(Func<string, string, Task<T>> body)
    {
        var root = Directory.CreateTempSubdirectory("inferpal-memkw").FullName;
        var mem  = Path.Combine(root, ".inferpal", "memory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mem)!);

        try { return await body(root, mem); }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("Replace")]
    [InlineData(" replace ")]
    public async Task Replace_OverwritesWhateverTheCaseAndSpacing(string mode) =>
        await InWorkspaceAsync(async (root, mem) =>
        {
            // The switch falls back to "append", so an unnormalised "Replace" did the opposite of
            // what was asked — in the file re-injected into every later session's system prompt.
            await File.WriteAllTextAsync(mem, "OLD NOTE\n");

            var tool = new UpdateMemoryTool(new StubEditor(Path.Combine(root, "a.cs")),
                                            new CountingApproval(answer: true), new FileHistoryService(),
                                            () => root);
            await tool.ExecuteAsync(Args(new { mode, content = "NEW NOTE" }), CancellationToken.None);

            var written = await File.ReadAllTextAsync(mem);
            Assert.Contains("NEW NOTE", written, StringComparison.Ordinal);
            Assert.DoesNotContain("OLD NOTE", written, StringComparison.Ordinal);
            return true;
        });

    [Fact]
    public async Task UnknownMode_IsRefusedBeforeAskingAndWritesNothing() =>
        await InWorkspaceAsync(async (root, mem) =>
        {
            await File.WriteAllTextAsync(mem, "OLD NOTE\n");

            var approval = new CountingApproval(answer: true);
            var tool     = new UpdateMemoryTool(new StubEditor(Path.Combine(root, "a.cs")), approval,
                                                new FileHistoryService(), () => root);
            var result = await tool.ExecuteAsync(Args(new { mode = "wipe", content = "NEW NOTE" }),
                                                 CancellationToken.None);

            Assert.Contains("Unknown mode", result, StringComparison.Ordinal);
            // Refused BEFORE the prompt: having the user approve a write we are going to perform
            // differently is worse than refusing it.
            Assert.Equal(0, approval.Calls);
            Assert.Equal("OLD NOTE\n", await File.ReadAllTextAsync(mem));
            return true;
        });
}
