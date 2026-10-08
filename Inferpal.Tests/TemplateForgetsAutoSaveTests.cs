using System.IO;
using System.Linq;
using Inferpal.Host;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: <c>/template</c> discards the conversation like <c>/clear</c> — the auto-save slot included — so the old
/// conversation does not come back at the next start.
/// </summary>
/// <remarks>
/// The host reset its history for <c>/template</c> on its own, without leaving the auto-save slot; the adapter saves no
/// conversation made of a greeting alone, so <c>last_session</c> kept the conversation just replaced, and the next start
/// of VS Code brought it back over the template. Visual Studio goes through <c>ClearAsync</c>, which forgets the slot.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public async Task Template_LeavesTheAutoSaveSlot_LikeClear()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync(
            "session/save", new SessionSaveParams("last_session",
                [new SavedMessageDto("user", "the old question"), new SavedMessageDto("assistant", "the old answer")]))
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var store = h.Server.CurrentSession!.Store;
        Assert.NotNull(await store.LoadAsync("last_session", CancellationToken.None));   // witness: the slot was written

        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // Forgotten in the background, as /clear does: polled within the suite's waiting budget.
        Services.Persistence.SessionData? slot = null;
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); DateTime.UtcNow < deadline; await Task.Delay(50))
        {
            slot = await store.LoadAsync("last_session", CancellationToken.None);
            if (slot is null || slot.Messages.Count == 0) break;
        }
        Assert.True(slot is null || slot.Messages.Count == 0, "the replaced conversation is still in the auto-save slot.");
        Assert.Contains("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDiscardedConversation_GoesThroughStartNewConversation()
    {
        // ResetHistory alone starts a history without leaving the slot: outside initialize (nothing to discard yet) it is
        // called by StartNewConversation only — the next door that discards a conversation inherits the slot's rule.
        var repo    = ConversationPersistenceSilenceTests.RepoRoot();
        var callers = new List<string>();
        foreach (var file in new[] { "HostServer.cs", "HostSlashCommands.cs" })
        {
            var path = Path.Combine(repo, "Inferpal.Host", file);
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                                     .Where(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "ResetHistory" }))
            {
                var owner = call.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.Text;
                callers.Add(owner);
            }
        }

        Assert.Contains("StartNewConversation", callers);   // witness: the calls are read
        Assert.Equal(["Initialize", "StartNewConversation"], callers.Distinct().Order().ToList()
                                                                   .Select(c => c.StartsWith("Initialize", StringComparison.Ordinal) ? "Initialize" : c)
                                                                   .Distinct().ToList());
    }
}
