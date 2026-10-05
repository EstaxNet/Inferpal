using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A custom tool's arguments go where the command says, and it runs in the workspace.
//
//  Both settings panels tell the user to write {args} where the agent's arguments go, in all ten
//  languages; the tool appended them after the command and left the placeholder in it. And the
//  command started in whatever folder the process had — the workspace in VS Code, the extension
//  host's own folder in Visual Studio — unlike every other command the product runs.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(ShellSerialCollection.Name)]
public sealed class UserShellToolArgsAndFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-usertool-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Approve : IApprovalService
    {
        public string? Shown { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Shown = details;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(string? args) =>
        JsonDocument.Parse(args is null ? "{}" : JsonSerializer.Serialize(new { args })).RootElement.Clone();

    [Theory]
    [InlineData("git grep -n {args} src", "Widget", "git grep -n Widget src")]
    [InlineData("npm run test:e2e -- {args}", "login.spec.ts", "npm run test:e2e -- login.spec.ts")]
    [InlineData("echo {args} and {args}", "x", "echo x and x")]
    // Reference arm: a command that names no spot still gets the arguments appended.
    [InlineData("dotnet test", "--no-build", "dotnet test --no-build")]
    public void TheArguments_GoWhereTheCommandSays(string command, string extra, string expected) =>
        Assert.Equal(expected, UserShellTool.Expand(command, extra));

    [Fact]
    public async Task ThePlaceholder_IsReplaced_InWhatRuns_AndInWhatTheApprovalShows()
    {
        var approval = new Approve();
        var output = await new UserShellTool("probe", "echo \"args=({args})\"", approval, new InferpalConfig(), () => _root)
            .ExecuteAsync(Args("hello"), CancellationToken.None);

        Assert.Contains("args=(hello)", output);
        Assert.Equal("echo \"args=(hello)\"", approval.Shown);
    }

    [Fact]
    public async Task TheCommand_RunsInTheWorkspace()
    {
        // The temp folder's name is unique: the test runner's own folder cannot print it. Printed as a plain string —
        // `echo "$PWD"` reads the same in bash and PowerShell — so no table view stands between the path and the test.
        var output = await new UserShellTool("probe", "echo \"$PWD\"", new Approve(), new InferpalConfig(), () => _root)
            .ExecuteAsync(Args(null), CancellationToken.None);

        Assert.Contains(Path.GetFileName(_root), output);
    }

    [Fact]
    public async Task OnWindows_ATableViewOfALongPath_ComesBackWhole()
    {
        // Past the console's width a PowerShell table view (what `pwd` prints there) cuts the path with "…" unless the
        // buffer is widened, which only Windows allows: pwsh on Linux and macOS formats at 80 columns, its buffer
        // refuses to grow ("Operation is not supported on this platform", measured), and the one other lever —
        // Out-String — would make the pipeline read native output. That limit is known, not tested here.
        if (!OperatingSystem.IsWindows()) return;

        var deep = Path.Combine(_root, "a-workspace-folder-whose-name-alone-is-longer-than-a-console-is-wide-"
                                       + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deep);
        Assert.True(deep.Length > 130, $"The path ({deep.Length} characters) must outrun a 120-column console.");

        var output = await new UserShellTool("probe", "pwd", new Approve(), new InferpalConfig(), () => deep)
            .ExecuteAsync(Args(null), CancellationToken.None);

        Assert.Contains(Path.GetFileName(deep), output);
    }
}
