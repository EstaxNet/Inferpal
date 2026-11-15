using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Documents protected by LibreOffice 26.2 (pari 89), password <c>s3cr3t-lo</c>: OpenDocument in both of its encryptions —
/// the whole package in AES-256-GCM with an Argon2id key (24.2 and later), each part in AES-256-CBC with a PBKDF2 key (before)
/// —, and the Office formats as LibreOffice protects them: the standard OOXML encryption, Word 97's and Excel 97's.
/// </summary>
public sealed class ProtectedByLibreOfficeTests
{
    private const string Password = "s3cr3t-lo";

    [Theory]
    [InlineData("cahier-protege.odt", "remise supérieure au prix")]        // AES-256-GCM, Argon2id
    [InlineData("cahier-protege-odf4.odt", "remise supérieure au prix")]   // AES-256-CBC, PBKDF2
    [InlineData("budget-protege-lo.xlsx", "Hébergement")]                  // OOXML standard encryption
    [InlineData("revue-protege-lo.pptx", "Architecture du module")]       // OOXML standard encryption
    [InlineData("cahier-protege.doc", "remise supérieure au prix")]       // Word 97, RC4 CryptoAPI
    [InlineData("budget-protege.xls", "Hébergement")]                      // Excel 97
    public void AFileProtectedByLibreOffice_OpensWithItsPassword_Only(string name, string inside)
    {
        var bytes = File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name));
        var extension = Path.GetExtension(name);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, extension).Problem);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, extension, password: "s3cr3t-LO").Problem);
        var read = DocumentText.Extract(bytes, extension, password: Password);
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains(inside, read.Text);
    }
}
