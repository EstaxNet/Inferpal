using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class ChecksServiceTests : IDisposable
{
    private readonly string _dir;

    public ChecksServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"checks_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteCheck(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Load_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(ChecksService.Load(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Load_NameFromDescription_WhenPresent()
    {
        WriteCheck("secrets.md", "---\ndescription: No hardcoded secrets\n---\nFlag any committed credential.");
        var checks = ChecksService.Load(_dir);

        var c = Assert.Single(checks);
        Assert.Equal("No hardcoded secrets", c.Name);
        Assert.Equal("Flag any committed credential.", c.Criteria);
    }

    [Fact]
    public void Load_NameFromFileName_WhenNoDescription()
    {
        WriteCheck("error-handling.md", "Every public method validates its arguments.");
        var checks = ChecksService.Load(_dir);

        var c = Assert.Single(checks);
        Assert.Equal("error-handling", c.Name);
        Assert.Equal("Every public method validates its arguments.", c.Criteria);
    }

    [Fact]
    public void Load_MultipleChecks_OrderedByFileName()
    {
        WriteCheck("b.md", "second");
        WriteCheck("a.md", "first");
        var checks = ChecksService.Load(_dir);

        Assert.Equal(2, checks.Count);
        Assert.Equal("a", checks[0].Name);
        Assert.Equal("b", checks[1].Name);
    }

    [Fact]
    public void Load_SkipsEmptyBody()
    {
        WriteCheck("empty.md", "---\ndescription: Empty\n---\n");
        Assert.Empty(ChecksService.Load(_dir));
    }

    [Fact]
    public void BuildReviewPrompt_SectionsPerCheckThenFencedDiff()
    {
        var prompt = ChecksService.BuildReviewPrompt(
            [new ReviewCheck("no-secrets", "Flag credentials."),
             new ReviewCheck("naming", "PascalCase publics.")],
            "+var apiKey = \"x\";");

        Assert.StartsWith("## Checks", prompt);
        Assert.Contains("### no-secrets\nFlag credentials.", prompt);
        Assert.Contains("### naming\nPascalCase publics.", prompt);
        Assert.Contains("## Diff\n\n```diff\n+var apiKey = \"x\";\n```", prompt);
    }
}
