using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The machine's prompts folder, beside the settings: where <c>/import</c> writes the commands of a Continue or Roo
/// configuration (configured per machine, not per repository). Its commands exist in every workspace, after the
/// project's — which a team commits, and which wins.
/// </summary>
[Collection(PromptFilesCacheCollection.Name)]
public sealed class MachinePromptsFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", "machine-prompts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        PromptFilesService.InvalidateCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ACommandInTheMachinesFolder_IsOffered_AndTheProjectsOwnOfTheSameNameWins()
    {
        var config = new InferpalConfig { SavePathForTests = Path.Combine(_dir, "appdata", "config.json") };
        Directory.CreateDirectory(config.MachinePromptsFolder);
        File.WriteAllText(Path.Combine(config.MachinePromptsFolder, "summarize.md"), "---\ndescription: from Continue\n---\n\nSummarize {args}");
        File.WriteAllText(Path.Combine(config.MachinePromptsFolder, "shared.md"), "the machine's");
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".inferpal", "prompts"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "prompts", "shared.md"), "the project's");
        PromptFilesService.InvalidateCache();

        var report = SlashTemplates.Report(config, root);

        var summarize = Assert.Single(report.Usable, t => t.Name == "/summarize");
        Assert.Equal("from Continue", summarize.Hint);
        Assert.StartsWith(config.MachinePromptsFolder, summarize.Source);     // /prompts says where it lives
        Assert.Equal("the project's", report.Usable.Single(t => t.Name == "/shared").Text);
        Assert.Contains(report.Entries, e => e.Template.Text == "the machine's" && e.Winner is not null);
    }

    [Fact]
    public void WithoutTheFolder_NothingChanges()
    {
        var config = new InferpalConfig { SavePathForTests = Path.Combine(_dir, "nowhere", "config.json") };

        Assert.Empty(SlashTemplates.Report(config, null).Entries);
    }
}
