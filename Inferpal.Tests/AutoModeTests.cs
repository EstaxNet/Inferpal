using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Auto mode (approvals off), as validated on 21/11: chosen as Ask me / Auto with what it risks said, turned on only through
/// a confirmation, and visible in the chat — a badge while it lasts, "auto" on each step it let through, counted on the bar.
/// </summary>
public class AutoModeTests
{
    private sealed class Approval(InferpalConfig config, ApprovalDecision answer = ApprovalDecision.Once) : ApprovalServiceBase(config, () => null)
    {
        public int Prompts;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task AutoMode_CountsWhatItLetThrough_AndNothingElse()
    {
        var auto = new Approval(new InferpalConfig { SecurityAlertsDisabled = true });
        Assert.True(await auto.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build"));
        Assert.Equal(1, auto.AutoApprovals);
        Assert.Equal(0, auto.Prompts);

        // A rule that allows is no auto mode; a call auto mode never skips (forced) asks, and is not counted.
        var ruled = new Approval(new InferpalConfig { PermissionRules = "allow run_command .*" });
        await ruled.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build");
        Assert.Equal(0, ruled.AutoApprovals);
        await auto.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build", forcePrompt: true);
        Assert.Equal(1, auto.AutoApprovals);
        Assert.Equal(1, auto.Prompts);

        // REFERENCE ARM: approvals on, a card, nothing counted.
        var asking = new Approval(new InferpalConfig());
        await asking.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build");
        Assert.Equal((0, 1), (asking.AutoApprovals, asking.Prompts));
    }

    [Fact]
    public async Task ACallAnAlwaysAlreadyLetsThrough_IsNotCountedAsAuto()
    {
        var config = new InferpalConfig();
        var approval = new Approval(config, ApprovalDecision.Always);
        await approval.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build");   // "Always"
        config.SecurityAlertsDisabled = true;
        await approval.RequestApprovalAsync("run_command", "dotnet build", CancellationToken.None, subject: "dotnet build");
        Assert.Equal(0, approval.AutoApprovals);
    }

    [Fact]
    public void TheResultBar_CountsTheStepsRunWithoutAsking()
    {
        var steps = new[]
        {
            new Inferpal.Services.Inference.ToolExecution("read_file", "{}", "x"),
            new Inferpal.Services.Inference.ToolExecution("run_command", """{"command":"dotnet build"}""", "ok [exit code 0]", Auto: true),
            new Inferpal.Services.Inference.ToolExecution("apply_diff", """{"path":"a.cs"}""", "Edited.", Wrote: true, Auto: true),
        };
        Assert.Contains(Strings.RunAutoSteps(2), RunSummary.Build(steps, null)!.Detail);
        Assert.DoesNotContain(Strings.RunAutoSteps(0), RunSummary.Build([steps[0]], null)!.Detail);
    }

    [Fact]
    public void TheHost_SaysAStepRanWithoutAsking()
    {
        var notice = Inferpal.Host.ToolNotice.Of(new Inferpal.Services.Inference.ToolExecution("run_command", "{}", "ok", Auto: true));
        Assert.True(notice.Auto);
    }

    [Fact]
    public void BothEditors_DrawTheChoiceTheRisksTheConfirmationAndTheBadge()
    {
        var field = SettingsSchema.Field("securityAlertsDisabled");
        Assert.Equal("approvalMode", field.Editor);

        var root = ConversationPersistenceSilenceTests.RepoRoot();
        string Ts(string rel) => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(root, "vscode", "src", rel)));
        var settings = Ts(Path.Combine("webview", "settings.ts"));
        Assert.Contains("field.editor === 'approvalMode'", settings);
        Assert.Contains("saveBtn.addEventListener('click', onSaveClicked)", settings);   // Save goes through the confirmation
        Assert.Contains("off.disabled = !box.checked", settings);                       // ticked first
        var chat = Ts(Path.Combine("webview", "main.ts"));
        Assert.Contains("case 'autoMode':", chat);
        Assert.Contains("item.toolAuto", chat);

        var xaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        Assert.Contains("{Binding ApprovalsAuto, Mode=TwoWay}", xaml);
        Assert.Contains("IsEnabled=\"{Binding AutoConfirmTicked}\"", xaml);
        var chatXaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains("Visibility=\"{Binding AutoMode, Converter={StaticResource b2v}}\"", chatXaml);
        Assert.Contains("Text=\"{Binding StepAuto}\"", chatXaml);
    }
}
