using System.Text;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A diff line of a GBK file is decoded as GBK only if its bytes are not also valid UTF-8 — and 92 of the 500 most
/// common Chinese characters (一 为 时 要 小 …) are, alone: "// 一" came out as "// һ". One line is too little
/// evidence; a SIDE of a file's diff is decided together (its lines that are not UTF-8, else the working-tree file's
/// encoding, read whole). The legacy code page is injected: the machine's own is Windows-1252 on every CI leg.
/// </summary>
public class GitDiffLegacySideTests
{
    private static readonly Encoding Gbk;

    static GitDiffLegacySideTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    /// <summary>git's output as the capture holds it: one char per byte.</summary>
    private static string Captured(params (string Text, Encoding Encoding)[] lines) =>
        Encoding.Latin1.GetString(lines.SelectMany(l => l.Encoding.GetBytes(l.Text + "\n")).ToArray());

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static (string, Encoding)[] Header(string path) =>
    [
        ($"diff --git a/{path} b/{path}", Utf8), ("index 1111111..2222222 100644", Utf8),
        ($"--- a/{path}", Utf8), ($"+++ b/{path}", Utf8), ("@@ -1,2 +1,2 @@", Utf8),
    ];

    [Fact]
    public void AnAmbiguousLine_IsReadLikeTheRestOfItsSide()
    {
        var output = Captured([.. Header("a.cs"),
            (" // 小", Gbk),          // context, ambiguous alone
            ("-// 为", Gbk),          // old side, ambiguous alone
            ("+// 中文注释", Gbk)]);   // new side, NOT UTF-8: the evidence

        var text = GitProcess.DecodeCaptured(output, Gbk, workingTree: _ => null);

        Assert.Contains(" // 小\n", text);
        Assert.Contains("-// 为\n", text);
        Assert.Contains("+// 中文注释\n", text);
    }

    [Fact]
    public void ASingleAmbiguousChange_IsReadInTheWorkingTreeFilesEncoding()
    {
        var output = Captured([.. Header("a.cs"), ("-// 一", Gbk), ("+// 小", Gbk)]);

        var text = GitProcess.DecodeCaptured(output, Gbk, workingTree: path => path == "a.cs" ? Gbk : null);

        Assert.Contains("-// 一\n", text);
        Assert.Contains("+// 小\n", text);
    }

    [Fact]
    public void TheSameBytes_StayUtf8_InAUtf8File()
    {
        // Reference arm: "һ" is a real letter; in a UTF-8 file the same two bytes are that letter.
        var output = Captured([.. Header("a.cs"), ("-// һ", Utf8), ("+// С", Utf8)]);

        var text = GitProcess.DecodeCaptured(output, Gbk, workingTree: _ => TextFileEncoding.Utf8NoBom);

        Assert.Contains("-// һ\n", text);
        Assert.Contains("+// С\n", text);
    }

    [Fact]
    public void AConversionToUtf8_ReadsEachSideInItsOwnEncoding()
    {
        var output = Captured([.. Header("a.cs"), ("-// 中文", Gbk), ("+// 中文", Utf8)]);

        var text = GitProcess.DecodeCaptured(output, Gbk, workingTree: _ => TextFileEncoding.Utf8NoBom);

        Assert.Contains("-// 中文\n", text);
        Assert.Contains("+// 中文\n", text);
    }

    [Fact]
    public void PathsOnHeaderLines_AndLinesOutsideADiff_StayUtf8()
    {
        // A path is git's own UTF-8; "说明" in UTF-8 is not GBK, whatever the file's content is in.
        var output = Captured([.. Header("说明.cs"), ("+// 中文", Gbk), ("M  说明.cs", Utf8)]);

        var text = GitProcess.DecodeCaptured(output, Gbk, workingTree: _ => Gbk);

        Assert.Contains("diff --git a/说明.cs b/说明.cs\n", text);
        Assert.Contains("+++ b/说明.cs\n", text);
        Assert.Contains("+// 中文\n", text);
        Assert.Contains("M  说明.cs\n", text);
    }
}
