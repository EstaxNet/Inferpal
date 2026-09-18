using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio settings window rewrites custom tools and command templates from its rows on every
/// add, delete, toggle and save: a malformed line — which the product already ignores and reports in
/// /diagnostics — was deleted before the user could correct it.
/// </summary>
public class EditableListTextTests
{
    [Fact]
    public void ALineThatIsNotAnEntry_SurvivesTheRewrite()
    {
        const string text = "build=dotnet build\nmytool: npx thing\n#off=echo off\n/deploy sans signe egal";

        var entries   = EditableListText.Parse(text, out var unparsed);
        var rewritten = EditableListText.Render(entries, unparsed);

        Assert.Equal(["build", "off"], entries.Select(e => e.Name));
        Assert.Contains("mytool: npx thing", rewritten);
        Assert.Contains("/deploy sans signe egal", rewritten);
    }

    [Fact]
    public void AListOfEntries_RoundTripsUnchanged()
    {
        // Witness: without a malformed line, the rewrite returns exactly the text that was read.
        const string text = "build=dotnet build\n#off=echo off";

        var entries = EditableListText.Parse(text, out var unparsed);

        Assert.Empty(unparsed);
        Assert.Equal(text, EditableListText.Render(entries, unparsed));
    }

    [Theory]
    [InlineData("name=")]
    [InlineData("=value")]
    [InlineData("no equals sign")]
    [InlineData("#")]
    public void AnIncompleteLine_IsKeptAsIs_NotTurnedIntoAnEntry(string line)
    {
        Assert.Empty(EditableListText.Parse(line, out var unparsed));
        Assert.Equal([line], unparsed);
    }
}
