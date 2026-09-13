using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// /template is served to both front-ends; its refusal of an unknown id is read in all ten languages.
// Literal expectation: reading the resource would turn the test green on a text left hard-coded.
[Collection(CultureSerialCollection.Name)]
public class TemplateCommandLocalizationTests
{
    [Fact]
    public void AnUnknownId_IsRefused_InTheInterfaceLanguage()
    {
        TemplateCommandResult result;
        try
        {
            Strings.ApplyLanguage("fr");
            result = TemplateCommandHandler.Handle(["/template", "nope"]);
        }
        finally { Strings.ApplyLanguage(null); }

        Assert.Null(result.Apply);
        Assert.Equal("Modèle inconnu : `nope`. Tapez `/template` pour voir la liste.", result.Message);
    }

    [Fact]
    public void TheTemplateListAndGreeting_FollowTheInterfaceLanguage()
    {
        string list, greeting;
        try
        {
            Strings.ApplyLanguage("fr");
            list     = Inferpal.Services.Persistence.SessionManager.FormatTemplateList();
            greeting = TemplateCommandHandler.Handle(["/template", "code-review"]).Apply!.Greeting;
        }
        finally { Strings.ApplyLanguage(null); }

        Assert.Contains("## Modèles disponibles", list);
        Assert.Contains("- **code-review** — Revue de code", list);
        Assert.Equal("Mode **Revue de code** activé. Partagez un fichier ou une sélection et j'en analyserai la qualité, la sécurité et la conception.", greeting);
    }
}
