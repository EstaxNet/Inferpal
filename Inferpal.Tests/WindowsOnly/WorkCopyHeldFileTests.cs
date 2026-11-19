using System.Diagnostics;
using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A discard while a build that has just ended still holds a file of the copy for a moment (pari 106): git stops at that file
/// and leaves the rest, which the discard removes once the file is let go. Windows only: elsewhere an exclusive hold is
/// advisory, and nothing would be held.
/// </summary>
[Collection(WorkCopyCollection.Name)]
public sealed class WorkCopyHeldFileTests : IDisposable
{
    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("inferpal-workcopy-held-repo-");
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("inferpal-workcopy-held-home-");
    private readonly string _homeBefore = WorkCopy.Home;

    public WorkCopyHeldFileTests() => WorkCopy.Home = _home.FullName;

    public void Dispose()
    {
        WorkCopy.Home = _homeBefore;
        foreach (var dir in new[] { _repo, _home })
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir.FullName, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                dir.Delete(recursive: true);
            }
            catch { }
    }

    private bool Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    [Fact]
    public async Task ADiscard_WhileABuildStillHoldsAFileForAMoment_RemovesTheWholeFolder()
    {
        File.WriteAllText(Path.Combine(_repo.FullName, "Pay.cs"), "class Pay { }\n");
        Assert.True(Git("init", "-q", ".") && Git("add", "-A") && Git("commit", "-q", "-m", "start"), "git is needed: this test is UNDECIDED, not green.");
        var (copy, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        var held = new FileStream(Path.Combine(copy!.Folder, "Pay.cs"), FileMode.Open, FileAccess.Read, FileShare.None);
        // Let go a second later, as a build that has just ended does.
        _ = Task.Delay(1000).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        var problem = await copy.DiscardAsync(CancellationToken.None);

        Assert.Null(problem);
        Assert.False(Directory.Exists(copy.Folder));
    }

    [Fact]
    public async Task AFileHeldPastTheDeadline_IsSaid_WithTheFolder()
    {
        File.WriteAllText(Path.Combine(_repo.FullName, "Pay.cs"), "class Pay { }\n");
        Assert.True(Git("init", "-q", ".") && Git("add", "-A") && Git("commit", "-q", "-m", "start"), "git is needed: this test is UNDECIDED, not green.");
        var removeFor = WorkCopy.RemoveFor;
        WorkCopy.RemoveFor = TimeSpan.FromMilliseconds(300);
        try
        {
            var (copy, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
            string? problem;
            using (new FileStream(Path.Combine(copy!.Folder, "Pay.cs"), FileMode.Open, FileAccess.Read, FileShare.None))
                problem = await copy.DiscardAsync(CancellationToken.None);

            Assert.Contains(copy.Folder, problem);
            Assert.Contains("a program still holds a file in it", problem);
        }
        finally { WorkCopy.RemoveFor = removeFor; }
    }
}
