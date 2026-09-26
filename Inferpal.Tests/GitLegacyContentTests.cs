using System.Diagnostics;
using System.IO;
using System.Text;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// git prints a file's CONTENT as the bytes the file holds. The whole output was decoded as UTF-8, so a diff of a
/// source saved in a legacy code page — the files the rest of the product reads and writes in their own encoding —
/// reached /commit, /check and get_git_status as "caf�": a line the model cannot quote back into an edit, and that a
/// reviewer reads as corruption.
/// </summary>
public sealed class GitLegacyContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitlegacy-{Guid.NewGuid():N}");

    public GitLegacyContentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private bool Git(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<string> DiffAfter(string file, byte[] before, byte[] after)
    {
        Assert.True(Git("init -q ."), "`git init` failed: this test is UNDECIDED, not green.");
        File.WriteAllBytes(Path.Combine(_root, file), before);
        Assert.True(Git("add .") && Git("-c user.name=t -c user.email=t@t.invalid commit -q -m init"),
            "`git commit` failed: this test is UNDECIDED, not green.");
        File.WriteAllBytes(Path.Combine(_root, file), after);

        var (diff, exit) = await GitProcess.RunAsync("diff", _root, CancellationToken.None);
        Assert.Equal(0, exit);
        return diff;
    }

    [Fact]
    public async Task ADiffOfALegacyFile_ShowsItsAccents()
    {
        // "é" is 0xE9 in Windows-1252 and Latin-1 — the legacy fallback on every leg of the CI.
        var diff = await DiffAfter("Menu.cs",
            [.. "// caf"u8, 0xE9, .. " au lait\n"u8],
            [.. "// caf"u8, 0xE9, .. " au lait\n// d"u8, 0xE9, .. "cision prise\n"u8]);

        Assert.Contains("+// décision prise", diff);
        Assert.Contains(" // café au lait", diff);
        Assert.DoesNotContain("\uFFFD", diff);
    }

    [Fact]
    public async Task ADiffOfAUtf8File_StaysUtf8_InEveryLanguage()
    {
        // Reference arm: the ordinary file, and a name in UTF-8 on the header lines of the same diff.
        var diff = await DiffAfter("Zażółć.cs",
            Encoding.UTF8.GetBytes("// Räksmörgås\n"),
            Encoding.UTF8.GetBytes("// Räksmörgås\n// 中文注释 — zażółć\n"));

        Assert.Contains("+// 中文注释 — zażółć", diff);
        Assert.Contains("Zażółć.cs", diff);
        Assert.DoesNotContain("\uFFFD", diff);
    }

    [Fact]
    public void TheCapturesOwnTruncationMarker_SurvivesTheByteRoundTrip()
    {
        // A git output over the capture's bound carries its "[… dropped …]" marker, the only text above U+00FF there.
        var captured = "café\n\n[… middle of the output dropped to bound memory …]\n+ok\n";

        var text = GitProcess.DecodeCaptured(captured);

        Assert.Contains("[… middle of the output dropped to bound memory …]", text);
        Assert.StartsWith(TextFileEncoding.LegacyEncoding.GetString([0x63, 0x61, 0x66, 0xE9]), text);   // the byte, decoded
    }
}
