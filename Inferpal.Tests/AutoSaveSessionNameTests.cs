using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The auto-save slot remembers the named session the conversation lives in.
//
//  Restored after a restart — Visual Studio reopened, or the VS Code host started again after a crash
//  or a settings change — the conversation came back without its name: /branch wrote a fresh dated
//  copy as the parent instead of updating the session it came from, and the family tree split.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class AutoSaveSessionNameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-slotname-").FullName;
    private static readonly string Here  = Path.Combine(Path.GetTempPath(), "workspace-here");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "workspace-other");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task TheSlot_KeepsTheNameItWasSavedWith()
    {
        var store = new ConversationStore(_dir);
        await store.AutoSaveAsync([new SavedMessage("user", "q")], CancellationToken.None, Here, currentName: "feature-x");

        var slot = await store.LoadAutoSaveAsync(Here, CancellationToken.None);

        Assert.Equal("feature-x", slot!.CurrentName);
    }

    [Fact]
    public void AProcessThatKnowsItsName_RecordsIt()
    {
        var slot = new SessionData(DateTime.UtcNow, [], WorkspaceRoot: Here, CurrentName: "older");
        Assert.Equal("feature-x", SessionManager.AutoSaveName(known: true, current: "feature-x", slot, Here));
        // A new conversation is known to have no name: it does not inherit the slot's.
        Assert.Null(SessionManager.AutoSaveName(known: true, current: null, slot, Here));
    }

    [Fact]
    public void AFreshlyStartedHost_KeepsTheSlotsName_ForThisWorkspaceOnly()
    {
        var slot = new SessionData(DateTime.UtcNow, [], WorkspaceRoot: Here, CurrentName: "feature-x");

        Assert.Equal("feature-x", SessionManager.AutoSaveName(known: false, current: null, slot, Here));
        // Reference arm: another workspace's slot names a session that is not this conversation's.
        Assert.Null(SessionManager.AutoSaveName(known: false, current: null, slot, Other));
    }

    [Fact]
    public void BothFrontEnds_TakeTheNameBack_WhenTheyRestoreTheSlot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.Host", "HostServer.cs"));
        var vs   = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));

        Assert.Contains("p.Name == \"last_session\" ? data.CurrentName : p.Name", host, StringComparison.Ordinal);
        Assert.Contains("session.CurrentName", vs, StringComparison.Ordinal);
    }
}
