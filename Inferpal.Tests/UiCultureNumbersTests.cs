using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A number written for the person follows the interface's language, not the system's: an English interface on a French
/// system would otherwise read "Ran for 20,2s." under a background task's report.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class UiCultureNumbersTests
{
    private static string Under(string system, string ui, Func<string> render)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(system);
        try
        {
            Strings.ApplyLanguage(ui);
            return render();
        }
        finally
        {
            Strings.ApplyLanguage(null);
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ATasksDuration_IsWrittenInTheInterfacesLanguage_NotTheSystems()
    {
        Assert.Equal("20.2s", Under("fr-FR", "en", () => TaskCommandHandler.FormatDuration(TimeSpan.FromSeconds(20.2))));
        Assert.Equal("20,2s", Under("en-US", "fr", () => TaskCommandHandler.FormatDuration(TimeSpan.FromSeconds(20.2))));
    }

    [Fact]
    public void Ui_FormatsEveryNumberInTheInterfaceCulture()
    {
        Assert.Equal("~12,345 of 1.5", Under("fr-FR", "en", () => Strings.Ui($"~{12345:N0} of {1.5:0.#}")));
    }
}
