using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/context</c> and <c>/memory</c> tell the truth about when an edit applies.
/// </summary>
/// <remarks>
/// ⚠ Both front-ends rebuild the system prompt right before every question
/// (<c>SystemPromptPerTurnTests</c>), and it was measured on the published product: a
/// <c>context.md</c> or <c>memory.md</c> edited between two questions is read by the next one. The
/// three messages still said "use <c>/clear</c> to reload" — in ten languages — sending the user to
/// wipe the conversation they were in, for nothing. The same test reads every culture, so a
/// translation cannot keep the old advice.
/// </remarks>
public class ReloadAdviceTests
{
    [Theory]
    [InlineData("SlashContextNotFound")]
    [InlineData("SlashContextLoaded")]
    [InlineData("SlashMemoryLoaded")]
    public void TheProjectContextAndMemoryScreens_NeverSendTheUserToClearTheConversation(string key)
    {
        var dir   = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files = Directory.GetFiles(dir, "Strings*.resx");
        Assert.Equal(10, files.Length);                                   // WITNESS: every culture is read

        foreach (var file in files)
        {
            var match = Regex.Match(File.ReadAllText(file),
                "<data name=\"" + key + "\" xml:space=\"preserve\"><value>(.*?)</value>", RegexOptions.Singleline);
            Assert.True(match.Success, $"{key} is not in {Path.GetFileName(file)}");
            Assert.DoesNotContain("/clear", match.Groups[1].Value, StringComparison.Ordinal);
        }

        // Positive half, on the neutral culture: the message says when an edit applies.
        var neutral = Regex.Match(File.ReadAllText(Path.Combine(dir, "Strings.resx")),
            "<data name=\"" + key + "\" xml:space=\"preserve\"><value>(.*?)</value>", RegexOptions.Singleline);
        Assert.Contains("before every question", neutral.Groups[1].Value, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
