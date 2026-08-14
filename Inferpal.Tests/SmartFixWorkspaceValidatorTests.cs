using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// Smart Fix runs a build command by itself after every write. When that command comes from the
// workspace's `.inferpal/validators.json`, it was written by the REPOSITORY — and a repository is
// cloned. So: cloning a project and letting the agent touch one file must not be enough to run
// that project's command. The hard denylist is not the answer (it matches text, obfuscation walks
// around it); the approval prompt is, because that is where a human reads the raw command.
public class SmartFixWorkspaceValidatorTests : IDisposable
{
    private readonly string _root;

    public SmartFixWorkspaceValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-smartfix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Records what it was asked, and whether the request could bypass consent.</summary>
    private sealed class RecordingApproval : IApprovalService
    {
        public bool Answer { get; set; }
        public List<(string Tool, string Details, bool ForcePrompt)> Requests { get; } = [];

        public Task<bool> RequestApprovalAsync(
            string toolName, string details, CancellationToken ct, string? subject = null,
            DiffInfo? diff = null, bool forcePrompt = false)
        {
            Requests.Add((toolName, details, forcePrompt));
            return Task.FromResult(Answer);
        }
    }

    // A command that would be trivially recognisable is deliberately NOT used: the point is that a
    // perfectly ordinary-looking command must still be shown, because its author is the repo.
    private const string RepoCommand = "node ./scripts/postbuild.js";

    private void WriteHostileWorkspace()
    {
        File.WriteAllText(
            Path.Combine(_root, ".inferpal", "validators.json"),
            $$"""{ ".cs": { "marker": "*.csproj", "command": "{{RepoCommand}}" } }""");
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { }");
    }

    private SmartFixValidator Validator(IApprovalService? approval) =>
        new(new InferpalConfig { SmartFixEnabled = true }, () => _root, approval);

    [Fact]
    public async Task WorkspaceCommand_IsSubmittedForApproval_AndForcePrompted()
    {
        WriteHostileWorkspace();
        var approval = new RecordingApproval { Answer = false };

        await Validator(approval).ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);

        var request = Assert.Single(approval.Requests);
        Assert.Equal(RepoCommand, request.Details);
        // Force-prompted: no allow rule, no session grant and no SecurityAlertsDisabled may
        // auto-approve a command the user never wrote.
        Assert.True(request.ForcePrompt);
    }

    [Fact]
    public async Task WorkspaceCommand_IsNotRunWhenTheUserDeclines()
    {
        WriteHostileWorkspace();
        var approval = new RecordingApproval { Answer = false };

        var result = await Validator(approval)
            .ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);

        // Nothing ran, so there is nothing to report — and the write itself is untouched.
        Assert.Null(result);
    }

    // Fail closed: an execution surface without an approval surface is not a licence to run.
    [Fact]
    public async Task WorkspaceCommand_WithNoApprovalService_IsRefusedRatherThanRun()
    {
        WriteHostileWorkspace();

        var result = await Validator(null)
            .ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ApprovalIsAskedOncePerSession_NotOncePerWrite()
    {
        WriteHostileWorkspace();
        var approval = new RecordingApproval { Answer = true };
        var validator = Validator(approval);

        await validator.ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);
        await validator.ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);
        await validator.ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);

        Assert.Single(approval.Requests);
    }

    // The built-in validators are ours, not the repository's: they must keep running unattended,
    // otherwise the fix would have turned Smart Fix into a prompt machine.
    [Fact]
    public async Task BuiltInValidator_RunsWithoutAsking()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { }");
        var approval = new RecordingApproval { Answer = true };

        await Validator(approval).ValidateAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None);

        Assert.Empty(approval.Requests);
    }

    [Fact]
    public void OverlayValidators_AreMarkedAsComingFromTheRepository()
    {
        var parsed = BuildValidators.ParseConfig(
            """{ ".ts": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }""");

        Assert.True(Assert.Single(parsed).FromWorkspace);
        Assert.All(BuildValidators.Defaults, v => Assert.False(v.FromWorkspace));
    }
}
