using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Why a copy cannot start has one reader for the copy's card and for <c>/run-copy</c>: a git that refused found no
/// repository with a commit; a git that did not answer or did not start is said as such — never "not a repository".
/// </summary>
public class CopyNoHeadTests
{
    [Theory]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", true)]
    [InlineData("fatal: Needed a single revision", true)]
    [InlineData("fatal: ambiguous argument 'HEAD': unknown revision or path not in the working tree.", true)]
    [InlineData("git did not answer within 10 s", false)]
    [InlineData("An error occurred trying to start process 'git'. The system cannot find the file specified.", false)]
    public void AGitThatDidNotAnswer_IsNeverCalledNotARepository(string failure, bool notARepository)
    {
        Assert.Equal(notARepository, WorkCopy.NoHead(failure).StartsWith("this folder is not a git repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderWithoutGit_IsSaidNotARepository_ByRunCopy()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-nohead-").FullName;
        try
        {
            var (copy, refusal) = await WorkCopy.CreateAsync(dir, CancellationToken.None);
            Assert.Null(copy);
            Assert.StartsWith("this folder is not a git repository with a commit", refusal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* teardown never throws */ } }
    }
}
