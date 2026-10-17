using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The code-analysis modes quote what they read (a method's signature, its name), and the model copies that text into
/// its next call or edit: a Windows-1252 source read as UTF-8 shows a replacement character where the file says "é". They read through the
/// shared reader, like the file tools.
/// </summary>
public sealed class AnalyzeCodeLegacyEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-analyze-1252-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public AnalyzeCodeLegacyEncodingTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "Facture.cs");
        // Windows-1252: the é of "Numéro" is one byte, 0xE9, not valid UTF-8.
        File.WriteAllBytes(_target, Encoding.Latin1.GetBytes(
            "class Numéro { }\r\nclass Facture\r\n{\r\n    public static int Total(Numéro n) { return Arrondi(1); }\r\n"
            + "    static int Arrondi(int n) { return n; }\r\n}\r\n"));
        // A caller in a second 1252 file: its name is read by the callers walk, through the other reads.
        File.WriteAllBytes(Path.Combine(_root, "Caisse.cs"), Encoding.Latin1.GetBytes(
            "class Caisse\r\n{\r\n    public static int Encaissé() { return Facture.Total(null); }\r\n}\r\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task TheCallGraph_QuotesASignature_AsTheFileWritesIt()
    {
        var report = await new TraceDependencyTool(() => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = _target, direction = "callees" }), CancellationToken.None);

        Assert.Contains("Total", report, StringComparison.Ordinal);      // witness: the file was read
        Assert.Contains("(Numéro)", report, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0xFFFD).ToString(), report, StringComparison.Ordinal);   // the replacement character
    }

    [Fact]
    public async Task TheCallers_NameACallerAsItsFileWritesIt()
    {
        var report = await new TraceDependencyTool(() => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = _target, direction = "callers" }), CancellationToken.None);

        Assert.Contains("Encaissé", report, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0xFFFD).ToString(), report, StringComparison.Ordinal);
    }
}
