using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file with no BOM that is not UTF-8 is kept in its legacy code page — and a code page holds only its own
/// alphabet. Polish is outside Windows-1252, Chinese outside every single-byte code page, an emoji outside all of
/// them: encoding such text anyway writes "?" or a look-alike ("zażółć" → "zazólc") behind an approval prompt that
/// showed the real text. And WHICH code page is the system's, not the regional format's.
/// </summary>
public sealed class LegacyEncodingLanguagesTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "inferpal-langs-" + Guid.NewGuid().ToString("N"));

    public LegacyEncodingLanguagesTests()
    {
        Directory.CreateDirectory(_ws);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData(1252, "Zażółć gęślą jaźń", "ż")]       // Polish, Western code page
    [InlineData(1250, "Zażółć gęślą jaźń", null)]      // Polish, its own code page
    [InlineData(1252, "Räksmörgås ÅÄÖ", null)]         // Swedish, Western code page
    [InlineData(1252, "中文注释", "中")]                 // Chinese, single-byte code page
    [InlineData(936, "中文注释：你好", null)]            // Chinese, GBK
    [InlineData(936, "gęś", "ę")]                      // Polish in GBK
    [InlineData(936, "ok 🙂", "🙂")]                   // an emoji, outside every legacy code page (a surrogate pair)
    [InlineData(65001, "Zażółć 中文 🙂", null)]         // UTF-8 holds everything
    public void ACodePage_HoldsItsOwnAlphabet_AndNamesTheFirstCharacterItCannot(int codePage, string text, string? expected)
    {
        var bad = TextFileEncoding.FirstUnrepresentable(Encoding.GetEncoding(codePage), text);

        Assert.Equal(expected, bad?.Character);
    }

    [Fact]
    public void TheUnrepresentableCharacter_IsLocatedByLine()
    {
        var bad = TextFileEncoding.FirstUnrepresentable(Encoding.GetEncoding(1252), "int a;\nint b;\nstring s = \"ż\";\n");

        Assert.Equal(3, bad?.Line);
    }

    [Theory]
    [InlineData(936, 1252, 936)]        // a Chinese system with English formats saved GBK: the system wins
    [InlineData(1250, 1252, 1250)]      // a Polish system with French formats
    [InlineData(65001, 936, 936)]       // Windows' "worldwide language support" (UTF-8): the culture is the guess left
    [InlineData(0, 1250, 1250)]         // Linux, macOS: no system code page
    [InlineData(0, 0, 0)]               // no guess at all: Latin-1, which keeps every byte
    public void TheLegacyCodePage_IsTheSystemsBeforeTheRegionalFormats(int system, int culture, int expected)
    {
        Assert.Equal(expected, TextFileEncoding.LegacyCodePage(system, culture));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [Fact]
    public void OnWindows_CodePageZero_IsTheSystemAnsiCodePage()
    {
        // The mechanism the resolution relies on: with the provider registered, code page 0 is GetACP, not UTF-8.
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal((int)GetACP(), Encoding.GetEncoding(0).CodePage);
    }

    // "// café" in the machine's legacy code page (é is 0xE9 in every Western and Central European one).
    private string LegacyFile(string name = "Legacy.cs")
    {
        var path = Path.Combine(_ws, name);
        File.WriteAllBytes(path, [.. "// caf"u8, 0xE9, (byte)'\r', (byte)'\n', .. "int x = 1;\r\n"u8]);
        return path;
    }

    private sealed class YesApproval : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task WriteFile_OnALegacyFile_RefusesAnEmoji_BeforeAsking()
    {
        var path     = LegacyFile();
        var before   = File.ReadAllBytes(path);
        var approval = new YesApproval();
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "// 🙂\r\nint x = 2;\r\n" }));

        var result = await new WriteFileTool(approval, new FileHistoryService(), () => _ws)
            .ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0, approval.Asked);                                                      // refused BEFORE the prompt
        Assert.Contains("U+1F642", result);
    }

    [Fact]
    public async Task TheWriter_RefusesToo_AndAMultiFileWritePutsTheOthersBack()
    {
        // The net under the tools: a path that did not ask first still cannot write "?" in place of a character.
        var utf8   = Path.Combine(_ws, "Utf8.cs");
        File.WriteAllText(utf8, "int y = 1;\n", new UTF8Encoding(false));
        var legacy = LegacyFile();
        var legacyBefore = File.ReadAllBytes(legacy);

        var outcome = await SafeFileWriter.WriteAllOrRollBackAsync([
            (utf8, "int y = 2;\n", "int y = 1;\n"),
            (legacy, "// 🙂\r\nint x = 1;\r\n", File.ReadAllText(legacy, TextFileEncoding.LegacyEncoding)),
        ]);

        Assert.Equal(legacy, outcome.FailedPath);
        Assert.Contains("U+1F642", outcome.Error);
        Assert.Equal("int y = 1;\n", File.ReadAllText(utf8));                                 // put back
        Assert.Equal(legacyBefore, File.ReadAllBytes(legacy));
    }
}
