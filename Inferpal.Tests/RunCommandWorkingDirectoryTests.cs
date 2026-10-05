using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>run_command</c> shows the folder it runs in on its approval prompt ("[cwd: …]") — the human approves
/// <c>git clean -fdx</c> for THAT folder. A <c>working_directory</c> that did not exist (a typo) was silently swapped for
/// the session's folder or the workspace root: the command ran somewhere the prompt never named. It is refused before
/// the prompt, and a session folder deleted under the shell is named above the output of what ran elsewhere.
/// </summary>
public sealed class RunCommandWorkingDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"cwd-{Guid.NewGuid():N}");

    public RunCommandWorkingDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task AWorkingDirectoryThatDoesNotExist_IsRefused_BeforeThePrompt()
    {
        var approval = new CountingApproval();
        var tool     = new RunCommandTool(approval, new InferpalConfig(), () => _root);
        var typo     = Path.Combine(_root, "src", "Generatd");

        var answer = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { command = "echo hi", working_directory = typo }), CancellationToken.None);

        Assert.StartsWith("Error: 'working_directory'", answer, StringComparison.Ordinal);
        Assert.Contains(typo, answer, StringComparison.Ordinal);
        Assert.Contains("nothing ran", answer, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task AnExistingWorkingDirectory_IsStillAskedAbout()   // reference arm: the guard refuses absence only
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var approval = new CountingApproval();
        var tool     = new RunCommandTool(approval, new InferpalConfig(), () => _root);

        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { command = "echo hi", working_directory = Path.Combine(_root, "src") }),
            CancellationToken.None);

        Assert.Equal(1, approval.Asked);
    }

    [Fact]
    public void ASessionFolderDeletedUnderTheShell_IsNamed_WhenTheCommandRunsElsewhere()
    {
        var session = new ShellSession(() => _root, new InferpalConfig());
        var gone    = Path.Combine(_root, "build");

        var (dir, vanished) = session.StartDirectory(gone);

        Assert.Equal(_root, dir);
        Assert.Equal(gone, vanished);
        Assert.Contains(gone, ShellSession.VanishedNote(gone, dir), StringComparison.Ordinal);
        Assert.Equal((_root, (string?)null), session.StartDirectory(null));   // reference arm: the root exists
    }
}
