using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>get_diagnostics</c> and <c>run_tests</c> took any path on the machine: given a project OUTSIDE the workspace,
/// they built it — and a build runs that project's MSBuild targets, which are code — in plan mode and in background
/// <c>/task</c> runs, with no approval prompt. Every other tool that takes a path confines it to the workspace
/// (convention rule 33). These tools are refused before anything runs.
/// </summary>
public sealed class BuildToolConfinementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"confine-{Guid.NewGuid():N}");
    private readonly string _workspace;
    private readonly string _outside;

    public BuildToolConfinementTests()
    {
        _workspace = Path.Combine(_root, "ws");
        _outside   = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        // A project whose build would run a target: never reached, the path is refused first.
        File.WriteAllText(Path.Combine(_outside, "Evil.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Target Name=\"Pwn\" BeforeTargets=\"Build\" /></Project>");
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "not a project");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task GetDiagnostics_RefusesAProjectOutsideTheWorkspace()
    {
        var tool = new GetDiagnosticsTool(getRoot: () => _workspace);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.ExecuteAsync(Args(new { path = Path.Combine(_outside, "Evil.csproj") }), CancellationToken.None));
        Assert.Contains("outside the workspace root", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTests_RefusesAPathOutsideTheWorkspace()
    {
        var tool = new RunTestsTool(() => _workspace);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.ExecuteAsync(Args(new { path = Path.Combine(_outside, "Evil.csproj") }), CancellationToken.None));
        Assert.Contains("outside the workspace root", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathInsideTheWorkspace_IsStillAccepted()   // reference arm: the guard refuses outside only
    {
        var answer = await new GetDiagnosticsTool(getRoot: () => _workspace)
            .ExecuteAsync(Args(new { path = Path.Combine(_workspace, "notes.txt") }), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(answer));   // answered (not a .NET project), not refused
    }
}
