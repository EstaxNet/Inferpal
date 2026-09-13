using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// /index is served to both front-ends and read in all ten languages. Expectations are literals:
// reading the resource would turn the test green on a report left hard-coded.
[Collection(CultureSerialCollection.Name)]
public class IndexCommandLocalizationTests
{
    private static InferpalConfig Config(bool enabled) => new() { RagEnabled = enabled, RagTopK = 7 };

    private static string RunInFrench(InferpalConfig config, string[] parts, string? root)
    {
        try
        {
            Strings.ApplyLanguage("fr");
            var index = new ProjectIndexService(
                new FakeInferenceProvider(), Config(true), new Inferpal.Services.Lsp.LspSemanticProvider());
            return IndexCommandHandler.Handle(index, config, parts, root);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void DisabledReport_FollowsTheLanguage()
    {
        var message = RunInFrench(Config(enabled: false), ["/index"], @"C:\proj");

        Assert.Contains("**Index RAG**", message);
        Assert.Contains("Statut : **désactivé** (`ragEnabled = false` dans les réglages)", message);
        Assert.DoesNotContain("disabled", message);
        Assert.DoesNotContain("Enable it", message);
    }

    [Fact]
    public void NotStartedReport_FollowsTheLanguage()
    {
        var message = RunInFrench(Config(enabled: true), ["/index"], @"C:\proj");

        Assert.Contains("Statut : non démarré", message);
        Assert.Contains("Utilisez `/index rebuild` pour construire l'index manuellement.", message);
        Assert.DoesNotContain("not started", message);
    }

    [Fact]
    public void RebuildWithoutARoot_FollowsTheLanguage()
    {
        var message = RunInFrench(Config(enabled: true), ["/index", "rebuild"], root: "");

        Assert.Equal("⚠ Racine de la solution introuvable — ouvrez d'abord un fichier.", message);
    }
}
