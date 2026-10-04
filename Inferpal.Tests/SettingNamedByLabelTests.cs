using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A message that tells the user to change a setting quotes it by the label the settings window shows, in every
/// language. Rewording the labels left two messages quoting names that no longer existed on screen ("raise
/// 'Compaction timeout'", "wired to the 'Enable autonomous agent mode' setting"): the label now travels as an
/// argument, so the message follows it.
/// </summary>
// Switches Strings.OverrideCulture (process-wide): serialized collection.
[Collection(CultureSerialCollection.Name)]
public class SettingNamedByLabelTests
{
    private static readonly string[] Cultures = ["en", "fr", "de", "es", "it", "ja", "ko", "pl", "ru", "zh-CN"];

    [Fact]
    public void MessagesThatNameASetting_QuoteItsCurrentLabel_InEveryLanguage()
    {
        var previous = Strings.OverrideCulture?.Name;
        try
        {
            foreach (var culture in Cultures)
            {
                Strings.ApplyLanguage(culture);
                Assert.True(Strings.LabelCompactionTimeout.Length >= 4, $"{culture}: label not read");   // WITNESS

                Assert.Contains(Strings.LabelCompactionTimeout, Strings.MsgContextCompactionFallback);
                Assert.Contains(Strings.SettingsPageContext, Strings.MsgContextCompactionFallback);
            }
        }
        finally { Strings.ApplyLanguage(previous); }
    }
}
