using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// Hand-maintained counters in the README rot silently — these tests turn drift into a red
// build. The tool count is read from a real ToolRegistry (the same graph the host builds),
// the test count from this very assembly (xUnit discovery = [Fact]s + one case per
// [InlineData]; the suite uses no MemberData/ClassData, which this test also enforces —
// adding one would silently break the count).
//
// Fixing a drift is one command, not arithmetic — the tests rewrite the README themselves when
// INFERPAL_UPDATE_DOCS is set (snapshot-test convention):
//     $env:INFERPAL_UPDATE_DOCS=1; dotnet test --filter DocCounters
public class DocCountersTests
{
    private static bool UpdateMode =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INFERPAL_UPDATE_DOCS"));

    /// <summary>
    /// Compares a counter against the README. In update mode the README is rewritten through
    /// <paramref name="rewrite"/> and the test passes; otherwise a drift fails with the exact
    /// command that fixes it.
    /// </summary>
    private static void AssertCounter(string readmePath, int actual, int documented,
                                      Func<string, string> rewrite, string what)
    {
        if (actual == documented) return;

        if (UpdateMode)
        {
            File.WriteAllText(readmePath, rewrite(File.ReadAllText(readmePath)));
            return;
        }

        Assert.Fail($"README states {documented} {what} but the code has {actual}. " +
                    "Fix it with: $env:INFERPAL_UPDATE_DOCS=1; dotnet test --filter DocCounters");
    }
    private sealed class NoopApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private sealed class NullEditorSurface : Services.Editor.IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<Services.Editor.ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<Services.Editor.ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<Services.Editor.EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<Services.Editor.EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// A front-end that serves a debugger. Passed on purpose: the two <c>/debug</c> tools are
    /// registered only where an <see cref="Services.Debugging.IDebugSession"/> exists, and since
    /// tranche 3 of §21 that is both front-ends — Visual Studio through the in-process driver, VS
    /// Code through the reverse RPC. The README's number describes what a user is offered, so it
    /// counts them.
    /// </summary>
    private sealed class NullDebugSession : Services.Debugging.IDebugSession
    {
        public bool IsAvailable => true;
        public Task<Services.Debugging.DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct) =>
            Task.FromResult<Services.Debugging.DebugBreakpointInfo?>(null);
        public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<Services.Debugging.DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Services.Debugging.DebugBreakpointInfo>>([]);
        public Task<Services.Debugging.DebugStartResult> StartAsync(CancellationToken ct) =>
            Task.FromResult(Services.Debugging.DebugStartResult.RanToCompletion);
        public Task<Services.Debugging.DebugStopState?> ContinueAsync(CancellationToken ct) =>
            Task.FromResult<Services.Debugging.DebugStopState?>(null);
        public Task<Services.Debugging.DebugStopState?> StepAsync(Services.Debugging.DebugStepKind kind, CancellationToken ct) =>
            Task.FromResult<Services.Debugging.DebugStopState?>(null);
        public Task<Services.Debugging.DebugStopState?> GetStateAsync(CancellationToken ct) =>
            Task.FromResult<Services.Debugging.DebugStopState?>(null);
        public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Readme_BuiltInToolCount_MatchesTheRegistry()
    {
        // Same graph the host builds, with MCP/custom tools left at their empty defaults,
        // so Definitions is exactly the built-in set.
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        var index    = new ProjectIndexService(client, config, new LspSemanticProvider());
        var registry = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor), new McpToolService(config, approval),
                                        new DocsIndexService(client, config), new OpenDocumentOverlay(),
                                        new NullDebugSession());

        // Bold or plain — the two repos phrase the claim differently.
        var path   = Path.Combine(RepoRoot(), "README.md");
        var claim  = Regex.Match(File.ReadAllText(path), @"(\d+) built-in tools");
        Assert.True(claim.Success, "README.md no longer states 'N built-in tools'.");

        var actual = registry.Definitions.Count;
        AssertCounter(path, actual, int.Parse(claim.Groups[1].Value),
                      text => Regex.Replace(text, @"\d+ built-in tools", $"{actual} built-in tools"),
                      "built-in tools");

        // The Marketplace listing is NOT generated from the README — it had silently drifted to
        // "26 built-in tools" while the product grew to 28 (pre-1.6.0 architecture review, §3.4). Since the
        // switch to in-process hosting (2026-08-23) the SDK forbids ExtensionConfiguration.
        // Metadata, so that description now lives in source.extension.vsixmanifest, the file
        // actually packaged in the VSIX. Same counter, same auto-rewrite, new home.
        var extPath  = Path.Combine(RepoRoot(), "Inferpal", "source.extension.vsixmanifest");
        var extClaim = Regex.Match(File.ReadAllText(extPath), @"(\d+) built-in tools");
        Assert.True(extClaim.Success, "source.extension.vsixmanifest no longer states 'N built-in tools'.");
        AssertCounter(extPath, actual, int.Parse(extClaim.Groups[1].Value),
                      text => Regex.Replace(text, @"\d+ built-in tools", $"{actual} built-in tools"),
                      "built-in tools (Marketplace description)");

        // ⚠ And the long-form listing body, which is a THIRD copy of the claim. It is the one that
        // had drifted the furthest — still "26 built-in tools" after the 1.6.0 release, because
        // only the two above were guarded. A counter that exists in three places and is checked in
        // two is a counter that will be wrong in the third (found by the post-1.6.0 review).
        var listPath  = Path.Combine(RepoRoot(), "MARKETPLACE.md");
        if (File.Exists(listPath))   // private-repo only: the listing copy never ships publicly
        {
            var listClaim = Regex.Match(File.ReadAllText(listPath), @"(\d+) built-in tools");
            Assert.True(listClaim.Success, "MARKETPLACE.md no longer states 'N built-in tools'.");
            AssertCounter(listPath, actual, int.Parse(listClaim.Groups[1].Value),
                          text => Regex.Replace(text, @"\d+ built-in tools", $"{actual} built-in tools"),
                          "built-in tools (Marketplace listing)");
        }
    }

    [Fact]
    public void Readme_TestsBadge_MatchesThisAssembly()
    {
        // §23: the net8.0 leg deliberately omits the VSIX-dependent test files, so its own count
        // can never match the badge — the badge is owned by the full net8.0-windows suite.
#if WINDOWS
        var total = 0;
        foreach (var type in typeof(DocCountersTests).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<Xunit.TheoryAttribute>() is not null)
                {
                    var inline = method.GetCustomAttributes<Xunit.InlineDataAttribute>().Count();
                    Assert.True(inline > 0,
                        $"{type.Name}.{method.Name} is a [Theory] without [InlineData] — " +
                        "this counter only understands InlineData-driven theories.");
                    total += inline;
                }
                else if (method.GetCustomAttribute<Xunit.FactAttribute>() is not null)
                {
                    total++;
                }
            }
        }

        var path  = Path.Combine(RepoRoot(), "README.md");
        var badge = Regex.Match(File.ReadAllText(path), @"tests-(\d+)%20passing");
        Assert.True(badge.Success, "README.md no longer carries the tests-N%20passing badge.");

        AssertCounter(path, total, int.Parse(badge.Groups[1].Value),
                      text => Regex.Replace(text, @"tests-\d+%20passing", $"tests-{total}%20passing"),
                      "passing tests");
#endif
    }
}
