using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/history</c> and <c>/template</c> are served by shared handlers: their headers, relative ages and
/// usage line were written in English in the Core, so both editors showed them in English whatever the
/// interface language.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class SessionListingLocalizationTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HistoryAndTemplateListings_FollowTheInterfaceLanguage()
    {
        Strings.ApplyLanguage("fr");
        try
        {
            // Witness: the switch does act on a key that is already translated.
            Assert.NotEqual("No sessions match \"vulkan\".", Strings.HistoryNoResults("vulkan"));

            var list = SessionManager.FormatHistoryList(
                [new SessionSummary("s1", Now.AddDays(-1), 12, "")], Now);
            Assert.Contains("## Sessions enregistrées (1)", list);
            Assert.Contains("il y a 1 j", list);

            var search = SessionManager.FormatHistorySearch("vulkan",
                [new SessionMatch("s1", Now.AddHours(-2), ["…"])], Now);
            Assert.Contains("## Historique : « vulkan » — 1 session(s)", search);
            Assert.Contains("il y a 2 h", search);

            var templates = SessionManager.FormatTemplateList();
            Assert.Contains("## Modèles disponibles", templates);
            Assert.Contains("Utilisation : `/template <id>`", templates);
        }
        finally { Strings.ApplyLanguage(null); }
    }
}
