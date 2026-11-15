using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Word 97-2003 documents (<c>.doc</c>, pari 89, lot D), read by b2xtranslator's extractor, and the protected ones decrypted
/// first — Word 97's RC4 and the RC4 CryptoAPI of Word 2002-2003. The fixtures are Apache POI's test files
/// (Fixtures/documents/THIRD-PARTY.md).
/// </summary>
public sealed class WordBinaryTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name));

    [Fact]
    public void AWord97Document_GivesItsText_HeadersAndFootersIncluded()
    {
        var read = DocumentText.Extract(Fixture("HeaderFooterUnicode.doc"), ".doc");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("L'Avare ou l'École du mensonge", read.Text);
        Assert.Contains("EUR - €", read.Text);
        Assert.Contains("The footer, with Molière, has Unicode in it.", read.Text);
    }

    [Fact]
    public void AWord95Document_IsReadToo()
    {
        Assert.Contains("The quick brown fox jumps over the lazy dog", DocumentText.Extract(Fixture("Word95.doc"), ".doc").Text);
    }

    [Theory]
    [InlineData("97-password1.doc", "password1", "I am a word document.")]            // Word 97's RC4
    [InlineData("password_password_cryptoapi.doc", "password", "This is a test")]   // RC4 CryptoAPI
    public void AProtectedWordDocument_OpensWithItsPassword_Only(string name, string password, string inside)
    {
        var bytes = Fixture(name);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, ".doc").Problem);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, ".doc", password: "not-" + password).Problem);
        var read = DocumentText.Extract(bytes, ".doc", password: password);
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains(inside, read.Text);
    }

    [Fact]
    public void ADamagedWordDocument_IsSaidUnreadable_NeverThrown()
    {
        var bytes = Fixture("HeaderFooterUnicode.doc");
        Assert.Equal(DocumentProblem.Unreadable, DocumentText.Extract(bytes[..3000], ".doc").Problem);
    }
}
