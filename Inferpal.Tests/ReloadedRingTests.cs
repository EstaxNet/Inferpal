using Inferpal.Host;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Loading a conversation in VS Code reset the context gauge's figure to ZERO, and zero hides the gauge: a long
/// conversation restored at every start showed no fill at all until the next question, where Visual Studio measures
/// the restored conversation at once. The host already measures it on arrival (<c>HostSession.History</c>):
/// <c>session/load</c> and <c>session/branch</c> now hand that figure back, and <c>applySession</c> shows it.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public async Task ALoadedConversation_ComesBackWithTheFillTheGaugeShows()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var name = $"test-host-{Guid.NewGuid():N}";
        try
        {
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new
            {
                name,
                messages = new object[]
                {
                    new { role = "user",      content = string.Join(' ', Enumerable.Range(0, 800).Select(i => $"word{i}")) },
                    new { role = "assistant", content = "a long answer about those words" },
                },
            }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var loaded = await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
                "session/load", new { name }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.NotNull(loaded);
            var s = h.Server.CurrentSession!;
            Assert.True(s.LastPromptTokens > 1_000, $"the restored history was not measured ({s.LastPromptTokens})");   // WITNESS
            Assert.Equal(
                ContextManager.NextTurnLoad(s.LastPromptTokens, ContextManager.NextTurnToolTokens(s.Tools, s.ToolsEnabled, s.PlanMode)),
                loaded!.NextTurnTokens);
        }
        finally
        {
            await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }
    }

    [Fact]
    public void VsCode_ShowsTheLoadedConversationsFill_OnEveryLoad()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var apply = WebviewRebuildTests.Body(provider, "private applySession(");
        Assert.Contains("this.promptTokens = nextTurnTokens;", apply, StringComparison.Ordinal);

        // Every caller hands the host's figure over: one left on the default shows no gauge after that load.
        var calls = System.Text.RegularExpressions.Regex.Matches(provider, @"this\.applySession\((\w+)\.messages(, \1\.nextTurnTokens \?\? 0)?\)");
        Assert.True(calls.Count >= 5, $"only {calls.Count} load site(s) found: the scan reads nothing");   // WITNESS
        Assert.All(calls, c => Assert.True(c.Groups[2].Success, $"{c.Value} drops the loaded conversation's fill"));
    }
}
