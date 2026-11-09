using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A forced card (opaque execution, the agent's own instructions, the repository's code, a deny that could not be read)
/// grants nothing beyond its call — "Always" there would open the tool without a card everywhere else while the next
/// identical call asks again — and no editor offers it. And a build or a test the model starts through the shell, in a
/// workspace whose own code the person declined to run, is such a card.
/// </summary>
public sealed class ForcedCardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("forced-card-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Card(InferpalConfig config, string root, ApprovalDecision answer)
        : ApprovalServiceBase(config, () => root)
    {
        public readonly List<ApprovalPrompt> Shown = [];

        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown.Add(prompt);
            return Task.FromResult(answer);
        }

        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            throw new InvalidOperationException("the card path asks");
    }

    /// <summary>"Always" answered on a forced card grants no session: the tool's next ordinary call is asked again.
    /// Reference arm: the same answer on an ordinary card grants it.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task AlwaysOnAForcedCard_GrantsNothing(bool forced, int cards)
    {
        var card = new Card(new InferpalConfig(), _root, ApprovalDecision.Always);

        Assert.True(await card.RequestApprovalAsync("smart_fix_validator", "dotnet build", default, forcePrompt: forced));
        Assert.True(await card.RequestApprovalAsync("smart_fix_validator", "dotnet build", default));

        Assert.Equal(cards, card.Shown.Count);
        Assert.Equal(forced, card.Shown[0].Forced);
    }

    /// <summary>The card a forced prompt draws does not offer "Always"; an ordinary one does.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheCard_OffersAlways_OnlyWhenItIsNotForced(bool forced, bool offers)
    {
        var prompt = new ApprovalPrompt("run_command", "dotnet test", "dotnet test", null, "Run dotnet test?", Forced: forced);
        Assert.Equal(offers, ApprovalCard.Build(prompt, _root).OffersAlways);
    }

    /// <summary>An Agent Client Protocol client is not sent "allow_always" for a forced card.</summary>
    [Fact]
    public void AnAcpClient_IsNotOfferedAlways_OnAForcedCard()
    {
        static string[] Ids(object[] options) =>
            [.. options.Select(o => JsonSerializer.SerializeToElement(o).GetProperty("optionId").GetString()!)];

        Assert.DoesNotContain("allow_always", Ids(Inferpal.Host.Acp.AcpSession.Options(null, offersAlways: false)));
        Assert.Contains("allow_always", Ids(Inferpal.Host.Acp.AcpSession.Options(null, offersAlways: true)));
    }

    private sealed class Recording(bool answer) : IApprovalService
    {
        public readonly List<(string Tool, bool Forced)> Asked = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked.Add((toolName, forcePrompt));
            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// Declined: the person said No to running this workspace's code. A test run the model starts through the shell is
    /// then a forced card; an ordinary command is not. Reference arm: a workspace never declined.
    /// </summary>
    [Theory]
    [InlineData(true, "dotnet test", true)]
    [InlineData(true, "cmd /c dotnet test", true)]
    [InlineData(true, "bash -c 'pytest -q'", true)]
    [InlineData(true, @"C:\tools\dotnet.exe build", true)]
    [InlineData(true, "echo hello && dotnet test", true)]
    [InlineData(true, "echo hello", false)]
    [InlineData(true, "echo dotnet test", false)]
    [InlineData(false, "dotnet test", false)]
    public async Task AShellCheck_InADeclinedWorkspace_IsAForcedCard(bool declined, string command, bool forced)
    {
        if (declined)
        {
            var no = new WorkspaceCodeConsent(new Recording(answer: false), () => _root);
            Assert.NotNull(await no.RefusalAsync("run_tests", CancellationToken.None));
        }
        Assert.Equal(declined, WorkspaceCodeConsent.Declined(_root));

        var approval = new Recording(answer: false);   // refused: nothing runs
        using var tool = new RunCommandTool(approval, new InferpalConfig(), () => _root);
        await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { command }), default);

        Assert.Equal(forced, Assert.Single(approval.Asked).Forced);
    }
}
