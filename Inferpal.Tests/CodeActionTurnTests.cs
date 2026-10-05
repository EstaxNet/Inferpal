using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A code action in VS Code was answered by another model, with tools, than the same action in Visual Studio.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>/explain</c> and <c>/review</c> went out as an ordinary chat turn: with agent mode on, the agent planned,
/// called tools and raised approval prompts to explain a selection — the case Visual Studio rules out by answering a
/// code action with no tool at all. And the turn, like <c>/fix</c>, <c>/refactor</c> and <c>/doc</c>, was answered
/// by the model picked in the chat: the adapter always sends one, and the host let it win over
/// <c>codeActionsModel</c>, which no VS Code code action could reach.
/// </para>
/// <para>
/// The rule is one table (<see cref="ModelRouter.Resolve(InferpalConfig, ModelRole, string?)"/>): a per-role model
/// wins over the pick, the pick stands in for the configured default.
/// </para>
/// </remarks>
public class CodeActionModelRoutingTests
{
    [Fact]
    public void ARoleModel_WinsOverThePick_AndThePickStandsInForTheDefault()
    {
        var config = new InferpalConfig { DefaultModel = "default", CodeActionsModel = "coder", AgentModel = "" };

        Assert.Equal("coder", ModelRouter.Resolve(config, ModelRole.CodeActions, "picked"));
        Assert.Equal("picked", ModelRouter.Resolve(config, ModelRole.Agent, "picked"));
        Assert.Equal("picked", ModelRouter.Resolve(config, ModelRole.Chat, "picked"));
        // No pick: the configured default, as the two-argument table has always answered.
        Assert.Equal("default", ModelRouter.Resolve(config, ModelRole.Agent, null));
        Assert.Equal(ModelRouter.Resolve(config, ModelRole.Agent), ModelRouter.Resolve(config, ModelRole.Agent, " "));
    }
}

public partial class HostServerTests
{
    private static Harness CodeActionHarness(string codeActionsModel, List<int> toolCounts)
    {
        var h = CreateHarness(cfg =>
        {
            cfg.AgentModeEnabled = true;
            cfg.CodeActionsModel = codeActionsModel;
        });
        h.Fake.OnChatRequest = (_, _, tools, _) =>
        {
            lock (toolCounts) toolCounts.Add(tools.Definitions.Count);
            return Task.FromResult(new ChatTurnResult("explained", null, 1, 1));
        };
        return h;
    }

    [Fact]
    public async Task ACodeActionTurn_IsAnsweredByTheCodeActionsModel_WithoutTools_WhateverTheAgentSwitch()
    {
        var toolCounts = new List<int>();
        using var h = CodeActionHarness("coder", toolCounts);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var r = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt = "Explain the following code", model = "picked", agentMode = true, codeAction = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(r.Error);
        Assert.Equal("coder", r.Model);
        Assert.Equal(["coder"], h.Fake.ChatModels);
        Assert.Empty(h.Fake.AgentRuns);                      // no tool loop
        Assert.Equal([0], toolCounts);                       // and no tool definition in the one request
    }

    [Fact]
    public async Task ACodeActionTurn_WithoutACodeActionsModel_IsAnsweredByThePick()
    {
        var toolCounts = new List<int>();
        using var h = CodeActionHarness("", toolCounts);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var r = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt = "Explain the following code", model = "picked", codeAction = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("picked", r.Model);
        Assert.Equal([0], toolCounts);
    }

    [Fact]
    public async Task AnOrdinaryTurn_KeepsItsToolsAndThePick()
    {
        // REFERENCE ARM: the switch only concerns code actions — a chat turn keeps the tool loop and the chat model.
        var toolCounts = new List<int>();
        using var h = CodeActionHarness("coder", toolCounts);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var r = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt = "hello", model = "picked", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("picked", r.Model);
        Assert.Single(h.Fake.AgentRuns);
        Assert.True(toolCounts.Single() > 0, "the chat turn lost its tools");
    }

    [Fact]
    public async Task AnInPlaceCodeAction_IsAnsweredByTheCodeActionsModel_NotThePick()
    {
        using var h = CreateHarness(cfg => cfg.CodeActionsModel = "coder");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("int y = 2;", null, 0, 0);

        await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "fix", text = "int x = 1;", selStart = 0, selEnd = 0, model = "picked" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(["coder"], h.Fake.ChatModels);
    }

    [Fact]
    public async Task TheExplainExcerpt_IsSizedForTheModelThatAnswersIt()
    {
        using var h = CreateHarness(cfg => cfg.CodeActionsModel = "coder");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.LoadedContextQueries.Clear();

        await h.Client.InvokeWithParameterObjectAsync<CodeExcerptResult>(
                "code/excerpt", new { code = "int x = 1;", fileName = "A.cs", selection = false, model = "picked" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(["coder"], h.Fake.LoadedContextQueries);
    }
}
