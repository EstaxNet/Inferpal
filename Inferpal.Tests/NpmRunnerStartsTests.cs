using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The npm runner launched "npm" with no shell. On Windows npm is a batch script (npm.cmd), and CreateProcess only
/// resolves an executable: the runner may not even start there — the platform the product is built for.
/// </summary>
public sealed class NpmRunnerStartsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-npm-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void OnWindows_NpmRunsThroughNode_NeverThroughCmd()
    {
        // No shell between the model's filter and the runner: cmd.exe would read "&", "|" or "%" in it as commands.
        // Paths built for the HOST: a literal "C:\…" has no directory part under Linux, where this test runs too.
        var dir  = Path.Combine(Path.GetTempPath(), "nodejs");
        var node = Path.Combine(dir, "node.exe");
        var cli  = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
        var npm = RunTestsTool.ResolveNpm(isWindows: true,
            onPath: name => name == "npm.cmd" ? Path.Combine(dir, "npm.cmd") : null,
            exists: path => path == node || path == cli);

        Assert.NotNull(npm);
        Assert.Equal(node, npm!.Value.FileName);
        Assert.Equal([cli], npm.Value.Prefix);
    }

    [Fact]
    public void OnWindows_WithoutNpmsCliBesideIt_ThereIsNoShellFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nodejs");
        Assert.Null(RunTestsTool.ResolveNpm(isWindows: true,
            onPath: name => name == "npm.cmd" ? Path.Combine(dir, "npm.cmd") : null, exists: _ => false));
    }

    [Fact]
    public void ElsewhereNpmIsExecutedDirectly()
    {
        Assert.Equal(("npm", Array.Empty<string>()),
            RunTestsTool.ResolveNpm(isWindows: false, onPath: _ => null, exists: _ => false)!.Value);
    }

    [Fact]
    public async Task TheNpmRunner_Starts()
    {
        if (!NpmTools.Installed()) return;   // UNDECIDED without npm on the machine — never read as a pass of the product
        File.WriteAllText(Path.Combine(_dir, "package.json"),
            """{ "name": "demo", "version": "1.0.0", "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }""");

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _dir, runner = "npm" }));
        var report = await new RunTestsTool(() => _dir).ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.DoesNotContain("Failed to start", report);
        Assert.Contains(RunTestsTool.NoTestScript, report);                                   // it ran npm's script
    }
}
