using System.IO;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's Source Control: "Propose a Commit Message" puts <c>/commit</c>'s proposal for that repository in its commit
/// message box — nothing committed, a message already typed replaced only with the person's agreement.
/// </summary>
/// <remarks>The menus and the gesture are held at the source: no test drives an editor. The Core's half is tested where it
/// runs (<c>CommitCommandHandlerTests.Propose_ForTheCommitBox_*</c>, <c>HostServerTests.GitProposeCommitMessage_*</c>).
/// Visual Studio's Git window offers no way for an extension to write its message: the docs say so.</remarks>
public sealed class SourceControlFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();

    [Fact]
    public void TheSourceControlMenus_OfferTheGesture_ForGitRepositories()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var menus = package.RootElement.GetProperty("contributes").GetProperty("menus");

        foreach (var menu in new[] { "scm/title", "scm/sourceControl" })
        {
            var entry = Assert.Single(menus.GetProperty(menu).EnumerateArray(),
                                      m => m.GetProperty("command").GetString() == "inferpal.proposeCommitMessage");
            Assert.Equal("scmProvider == git", entry.GetProperty("when").GetString());
        }
        Assert.Equal("gitOpenRepositoryCount != 0", menus.GetProperty("commandPalette").EnumerateArray()
            .Single(m => m.GetProperty("command").GetString() == "inferpal.proposeCommitMessage").GetProperty("when").GetString());
    }

    [Fact]
    public void TheGesture_FillsTheBoxOfThatRepository_AndAsksBeforeReplacingWhatWasTyped()
    {
        var provider = Path.Combine(Root, "vscode", "src", "chatViewProvider.ts");
        // Anchored past the parameter's own braces: the body is the first brace after the anchor.
        var gesture  = ConversationPersistenceSilenceTests.TsMethodBody(provider, "rootUri?: vscode.Uri }): Promise<void>");
        Assert.Contains("const api = await gitApi()", gesture, StringComparison.Ordinal);   // witness: the right method

        Assert.Contains("repositoryFor(api, sourceControl?.rootUri)", gesture, StringComparison.Ordinal);
        // Cancellable, a reply crossing the cancellation never written (whileShown, held in TestViewFrontEndTests).
        Assert.Contains("ChatViewProvider.whileShown(t('Writing a commit message…'),", gesture, StringComparison.Ordinal);
        Assert.Contains("(token) => host.proposeCommitMessage(repo.rootUri.fsPath, token)", gesture, StringComparison.Ordinal);
        // Emptied while the proposal was written — committed meanwhile, most likely: nothing written, said.
        var emptied = gesture.IndexOf("if (!typed && before.trim()) {", StringComparison.Ordinal);
        Assert.True(gesture.IndexOf("const before = repo.inputBox.value;", StringComparison.Ordinal) is >= 0 and var b
                    && b < gesture.IndexOf("proposal = await", StringComparison.Ordinal) && emptied > 0
                    && emptied < gesture.IndexOf("repo.inputBox.value = proposal.message;", StringComparison.Ordinal),
                    "the box is read at the click, and an emptied box stops the write");
        // What was typed is read AFTER the proposal, and replaced only on "Replace".
        var proposed = gesture.IndexOf("proposal = await", StringComparison.Ordinal);
        var typed    = gesture.IndexOf("const typed = repo.inputBox.value.trim();", StringComparison.Ordinal);
        var asked    = gesture.IndexOf("if (answer !== replace)", StringComparison.Ordinal);
        var written  = gesture.IndexOf("repo.inputBox.value = proposal.message;", StringComparison.Ordinal);
        Assert.True(proposed >= 0 && proposed < typed && typed < asked && asked < written,
                    $"proposal {proposed}, typed read {typed}, asked {asked}, written {written}");
        Assert.Contains("    if (typed && typed !== proposal.message.trim()) {", gesture, StringComparison.Ordinal);
        // Modal, the proposal shown: nothing typed while the question is open, the choice made knowing the replacement.
        Assert.Contains("{ modal: true, detail: proposal.message }, replace)", gesture, StringComparison.Ordinal);
        // A refusal keeps what was noted on the way.
        Assert.Contains("[proposal.refusal, proposal.notice].filter(Boolean).join('\\n\\n')", gesture, StringComparison.Ordinal);
        // A repository the Git extension no longer has is named, never "open a folder under Git".
        Assert.Contains("if (repo === 'unknown')", gesture, StringComparison.Ordinal);
        Assert.Contains("return api.getRepository(rootUri) ?? 'unknown';",
                        File.ReadAllText(Path.Combine(Root, "vscode", "src", "gitGestures.ts")), StringComparison.Ordinal);
        // Several repositories and no way to choose: said, never guessed.
        Assert.Contains("if (repo === 'several')", gesture, StringComparison.Ordinal);
        Assert.Contains("return api.repositories.length > 1 ? 'several' : api.repositories[0];",
                        File.ReadAllText(Path.Combine(Root, "vscode", "src", "gitGestures.ts")), StringComparison.Ordinal);
    }
}
