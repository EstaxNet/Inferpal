using System.IO;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Plans are hand-written and shared by a team. <c>/plan list</c> showed a committed <c>migration_v2.md</c> as
/// <c>migration_v2</c> — its file's own name — and <c>/plan open migration_v2</c> looked for the NORMALISED name,
/// <c>migration-v2.md</c>: "no plan named migration-v2 — /plan list shows the ones there are", the advice leading
/// straight back to the name that failed. A name that is a file of the plans folder now designates it, and the active
/// plan is remembered under that name so the next command finds the same file.
/// </summary>
public sealed class PlanHandNamedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"plans-{Guid.NewGuid():N}");

    public PlanHandNamedFileTests()
    {
        Directory.CreateDirectory(PlanStore.DirectoryFor(_root));
        File.WriteAllText(Path.Combine(PlanStore.DirectoryFor(_root), "migration_v2.md"),
            "# Migration v2\n\n- [x] freeze the schema\n- [ ] copy the data\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ThePlanListed_IsTheOneOpened_UnderTheNameListed()
    {
        var listed = Assert.Single(PlanStore.List(_root)).Name;
        Assert.Equal("migration_v2", listed);

        var open = PlanCommandHandler.Handle(_root, ["/plan", "open", listed], null, null);
        Assert.Contains("Migration v2", open.Message, StringComparison.Ordinal);
        Assert.Equal("migration_v2", open.SetActivePlan);

        // And the next command, on the plan just opened, finds the same file.
        var next = PlanCommandHandler.Handle(_root, ["/plan", "next"], null, open.SetActivePlan);
        Assert.Contains("copy the data", next.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypedNameWithNoSuchFile_IsStillNormalised()   // reference arm: "My Plan" → my-plan.md
    {
        File.WriteAllText(Path.Combine(PlanStore.DirectoryFor(_root), "my-plan.md"), "# My plan\n\n- [ ] one\n");

        Assert.NotNull(PlanStore.Load(_root, "My Plan"));
        Assert.Equal(Path.Combine(PlanStore.DirectoryFor(_root), "never-written.md"), PlanStore.PathFor(_root, "Never Written"));
    }
}
