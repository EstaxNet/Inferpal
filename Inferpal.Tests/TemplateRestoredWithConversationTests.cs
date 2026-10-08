using System.IO;
using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A conversation held in a <c>/template</c> mode is restored in that mode — its greeting ("Code Review mode active") is
/// among its messages, so a restore without the mode shows a mode no answer follows.
/// </summary>
/// <remarks>
/// ⚠ Every load set the mode back to none. VS Code's host reloads the auto-save slot at each restart (a provider changed,
/// a crash): the greeting stayed on screen while every answer came without the mode's instructions — and a named
/// session, reloaded in either editor, did the same. The mode now travels in the session file (<c>template_suffix</c>).
/// </remarks>
public partial class HostServerTests
{
    private static readonly SavedMessageDto[] AnExchange =
        [new SavedMessageDto("user", "review this"), new SavedMessageDto("assistant", "looks fine")];

    [Fact]
    public async Task AConversationHeldInATemplate_IsReloadedInIt()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var name = $"tpl-{Guid.NewGuid():N}";
        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync(
            "session/save", new SessionSaveParams(name, [.. AnExchange])).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.DoesNotContain("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);

        await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
            "session/load", new SessionRefParams(name)).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
        h.Server.CurrentSession!.Store.Delete(name);
    }

    [Fact]
    public async Task TheArchiveOfAConversationJustLeft_KeepsItsMode()
    {
        // The adapter archives AFTER the reset: by then the session's mode is the new conversation's.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var name = $"tpl-archive-{Guid.NewGuid():N}";
        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeWithParameterObjectAsync(
            "session/save", new SessionSaveParams(name, [.. AnExchange], Archive: true)).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var saved = await h.Server.CurrentSession!.Store.LoadAsync(name, CancellationToken.None);
        Assert.Contains("Code Review", saved!.TemplateSuffix ?? string.Empty, StringComparison.Ordinal);
        h.Server.CurrentSession!.Store.Delete(name);
    }

    [Fact]
    public async Task ABranch_KeepsTheModeOfTheConversationItCuts()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        SavedMessageDto[] messages = [.. AnExchange, new("user", "and this"), new("assistant", "also fine")];

        var branch = await h.Client.InvokeWithParameterObjectAsync<SessionBranchResult>(
            "session/branch", new { turn = 1, messages }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        try
        {
            Assert.Contains("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
            var saved = await h.Server.CurrentSession!.Store.LoadAsync(branch.Name, CancellationToken.None);
            Assert.NotNull(saved!.TemplateSuffix);
        }
        finally
        {
            h.Server.CurrentSession!.Store.Delete(branch.Name);
            h.Server.CurrentSession!.Store.Delete(branch.Parent);
        }
    }

    /// <summary>Reference arm: a conversation held in no mode is reloaded in none.</summary>
    [Fact]
    public async Task AConversationWithoutATemplate_IsReloadedWithout()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var name = $"plain-{Guid.NewGuid():N}";
        await h.Client.InvokeWithParameterObjectAsync(
            "session/save", new SessionSaveParams(name, [.. AnExchange])).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
            "session/load", new SessionRefParams(name)).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.DoesNotContain("## Mode:", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
        h.Server.CurrentSession!.Store.Delete(name);
    }

    [Fact]
    public void TheVisualStudioWindow_SavesAndRestoresTheMode()
    {
        var dir  = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow");
        string Code(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(dir, file));
        var connection = Code("InferpalToolWindowData.Connection.cs");
        var pending    = Code("InferpalToolWindowData.PendingPrompt.cs");
        var history    = Code("InferpalToolWindowData.PromptHistory.cs");

        // Restored with its mode, at every door that restores one.
        Assert.Contains("_activeTemplateSuffix     = templateSuffix ?? string.Empty;", connection);
        Assert.Contains("session.TemplateSuffix);", connection);                       // a session loaded
        Assert.Contains("RestoreConversation(session.Messages, target, session.TemplateSuffix);", history);   // a branch switched to
        // Saved with its mode: the auto-save slot, the archive of /clear (read with the snapshot), a branch.
        Assert.Contains("templateSuffix: templateSuffix);", pending);
        Assert.Contains("_ = SaveNamedSessionAsync(firstUserContent, snapshot, templateSuffix);", connection);
        Assert.Contains("forkTurn: plan.ForkTurn, templateSuffix: templateSuffix);", history);
    }
}
