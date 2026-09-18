using System.IO;
using System.Linq;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Context X-Ray counted the system prompt <b>twice</b>: once broken down into layers, once
/// again inside "history".
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A conversation's history <b>starts with</b> the system message — <c>_history =
/// [new("system", …)]</c> in the view-model, <c>s.History = [new("system", …)]</c> in the host — and
/// both X-Ray surfaces were handed <c>EstimateTokens(history)</c> as their "history" figure, then
/// added it to their own sum of the prompt sections. So a <b>brand-new</b> conversation reported a
/// history already weighing as much as the whole system prompt, and the budget line over-reported by
/// exactly the part X-Ray exists to display: base prompt, <c>.inferpal/context.md</c>, memory, notes,
/// rules.
/// </para>
/// <para>
/// ⚠ <b>Two indicators, one conversation, two answers.</b> The header gauge measures
/// <c>EstimateTokens(history)</c> — correct, that is what gets sent — while clicking it opened a
/// panel that added the prompt on top. The figures disagreed by the size of the system prompt, which
/// on a real workspace is the largest static part of the context.
/// </para>
/// <para>
/// ⚠ It stayed invisible longer than it should have: the fill was capped at 100 %, so the
/// over-count mostly landed on a number that already read "full". Removing that cap (previous round)
/// is what made this one measurable.
/// </para>
/// </remarks>
public sealed class XRayDoubleCountTests
{
    private static ChatMessageDto Msg(string role, string content) => new(role, content);

    [Fact]
    public void TheConversationCount_LeavesOutTheLeadingSystemPrompt()
    {
        var history = new List<ChatMessageDto>
        {
            Msg("system",    new string('s', 4_000)),   // ~1000 tokens
            Msg("user",      new string('u', 400)),     // ~100
            Msg("assistant", new string('a', 400)),     // ~100
        };

        Assert.Equal(200, AgentOrchestrator.EstimateConversationTokens(history));
        // WITNESS: the whole-prompt estimate is unchanged — the gauge still measures what is sent.
        Assert.Equal(1_200, AgentOrchestrator.EstimateTokens(history));
    }

    [Fact]
    public void AFreshConversation_HasNoHistoryAtAll()
    {
        // The shape both front-ends start from.
        Assert.Equal(0, AgentOrchestrator.EstimateConversationTokens(
            [Msg("system", new string('s', 4_000))]));
    }

    [Fact]
    public void AHistoryWithoutASystemMessage_IsCountedWhole()
    {
        // REFERENCE ARM: only a LEADING system message is the prompt. A history that starts with a
        // user turn (a restored session, a sub-run built without one) must not lose its first turn.
        var history = new List<ChatMessageDto> { Msg("user", new string('u', 400)), Msg("assistant", new string('a', 400)) };

        Assert.Equal(200, AgentOrchestrator.EstimateConversationTokens(history));
    }

    [Fact]
    public void OnlyTheFirstMessageIsSkipped()
    {
        // REFERENCE ARM: a fix written as "skip every system message" would silently drop a mid-run
        // system turn, which some flows insert.
        var history = new List<ChatMessageDto>
        {
            Msg("system", new string('s', 400)),
            Msg("user",   new string('u', 400)),
            Msg("system", new string('t', 400)),
        };

        Assert.Equal(200, AgentOrchestrator.EstimateConversationTokens(history));
    }

    // ── Both front-ends feed X-Ray the conversation, not the whole prompt ──────

    /// <summary>
    /// The four call sites are a view-model (Remote UI) and a JSON-RPC host; the host half is
    /// exercised end to end in <c>HostServerTests</c>, and this scan keeps the Visual Studio half
    /// from drifting back. Same shape as <c>ArchiveFailureSilenceTests</c>.
    /// </summary>
    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Xray.cs")]
    public void TheVisualStudioPanel_CountsTheConversationOnly(params string[] relativePath)
    {
        var path = Path.Combine([RepoRoot(), .. relativePath]);
        var code = ConventionCoverageTests.CodeOnly(path);

        // WITNESS: this really is the file that builds the panel.
        Assert.Contains("XRayPanelPresenter.Build(", code, StringComparison.Ordinal);

        Assert.DoesNotContain("AgentOrchestrator.EstimateTokens(_history)", code, StringComparison.Ordinal);
        Assert.Contains("EstimateConversationTokens(_history)", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
