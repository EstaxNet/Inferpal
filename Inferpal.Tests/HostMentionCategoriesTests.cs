using Inferpal.Host;
using Xunit;

namespace Inferpal.Tests;

/// <summary>The host publishes to VS Code's popup every category but Visual Studio's own: <c>@terminal</c>, which VS Code
/// serves itself, is there; <c>@output</c>, which reads Visual Studio's Output window, is not.</summary>
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
        Assert.DoesNotContain(categories, c => c.Token == "@output");
        Assert.Equal(MentionController.Categories.Count(c => c.Editors != MentionEditors.VisualStudio), categories.Count);
    }
}
