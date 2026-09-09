using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A write aimed at the agent's own future instructions always reaches the human.
/// </summary>
/// <remarks>
/// <para>
/// <c>SystemPromptBuilder</c> injects <c>.inferpal/context.md</c>, <c>memory.md</c>, <c>notes.md</c>
/// and <c>rules/*.md</c> into the system prompt of every later session. <c>UpdateMemoryTool</c>'s
/// own remarks call an unattended write there "the persistence half of a prompt-injection chain" —
/// and nothing held it. Measured 2026-09-09: seven write paths reach those files and not one asked
/// for a forced prompt, so a single "Always" on <c>write_file</c> — the main editing tool, clicked
/// once — left the model free to rewrite its own instructions in silence for the rest of the
/// session.
/// </para>
/// <para>
/// Force-prompt, never refuse: writing memory or a rule is a legitimate thing for the agent to do.
/// What must not happen is that it happens invisibly. Same shape as <c>TestFileWriteGuard</c>, which
/// already did this for the strictly less sensitive case of a test file.
/// </para>
/// </remarks>
public class AgentInstructionWriteTests
{
    private sealed class RecordingApproval(InferpalConfig config, ApprovalDecision answer)
        : ApprovalServiceBase(config, () => null)
    {
        public int Prompts;

        protected override Task<ApprovalDecision> PromptUserAsync(
            string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(answer);
        }
    }

    // ── The predicate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\repo\.inferpal\memory.md")]
    [InlineData(@"C:\repo\.inferpal\context.md")]
    [InlineData(@"C:\repo\.inferpal\notes.md")]
    [InlineData("/home/dev/repo/.inferpal/memory.md")]
    [InlineData(@"C:\repo\.inferpal\rules\style.md")]
    [InlineData("/home/dev/repo/.inferpal/rules/nested/team.md")]
    // Case: the directory is matched case-insensitively, like the file system it names.
    [InlineData(@"C:\repo\.Inferpal\Memory.md")]
    // apply_edits joins several paths with newlines -- one is enough.
    [InlineData("C:\\repo\\src\\A.cs\nC:\\repo\\.inferpal\\memory.md")]
    public void AWriteAtTheSystemPrompt_IsRecognised(string subject) =>
        Assert.True(AgentInstructionFiles.Targets(subject));

    [Theory]
    [InlineData(@"C:\repo\src\Program.cs")]
    [InlineData("/home/dev/repo/README.md")]
    // Not injected: the overlay is deny-only, so writing it can only ever restrict the agent.
    [InlineData(@"C:\repo\.inferpal\permissions.json")]
    [InlineData(@"C:\repo\.inferpal\project.json")]
    // A rules directory that is not .inferpal's.
    [InlineData(@"C:\repo\docs\rules\style.md")]
    // The name alone, outside the directory, is an ordinary file.
    [InlineData(@"C:\repo\memory.md")]
    // Sessions and snapshots live under .inferpal but are never read back as instructions.
    [InlineData(@"C:\repo\.inferpal\history\2026-05-17_Strings.fr.resx")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryWrite_IsNot(string? subject) =>
        Assert.False(AgentInstructionFiles.Targets(subject));

    // ── The three auto-approval paths, each neutralised ───────────────────────

    private const string Memory = @"C:\repo\.inferpal\memory.md";
    private const string Source = @"C:\repo\src\Program.cs";

    [Fact]
    public async Task AllowRule_DoesNotCoverTheSystemPrompt()
    {
        var config  = new InferpalConfig { PermissionRules = "allow write_file .*" };
        var service = new RecordingApproval(config, ApprovalDecision.Once);

        Assert.True(await service.RequestApprovalAsync("write_file", Source, CancellationToken.None, subject: Source));
        Assert.Equal(0, service.Prompts);

        Assert.True(await service.RequestApprovalAsync("write_file", Memory, CancellationToken.None, subject: Memory));
        Assert.Equal(1, service.Prompts);
    }

    [Fact]
    public async Task SecurityAlertsDisabled_DoesNotCoverTheSystemPrompt()
    {
        var config  = new InferpalConfig { SecurityAlertsDisabled = true };
        var service = new RecordingApproval(config, ApprovalDecision.Once);

        Assert.True(await service.RequestApprovalAsync("write_file", Source, CancellationToken.None, subject: Source));
        Assert.Equal(0, service.Prompts);

        Assert.True(await service.RequestApprovalAsync("write_file", Memory, CancellationToken.None, subject: Memory));
        Assert.Equal(1, service.Prompts);
    }

    [Fact]
    public async Task SessionGrant_DoesNotCoverTheSystemPrompt()
    {
        // The realistic one: "Always" on write_file is a click the user makes in the first ten
        // minutes, and it used to carry every later write to the agent's own instructions.
        var config  = new InferpalConfig();
        var service = new RecordingApproval(config, ApprovalDecision.Always);

        await service.RequestApprovalAsync("write_file", Source, CancellationToken.None, subject: Source);
        Assert.Equal(1, service.Prompts);                       // the grant is taken here

        await service.RequestApprovalAsync("write_file", Source, CancellationToken.None, subject: Source);
        Assert.Equal(1, service.Prompts);                       // and covers ordinary files

        await service.RequestApprovalAsync("write_file", Memory, CancellationToken.None, subject: Memory);
        Assert.Equal(2, service.Prompts);                       // but never the system prompt
    }

    [Fact]
    public async Task ItPromptsRatherThanRefuses()
    {
        // The other half, and the one that keeps the feature usable: writing memory or a rule is a
        // legitimate thing to do. Approved at the prompt, the write goes through.
        var service = new RecordingApproval(new InferpalConfig(), ApprovalDecision.Once);

        Assert.True(await service.RequestApprovalAsync("update_memory", Memory, CancellationToken.None, subject: Memory));
        Assert.Equal(1, service.Prompts);
    }

    [Fact]
    public async Task AnswerAlwaysOnTheSystemPrompt_DoesNotBuyAFreePassEither()
    {
        // "Always" stores a session grant for the tool -- which the funnel then refuses to honour
        // for these paths. Otherwise the very first instruction write would disarm the guard.
        var service = new RecordingApproval(new InferpalConfig(), ApprovalDecision.Always);

        await service.RequestApprovalAsync("update_memory", Memory, CancellationToken.None, subject: Memory);
        await service.RequestApprovalAsync("update_memory", Memory, CancellationToken.None, subject: Memory);

        Assert.Equal(2, service.Prompts);
    }
}
