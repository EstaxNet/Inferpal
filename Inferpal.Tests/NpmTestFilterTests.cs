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

        Assert.StartsWith("✓ PASSED — Failed: 0, Passed: 1", report);
    }
}
