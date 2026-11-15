using System.IO;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// E-mails read as text (pari 89, lot C): an Outlook <c>.msg</c> by OpenMcdf, a MIME <c>.eml</c> by MimeKitLite — the
/// headers a person reads, the body, the attachments named and never read. The three <c>.msg</c> fixtures are the MSGReader
/// project's samples (Fixtures/documents/THIRD-PARTY.md).
/// </summary>
public sealed class EmailTextTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name));

    [Fact]
    public void AnOutlookMessage_GivesItsHeadersBodyAndAttachmentNames()
    {
        var read = DocumentText.Extract(Fixture("EmailWithAttachments.msg"), ".msg");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.StartsWith("Subject: This is the subject\nFrom: Kees <peterpan@neverland.com>\nTo: crocodile@neverland.com\nCc: tinkerbel@neverland.com\nBcc: wendy@neverland.com\nDate: 2016-12-03 18:08 +00:00", read.Text);
        Assert.Contains("Hello Neverland text", read.Text);
        Assert.EndsWith("Attachments (not included): peterpan.jpg, tinkerbell.jpg", read.Text);
    }

    [Fact]
    public void AnOutlookMessage_InCyrillic_IsReadInItsOwnCharacters()
    {
        var read = DocumentText.Extract(Fixture("RtfWithShortRussianString.msg"), ".msg");
        Assert.Contains("«Имя пользователя» (email) и  «Пароль» (password)", read.Text);
    }

    [Fact]
    public void ALongOutlookMessage_KeepsItsSenderAsNameAndAddress()
    {
        var read = DocumentText.Extract(Fixture("TxtSampleEmail.msg"), ".msg");
        Assert.Contains("From: Wilson, Chris <Chris.Wilson@leeds.gov.uk>", read.Text);
        Assert.Contains("so Genoa and Lucca are now just family estates", read.Text);
        Assert.DoesNotContain("\n\n\n", read.Text);
    }

    /// <summary>A MIME message with only an HTML body, an encoded subject, a Latin-1 quoted-printable part, an attachment.</summary>
    [Fact]
    public void AMimeMessage_DecodesItsSubjectAndBody_AndNamesItsAttachment()
    {
        var eml = string.Join("\r\n",
            "From: =?iso-8859-1?Q?S=E9verine_Durand?= <severine@example.org>",
            "To: equipe@example.org",
            "Subject: =?utf-8?B?UsOpdW5pb24gZGUgbHVuZGk=?=",
            "Date: Mon, 16 Nov 2026 09:30:00 +0100",
            "MIME-Version: 1.0",
            "Content-Type: multipart/mixed; boundary=\"b1\"",
            "",
            "--b1",
            "Content-Type: text/html; charset=iso-8859-1",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "<html><body><p>La remise sup=E9rieure au prix est <b>refus=E9e</b>.</p></body></html>",
            "--b1",
            "Content-Type: application/pdf; name=\"contrat.pdf\"",
            "Content-Disposition: attachment; filename=\"contrat.pdf\"",
            "Content-Transfer-Encoding: base64",
            "",
            "JVBERi0xLjQK",
            "--b1--",
            "");
        var read = DocumentText.Extract(Encoding.ASCII.GetBytes(eml), ".eml");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.StartsWith("Subject: Réunion de lundi\nFrom: \"Séverine Durand\" <severine@example.org>\nTo: equipe@example.org\nDate: 2026-11-16 09:30 +01:00", read.Text);
        Assert.Contains("La remise supérieure au prix est refusée.", read.Text);
        Assert.EndsWith("Attachments (not included): contrat.pdf", read.Text);
    }

    /// <summary>Outlook's compressed RTF, on the example of its specification (MS-OXRTFCP 3.1.1).</summary>
    [Fact]
    public void OutlooksCompressedRtf_Decompresses_AsItsSpecificationSays()
    {
        byte[] compressed =
        [
            0x2d, 0x00, 0x00, 0x00, 0x2b, 0x00, 0x00, 0x00, 0x4c, 0x5a, 0x46, 0x75, 0xf1, 0xc5, 0xc7, 0xa7, 0x03, 0x00, 0x0a,
            0x00, 0x72, 0x63, 0x70, 0x67, 0x31, 0x32, 0x35, 0x42, 0x32, 0x0a, 0xf3, 0x20, 0x68, 0x65, 0x6c, 0x09, 0x00,
            0x20, 0x62, 0x77, 0x05, 0xb0, 0x6c, 0x64, 0x7d, 0x0a, 0x80, 0x0f, 0xa0,
        ];
        Assert.Equal("{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n", Encoding.ASCII.GetString(CompressedRtf.Decompress(compressed)!));
    }

    [Fact]
    public void ADamagedMessage_IsSaidUnreadable_NeverThrown()
    {
        var bytes = Fixture("EmailWithAttachments.msg");
        Assert.Equal(DocumentProblem.Unreadable, DocumentText.Extract(bytes[..4096], ".msg").Problem);
    }
}
