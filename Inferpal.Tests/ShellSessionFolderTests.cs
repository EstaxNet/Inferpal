using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  run_command runs where its approval prompt says, and only the session's own moves persist.
//
//  Three ways the folder slipped: a working_directory "for this command" became the session's
//  folder for every later command; those later commands — and any after a plain `cd` — went to the
//  prompt with no folder named, which reads as the root; and in Visual Studio, where one session
//  lives as long as devenv, opening another solution left commands running in the previous one.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public sealed class ShellSessionFolderTests : IDisposable
{
    private readonly string _root  = Directory.CreateTempSubdirectory("inferpal-shellroot-").FullName;
    private readonly string _other = Directory.CreateTempSubdirectory("inferpal-shellother-").FullName;
    private string Sub => Path.Combine(_root, "only-here");

    public ShellSessionFolderTests() => Directory.CreateDirectory(Sub);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_other, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Recording : IApprovalService
    {
        public string? Last { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Last = details;
            return Task.FromResult(true);
        }
    }

    private static Task<string> Run(RunCommandTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task AWorkingDirectory_IsForThatCommandOnly()
    {
        var session = new ShellSession(() => _root, new InferpalConfig());

        await session.RunAsync("echo hi", Sub, CancellationToken.None);

        Assert.True(ShellSession.SameFolder(_root, session.CurrentDirectory), session.CurrentDirectory);
    }

    [Fact]
    public async Task AfterACd_TheNextPrompt_NamesTheFolder()
    {
        var approval = new Recording();
        using var tool = new RunCommandTool(approval, new InferpalConfig(), () => _root);

        await Run(tool, new { command = "echo at-root" });
        // Reference arm: at the root, the prompt is the bare command — what permission rules and users read.
        Assert.Equal("echo at-root", approval.Last);

        await Run(tool, new { command = "cd only-here" });
        await Run(tool, new { command = "echo again" });

        Assert.Contains("[cwd: ", approval.Last, StringComparison.Ordinal);
        Assert.Contains("only-here", approval.Last, StringComparison.Ordinal);
    }

    /// <summary>⚠ A command that spans a solution switch leaves the state of the root it STARTED under: filed under the
    /// new root, its folder became the next solution's, and every later command ran in the old one.</summary>
    [Fact]
    public async Task ACommandSpanningASolutionSwitch_LeavesItsStateToTheRootItStartedUnder()
    {
        var reads   = 0;
        var session = new ShellSession(() => reads++ == 0 ? _root : _other, new InferpalConfig());   // switched mid-command

        await session.RunAsync("echo hi", null, CancellationToken.None);

        Assert.True(ShellSession.SameFolder(_other, session.CurrentDirectory), session.CurrentDirectory);
    }

    [Fact]
    public async Task AnotherWorkspaceRoot_StartsFromThatRoot()
    {
        var root    = _root;
        var session = new ShellSession(() => root, new InferpalConfig());
        await session.RunAsync("cd only-here", null, CancellationToken.None);
        // Witness: the session did move — otherwise the switch below proves nothing.
        Assert.EndsWith("only-here", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);

        root = _other;   // another solution opened in the same Visual Studio

        Assert.True(ShellSession.SameFolder(_other, session.CurrentDirectory), session.CurrentDirectory);
    }

    /// <summary>A command started in A that ends after B opened writes A's folder: set aside under A, never kept for B.</summary>
    [Fact]
    public async Task ACommandThatEndsAfterTheSwitch_LeavesItsFolderToItsOwnWorkspace()
    {
        var root    = _root;
        var session = new ShellSession(() => root, new InferpalConfig());
        var ran = session.RunAsync("cd only-here", null, CancellationToken.None);
        root = _other;                                   // B opened while A's command runs
        Assert.True(ShellSession.SameFolder(_other, session.CurrentDirectory), session.CurrentDirectory);
        await ran;                                       // it ends, and writes A's folder

        Assert.True(ShellSession.SameFolder(_other, session.CurrentDirectory), session.CurrentDirectory);
        root = _root;
        Assert.EndsWith("only-here", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
    }

    /// <summary>A command started in B that ends after the person came back to A writes B's set-aside state, never A's.</summary>
    [Fact]
    public async Task ACommandOfTheRootLeft_DoesNotOverwriteTheStateFoundAgain()
    {
        Directory.CreateDirectory(Path.Combine(_other, "there"));
        var root    = _root;
        var session = new ShellSession(() => root, new InferpalConfig());
        await session.RunAsync("cd only-here", null, CancellationToken.None);
        root = _other;
        var ran = session.RunAsync("cd there", null, CancellationToken.None);   // B's command, still running…
        root = _root;                                                           // …when the person goes back to A
        Assert.EndsWith("only-here", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
        await ran;

        Assert.EndsWith("only-here", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
        root = _other;
        Assert.EndsWith("there", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
    }

    /// <summary>Set aside, not forgotten: back in the first solution, its folder holds again — and the other's stays its own.</summary>
    [Fact]
    public async Task BackInTheFirstWorkspace_ItsFolderHoldsAgain()
    {
        Directory.CreateDirectory(Path.Combine(_other, "there"));
        var root    = _root;
        var session = new ShellSession(() => root, new InferpalConfig());
        await session.RunAsync("cd only-here", null, CancellationToken.None);

        root = _other;
        Assert.True(ShellSession.SameFolder(_other, session.CurrentDirectory), session.CurrentDirectory);
        await session.RunAsync("cd there", null, CancellationToken.None);

        root = _root;
        Assert.EndsWith("only-here", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
        root = _other;
        Assert.EndsWith("there", Path.TrimEndingDirectorySeparator(session.CurrentDirectory), StringComparison.Ordinal);
    }
}
