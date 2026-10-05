using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code cut an attached file at 40,000 characters (a typed <c>@file</c>) or 60,000 (a chip) with a bare
/// "… [truncated]", and dropped every <c>@file</c> past the fifth without a word: "is X defined in @big.cs?" was answered
/// "no" about code past the cut, and a sixth file the person named never reached the model, which never learnt it had
/// been asked about. A cut now names both counts — to the model in the marker, to the person under the question — and a
/// file past the cap is named to both. (Visual Studio attaches whole files and names its refusals.)
/// These rules read the sources: the extension has no TypeScript test runner.
/// </summary>
public class VsCodeAttachmentCapTests
{
    /// <summary>A member, up to the next one: <c>Body()</c> stops at the first brace, which here is the return type's.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} moved: the rule reads nothing");
        var end = source.IndexOf("\n  private ", start + signature.Length, StringComparison.Ordinal);
        return end > 0 ? source[start..end] : source[start..];
    }

    [Fact]
    public void TheCut_NamesBothCounts_ForTheModel()
    {
        var cap = WebviewRebuildTests.TsCode("attachmentCap.ts");
        Assert.DoesNotContain("from 'vscode'", cap, StringComparison.Ordinal);   // pure, as mentionPaths.ts
        Assert.Contains("the first ${shown.toLocaleString('en-US')} of ${text.length.toLocaleString('en-US')}", cap,
                        StringComparison.Ordinal);
        Assert.Contains("text.lastIndexOf('\\n', max)", cap, StringComparison.Ordinal);   // whole lines
    }

    [Fact]
    public void BothKindsOfAttachment_GoThroughTheCap_AndNameTheCut()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.DoesNotContain("… [truncated]", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("'\\n…(truncated)'", provider, StringComparison.Ordinal);

        var chip = WebviewRebuildTests.Body(provider, "private addChip(");
        Assert.Contains("capAttachment(", chip, StringComparison.Ordinal);
        Assert.Contains("cutLabel(", chip, StringComparison.Ordinal);

        var typed = Method(provider, "private async expandMentions(");
        Assert.Contains("capAttachment(", typed, StringComparison.Ordinal);
        Assert.Contains("labels.push(ChatViewProvider.cutLabel(", typed, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilePastTheCap_IsNamedToBothReaders()
    {
        var typed = Method(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async expandMentions(");
        Assert.Contains("paths.length >= MAX_FILES", typed, StringComparison.Ordinal);   // WITNESS: the cap is still there
        Assert.Contains("## Not attached:", typed, StringComparison.Ordinal);
        Assert.Contains("t('{0} (not attached: at most {1} files per question)'", typed, StringComparison.Ordinal);

        // And the labels reach the line under the question.
        var turn = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async chatTurn(");
        Assert.Contains("[...mentions.labels]", turn, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The host leaves an ATTACHED file's chunks out of the relevant code it adds — "already in the prompt". For a file
    /// sent in part that is false past the cut: a question about that code got neither the code nor its snippets.
    /// </summary>
    [Fact]
    public void AFileSentInPart_IsNotCountedAsAttached_ForTheRelevantCode()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");

        var chip = WebviewRebuildTests.Body(provider, "private addChip(");
        Assert.Contains("sourcePath: capped.cut ? undefined : sourcePath,", chip, StringComparison.Ordinal);

        var typed = Method(provider, "private async expandMentions(");
        var cut   = typed.IndexOf("if (capped.cut) {", StringComparison.Ordinal);
        var whole = typed.IndexOf("} else {", cut, StringComparison.Ordinal);
        var path  = typed.IndexOf("paths.push(doc.uri.fsPath);", StringComparison.Ordinal);
        Assert.True(cut > 0 && whole > cut && path > whole, "a file sent in part is still sent as an attached path");
    }
}
