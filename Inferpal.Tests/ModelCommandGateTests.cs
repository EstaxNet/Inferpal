using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>/model</c> takes no embedding model, in either editor: every list offered for the chat leaves them out, and the
/// name typed by hand was saved — "Model changed", then every question refused by the server.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public void TheGate_RefusesAnEmbeddingModel_AndLetsAChatModelThrough()
    {
        Assert.Equal(Strings.SlashModelIsEmbedding("nomic-embed-text"), ModelsCommandHandler.RefusedForChat("nomic-embed-text"));
        Assert.Null(ModelsCommandHandler.RefusedForChat("qwen3:8b"));   // reference arm
    }

    [Fact]
    public async Task TheHostsModelCommand_DoesNotSaveAnEmbeddingModel()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var before = h.Server.CurrentSession!.Config.DefaultModel;

        var result = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/model nomic-embed-text" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.SlashModelIsEmbedding("nomic-embed-text"), result.Markdown);
        Assert.Equal(before, h.Server.CurrentSession!.Config.DefaultModel);
    }

    [Fact]
    public void VisualStudiosModelCommand_ReadsTheSameGate()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
                                                               "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var model = vm[vm.IndexOf("case SlashCommandId.Model:", StringComparison.Ordinal)..];
        Assert.True(model.IndexOf("ModelsCommandHandler.RefusedForChatAsync(_client, parts[1], ct)", StringComparison.Ordinal)
                    < model.IndexOf("_config.DefaultModel = parts[1];", StringComparison.Ordinal));
        Assert.Contains("ModelsCommandHandler.RefusedForChatAsync(_client, parts[1], ct)", model[..model.IndexOf("_config.DefaultModel = parts[1];", StringComparison.Ordinal)],
                        StringComparison.Ordinal);
    }
}
