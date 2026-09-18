using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A session name the product CREATES never overwrites an existing session.
/// </summary>
/// <remarks>
/// <c>SessionFileName</c> is minute-precise (<c>yyyy-MM-dd_HHmm_title</c>) and the title comes from the
/// first message. <c>/clear</c> then the same question asked again within the minute — the usual
/// gesture after a bad answer — produced two archives with the same name, and the save overwrites:
/// the first conversation vanished without a word. Same path for the new parent of <c>/branch</c> and
/// for the name <c>session/title</c> suggests to VS Code, which archives without asking.
/// <c>ConversationStore.SaveAsync</c> is NOT at fault: re-saving an existing session overwrites it on
/// purpose (<c>/branch</c> rewrites the parent); creating a new name is what must stay free.
/// </remarks>
public class SessionNameCollisionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void AFreeName_IsKept_AndATakenOneGetsTheFirstFreeSuffix()
    {
        // Witness: a free name does not change — existing archives keep the same format.
        Assert.Equal("2026-07-30_1012_question", SessionManager.UniqueSessionName("2026-07-30_1012_question", []));

        Assert.Equal("2026-07-30_1012_question_2",
            SessionManager.UniqueSessionName("2026-07-30_1012_question", ["2026-07-30_1012_QUESTION"]));
        Assert.Equal("2026-07-30_1012_question_3",
            SessionManager.UniqueSessionName("2026-07-30_1012_question",
                                             ["2026-07-30_1012_question", "2026-07-30_1012_question_2"]));
    }

    /// <summary>A session folder every file of which could be read.</summary>
    /// <remarks>Explicit rather than an implicit conversion from a list: the unreadable half is
    /// exactly what a caller must not be able to forget.</remarks>
    private static SessionScan<SessionSummary> Scan(params SessionSummary[] sessions) =>
        new([.. sessions], []);

    [Fact]
    public void BranchingAnUnsavedConversation_DoesNotTakeAnExistingSessionsName()
    {
        List<SavedMessage> conversation =
        [
            new("user", "first question"), new("assistant", "first answer"),
            new("user", "second question"), new("assistant", "second answer"),
        ];
        var existing = new SessionSummary("2026-07-30_1012_first_question", DateTime.UtcNow, 2, "first question");

        var plan = BranchManager.Plan(conversation, 1, currentName: null, Scan(existing),
                                      new DateTime(2026, 7, 30, 10, 12, 0));

        Assert.NotNull(plan);
        Assert.True(plan!.ParentIsNew);
        Assert.NotEqual("2026-07-30_1012_first_question", plan.ParentName, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(plan.ParentName, plan.BranchName, StringComparison.Ordinal);
    }

    [Fact]
    public void BothFrontEnds_ArchiveUnderAFreeName()
    {
        var vm   = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.PendingPrompt.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));

        // Witness: both sites still name an archive with SessionFileName.
        Assert.Contains("SessionManager.SessionFileName(", vm,   StringComparison.Ordinal);
        Assert.Contains("SessionManager.SessionFileName(", host, StringComparison.Ordinal);

        Assert.Contains("SessionManager.UniqueSessionName(", vm,   StringComparison.Ordinal);
        Assert.Contains("SessionManager.UniqueSessionName(", host, StringComparison.Ordinal);
    }
}
