using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run that FAILED is not an answer. <c>RunAgentAsync</c> never throws for a backend failure — it returns the
/// message as <c>FinalResponse</c>, which suits a chat bubble — and three callers took that text for content: the
/// session summary joined the system prompt of every following question, <c>/commit</c> pre-filled it as the commit
/// message, and the session was named after it.
/// </summary>
public partial class FailedAgentRunTests
{
    private const string Refusal = """{"error":"model 'm' crashed"}""";

    /// <summary>A real client against a server that refuses every chat request.</summary>
    private static (LoopbackHttpServer Server, OpenAiCompatibleClient Client) Refusing()
    {
        var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Refusal : null, status: _ => 500);
        return (server, new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl }));
    }

    [Fact]
    public async Task ACommitProposal_IsNeverTheErrorMessage()
    {
        var (server, client) = Refusing();
        using var _ = server;
        GitRunner git = (args, _) => Task.FromResult((args == "diff --staged" ? "diff --git a/A.cs b/A.cs\n+added" : "", 0));

        var result = await CommitCommandHandler.ProposeAsync(client, new InferpalConfig(), git, null, CancellationToken.None);

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));   // witness: the model was asked
        Assert.Null(result.Proposal);                                              // nothing pre-filled to commit
        Assert.Contains("crashed", result.Message);                                // and the cause is said
    }

    [Fact]
    public async Task ASessionTitle_IsNeverTheErrorMessage()
    {
        var (server, client) = Refusing();
        using var _ = server;
        const string question = "Why does the parser drop the last token?";

        var title = await SessionTitleGenerator.GenerateAsync(client, new InferpalConfig(), question, CancellationToken.None);

        Assert.Contains("/v1/chat/completions", string.Join(" ", server.Paths));   // witness
        Assert.Equal(SessionManager.MakeSnippet(question), title);
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// The session summary joins the system prompt of every following question. Written from a failed run, it
    /// carried the error message there — "## Session Summary" followed by a refusal — and showed it as a recap.
    /// The request that fails is the likely one: it carries the whole conversation, after many turns.
    /// </summary>
    [Fact]
    public async Task ASessionSummary_IsNeverTheErrorMessage()
    {
        using var h = CreateHarness(cfg => cfg.OodaTurnThreshold = 1);
        var calls = 0;
        string? system = null;
        h.Fake.OnChatRequest = (_, history, _, _) =>
        {
            system = history.FirstOrDefault(m => m.Role == "system")?.Content;
            return ++calls == 2
                ? throw new AgentHttpException("the request is too large for the loaded context", isTimeout: false)
                : Task.FromResult(new ChatTurnResult("ok", null, 1, 1));
        };
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));   // turn 1, then the summary (call 2) fails
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "again", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(calls >= 3, $"only {calls} model call(s): the summary was never attempted");   // witness
        Assert.DoesNotContain("too large for the loaded context", system);
        Assert.DoesNotContain(h.Target.ToolNotes, n => n.Name == "ooda_recap" && n.Output.Contains("too large"));
    }
}

public partial class FailedAgentRunTests
{
    /// <summary>The Visual Studio half of the session summary reads the flag too — the view model is not executable
    /// from this suite, so this is a source scan with its witness; the host half is executed above.</summary>
    [Fact]
    public void TheViewModelsSessionSummary_SkipsAFailedRun()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));

        var method = code.IndexOf("Task RunOodaSummaryAsync(", StringComparison.Ordinal);
        var write  = code.IndexOf("_oodaSummary  = summary", method, StringComparison.Ordinal);
        Assert.True(method >= 0 && write > method, "the session summary moved: the scan reads nothing");   // WITNESS
        Assert.Contains("result.Failed", code[method..write]);
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// A turn whose run FAILED is not answered: the plain chat already said so (its request throws, the adapter shows
    /// an error), and the two loop paths — tools, agent — returned the error as the answer and KEPT it: the model
    /// re-read "the backend refused" as what it had said, and a reload handed it back.
    /// </summary>
    [Theory]
    [InlineData(false)]   // chat with tools: the basic loop
    [InlineData(true)]    // agent mode: the orchestrator
    public async Task AFailedTurn_IsShownAsAnError_NeverKeptAsTheAnswer(bool agentMode)
    {
        using var h = CreateHarness();
        var calls = 0;
        List<ChatMessageDto>? next = null;
        h.Fake.OnChatRequest = (_, history, _, _) =>
        {
            if (++calls == 1) throw new AgentHttpException("the backend refused the request", isTimeout: false);
            next ??= [.. history];
            return Task.FromResult(new ChatTurnResult("ok", null, 1, 1));
        };
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var failed = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "first", agentMode }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "second", agentMode }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("the backend refused the request", failed.Error);           // shown as the error it is
        Assert.NotNull(next);                                                       // witness: the second turn ran
        Assert.DoesNotContain(next!, m => m.Role == "assistant" && (m.Content ?? "").Contains("backend refused"));
    }
}

public partial class FailedAgentRunTests
{
    /// <summary>The Visual Studio half of "a failed turn is never kept as the answer": both run paths record the
    /// failure, and the durable history leaves it out (the view model is not executable from this suite).</summary>
    [Fact]
    public void TheViewModel_NeverKeepsAFailedTurnAsTheAnswer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));

        Assert.Contains("agentFailed        = orchResult.Failed;", code);          // the agent path
        Assert.Contains("agentFailed        = result.Failed;", code);              // the basic loop
        Assert.Contains("if (persistedAnswer.Length > 0 && !agentFailed)", code);
    }
}
