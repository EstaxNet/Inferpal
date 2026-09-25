using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An edit must not touch the bytes it was not asked to change. A file with no BOM that is not UTF-8 — a Windows-1252
/// source with accented comments, the legacy default of Visual Studio on a Western machine — was read as UTF-8, every
/// accent became U+FFFD, and the tools that write the whole file back (apply_diff, apply_edits, rename_symbol,
/// write_file) replaced every accent of the file with "�" — lines the edit never touched, behind an approval prompt
/// that compares two already-decoded texts and therefore shows none of it.
/// </summary>
public sealed class LegacyEncodingEditTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "inferpal-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyEncodingEditTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    // "// café" in Windows-1252: é is the single byte 0xE9, which is not valid UTF-8.
    private static readonly byte[] Cafe = [.. "// caf"u8, 0xE9, (byte)'\r', (byte)'\n'];

    private string LegacyFile()
    {
        var path = Path.Combine(_ws, "Legacy.cs");
        File.WriteAllBytes(path, [.. Cafe, .. "int x = 1;\r\n"u8]);
        return path;
    }

    [Fact]
    public async Task ApplyDiff_OnALegacyFile_LeavesTheUntouchedLinesByteForByte()
    {
        var path = LegacyFile();

        var result = await new ApplyDiffTool(new YesApproval(), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Args(new { path, old_content = "int x = 1;", new_content = "int x = 2;" }), CancellationToken.None);

        var bytes = File.ReadAllBytes(path);
        Assert.Contains("int x = 2;", Encoding.Latin1.GetString(bytes));                       // witness: applied
        Assert.Equal(Cafe, bytes[..Cafe.Length]);                                             // line 1 untouched
    }

    [Fact]
    public async Task WriteFile_OnALegacyFile_KeepsItsEncoding()
    {
        var path = LegacyFile();

        await new WriteFileTool(new YesApproval(), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Args(new { path, content = "// café\r\nint x = 2;\r\n" }), CancellationToken.None);

        Assert.Equal([.. Cafe, .. "int x = 2;\r\n"u8], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReadFile_OnALegacyFile_ShowsTheAccent_SoAnEditCanQuoteIt()
    {
        // What the model reads is what an edit matches against: shown as "�", the accent could never be quoted back.
        var path = LegacyFile();

        var shown = await new ReadFileTool(() => _ws).ExecuteAsync(Args(new { path }), CancellationToken.None);

        Assert.DoesNotContain("�", shown);   // the accent is not a replacement character
        Assert.Contains("// caf", shown);
    }

    // ── What the model is SHOWN of a file must be what an edit matches ────────────

    [Fact]
    public async Task ALineShownBySearch_CanBeQuotedIntoAnEdit()
    {
        var path = LegacyFile();

        var found = await new SearchInFilesTool(() => _ws)
            .ExecuteAsync(Args(new { path = _ws, pattern = "caf" }), CancellationToken.None);
        var line = found.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("Legacy.cs:1: ", StringComparison.Ordinal));
        var quoted = line["Legacy.cs:1: ".Length..];

        var result = await new ApplyDiffTool(new YesApproval(), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Args(new { path, old_content = quoted, new_content = "// coffee" }), CancellationToken.None);

        Assert.DoesNotContain("�", quoted);
        Assert.Contains("// coffee", Encoding.Latin1.GetString(File.ReadAllBytes(path)));   // the quote matched
    }

    [Fact]
    public void AFolderMention_ShowsTheAccent()
    {
        LegacyFile();

        var body = Inferpal.Services.Presentation.MentionController.BuildFolderContext(_ws, CancellationToken.None);

        Assert.Contains("// caf", body);                                                      // witness: it was read
        Assert.DoesNotContain("�", body);
    }

    [Fact]
    public void APinnedFile_ShowsTheAccent()
    {
        var path = LegacyFile();

        var prompt = new Inferpal.Services.Prompting.SystemPromptBuilder(new Inferpal.Config.InferpalConfig { PinnedContextFiles = path })
            .Build("BASE", projectRoot: _ws);

        Assert.Contains("// caf", prompt);                                                    // witness: it was pinned
        Assert.DoesNotContain("�", prompt);
    }

    [Fact]
    public void TheFixPrompt_ShowsTheAccent()
    {
        // "Fix with AI" and /fix-build show the lines around each error: exactly what the model copies into old_content.
        var path = LegacyFile();

        var prompt = Inferpal.Services.CodeActions.FixPromptBuilder.Build($"{path}(2,9): error CS0103: The name 'y' does not exist");

        Assert.Contains("int x = 1;", prompt);                                                // witness: the file was shown
        Assert.Contains("// café", prompt);
        Assert.DoesNotContain("�", prompt);
    }

    [Fact]
    public void TheVisualStudioAdapter_ReadsNoFileTextOutsideTheSharedReader()
    {
        // Not executable from this suite (Remote UI): a source scan, with its witness. A property of the whole adapter, not
        // one method: what it reads from disk (a browsed file, an @file mention, the files of a failed build) becomes
        // something the model sees, then quotes back into an edit. "Attach active file" reads the editor's buffer, which
        // Visual Studio decoded itself, and never goes through File.
        var offenders = new List<string>();
        var shared    = 0;
        foreach (var file in ConventionCoverageTests.ProjectSources("Inferpal"))
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            shared += System.Text.RegularExpressions.Regex.Matches(code, @"TextFileEncoding\.ReadText(Async)?\(").Count;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         code, @"\bFile\.(ReadAllText|ReadAllLines|ReadLines)(Async)?\("))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(shared >= 2, "the adapter's file attachments moved: the scan reads nothing");          // WITNESS
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("string s = \"🙂\";")]          // outside every legacy code page
    [InlineData("string s = \"中文\";")]         // outside every single-byte code page
    public async Task ApplyDiff_WritingACharacterTheFilesEncodingCannotHold_WritesNothing_AndSaysWhy(string line)
    {
        // Encoding it anyway replaces the character with "?" — silently, behind an approval prompt that showed it.
        var path   = LegacyFile();
        var before = File.ReadAllBytes(path);

        var result = await new ApplyDiffTool(new YesApproval(), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Args(new { path, old_content = "int x = 1;", new_content = line }), CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(TextFileEncoding.LegacyEncoding.WebName, result);
    }

    [Fact]
    public async Task ApplyDiff_OnAUtf8File_StaysUtf8()
    {
        // Reference arm: a BOM-less UTF-8 file is still read and written as UTF-8.
        var path = Path.Combine(_ws, "Utf8.cs");
        File.WriteAllBytes(path, [.. "// café\nint x = 1;\n"u8]);

        await new ApplyDiffTool(new YesApproval(), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Args(new { path, old_content = "int x = 1;", new_content = "int x = 2;" }), CancellationToken.None);

        Assert.Equal([.. "// café\nint x = 2;\n"u8], File.ReadAllBytes(path));
    }
}
