using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The npm runner passed jest's <c>--testNamePattern</c> whatever the test script ran: Node's own runner answered
/// "bad option" and ran nothing — read as a red suite by /tdd — or, after a positional file, ignored the filter and
/// ran every test.
/// </summary>
public sealed class NpmTestFilterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-npmfilter-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("node --test")]
    [InlineData("node --test test/a.test.js")]
    [InlineData("tsx --test")]
    public void NodesRunner_GetsTheFilterThroughNodeOptions(string script)
    {
        var (args, nodeOptions) = RunTestsTool.NpmFilter(script, "alpha works", inheritedNodeOptions: null);

        Assert.Empty(args);
        Assert.Equal("--test-name-pattern=\"alpha works\"", nodeOptions);
    }

    [Fact]
    public void NodeOptionsAlreadySet_AreKept()
    {
        var (_, nodeOptions) = RunTestsTool.NpmFilter("node --test", "alpha", "--max-old-space-size=4096");

        Assert.Equal("--max-old-space-size=4096 --test-name-pattern=\"alpha\"", nodeOptions);
    }

    [Fact]
    public void AQuoteOrBackslashInTheFilter_StaysInsideItsQuotes()
    {
        var (_, nodeOptions) = RunTestsTool.NpmFilter("node --test", "say \"hi\" \\d", null);

        Assert.Equal("--test-name-pattern=\"say \\\"hi\\\" \\\\d\"", nodeOptions);
    }

    [Fact]
    public void Mocha_GetsGrep()
    {
        Assert.Equal(["--", "--grep=alpha"], RunTestsTool.NpmFilter("mocha --recursive", "alpha", null).Args);
    }

    [Theory]
    [InlineData("jest")]
    [InlineData("vitest run")]
    [InlineData(null)]                    // no script to read: jest's form, as before
    [InlineData("npm run build --test-coverage")]   // a flag that only starts with --test is not Node's runner
    public void JestVitestAndTheUnknown_GetTestNamePattern(string? script)
    {
        var (args, nodeOptions) = RunTestsTool.NpmFilter(script, "alpha", null);

        Assert.Equal(["--", "--testNamePattern=alpha"], args);
        Assert.Null(nodeOptions);
    }

    [Fact]
    public void ANodeThatRefusesTheFlagInNodeOptions_TakesItAsAnArgument_WhenTheScriptNamesNoFile()
    {
        // Node 20, 21 and early 22: "--test-name-pattern= is not allowed in NODE_OPTIONS", exit 9 — the whole run failed.
        var plan = RunTestsTool.NpmFilter("node --test", "alpha works", null, nodeOptionsTakeTestFlags: false);

        Assert.Equal(["--", "--test-name-pattern=alpha works"], plan.Args);
        Assert.Null(plan.NodeOptions);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void ANodeThatRefusesTheFlag_AndAScriptThatNamesItsFiles_RunsTheSuite_AndSaysTheFilterWasNotApplied()
    {
        // After a named file an argument goes to the tests, not to Node: nothing can carry the filter there.
        var plan = RunTestsTool.NpmFilter("node --test test/a.test.js", "alpha", null, nodeOptionsTakeTestFlags: false);

        Assert.Empty(plan.Args);
        Assert.Null(plan.NodeOptions);
        Assert.StartsWith("⚠ The filter 'alpha' was NOT applied", plan.Note);
    }

    /// <summary>Whether the machine's Node takes the flag in NODE_OPTIONS — asked independently of the product's probe.</summary>
    private static bool NodeTakesTestFlagsInNodeOptions()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "node.exe" : "node", "-e \"\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["NODE_OPTIONS"] = "--test-name-pattern=x";
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(30_000);
        return p.ExitCode == 0;
    }

    [Theory]
    [InlineData("node --test")]
    [InlineData("node --test test/a.test.js")]
    public async Task AFilteredRun_OfNodesRunner_RunsExactlyTheMatchingTest(string script)
    {
        if (!NpmTools.Installed()) return;   // UNDECIDED without npm on the machine — never read as a pass of the product
        Directory.CreateDirectory(Path.Combine(_dir, "test"));
        File.WriteAllText(Path.Combine(_dir, "package.json"),
            JsonSerializer.Serialize(new { name = "demo", version = "1.0.0", scripts = new { test = script } }));
        File.WriteAllText(Path.Combine(_dir, "test", "a.test.js"), """
            const test = require('node:test');
            const assert = require('node:assert');
            test('alpha works', () => assert.equal(1, 1));
            test('beta fails', () => assert.equal(1, 2));
            """);

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _dir, runner = "npm", filter = "alpha" }));
        var report = await new RunTestsTool(() => _dir).ExecuteAsync(args.RootElement, CancellationToken.None);

        // A Node that refuses the flag in NODE_OPTIONS cannot filter a script that names its files: the suite runs,
        // and the report says so first. Every other case runs exactly the matching test.
        if (script.EndsWith(".js", StringComparison.Ordinal) && !NodeTakesTestFlagsInNodeOptions())
            Assert.StartsWith("⚠ The filter 'alpha' was NOT applied", report);
        else
            Assert.StartsWith("✓ PASSED — Failed: 0, Passed: 1", report);
    }
}
