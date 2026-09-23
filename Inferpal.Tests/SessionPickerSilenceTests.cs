using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>/history</c> names the sessions it could not read, and says so on the EMPTY branch too — but
/// VS Code does not list sessions through <c>/history</c>. Its session pickers and the collision check
/// of "Save session" read <c>session/list</c>, which returned the readable ones only: the picker
/// answered "No saved sessions." to someone who has ten, and a save under an unreadable session's
/// name replaced it without the "Replace?" question — unreadable ⇒ absent ⇒ overwritten, the cycle
/// <c>BranchManager.Plan</c> was already made to stop.
/// </summary>
public class SessionPickerSilenceTests
{
    private static SessionSummary Summary(string name) => new(name, DateTime.UtcNow, 2, "hello", null, null);

    [Fact]
    public void AnUnreadableSession_IsNamed_AndItsNameStaysTaken()
    {
        var listed = HostServer.ToSessionList(new SessionScan<SessionSummary>([], ["kept-a", "kept-b"]));

        Assert.Empty(listed.Sessions);
        Assert.Equal(["kept-a", "kept-b"], listed.Unreadable);
        Assert.Equal(Strings.SessionsUnreadableListed(2, "kept-a, kept-b"), listed.Notice);
    }

    [Fact]
    public void EveryFileRead_SaysNothing()
    {
        // Reference arm: a notice on an ordinary listing is the noise that gets a warning ignored.
        var listed = HostServer.ToSessionList(new SessionScan<SessionSummary>([Summary("one")], []));

        Assert.Equal("one", Assert.Single(listed.Sessions).Name);
        Assert.Empty(listed.Unreadable);
        Assert.Null(listed.Notice);
    }

    [Fact]
    public void ThePicker_SaysTheNotice_AndTheSaveCountsTheUnreadableAsTaken()
    {
        // The adapter's half cannot run in the suite (extension host): a source scan, with witnesses.
        var picker = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatSessions.ts")));
        var chat = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts")));

        // WITNESSES: both readers of the list are still where this test looks.
        Assert.Contains("host.sessionList()", picker, StringComparison.Ordinal);
        Assert.Contains("A session named {0} already exists. Replace it?", chat, StringComparison.Ordinal);

        Assert.Contains("showWarningMessage(notice)", picker, StringComparison.Ordinal);
        Assert.Contains("...existing.unreadable", chat, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
