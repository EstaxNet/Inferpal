using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
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

    // ── And the source its own remarks name: an MCP server ────────────────────
    //
    // `AgentInstructionFiles` names MCP as a possible origin of the content, and `McpTool`
    // passed only the raw argument JSON: in {"path":"…\\.inferpal\\memory.md"} the path is not
    // a path token -- it carries a trailing quote -- so the guard answered false. One "Always"
    // clicked on an MCP filesystem tool, and the write to every later session's instructions
    // happened with no prompt at all.

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AnMcpSubject_ShowsTheGuardsTheBareValues()
    {
        var subject = McpApprovalSubject.From(Args("""
            {"path":"C:\\repo\\.inferpal\\memory.md","content":"remember this"}
            """));

        Assert.True(AgentInstructionFiles.Targets(subject));

        // Additive: the raw JSON stays first, so a rule already written against it still matches.
        Assert.StartsWith("{", subject);
        Assert.Contains("\\\\repo", subject);
    }

    [Fact]
    public void AnMcpSubject_ReachesValuesNestedInObjectsAndArrays()
    {
        var subject = McpApprovalSubject.From(Args("""
            {"edits":[{"file":"src/App.cs"},{"file":"/repo/.inferpal/rules/team.md"}]}
            """));

        Assert.True(AgentInstructionFiles.Targets(subject));
    }

    [Fact]
    public void AnOrdinaryMcpCall_IsStillNotTargeted()
    {
        // Witness: without it, a guard answering "yes" to everything would be green above.
        var subject = McpApprovalSubject.From(Args("""{"path":"C:\\repo\\src\\App.cs"}"""));

        Assert.False(AgentInstructionFiles.Targets(subject));
        Assert.Contains("App.cs", subject);          // and it did read something
    }

    [Fact]
    public async Task AnMcpSessionGrant_DoesNotCoverTheSystemPrompt()
    {
        // The whole path, exactly as McpTool takes it.
        var service = new RecordingApproval(new InferpalConfig(), ApprovalDecision.Always);
        var ordinary = McpApprovalSubject.From(Args("""{"path":"C:\\repo\\src\\App.cs"}"""));
        var memory   = McpApprovalSubject.From(Args("""{"path":"C:\\repo\\.inferpal\\memory.md"}"""));

        await service.RequestApprovalAsync("mcp__fs__write", ordinary, CancellationToken.None, subject: ordinary);
        Assert.Equal(1, service.Prompts);             // the grant is taken here

        await service.RequestApprovalAsync("mcp__fs__write", ordinary, CancellationToken.None, subject: ordinary);
        Assert.Equal(1, service.Prompts);             // and covers ordinary files

        await service.RequestApprovalAsync("mcp__fs__write", memory, CancellationToken.None, subject: memory);
        Assert.Equal(2, service.Prompts);             // but never the system prompt
    }

    /// <summary>
    /// And the WIRING, without which the three tests above would describe a helper nobody
    /// calls -- the failure mode of <c>Test-ArtifactProvenance</c>: written, documented, guarded
    /// by a test, and never wired in.
    /// </summary>
    [Fact]
    public async Task McpTool_HandsTheApprovalPipelineASubjectItsGuardsCanRead()
    {
        var approval = new SubjectSpy();
        var tool = new McpTool(new SilentClient("fs"),
                               new McpToolInfo("write", "desc", Args("{}")),
                               approval);

        await tool.ExecuteAsync(Args("""{"path":"C:\\repo\\.inferpal\\memory.md"}"""), CancellationToken.None);

        Assert.NotNull(approval.LastSubject);
        Assert.True(AgentInstructionFiles.Targets(approval.LastSubject),
            "McpTool no longer passes a subject the guards can read: subject = " + approval.LastSubject);

        // Witness: the ordinary call goes through the same path and is NOT targeted.
        await tool.ExecuteAsync(Args("""{"path":"C:\\repo\\src\\App.cs"}"""), CancellationToken.None);
        Assert.False(AgentInstructionFiles.Targets(approval.LastSubject));
        Assert.Contains("App.cs", approval.LastSubject!);
    }

    private sealed class SubjectSpy : IApprovalService
    {
        public string? LastSubject;

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false)
        {
            // Exactly what the real service does: the explicit subject, otherwise the details.
            LastSubject = subject ?? details;
            return Task.FromResult(true);
        }
    }

    private sealed class SilentClient(string name) : Inferpal.Services.Mcp.IMcpClient
    {
        public string ServerName => name;
        public string? LastError => null;
        public bool NeedsAuthorization => false;
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
    }

    [Fact]
    public async Task AnMcpSessionGrant_StillHidTheSystemPrompt_WhenOnlyTheRawJsonWasMatched()
    {
        // Reference arm: the shape as it WAS, so that "it works" is not a coincidence.
        // Raw JSON alone does not read as a path, and the grant then covers everything.
        var service = new RecordingApproval(new InferpalConfig(), ApprovalDecision.Always);
        var rawJson = """{"path":"C:\\repo\\.inferpal\\memory.md"}""";

        Assert.False(AgentInstructionFiles.Targets(rawJson));

        await service.RequestApprovalAsync("mcp__fs__write", rawJson, CancellationToken.None, subject: rawJson);
        await service.RequestApprovalAsync("mcp__fs__write", rawJson, CancellationToken.None, subject: rawJson);
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
