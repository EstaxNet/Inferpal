using System.ComponentModel;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The first run's server install (<see cref="ServerInstall"/>, <see cref="ServerInstallCard"/>): what each machine is
/// offered, that the command shown is the command run, how a run ends, and that what it starts is never bound to the
/// editor's lifetime.
/// </summary>
public sealed class ServerInstallTests
{
    private const string Winget = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private const string OllamaApp = @"C:\Users\me\AppData\Local\Programs\Ollama\ollama app.exe";

    private static ServerInstallOffer InstallOffer() =>
        ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, installed: null);

    /// <summary>Each machine its offer: Windows with winget installs, an installed Ollama starts (never 1.6 GB again), no
    /// winget offers nothing to run, and macOS, Linux or another machine get a command that Inferpal never runs.</summary>
    [Fact]
    public void EachMachine_IsOfferedWhatWorksThere()
    {
        Assert.Equal(ServerInstallKind.Install, InstallOffer().Kind);
        Assert.Equal(ServerInstallKind.Start,
                     ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, OllamaApp).Kind);
        Assert.Equal(ServerInstallKind.NoWinget,
                     ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, winget: null, installed: null).Kind);

        var remote = ServerInstall.OfferFor("ssh-remote", ServerInstall.HostOs.Windows, Winget, OllamaApp);
        var mac    = ServerInstall.OfferFor(null, ServerInstall.HostOs.MacOs, null, null);
        var linux  = ServerInstall.OfferFor("wsl", ServerInstall.HostOs.Linux, null, null);
        Assert.Equal((ServerInstallKind.Command, ServerInstall.MacCommand, ServerInstall.LinuxCommand),
                     (remote.Kind, mac.Command, linux.Command));
        Assert.All(new[] { remote, mac, linux }, o => Assert.False(o.Runs));
        Assert.True(InstallOffer().Runs);
    }

    /// <summary>The command the consent card shows is the one that runs: one source, the program and its arguments.</summary>
    [Fact]
    public void TheCommandShown_IsTheCommandRun()
    {
        var offer = InstallOffer();
        var card  = ServerInstallCard.For(offer, null);

        Assert.Equal(Winget, offer.Program);
        Assert.Equal("winget " + string.Join(' ', offer.Arguments), offer.Command);
        Assert.Equal(offer.Command, card.Consent!.Command);
        // For this account, from the community source, without a question: what the card's facts promise.
        Assert.Contains("--scope user", offer.Command);
        Assert.Contains("--source winget", offer.Command);
        Assert.Contains("--disable-interactivity", offer.Command);
    }

    /// <summary>The box says what each offer is: an install asks first and can be stopped; a start runs on its click and
    /// only waits; no winget offers no button; a command is shown with the note that Inferpal never runs it.</summary>
    [Fact]
    public void TheBox_SaysWhatEachOfferIs()
    {
        var install = ServerInstallCard.For(InstallOffer(), null);
        var start   = ServerInstallCard.For(ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, OllamaApp), null);
        var none    = ServerInstallCard.For(ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, null, null), null);
        var command = ServerInstallCard.For(ServerInstall.OfferFor(null, ServerInstall.HostOs.Linux, null, null), null);

        Assert.Equal((Strings.SetupInstallButton, Strings.SetupInstallStop), (install.Button, install.Running!.Stop));
        Assert.Equal(4, install.Consent!.Facts.Count);
        Assert.Equal((Strings.SetupStartButton, (string?)null, (ServerInstallConsent?)null),
                     (start.Button, start.Running!.Stop, start.Consent));
        Assert.Equal(((string?)null, (ServerInstallRunning?)null), (none.Button, none.Running));
        Assert.Equal(((string?)null, Strings.SetupCommandShownOnly, ServerInstall.LinuxCommand),
                     (command.Button, command.CommandNote, command.Command));
    }

    /// <summary>A terminal setup says the command and to run the setup again: it runs nothing for the person.</summary>
    [Fact]
    public void ATerminalSetup_SaysTheCommand()
    {
        var lines = ServerInstallCard.TerminalLines(InstallOffer());
        Assert.Contains("  " + InstallOffer().Command, lines);
        Assert.Equal(Strings.AcpSetupRunAgain, lines[^1]);
        Assert.Equal([Strings.AcpSetupNoWinget],
                     ServerInstallCard.TerminalLines(ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, null, null)));
    }

    private static string LockPath() => Path.Combine(Path.GetTempPath(), "inferpal-tests", $"install-{Guid.NewGuid():N}.lock");

    /// <summary>winget's failure is said with its code and its last lines, as winget wrote them — and never more lines than
    /// the card shows.</summary>
    [Fact]
    public async Task AFailedWinget_IsSaidWithItsCodeAndLastLines()
    {
        var said = new List<string>();
        var result = await ServerInstall.RunAsync(InstallOffer(), said.Add, CancellationToken.None,
            serverAnswers: _ => Task.FromResult(true), lockPath: LockPath(),
            run: (_, line, _) =>
            {
                for (var i = 1; i <= 8; i++) line($"line {i}");
                return Task.FromResult(unchecked((int)0x80072EE7));
            });
        var report = ServerInstallReport.Of(result, InstallOffer(), startsNow: false);

        Assert.Equal(ServerInstallOutcome.Failed, result.Outcome);
        Assert.Equal(8, said.Count);
        Assert.Equal(Enumerable.Range(3, ServerInstall.LastLineCount).Select(i => $"line {i}"), result.LastLines);
        Assert.Equal(Strings.SetupInstallFailedCause("0x80072EE7"), report.Cause);
        Assert.Equal((Strings.SetupInstallTryAgain, "install"), (report.Retry, report.RetryKind));
    }

    /// <summary>A winget that succeeded is not a server: the run waits until Ollama answers, and says when it does not —
    /// with Start as what may work once Ollama is installed.</summary>
    [Fact]
    public async Task ASucceededWinget_WaitsForTheServer_AndSaysWhenNoneAnswers()
    {
        var polls = 0;
        var answers = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None,
            serverAnswers: _ => Task.FromResult(++polls >= 2), serverWait: TimeSpan.FromSeconds(30), lockPath: LockPath(),
            run: (_, _, _) => Task.FromResult(0));
        Assert.Equal((ServerInstallOutcome.Answers, 2), (answers.Outcome, polls));
        Assert.True(ServerInstallReport.Of(answers, InstallOffer(), startsNow: true).Answers);

        var silent = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None,
            serverAnswers: _ => Task.FromResult(false), serverWait: TimeSpan.Zero, lockPath: LockPath(),
            run: (_, _, _) => Task.FromResult(0));
        var report = ServerInstallReport.Of(silent, InstallOffer(), startsNow: true);
        Assert.Equal(ServerInstallOutcome.NoServer, silent.Outcome);
        Assert.Equal((Strings.SetupInstallNoServerTitle, Strings.SetupStartButton, "start"),
                     (report.Title, report.Retry, report.RetryKind));
    }

    /// <summary>Stop ends the run with "stopped", never "failed"; a program that cannot start says why.</summary>
    [Fact]
    public async Task Stop_IsStopped_AndAProgramThatCannotStart_SaysWhy()
    {
        using var stop = new CancellationTokenSource();
        var running = ServerInstall.RunAsync(InstallOffer(), _ => { }, stop.Token, lockPath: LockPath(),
            run: async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return 0; });
        stop.Cancel();
        Assert.Equal(ServerInstallOutcome.Stopped, (await running.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);

        var notStarted = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None, lockPath: LockPath(),
            run: (_, _, _) => throw new Win32Exception(2, "The system cannot find the file specified"));
        Assert.Equal((ServerInstallOutcome.NotStarted, "The system cannot find the file specified"),
                     (notStarted.Outcome, notStarted.Detail));
    }

    /// <summary>Two windows never run winget at once: the second is told, and starts nothing.</summary>
    [Fact]
    public async Task ASecondWindow_IsToldAnInstallRuns_AndStartsNothing()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        var path = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ran = false;
            var result = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None, lockPath: path,
                run: (_, _, _) => { ran = true; return Task.FromResult(0); });
            Assert.Equal((ServerInstallOutcome.Busy, false), (result.Outcome, ran));
        }
        // Reference arm: the lock released, the run goes.
        var free = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None, lockPath: path,
            serverAnswers: _ => Task.FromResult(true), run: (_, _, _) => Task.FromResult(0));
        Assert.Equal(ServerInstallOutcome.Answers, free.Outcome);
    }

    /// <summary>Nothing runs for an offer Inferpal does not run: a command is shown, never executed.</summary>
    [Fact]
    public async Task AShownCommand_IsNeverRun()
    {
        var shown = ServerInstall.OfferFor(null, ServerInstall.HostOs.Linux, null, null);
        await Assert.ThrowsAsync<ArgumentException>(() => ServerInstall.RunAsync(shown, _ => { }, CancellationToken.None,
            lockPath: LockPath(), run: (_, _, _) => throw new InvalidOperationException("ran")));
    }

    /// <summary>A server that runs — answering, asking for a key — or anything holding Ollama's port leaves nothing to
    /// install: a second Ollama could not listen there. Only places where nothing listens leave room for one.</summary>
    [Fact]
    public void ARunningServer_OrAPortTaken_LeavesNothingToInstall()
    {
        ServerPlace At(string url, ServerPlaceOutcome outcome) => new(url, null, outcome, null, null);
        var quiet = new[] { At("http://localhost:11434", ServerPlaceOutcome.NothingListening),
                            At("http://localhost:1234", ServerPlaceOutcome.NothingListening) };
        Assert.True(ServerInstall.Offers(quiet));                                                       // reference arm
        Assert.False(ServerInstall.Offers([.. quiet, At("http://localhost:8000", ServerPlaceOutcome.NeedsKey)]));
        Assert.False(ServerInstall.Offers([.. quiet, At("http://localhost:1234", ServerPlaceOutcome.Answers)]));
        Assert.False(ServerInstall.Offers([At("http://localhost:11434", ServerPlaceOutcome.NotAModelServer)]));
        Assert.True(ServerInstall.Offers([.. quiet, At("http://localhost:8080", ServerPlaceOutcome.NotAModelServer)]));
    }

    /// <summary>An Ollama installed another way (scoop, choco, a zip) is started as its server, never installed again.</summary>
    [Fact]
    public void AnOllamaInstalledAnotherWay_IsStartedAsItsServer()
    {
        var offer = ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, @"C:\Users\me\scoop\shims\ollama.exe");
        Assert.Equal(ServerInstallKind.Start, offer.Kind);
        Assert.Equal(["serve"], offer.Arguments);
        Assert.Equal(@"""C:\Users\me\scoop\shims\ollama.exe"" serve", offer.Command);
        Assert.Empty(ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, OllamaApp).Arguments);
    }

    /// <summary>
    /// ⚠ Ollama's application starts no server unless its own folder is on its PATH: its installer launches it with that
    /// folder first, and an editor opened before the install has a PATH without it. A start hands it the same PATH,
    /// whichever way Ollama was installed.
    /// </summary>
    [Fact]
    public void AStart_PutsOllamasFolderFirstOnItsPath()
    {
        // Paths of this platform: a Windows path has no folder part elsewhere, and every assertion would hold on "".
        var programs = Path.Combine(Path.GetTempPath(), "Programs");
        foreach (var installed in new[] { Path.Combine(programs, "Ollama", "ollama app.exe"), Path.Combine(programs, "scoop", "ollama.exe") })
        {
            var start = ServerInstall.StartInfoFor(ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, installed));
            var folder = Path.GetDirectoryName(installed)!;
            Assert.NotEmpty(folder);
            Assert.False(start.UseShellExecute);                        // the shell would hand it the editor's environment
            Assert.StartsWith(folder + Path.PathSeparator, start.Environment["PATH"]);
            Assert.Equal(folder, start.WorkingDirectory);
        }
    }

    /// <summary>A start says what a start does — no winget, nothing to stop — and Stop pressed once winget succeeded, while
    /// the server is waited for, is said as an installed Ollama that does not answer, never as "winget was stopped".</summary>
    [Fact]
    public void AStart_IsSaidAsAStart_AndAStopAfterWinget_AsNotAnswering()
    {
        var start = ServerInstallCard.RunningFor(ServerInstallKind.Start)!;
        Assert.Equal((Strings.SetupStartingLead(ServerInstall.OllamaAddress), (string?)null), (start.Lead, start.Stop));

        var waited = ServerInstallReport.Of(new ServerInstallResult(ServerInstallOutcome.Stopped, 0), InstallOffer(), startsNow: true);
        Assert.Equal(("noServer", Strings.SetupInstallNoServerTitle, "start"), (waited.Outcome, waited.Title, waited.RetryKind));
        Assert.Equal((start.Title, start.Lead, (string?)null), (waited.RetryRunning!.Title, waited.RetryRunning.Lead, waited.RetryRunning.Stop));

        // Reference arm: stopped while winget ran — winget was stopped, and the retry is the install again.
        var cut = ServerInstallReport.Of(new ServerInstallResult(ServerInstallOutcome.Stopped), InstallOffer(), startsNow: false);
        Assert.Equal(("stopped", "install"), (cut.Outcome, cut.RetryKind));
    }

    /// <summary>A retry never turns a start into an install: a start card showed no command to consent to.</summary>
    [Fact]
    public void ARetry_NeverTurnsAStartIntoAnInstall()
    {
        var start = ServerInstall.OfferFor(null, ServerInstall.HostOs.Windows, Winget, OllamaApp);
        foreach (var outcome in Enum.GetValues<ServerInstallOutcome>())
            Assert.NotEqual("install", ServerInstallReport.Of(new ServerInstallResult(outcome, 1), start, startsNow: false).RetryKind);

#if WINDOWS
        // The Visual Studio card holds the same line whatever report it is handed.
        var card = ToolWindow.ChatMessageItem.ServerInstallMsg(ServerInstallCard.For(start, null),
            () => Task.CompletedTask, () => Task.CompletedTask, () => { });
        card.InstallEnded(ServerInstallReport.Of(new ServerInstallResult(ServerInstallOutcome.Failed, 1), InstallOffer(), startsNow: false));
        Assert.Equal(("start", false), (card.InstallKind, card.HasInstallPrimary));
#endif
    }

    /// <summary>"Busy" follows winget itself: the lock ends with the editor, winget does not. The process named in the file
    /// counts only while it runs with the same start time — a process id is reused.</summary>
    [Fact]
    public void AnInstall_IsBusyForAsLongAsItsWingetRuns()
    {
        var path = LockPath() + ".pid";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(path, $"{me.Id}\n{me.StartTime.ToUniversalTime().Ticks}");
        Assert.True(ServerInstall.WingetStillRuns(path));
        File.WriteAllText(path, $"{me.Id}\n{me.StartTime.ToUniversalTime().Ticks - 1}");
        Assert.False(ServerInstall.WingetStillRuns(path));
        File.Delete(path);
        Assert.False(ServerInstall.WingetStillRuns(path));
    }

    /// <summary>A lock that cannot be taken for another cause than another window says that cause, never "busy".</summary>
    [Fact]
    public async Task ALockThatCannotBeTaken_SaysWhy_NotBusy()
    {
        var file = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "a file where the lock's folder should be");
        var result = await ServerInstall.RunAsync(InstallOffer(), _ => { }, CancellationToken.None,
            lockPath: Path.Combine(file, "server-install.lock"), run: (_, _, _) => Task.FromResult(0));
        Assert.Equal(ServerInstallOutcome.NotStarted, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    /// <summary>Visual Studio's <c>/setup</c> offers the install only where it helps: nothing chosen yet, or the address
    /// chosen is Ollama's. Someone whose server is another one (LM Studio, a remote machine) is not sent to install
    /// Ollama, which their settings would never use — as the first run already decides.</summary>
    [Fact]
    public void VisualStudio_SetupOffersTheInstall_OnlyWhereItsServerWouldBeUsed()
    {
        var rag = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        Assert.Contains("if (client.ConnectionRefusal is null && Services.Inference.ServerInstall.Offers(places)\n"
                        + "                    && (Services.Inference.ServerScan.NothingChosen(_config)\n"
                        + "                        || Services.Inference.ServerScan.SameAddress(url, Services.Inference.ServerInstall.OllamaAddress)))\n"
                        + "                    await OfferServerInstallAsync(null, once: false)",
                        rag.Replace("\r\n", "\n"));
    }

    /// <summary>After an install that answers, the card's discovery and the heartbeat that sees the server come back both
    /// find the fresh Ollama without a model: the first run's notice is posted once, not twice in a row.</summary>
    [Fact]
    public void VisualStudio_TheFirstRunsNotice_IsNotPostedTwiceInARow()
    {
        var rag = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var present = rag[rag.IndexOf("private Task FirstRunPresentAsync(string text)", StringComparison.Ordinal)..];
        Assert.Contains("if (Messages.Count >= 3 && Messages[Messages.Count - 3] is { ToolName: SessionManager.NoticeMarker } last"
                        + " && last.Content == text) return;", present[..present.IndexOf("});", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// ⚠ What the install starts outlives the editor: winget, the installer and the Ollama it launches are never bound to
    /// this process (<c>ProcessLifetime.Bind</c> would kill them with the editor). The positive half of rule 34's
    /// exemption: the file starts processes, and binds none.
    /// </summary>
    [Fact]
    public void WhatTheInstallStarts_IsNeverBoundToTheEditor()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", "Inference", "ServerInstall.cs"));
        Assert.Contains("Process.Start(", code);                    // witness: the file really starts the programs
        Assert.DoesNotContain("ProcessLifetime.Bind(", code);
        Assert.DoesNotContain("ChildProcess.Start(", code);         // which binds for its callers
        Assert.DoesNotContain("entireProcessTree: true", code);     // Stop ends winget, never the installer it started
    }
}
