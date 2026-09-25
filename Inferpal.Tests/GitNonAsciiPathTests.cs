using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// git quotes every path with a byte above 0x7F by default (<c>core.quotePath</c>): <c>Zażółć.cs</c> comes out as
/// <c>"Za\305\274\303\263\305\202\304\207.cs"</c>. That is the name the model reads in <c>get_git_status</c>,
/// <c>/commit</c> and <c>/check</c> — and a name it cannot pass back to <c>read_file</c>.
/// </summary>
public sealed class GitNonAsciiPathTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitpath-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("Zażółć")]
    [InlineData("中文注释")]
    [InlineData("Räksmörgås")]
    public async Task AFileNamedInAnyLanguage_IsNamedAsItIs(string name)
    {
        var root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(root);
        Assert.True(RunGit("init -q .", root), "`git init` failed: this test is UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(root, name + ".cs"), "// x\n");
        File.WriteAllText(Path.Combine(root, "plain.cs"), "// x\n");

        var (status, _) = await GitProcess.RunAsync("status --short", root, CancellationToken.None);
        Assert.Contains("plain.cs", status);                                                  // witness: status ran
        Assert.Contains(name + ".cs", status);

        var tool = new GetGitStatusTool(new NullEditorSurface(), () => root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = root }));
        Assert.Contains(name + ".cs", await tool.ExecuteAsync(args.RootElement, CancellationToken.None));
    }

    private static bool RunGit(string args, string workDir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
