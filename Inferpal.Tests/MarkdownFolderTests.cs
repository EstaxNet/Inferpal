using System.IO;
using System.Linq;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Governance;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>.inferpal/</c> file that cannot be <b>read</b> - held open by an editor, permissions
/// refused, a network drive that dropped - simply left its list.
/// </summary>
/// <remarks>
/// The three services did <c>try { … } catch { continue; }</c>. In order of severity: a
/// <b>rule</b> the user wrote to constrain the model stopped applying and the model answered as
/// though it had never existed; <c>/check</c> reviewed a diff against fewer criteria; a
/// <c>/mycommand</c> disappeared.
///
/// The repository had already written the right rule - in <c>PlanStore.List</c>: "A plan we cannot
/// read is still a plan the user has: list it rather than hide it, so a permission problem shows up
/// instead of a silently shorter list." It held for all four; it was applied to one.
/// </remarks>
[Collection("Diagnostics")]
public class MarkdownFolderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"md-{Guid.NewGuid():N}");

    public MarkdownFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Write(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>A genuinely unreadable file: held open with <see cref="FileShare.None"/>, as another
    /// process would. No simulation - <c>File.ReadAllText</c> really throws.</summary>
    private static FileStream Lock(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public void AnUnreadableFile_IsReportedInsteadOfVanishing()
    {
        Write("readable.md", "---\ndescription: A\n---\nbody A");
        var locked = Write("locked.md", "---\ndescription: B\n---\nbody B");

        Diagnostics.Clear();
        using (Lock(locked))
        {
            var read = MarkdownFolder.ReadAll(_dir, "Test.Load", out var unreadable);

            Assert.Single(read);                                   // the readable one comes back
            Assert.Equal(["locked.md"], unreadable);               // the other is NAMED, not forgotten
            Assert.Contains(Diagnostics.Snapshot(),
                e => e.Context.Contains("locked.md"));             // and its cause is available
        }
    }

    /// <summary>Ordinary path: everything reads, nothing is reported, nothing is traced. A channel
    /// that speaks when all is well stops being read.</summary>
    [Fact]
    public void WhenEverythingReads_NothingIsReported()
    {
        Write("a.md", "body A");
        Write("b.md", "body B");

        Diagnostics.Clear();
        var read = MarkdownFolder.ReadAll(_dir, "Test.Load", out var unreadable);

        Assert.Equal(2, read.Count);
        Assert.Empty(unreadable);
        Assert.Empty(Diagnostics.Snapshot());
    }

    /// <summary>An empty file stays a SILENT omission, deliberately: that is not a failure, it is a
    /// file with nothing in it. Reporting it would be noise about a deliberate state.</summary>
    [Fact]
    public void AnEmptyFile_IsNotAFailure()
    {
        Write("empty.md", "");

        var read = MarkdownFolder.ReadAll(_dir, "Test.Load", out var unreadable);

        Assert.Single(read);        // the reader returns it: skipping is the service's job
        Assert.Empty(unreadable);
        Assert.Empty(RulesService.Load(_dir));   // and it does skip it, reporting nothing
    }

    [Fact]
    public void EachServiceReportsItsUnreadableFiles()
    {
        var locked = Write("rule.md", "---\ndescription: R\n---\nbody");

        using (Lock(locked))
        {
            Assert.Empty(RulesService.Load(_dir, out var r));
            Assert.Equal(["rule.md"], r);

            Assert.Empty(ChecksService.Load(_dir, out var c));
            Assert.Equal(["rule.md"], c);

            Assert.Empty(PromptFilesService.LoadUncached(_dir, out var p));
            Assert.Equal(["rule.md"], p);
        }
    }

    /// <summary>
    /// The worst possible rendering, and the one to catch: when EVERY file is unreadable the list is
    /// empty and the absence message claimed the user had written no rule at all - while their files
    /// are right there, simply not read.
    /// </summary>
    [Fact]
    public void WhenEveryFileIsUnreadable_TheListingDoesNotClaimThereAreNone()
    {
        var locked = Write("rule.md", "---\ndescription: R\n---\nbody");

        // The handler builds its own path: <root>/.inferpal/rules
        var rulesDir = Path.Combine(_dir, ".inferpal", "rules");
        Directory.CreateDirectory(rulesDir);
        File.Move(locked, Path.Combine(rulesDir, "rule.md"));

        using (Lock(Path.Combine(rulesDir, "rule.md")))
        {
            var result = RulesChecksPromptsCommandHandler.Rules(_dir, ["/rules"]);

            Assert.NotNull(result.Message);
            Assert.Contains("rule.md", result.Message!);   // the file is named
        }
    }
}
