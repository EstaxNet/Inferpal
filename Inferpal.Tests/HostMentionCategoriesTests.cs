using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

/// <summary>The host publishes every category to VS Code's popup, <c>@terminal</c> included — the one Visual Studio does
/// not offer yet, which VS Code serves itself.</summary>
public partial class HostServerTests
{
    [Fact]
    public async Task TheHostPublishesTheTerminalMention_ToVsCode()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var categories = await h.Client.InvokeWithParameterObjectAsync<List<MentionCategoryDto>>("mention/categories", new { })
                                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var terminal = Assert.Single(categories, c => c.Token == "@terminal");
        Assert.False(terminal.QueryBased);
        Assert.Equal(MentionController.Categories.Length, categories.Count);
    }
}
