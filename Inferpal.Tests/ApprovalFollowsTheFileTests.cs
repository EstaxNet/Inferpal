using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings that decide an approval — the permission rules, the switch that turns security alerts off — are the
/// ones in <c>config.json</c> NOW: a rule removed or alerts turned back on in another window apply here at once.
/// </summary>
/// <remarks>
/// ⚠ Each window of both editors reads the file once and keeps its copy. An <c>allow run_command .*</c> removed in one
/// window went on approving every command, unasked, in the other until it restarted — and its <c>/permissions</c> still
/// listed the rule.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public class ApprovalFollowsTheFileTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"approval-{Guid.NewGuid():N}.json");

    public ApprovalFollowsTheFileTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private sealed class CountingApproval(InferpalConfig config) : ApprovalServiceBase(config, () => null)
    {
        public int Prompts;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(ApprovalDecision.Once);
        }
    }

    private void WriteAsTheOtherWindow(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
        // A different stamp than the save above, however fast the machine.
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));
    }

    private static Task<bool> RunCommand(CountingApproval approval) =>
        approval.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None);

    [Fact]
    public async Task ARuleRemovedInAnotherWindow_StopsApprovingHere()
    {
        new InferpalConfig { PermissionRules = "allow run_command dotnet.*" }.Save();
        var approval = new CountingApproval(InferpalConfig.Load());

        await RunCommand(approval);
        Assert.Equal(0, approval.Prompts);                                   // witness: the rule approved it

        WriteAsTheOtherWindow(c => c.PermissionRules = string.Empty);
        await RunCommand(approval);

        Assert.Equal(1, approval.Prompts);
    }

    [Fact]
    public async Task AlertsTurnedBackOnInAnotherWindow_AskHere()
    {
        new InferpalConfig { SecurityAlertsDisabled = true }.Save();
        var approval = new CountingApproval(InferpalConfig.Load());

        await RunCommand(approval);
        Assert.Equal(0, approval.Prompts);                                   // witness: alerts off approved it

        WriteAsTheOtherWindow(c => c.SecurityAlertsDisabled = false);
        await RunCommand(approval);

        Assert.Equal(1, approval.Prompts);
    }

    [Fact]
    public async Task ARuleChangedInThisWindow_IsThisWindowsUntilSaved()
    {
        // Reference arm: what this copy changed itself is its own — the file follows at its save.
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();
        mine.PermissionRules = "allow run_command dotnet.*";
        var approval = new CountingApproval(mine);

        await RunCommand(approval);

        Assert.Equal(0, approval.Prompts);
    }

    [Fact]
    public async Task ACopyBuiltInCode_IsItsOwnTruth()
    {
        var approval = new CountingApproval(new InferpalConfig { PermissionRules = "allow run_command dotnet.*" });

        await RunCommand(approval);

        Assert.Equal(0, approval.Prompts);
    }

    [Fact]
    public void Permissions_ListsTheRulesThatDecide()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var vs   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSlashCommands.cs"));

        Assert.Contains("PermissionsCommandHandler.Permissions(", vs, StringComparison.Ordinal);    // witness
        Assert.Contains("SharedPermissionRules", vs, StringComparison.Ordinal);
        Assert.Contains("s.Config.SharedPermissionRules", host, StringComparison.Ordinal);
    }
}
