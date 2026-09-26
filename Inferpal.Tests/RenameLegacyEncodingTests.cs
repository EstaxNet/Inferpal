using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The C# index computes <c>rename_symbol</c>'s spans on the text IT read, and the rename applies them to the text
/// <see cref="TextFileEncoding"/> read. Read raw as UTF-8, a Windows-1252 file mostly keeps one character per byte —
/// until a run of bytes happens to be valid UTF-8: French typography's <c>é</c> + no-break space + <c>»</c> is
/// <c>E9 A0 BB</c>, one UTF-8 character for three. Every offset after it is off, the span guard reads that as a
/// file that changed, and the whole rename was refused with "Retry in a few seconds" — for good, since nothing
/// ever changes.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class RenameLegacyEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ob-rename-1252-" + Guid.NewGuid().ToString("N"));
    private static readonly Encoding W1252;

    static RenameLegacyEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        W1252 = Encoding.GetEncoding(1252);
    }

    public RenameLegacyEncodingTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class AlwaysApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    private async Task<string> RenameAsync(string oldName, string newName)
    {
        var tool = new RenameSymbolTool(new AlwaysApprove(), new FileHistoryService(), () => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { old_name = oldName, new_name = newName, dry_run = false }));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Theory]
    [InlineData("// Voir « le résumé\u00A0» ci-dessous.")]   // é + NBSP + » = E9 A0 BB: valid UTF-8, one char for three
    [InlineData("// Café, thé, crème — accents only.")]        // one byte, one replacement char: the arm that always worked
    public async Task AWindows1252File_IsRenamedInPlace_WhateverItsCommentsHold(string comment)
    {
        var path = Path.Combine(_root, "Invoice.cs");
        var src  = $"namespace App;\r\n{comment}\r\npublic class Invoice\r\n{{\r\n    public decimal Total() => 0m;\r\n    public decimal Twice() => Total() * 2;\r\n}}\r\n";
        File.WriteAllBytes(path, W1252.GetBytes(src));

        var report = await RenameAsync("Total", "Amount");

        Assert.DoesNotContain("out of date", report);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(W1252.GetBytes(src.Replace("Total()", "Amount()")), bytes);   // renamed, and still 1252
    }

    /// <summary>
    /// <c>/doc</c> quotes the summaries of the interface a class implements, and the model writes its doc comment from
    /// them — into the file. Read raw, a French summary in a legacy-encoded interface reached it as "�".
    /// </summary>
    [Fact]
    public async Task DocContext_QuotesALegacyEncodedInterfaceSummary_AsWritten()
    {
        File.WriteAllBytes(Path.Combine(_root, "IInvoice.cs"), W1252.GetBytes(
            "namespace App;\r\npublic interface IInvoice\r\n{\r\n    /// <summary>Calcule la TVA à payer.</summary>\r\n    decimal Tax();\r\n}\r\n"));
        var source  = Path.Combine(_root, "Invoice.cs");
        var content = "namespace App;\npublic class Invoice : IInvoice\n{\n    public decimal Tax() => 0m;\n}\n";

        var block = await Services.Prompting.DocContextExtractor.BuildContextBlockAsync(source, content, CancellationToken.None);

        Assert.Contains("Calcule la TVA à payer.", block, StringComparison.Ordinal);
    }
}
