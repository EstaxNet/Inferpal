using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>write_file</c> writes an existing file in its OWN line endings, like <c>apply_diff</c> and the code actions
/// (<c>LineEndings</c>: model output is LF). It kept the encoding and the BOM but wrote the model's LF over a CRLF file
/// — Visual Studio's default — so the approval prompt, which compares lines with their "\r", showed a one-line change
/// as a rewrite of EVERY line (or "too large to show" past 300 lines): the one surface where the human reads what they
/// agree to showed nothing of the real change. And the file changed its line endings behind that prompt.
/// </summary>
public sealed class WriteFileLineEndingsTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "inferpal-eol-" + Guid.NewGuid().ToString("N"));

    public WriteFileLineEndingsTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CapturingApproval : IApprovalService
    {
        public DiffInfo? Diff { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Diff = diff;
            return Task.FromResult(true);
        }
    }

    private static string Lines(string eol, string line5) =>
        string.Join(eol, Enumerable.Range(1, 20).Select(i => i == 5 ? line5 : $"    line {i};")) + eol;

    private async Task<(string Written, DiffInfo? Diff)> WriteAsync(string path, string content)
    {
        var approval = new CapturingApproval();
        var tool     = new WriteFileTool(approval, new FileHistoryService(), () => _ws);
        await tool.ExecuteAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { path, content })).RootElement.Clone(),
                                CancellationToken.None);
        return (Encoding.UTF8.GetString(File.ReadAllBytes(path)), approval.Diff);
    }

    private static int ChangedLines(DiffInfo diff) =>
        DiffComputer.Compute(diff.OldText, diff.NewText).Count(l => l.Prefix is "+" or "-");

    [Fact]
    public async Task ACrlfFile_KeepsItsLineEndings_AndTheApprovalShowsOnlyTheChange()
    {
        var path = Path.Combine(_ws, "Crlf.cs");
        File.WriteAllText(path, Lines("\r\n", "    line 5;"));

        var (written, diff) = await WriteAsync(path, Lines("\n", "    line FIVE;"));   // the model writes LF

        Assert.Contains("line FIVE", written);                                           // witness: it was written
        Assert.NotNull(diff);
        Assert.Equal(2, ChangedLines(diff!));                                           // one line out, one in
        Assert.DoesNotContain("\n", written.Replace("\r\n", string.Empty));              // no bare LF
    }

    [Fact]
    public async Task AnLfFile_StaysLf()
    {
        // Reference arm.
        var path = Path.Combine(_ws, "Lf.cs");
        File.WriteAllText(path, Lines("\n", "    line 5;"));

        var (written, diff) = await WriteAsync(path, Lines("\n", "    line FIVE;"));

        Assert.DoesNotContain("\r", written);
        Assert.Equal(2, ChangedLines(diff!));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task AFileThatEndsOnABreak_KeepsIt_WhenTheModelDropsIt(string eol)
    {
        // A model rewriting a whole file often drops the final break: without this, the diff shows the last line
        // changed, and a hand-written "undo" comes out one byte off the original.
        var path     = Path.Combine(_ws, "Cart.cs");
        var original = Lines(eol, "    line 5;");
        File.WriteAllText(path, original);

        var (written, diff) = await WriteAsync(path, original.TrimEnd());

        Assert.Equal(original, written);
        Assert.Equal(0, ChangedLines(diff!));
    }

    [Fact]
    public async Task AFileWithoutAFinalBreak_DoesNotGetOne()
    {
        var path = Path.Combine(_ws, "NoFinal.cs");
        File.WriteAllText(path, "a\nb");

        var (written, _) = await WriteAsync(path, "a\nB\n");

        Assert.Equal("a\nB", written);
    }

    [Fact]
    public async Task AnEmptyRewrite_AndASingleLineOriginal_AreWrittenAsGiven()
    {
        // Reference arm: nothing to follow.
        var single = Path.Combine(_ws, "Single.txt");
        File.WriteAllText(single, "one line");
        Assert.Equal("two\n", (await WriteAsync(single, "two\n")).Written);

        var emptied = Path.Combine(_ws, "Emptied.cs");
        File.WriteAllText(emptied, "a\nb\n");
        Assert.Equal(string.Empty, (await WriteAsync(emptied, string.Empty)).Written);
    }

    [Fact]
    public async Task ANewFile_IsWrittenAsGiven()
    {
        var path = Path.Combine(_ws, "New.cs");

        var (written, _) = await WriteAsync(path, "a\nb\n");

        Assert.Equal("a\nb\n", written);
    }
}
