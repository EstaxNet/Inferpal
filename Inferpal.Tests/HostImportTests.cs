using System.IO;
using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

public partial class HostServerTests
{
    /// <summary>
    /// VS Code: <c>/import</c> reaches the editor as a card — typed, never a text to parse — with its Markdown beside it
    /// for the agent protocol; and the card's Apply, sent with no box ticked, writes nothing and saves nothing.
    /// </summary>
    [Fact]
    public async Task Import_ReachesTheEditorAsACard_AndAnEmptyApplyWritesNothing()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var file = Path.Combine(h.RootDir, "config.json");
        File.WriteAllText(file, """{ "models": [ { "title": "L", "provider": "ollama", "model": "qwen3:8b" } ] }""");

        var preview = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/import continue " + file }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(preview.Handled);
        var card = Assert.IsType<Inferpal.Services.Import.ImportCard>(preview.ImportCard);
        Assert.Equal("continue", card.Source);
        Assert.Equal(file, card.File);
        Assert.Contains(card.Items, i => i.Label == "qwen3:8b");
        Assert.Contains("qwen3:8b", preview.Markdown);     // what the agent protocol, which draws no card, shows

        var none = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = $"/import continue {file} apply none" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Inferpal.Localization.Strings.ImportNothingChosen, none.Markdown);
        Assert.Null(none.ImportCard);
        Assert.True(none.Effects is null or { Count: 0 });   // no "configSaved": nothing was saved
    }
}
