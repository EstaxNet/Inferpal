using System.IO;
using Inferpal.Config;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A multi-root VS Code workspace: the tools serve the first folder, and the others are neither silently missing for
/// the model nor mistaken for the first folder's files by the <c>@</c> picker.
/// </summary>
/// <remarks>
/// The picker listed every folder's files relative to their own folder while the expansion read every name under the
/// first one: another folder's <c>src/index.ts</c>, <c>README.md</c> or <c>package.json</c> attached the first
/// folder's file of that name, or nothing. And the model, told only the root, looked for the other folders' code
/// under it and concluded it did not exist. The resolution itself (<c>vscode/src/mentionPaths.ts</c>) is plain string
/// work, run under Node by the probe of the journal; the suite holds the wiring.
/// </remarks>
public class MultiRootWorkspaceTests
{
    private static readonly string Root  = Path.Combine(Path.GetTempPath(), "frontend");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "backend");

    [Fact]
    public void TheFacts_NameTheFoldersTheToolsCannotReach_WithTheRoot()
    {
        var facts = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code", 0, Root, [Other]).EnvironmentFacts();

        Assert.Contains($"The workspace root is {Root}. Other folders open in the editor are outside it, so the tools "
                      + $"cannot read, search or edit them: {Other}.", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleFolder_OrNoRoot_SaysNothingOfOtherFolders()
    {
        // Reference arms: an ordinary workspace states its root only, and without a root nothing is stated at all.
        var single = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code", 0, Root).EnvironmentFacts();
        Assert.Contains($"The workspace root is {Root}.", single, StringComparison.Ordinal);
        Assert.DoesNotContain("Other folders", single, StringComparison.Ordinal);

        var noRoot = new SystemPromptBuilder(new InferpalConfig(), "Visual Studio Code", 0, null, [Other]).EnvironmentFacts();
        Assert.DoesNotContain("Other folders", noRoot, StringComparison.Ordinal);
        Assert.Contains("Operating system:", noRoot, StringComparison.Ordinal);   // witness: the facts are there
    }

    // ── The extension's wiring (TypeScript, read without its comments) ──────────

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "vscode", "src", file);
        Assert.True(File.Exists(path), $"{file} moved: this test reads nothing.");
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    [Fact]
    public void ThePicker_AndTheExpansion_AgreeOnWhichFolderANameBelongsTo()
    {
        var chat = Source("chatViewProvider.ts");
        Assert.Contains("mentionSuggestions", chat, StringComparison.Ordinal);   // witness

        // The picker writes the folder's name first when there are several (asRelativePath's default)...
        Assert.Contains("asRelativePath(doc.uri)", chat, StringComparison.Ordinal);
        Assert.Contains("asRelativePath(uri)", chat, StringComparison.Ordinal);
        // ...and the expansion reads it back, never joining every name with the first folder.
        Assert.Contains("resolveMention(token, folders)", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("workspaceFolders?.[0]?.uri;", chat, StringComparison.Ordinal);

        // The rule stays runnable outside the editor.
        Assert.DoesNotContain("from 'vscode'", Source("mentionPaths.ts"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every name the extension gives the model for a workspace file — chips, Problems-panel diagnostics, the label of
    /// <c>/explain</c> and <c>/review</c> — has the folder's name first in a multi-root workspace. Relative to its own
    /// folder, <c>src/x.ts(3,4): error</c> reads as the root's file, and the model fixes the wrong one.
    /// </summary>
    [Fact]
    public void NoNameGivenToTheModel_IsRelativeToAFolderItDoesNotName()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var calls = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir!.FullName, "vscode", "src"), "*.ts", SearchOption.AllDirectories))
        {
            var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(file));
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(code, @"asRelativePath\(([^()]*)\)"))
            {
                calls++;
                Assert.False(m.Groups[1].Value.Contains(','),
                    $"{Path.GetFileName(file)}: asRelativePath({m.Groups[1].Value}) — pass no includeWorkspaceFolder flag.");
            }
        }
        Assert.True(calls >= 5, $"only {calls} asRelativePath calls read: the scan is not looking where they live.");
    }

    [Fact]
    public void TheHost_LearnsTheOtherFolders_AtStartAndWhenTheyChange()
    {
        var extension = Source("extension.ts");
        Assert.Contains("otherFolders: otherWorkspaceFolders()", extension, StringComparison.Ordinal);
        Assert.Contains("setWorkspaceFolders(otherWorkspaceFolders())", extension, StringComparison.Ordinal);
        Assert.Contains("otherFolders: this.options.otherFolders", Source("hostClient.ts"), StringComparison.Ordinal);
    }
}
