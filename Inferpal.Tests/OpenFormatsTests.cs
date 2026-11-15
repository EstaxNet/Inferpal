using System.IO;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Notebooks, OpenDocument and RTF read as text (pari 89, lot B). The fixtures are made by nbformat, odfpy, by hand (an
/// RTF with every kind of thing a reader must skip) and by Windows' own rich edit control (an RTF that writes Chinese in
/// the code page of its font).
/// </summary>
public sealed class OpenFormatsTests
{
    private static string Read(string name, out DocumentProblem problem)
    {
        var bytes = File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name));
        var result = DocumentText.Extract(bytes, Path.GetExtension(name));
        problem = result.Problem;
        return result.Text;
    }

    [Fact]
    public void ANotebook_GivesItsCellsAndOutputs_NeverItsJsonNorItsImages()
    {
        var text = Read("analyse.ipynb", out var problem);
        Assert.Equal(DocumentProblem.None, problem);
        Assert.StartsWith("# Analyse des factures\nOn vérifie le **taux de TVA** par pays.", text);
        Assert.Contains("In [1]:\n```\nimport pandas as pd", text);
        Assert.Contains("Output:\npays\nFrance    0.20", text);
        Assert.Contains("ZeroDivisionError: division by zero", text);
        Assert.Contains("[an image, not included]", text);
        Assert.DoesNotContain("iVBORw0KGgo", text);
        Assert.DoesNotContain("\"cell_type\"", text);
    }

    [Fact]
    public void AnOpenDocumentText_GivesItsParagraphsAndItsTable()
    {
        var text = Read("cahier.odt", out var problem);
        Assert.Equal(DocumentProblem.None, problem);
        Assert.Contains("Cahier des charges\nLe module arrondit au centime le plus proche.\nUne remise supérieure au prix est refusée.", text);
        Assert.Contains("France\tvingt pour cent", text);
        Assert.DoesNotContain("# Taux", text);   // a table inside a text is no sheet
    }

    [Fact]
    public void AnOpenDocumentSpreadsheet_GivesEachSheetUnderItsName()
    {
        var text = Read("budget.ods", out _);
        Assert.Contains("# Budget\nPoste\tMontant\nHébergement\t4200", text);
        Assert.Contains("# Notes\nValidé par la direction", text);
    }

    [Fact]
    public void AnOpenDocumentPresentation_GivesItsSlidesInOrder()
    {
        var text = Read("revue.odp", out _);
        Assert.Equal("## Slide 1\nArchitecture du module\nRevue du 15 novembre\n## Slide 2\nDécisions\nNuméros sans trou par année", text);
    }

    [Fact]
    public void AnRtf_GivesItsText_WithoutItsTablesOfFontsColoursAndProperties()
    {
        var text = Read("cahier.rtf", out var problem);
        Assert.Equal(DocumentProblem.None, problem);
        Assert.Equal("Cahier des charges de l'export\nMontant à vérifier : la remise est refusée\nNom japonais : 東京\nPays\tTaux\nFrance\tvingt\nFin du document", text);
    }

    /// <summary>Windows' rich edit control writes Chinese as bytes in the code page its font declares (GB2312): decoded in
    /// the document's own code page, it is noise.</summary>
    [Fact]
    public void AnRtf_DecodesEachRunInTheCodePageOfItsFont()
    {
        var text = Read("richedit.rtf", out _);
        Assert.Contains("東京商事 : contrat signé", text);
        Assert.Contains("Ölwerke Müller", text);
    }

    [Fact]
    public void AnRtfNestedPastItsLimit_StopsAndIsSaidTooLarge()
    {
        var deep = Encoding.ASCII.GetBytes(@"{\rtf1 " + new string('{', 10_000) + "x" + new string('}', 10_000) + "}");
        Assert.Equal(DocumentProblem.TooLarge, DocumentText.Extract(deep, ".rtf").Problem);
    }
}
