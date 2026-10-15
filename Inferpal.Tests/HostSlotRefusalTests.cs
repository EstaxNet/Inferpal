using System.Text.RegularExpressions;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A host that refuses because its slot is held says what holds it, and VS Code does not take that refusal for a host
/// that left.
/// </summary>
/// <remarks>
/// The slot is held by a reply AND by operations of a moment — a settings save, an MCP restart. Refused during a settings
/// save, a question read "a reply is still being generated — stop it", about a reply that did not exist; and VS Code,
/// reading any refused request as a host that crashed, denied the approval cards still waiting — the question a settings
/// save had asked (a repository's MCP server) answered "no" without the user.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task ARequestRefusedDuringASettingsSave_SaysSo_NotThatAReplyIsBeingGenerated()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fake.OnLoadedContextWindow = async _ => { holding.TrySetResult(); await release.Task; };

        var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        cfg["customSystemPrompt"] = "saved while a question comes";
        var save = h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() });
        await holding.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));   // the save holds the slot

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hello" }));
        release.SetResult();
        await save.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.HostOperationBusy, refused.Message);
    }

    [Fact]
    public async Task ARequestRefusedDuringAReply_SaysAReplyIsBeingGenerated()
    {
        // Reference arm: the slot held by a turn keeps its sentence.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var answering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fake.OnChat = async (_, _) =>
        {
            answering.TrySetResult();
            await release.Task;
            return new ChatTurnResult("done", null, 0, 0);
        };

        var turn = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hello", agentMode = false });
        await answering.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() => h.Client.InvokeAsync("chat/reset"));
        release.SetResult();
        await turn.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.HostTurnBusy, refused.Message);
    }

    [Fact]
    public void VsCode_DismissesTheApprovalCards_OnlyWhenTheHostIsGone()
    {
        var ts = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        // The two places a request fails: each dismisses the cards under the connection-lost test only.
        Assert.Equal(2, Regex.Matches(ts, @"if \(isConnectionLost\(err\)\) \{\s*this\.dismissAllPending\(\);").Count);
        // Elsewhere, only where the host stopped.
        Assert.Equal(3, Regex.Matches(ts, @"this\.dismissAllPending\(\);").Count);
        Assert.Contains("onHostStopped(): void {", ts, StringComparison.Ordinal);
    }
}
