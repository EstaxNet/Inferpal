using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The auto-save (<c>last_session</c>) is restored only in the workspace that wrote it.
/// </summary>
/// <remarks>
/// It is ONE file under <c>%AppData%</c>, shared by both editors and every project, and both
/// front-ends reload it at start-up. Opening project B after working on A brought A's conversation
/// back — and, through <c>BuildRestoredHistory</c>, its tool results into the model's history, which
/// then answered about B with A's context.
/// </remarks>
public class AutoSaveSlotTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static SessionData SavedIn(string? root) =>
        new(DateTime.UtcNow, [new SavedMessage("user", "hello")], WorkspaceRoot: root);

    private static readonly string ProjectA = Path.Combine(Path.GetTempPath(), "inferpal-autosave", "project-a");
    private static readonly string ProjectB = Path.Combine(Path.GetTempPath(), "inferpal-autosave", "project-b");

    [Fact]
    public void AnotherProjectsAutoSave_IsNotRestoredHere() =>
        Assert.False(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA), ProjectB));

    [Fact]
    public void TheSameProject_GetsItsConversationBack()
    {
        // Witness: the rule does not refuse everything.
        Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA), ProjectA));

        // The same folder written differently is still the same folder.
        Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA + Path.DirectorySeparatorChar), ProjectA));
        if (OperatingSystem.IsWindows())
            Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA.ToUpperInvariant()), ProjectA));
    }

    [Fact]
    public void WhenEitherRootIsUnknown_TheContinuityIsKept()
    {
        // A file written before the root was recorded: no way to know, the previous behaviour stays.
        Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(null), ProjectB));

        // A front-end that does not know its root yet (VS open with no solution loaded).
        Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA), null));
        Assert.True(SessionManager.AutoSaveBelongsHere(SavedIn(ProjectA), ""));
    }

    [Fact]
    public async Task TheWorkspaceRoot_IsWrittenToTheAutoSaveFile()
    {
        var store = new ConversationStore();
        var name  = $"test-autosave-{Guid.NewGuid():N}";
        try
        {
            await store.SaveAsync(name, [new SavedMessage("user", "hello")], CancellationToken.None,
                                  workspaceRoot: ProjectA);

            var loaded = await store.LoadAsync(name, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(ProjectA, loaded!.WorkspaceRoot);
        }
        finally { store.Delete(name); }
    }

    /// <summary>
    /// On the Visual Studio side the VM cannot be instantiated outside VS: the rule reads the source.
    /// The save must carry the applied root, and the load must consult the rule.
    /// </summary>
    [Fact]
    public void TheVsWindow_SavesItsRoot_AndRestoresOnlyItsOwnAutoSave()
    {
        var toolWindow = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var connection = ConventionCoverageTests.CodeOnly(Path.Combine(toolWindow, "InferpalToolWindowData.Connection.cs"));
        var pending    = ConventionCoverageTests.CodeOnly(Path.Combine(toolWindow, "InferpalToolWindowData.PendingPrompt.cs"));

        // Witness: both paths still live where the rule looks for them.
        Assert.Contains("private async Task LoadSessionAsync(", connection, StringComparison.Ordinal);
        Assert.Contains("_store.AutoSaveAsync(", pending, StringComparison.Ordinal);

        Assert.Contains("SessionManager.AutoSaveBelongsHere(", connection, StringComparison.Ordinal);
        Assert.Matches(@"_store\.AutoSaveAsync\([^;]*_indexService\.RootDir", pending);
    }
}
