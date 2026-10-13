using System.Globalization;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Visual Studio: a chip the user adds is cut as VS Code cuts it, and the cut is said to both readers.
/// </summary>
/// <remarks>
/// The active document went whole into the question, whatever its size, and a picked or @-mentioned file up to 512 KB:
/// past the window LM Studio refuses the request and Ollama drops its head — system prompt included — and nothing said
/// so. VS Code cut its chips at 60,000 characters with a marker for the model and a label for the person.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class AttachmentCapTests
{
    [Fact]
    public void AFileThatFits_IsSentWhole_AndSaysNothing()
    {
        // Reference arm: an ordinary file passes untouched.
        var text = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i} of an ordinary file"));
        var capped = AttachmentCap.Of(text);
        Assert.False(capped.Cut);
        Assert.Equal(text, capped.Text);
    }

    [Fact]
    public void ALongFile_IsCutOnALineEnd_AndTheMarkerGivesBothCounts()
    {
        var text = string.Join('\n', Enumerable.Range(1, 5_000).Select(i => $"line {i,5}: some code that is long enough"));
        var capped = AttachmentCap.Of(text);

        Assert.True(capped.Cut);
        Assert.True(capped.Shown <= AttachmentCap.MaxChars);
        Assert.Equal('\n', text[capped.Shown]);                         // on a line end, never mid-line
        Assert.Equal(text.Length, capped.Total);
        Assert.StartsWith(text[..capped.Shown], capped.Text, StringComparison.Ordinal);
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        Assert.EndsWith($"\n…(truncated: the first {N(capped.Shown)} of {N(text.Length)} characters are attached; read_file reads the rest)",
                        capped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLabel_GivesBothCounts_InTheLanguage()
    {
        try
        {
            Strings.ApplyLanguage("en");
            // The numbers in the person's regional format, as every count of the interface.
            Assert.Equal($"big.cs (first {60_000:N0} of {183_402:N0} characters)", Strings.AttachmentCutLabel("big.cs", 60_000, 183_402));
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void EveryChipTheUserAdds_GoesThroughTheCut_AndACutChipIsNotTheWholeFile()
    {
        var root = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "InferpalToolWindowData.Attachments.cs"));
        var add = vm[vm.IndexOf("private void AddAttachment(", StringComparison.Ordinal)..];
        add = add[..add.IndexOf("Attachments.Add(item);", StringComparison.Ordinal)];
        Assert.Contains("var capped = Services.Agent.AttachmentCap.Of(content);", add, StringComparison.Ordinal);
        Assert.Contains("if (capped.Cut) label = Strings.AttachmentCutLabel(label, capped.Shown, capped.Total);", add, StringComparison.Ordinal);
        Assert.Contains("content = capped.Text;", add, StringComparison.Ordinal);
        Assert.Contains("isCut: capped.Cut", add, StringComparison.Ordinal);
        // The relevant code skips only a WHOLE attached file.
        Assert.Contains(".Where(a => !string.IsNullOrEmpty(a.SourcePath) && !a.IsCut)", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void BothEditors_CutAtTheSameFigure_WithTheSameMarker()
    {
        var ts = WebviewRebuildTests.TsCode("attachmentCap.ts");
        Assert.Contains("const marker = `\\n…(truncated: the first ${shown.toLocaleString('en-US')} of ${text.length.toLocaleString('en-US')}`", ts, StringComparison.Ordinal);
        Assert.Contains("+ ' characters are attached; read_file reads the rest)';", ts, StringComparison.Ordinal);
        Assert.Contains("const lineEnd = text.lastIndexOf('\\n', max);", ts, StringComparison.Ordinal);
        Assert.Contains("const shown = lineEnd > max / 2 ? lineEnd : max;", ts, StringComparison.Ordinal);
        Assert.Contains($"capAttachment(content, {AttachmentCap.MaxChars.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '_')})",
                        WebviewRebuildTests.TsCode("chatViewProvider.ts"), StringComparison.Ordinal);
    }
}
