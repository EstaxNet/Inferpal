using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A test runner that could not START is a run in which nothing ran — not a failing one.
//
//  run_tests answered "Failed to start 'cargo': …" for a toolchain that is not installed, the npm
//  runner said npm could not be launched, a path naming nothing was refused, and Python's Windows
//  Store alias prints its install advice and exits 9009 — and /tdd read every one of them as a red
//  run: five agent rounds patching code whose tests never ran, then "could not make the tests pass".
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]   // compares a localized notice
public sealed class RunnerNeverStartedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-nostart-" + Guid.NewGuid().ToString("N"));

    public RunnerNeverStartedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(RunTestsTool.RunnerNotStarted + " 'cargo': An error occurred trying to start process 'cargo'.")]
    [InlineData(RunTestsTool.NpmNotRunnable)]
    [InlineData(RunTestsTool.PathNotFound + ": tests/missing. Check the path, or omit it to run the whole suite.")]
    public void ARunnerThatDidNotStart_IsARunInWhichNothingRan(string report)
    {
        Assert.True(TddCommandHandler.NothingRan(report));
        Assert.False(TddCommandHandler.TestsFailed(report));
    }

    /// <summary>The real run_tests, behind a registry that serves nothing else.</summary>
    private sealed class RunTestsOnly(RunTestsTool tool) : IToolRegistry
    {
        public int Runs { get; private set; }
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Runs++;
            return tool.ExecuteAsync(args, ct);
        }
    }

    [Fact]
    public async Task Tdd_OnAToolchainThatIsNotInstalled_StopsAtOnce_AndPatchesNothing()
    {
        // A Rust project on a machine without cargo. Where cargo IS installed this arm measures nothing: the theory above
        // holds the rule, this one the real start failure.
        if (ShellLauncher.FindOnPath("cargo") is not null) return;
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"shop\"\nversion = \"0.1.0\"\n");
        var tools  = new RunTestsOnly(new RunTestsTool(() => _root));
        var client = new FakeInferenceProvider();

        var result = await TddCommandHandler.HandleAsync(
            client, new InferpalConfig(), tools, systemPrompt: null, ["/tdd"], projectRoot: _root,
            onProgress: null, onTestReport: null, onStep: null, onToken: null, onFixResult: null, CancellationToken.None);

        Assert.Equal(1, tools.Runs);                                           // witness: the real tool ran once
        Assert.Contains(RunTestsTool.RunnerNotStarted, result.Message, StringComparison.Ordinal);
        Assert.StartsWith(Strings.TddNothingRan, result.Message, StringComparison.Ordinal);
        Assert.Empty(client.AgentRuns);                                        // no round patched anything
    }
}
