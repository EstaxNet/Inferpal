using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A message that tells the user how to get a file back names a command that <b>restores a
/// file</b>, in all ten languages.
/// </summary>
/// <remarks>
/// ⚠ Checking that the command <i>exists</i> is not enough: <c>/history</c> exists, and it searches
/// saved conversations. The message therefore goes through the <b>real router</b>, and must land on
/// <c>restore_file</c>.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public class RecoveryMessageTests
{
    private static readonly string[] Cultures = ["en", "fr", "de", "es", "it", "ru", "ja", "ko", "pl", "zh-CN"];

    private static void InEveryCulture(Action<string> body)
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            foreach (var culture in Cultures)
            {
                Strings.ApplyLanguage(culture);
                body(culture);
            }
        }
        finally { Strings.ApplyLanguage(previous); }
    }

    /// <summary>The command quoted in backticks, with its placeholder replaced by a real path.</summary>
    private static SlashAction RouteQuotedCommand(string message, string culture)
    {
        var quoted = Regex.Match(message, @"`(/[^`]+)`");
        Assert.True(quoted.Success, $"[{culture}] no command quoted in: {message}");
        var typed = Regex.Replace(quoted.Groups[1].Value, "<[^>]*>", @"C:\repo\src\File.cs");
        return SlashCommandRouter.Route(typed, []);
    }

    [Fact]
    public void UndoRunSavedFirst_NamesACommandThatRestoresAFile() =>
        InEveryCulture(culture =>
        {
            var tool = Assert.IsType<SlashToolAction>(RouteQuotedCommand(Strings.UndoRunSavedFirst, culture));
            Assert.Equal("restore_file", tool.Tool);
        });

    [Fact]
    public void OnboardPreviousSaved_NamesACommandThatRestoresTheFile() =>
        InEveryCulture(culture =>
        {
            var message = Strings.FilePreviousVersionSaved(@"C:\repo\.inferpal\context.md");
            var tool    = Assert.IsType<SlashToolAction>(RouteQuotedCommand(message, culture));
            Assert.Equal("restore_file", tool.Tool);
        });

    [Fact]
    public void Witness_TheCheckTellsASearchCommandFromARestoringOne()
    {
        // Without this witness, a router returning restore_file for everything would be green.
        Assert.IsNotType<SlashToolAction>(SlashCommandRouter.Route(@"/history C:\repo\src\File.cs", []));
    }
}
