using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Indexing a git repository added <c>.inferpal/</c> — the whole folder — to its <c>.gitignore</c>.
/// </summary>
/// <remarks>
/// docs/configuration.md calls permissions.json, validators.json and project.json "committable,
/// team-shared"; rules, checks and prompts are the same kind of file. Once one team member's editor had
/// indexed the repository, each of those created afterwards was ignored by git without a word: a deny
/// rule written for everyone reached no one. Only history/ — copies of overwritten files — is local.
/// The expected entries are written as literals: a test that read GitIgnorePatch's constant would stay
/// green if the constant itself went back to the whole folder.
/// </remarks>
public class GitIgnorePatchTests
{
    private static string[] Rules(string content) =>
        [.. content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    [Fact]
    public void ANewGitIgnore_IgnoresTheSnapshotsAndNothingElse() =>
        Assert.Equal(new[] { ".inferpal/history/" }, Rules(GitIgnorePatch.Apply("")!));

    [Fact]
    public void TheBlockEarlierVersionsWrote_IsNarrowedInPlace() =>
        Assert.Equal("bin/\n\n# Inferpal AI assistant\n.inferpal/history/\n",
            GitIgnorePatch.Apply("bin/\n\n# Inferpal AI assistant\n.inferpal/\n"));

    [Fact]
    public void Narrowing_KeepsWindowsLineEndings() =>
        Assert.Equal("bin/\r\n# Inferpal AI assistant\r\n.inferpal/history/\r\nobj/\r\n",
            GitIgnorePatch.Apply("bin/\r\n# Inferpal AI assistant\r\n.inferpal/\r\nobj/\r\n"));

    /// <summary>Reference arm: a covering rule is left alone — including a whole-folder rule the USER
    /// wrote, which is their decision, not ours.</summary>
    [Theory]
    [InlineData("node_modules/\n.inferpal/\n")]
    [InlineData(".inferpal/history\n")]
    [InlineData("/.inferpal/history/\n")]
    public void AnAlreadyCoveredGitIgnore_IsLeftAlone(string existing) =>
        Assert.Null(GitIgnorePatch.Apply(existing));

    [Theory]
    [InlineData("bin/\n")]
    [InlineData("bin/")]
    public void AnExistingGitIgnore_KeepsItsContentAndGainsTheEntry(string existing)
    {
        var patched = GitIgnorePatch.Apply(existing)!;

        Assert.StartsWith(existing, patched);
        Assert.Equal(new[] { "bin/", ".inferpal/history/" }, Rules(patched));
    }

    /// <summary>The wiring: what indexing writes is the narrow entry.</summary>
    [Fact]
    public void IndexingARepository_WritesTheNarrowEntry()
    {
        var ragDb = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragdb-{Guid.NewGuid():N}");
        RagDatabase.BaseDir = () => ragDb;   // never the user's %AppData% index
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            using (var lsp = new LspSemanticProvider())
            using (var svc = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true }, lsp))
                svc.StartIndexing(root);   // the .gitignore is patched synchronously, before the pass

            Assert.Equal(new[] { ".inferpal/history/" },
                Rules(File.ReadAllText(Path.Combine(root, ".gitignore"))));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
