using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Persistence;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file saved in UTF-16 or UTF-32 — Windows' "Unicode", in which some editors save CJK sources — comes through every
/// reader and writer that decides an encoding for itself: its BOM read, its text intact, and written back in its own
/// encoding, BOM included. "上" (U+4E0A) is <c>0A 4E</c> in UTF-16 LE: a line feed inside a character to anything that
/// splits bytes on 0x0A, which is why it is in every fixture.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class UnicodeEncodedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ob-unicode-files-" + Guid.NewGuid().ToString("N"));

    public UnicodeEncodedFilesTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    internal static Encoding EncodingNamed(string name) => name switch
    {
        "utf-16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        "utf-16BE" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        "utf-32LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        _          => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    internal static byte[] Saved(Encoding encoding, string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    private sealed class AlwaysApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    [Fact]
    public void TheFixtures_HoldALineFeedByteInsideACharacter()
    {
        // Witness: without it, every test below could pass on text that never exercised the hazard.
        Assert.Contains((byte)0x0A, EncodingNamed("utf-16LE").GetBytes("上"));
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    public async Task RenameSymbol_RenamesAChineseIdentifier_AndKeepsTheFilesEncoding(string name)
    {
        var encoding = EncodingNamed(name);
        var path = Path.Combine(_root, "Invoice.cs");
        var src  = "namespace App;\r\n// 上下文：计算总价\r\npublic class Invoice\r\n{\r\n    public decimal 计算() => 0m;\r\n" +
                   "    public decimal Twice() => 计算() * 2;\r\n}\r\n";
        File.WriteAllBytes(path, Saved(encoding, src));

        var tool = new RenameSymbolTool(new AlwaysApprove(), new FileHistoryService(), () => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { old_name = "计算", new_name = "合计", dry_run = false }));
        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.DoesNotContain("out of date", report);
        Assert.Equal(Saved(encoding, src.Replace("计算()", "合计()")), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    public async Task DocContext_QuotesAnInterfaceSummary_AsWritten(string name)
    {
        File.WriteAllBytes(Path.Combine(_root, "IInvoice.cs"), Saved(EncodingNamed(name),
            "namespace App;\r\npublic interface IInvoice\r\n{\r\n    /// <summary>计算上一期应付的增值税。</summary>\r\n    decimal Tax();\r\n}\r\n"));
        var source  = Path.Combine(_root, "Invoice.cs");
        var content = "namespace App;\npublic class Invoice : IInvoice\n{\n    public decimal Tax() => 0m;\n}\n";

        var block = await Services.Prompting.DocContextExtractor.BuildContextBlockAsync(source, content, CancellationToken.None);

        Assert.Contains("计算上一期应付的增值税。", block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    public async Task Note_AppendedToAUnicodeNotesFile_KeepsItsEncoding(string name)
    {
        var encoding = EncodingNamed(name);
        var path = NotesStore.NotesPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Saved(encoding, "- [2026-09-01 10:00] 上周的决定\n"));

        await NotesCommandHandler.HandleNoteAsync(_root, ["/note", "新的", "笔记"], new DateTime(2026, 9, 26, 12, 0, 0), CancellationToken.None);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(encoding.GetPreamble(), bytes[..encoding.GetPreamble().Length]);   // still that encoding, one BOM
        var text = encoding.GetString(bytes, encoding.GetPreamble().Length, bytes.Length - encoding.GetPreamble().Length);
        Assert.Contains("上周的决定", text);
        Assert.Contains("新的 笔记", text);
    }
}
