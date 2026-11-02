using System.Globalization;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>
/// The "no server yet" box of the first run, composed for both editors from this machine's
/// <see cref="ServerInstallOffer"/>: what it says, its button, the command it shows, the consent card, and what an install in
/// progress says. The front-ends draw it and decide nothing.
/// </summary>
/// <param name="Kind">install | start | noWinget | command.</param>
/// <param name="Button">null: nothing Inferpal runs here (the command is shown, or only the links).</param>
/// <param name="Command">The command shown (to copy, or in the consent card); null when there is none.</param>
/// <param name="CommandNote">Under a command Inferpal does not run: it shows, it never runs.</param>
/// <param name="OthersLead">Before the links to install a server by hand.</param>
/// <param name="Consent">The card that asks before installing; null for anything else.</param>
/// <param name="Running">What the box says while the program runs; null when nothing runs.</param>
internal sealed record ServerInstallCard(
    string Kind, string Title, string Lead, string? Button, string? Detail, string? Command, string? CommandNote,
    string OthersLead, ServerInstallConsent? Consent, ServerInstallRunning? Running)
{
    /// <summary>The box for <paramref name="offer"/>, on the host's side of <paramref name="remoteName"/>.</summary>
    internal static ServerInstallCard For(ServerInstallOffer offer, string? remoteName)
    {
        var gb      = Gb(ServerInstall.ApproxDownloadGb);
        var address = ServerInstall.OllamaAddress;
        return offer.Kind switch
        {
            ServerInstallKind.Install => new(
                "install", Strings.SetupNoServerYet, Strings.SetupInstallLead, Strings.SetupInstallButton,
                Strings.SetupInstallDetail(gb), offer.Command, null, Strings.SetupInstallOthers,
                new ServerInstallConsent(
                    Strings.SetupInstallButton, Strings.SetupInstallDetail(gb), Strings.SetupConsentRuns, offer.Command,
                    [Strings.SetupConsentSource, Strings.SetupConsentAccount, Strings.SetupConsentStartup, Strings.SetupConsentNext],
                    Strings.SetupConsentInstall, Strings.SetupConsentCancel),
                RunningFor(ServerInstallKind.Install)),
            ServerInstallKind.Start => new(
                "start", Strings.SetupStartTitle, Strings.SetupStartLead(address), Strings.SetupStartButton,
                Strings.SetupStartDetail, null, null, Strings.SetupInstallOthers, null,
                RunningFor(ServerInstallKind.Start)),
            ServerInstallKind.NoWinget => new(
                "noWinget", Strings.SetupNoServerYet, Strings.SetupNoWinget, null, null, null,
                null, Strings.SetupInstallHint, null, null),
            _ => new(
                "command", Strings.SetupNoServerYet,
                RemoteSide.Of(remoteName) != RemoteKind.None ? Strings.SetupCommandLeadRemote
                : offer.Command == ServerInstall.MacCommand ? Strings.SetupCommandLeadMac : Strings.SetupCommandLeadLinux,
                null, null, offer.Command, Strings.SetupCommandShownOnly, Strings.SetupInstallOthers, null, null),
        };
    }

    /// <summary>What the box says while <paramref name="kind"/> runs: an install (winget, its steps, Stop) or a start (it
    /// only waits for the server: nothing to stop, no winget to quote).</summary>
    internal static ServerInstallRunning? RunningFor(ServerInstallKind kind)
    {
        var address = ServerInstall.OllamaAddress;
        return kind switch
        {
            ServerInstallKind.Install => new(
                Strings.SetupInstallingTitle, Strings.SetupInstallingLead,
                [Strings.SetupStepInstall(Gb(ServerInstall.ApproxDownloadGb)), Strings.SetupStepServer(address), Strings.SetupStepModels],
                Strings.SetupWingetSays, Strings.SetupInstallStop, Strings.SetupInstallStopNote),
            ServerInstallKind.Start => new(
                Strings.SetupStartingTitle, Strings.SetupStartingLead(address),
                [Strings.SetupStepServer(address), Strings.SetupStepModels], Strings.SetupWingetSays, null, null),
            _ => null,
        };
    }

    /// <summary>A size in GB the way the setup card writes one.</summary>
    internal static string Gb(double gb) => gb.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>What a terminal setup (<c>Inferpal.Host --setup</c>) says for <paramref name="offer"/>: the command to run
    /// by hand, then to run the setup again. A terminal runs nothing on the person's behalf.</summary>
    internal static IReadOnlyList<string> TerminalLines(ServerInstallOffer offer) => offer.Kind switch
    {
        ServerInstallKind.NoWinget => [Strings.AcpSetupNoWinget],
        ServerInstallKind.Install  => [Strings.AcpSetupInstallLead(Gb(ServerInstall.ApproxDownloadGb)), "", "  " + offer.Command, "", Strings.AcpSetupRunAgain],
        ServerInstallKind.Start    => [Strings.AcpSetupStartLead, "", "  " + offer.Command, "", Strings.AcpSetupRunAgain],
        _                          => [Strings.AcpSetupCommandLead, "", "  " + offer.Command, "", Strings.AcpSetupRunAgain],
    };
}

/// <summary>The card that asks before installing: the exact command, then what it does, then Install and Cancel.</summary>
internal sealed record ServerInstallConsent(
    string Title, string Size, string Runs, string Command, IReadOnlyList<string> Facts, string Confirm, string Cancel);

/// <summary>What the box says while the program runs: its steps in order, the label of winget's last line, and Stop
/// (null when there is nothing to stop: a start only waits).</summary>
internal sealed record ServerInstallRunning(
    string Title, string Lead, IReadOnlyList<string> Steps, string Says, string? Stop, string? StopNote);

/// <summary>
/// How an install or a start ended, for both editors: <see cref="Answers"/> (the first run goes on to the models), or a
/// title, its cause, winget's last lines, and the gesture that may work (<see cref="Retry"/>, <see cref="RetryKind"/>,
/// and what the box says while it runs, <see cref="RetryRunning"/>). ⚠ A retry never turns a start into an install: an
/// install runs only from a card that showed its command.
/// </summary>
internal sealed record ServerInstallReport(
    string Outcome, bool Answers, string? Title, string? Cause, IReadOnlyList<string> LastLines, string? LastLinesLabel,
    string? Retry, string? RetryKind, string? Yourself, ServerInstallRunning? RetryRunning = null)
{
    /// <summary>The card's own answer when the machine no longer offers what it asked for: it looks again.</summary>
    internal static readonly ServerInstallReport Changed = new("changed", false, null, null, [], null, null, null, null);

    /// <summary>The report of <paramref name="result"/>, for the offer that ran, with what its retry says while it runs.</summary>
    /// <param name="startsNow">The machine offers a start now (Ollama found after the run): what may work next is Start.</param>
    internal static ServerInstallReport Of(ServerInstallResult result, ServerInstallOffer offer, bool startsNow)
    {
        var report = OfRun(result, offer, startsNow);
        return report.RetryKind is null ? report : report with
        {
            RetryRunning = ServerInstallCard.RunningFor(report.RetryKind == "start" ? ServerInstallKind.Start : ServerInstallKind.Install),
        };
    }

    private static ServerInstallReport OfRun(ServerInstallResult result, ServerInstallOffer offer, bool startsNow)
    {
        var lines   = result.LastLines ?? [];
        var program = offer.Kind == ServerInstallKind.Start ? "Ollama" : "winget";
        // What may work next: installed but silent → start it (the machine's offer is then Start); anything else → the
        // same gesture again.
        var startable = offer.Kind == ServerInstallKind.Start || startsNow;
        var (retry, retryKind) = result.Outcome switch
        {
            ServerInstallOutcome.Answers or ServerInstallOutcome.Busy => ((string?)null, (string?)null),
            ServerInstallOutcome.NoServer or ServerInstallOutcome.Stopped when startable
                => (Strings.SetupStartButton, "start"),
            _ => (Strings.SetupInstallTryAgain, offer.Kind == ServerInstallKind.Start ? "start" : "install"),
        };
        return result.Outcome switch
        {
            ServerInstallOutcome.Answers => new("answers", true, null, null, [], null, null, null, null),
            ServerInstallOutcome.Failed => new(
                "failed", false, Strings.SetupInstallFailedTitle,
                Strings.SetupInstallFailedCause($"0x{result.ExitCode ?? 0:X8}"),
                lines, lines.Count > 0 ? Strings.SetupInstallLastLines : null, retry, retryKind, Strings.SetupInstallYourself),
            ServerInstallOutcome.NoServer => new(
                "noServer", false, Strings.SetupInstallNoServerTitle,
                Strings.SetupInstallNoServerCause(ServerInstall.OllamaAddress, (int)ServerInstall.ServerWait.TotalSeconds),
                [], null, retry, retryKind, Strings.SetupInstallYourself),
            // Stopped while the server was waited for, winget already done: installed, not answering — no winget stopped.
            ServerInstallOutcome.Stopped when result.ExitCode == 0 => new(
                "noServer", false, Strings.SetupInstallNoServerTitle, Strings.SetupStartLead(ServerInstall.OllamaAddress),
                [], null, retry, retryKind, Strings.SetupInstallYourself),
            ServerInstallOutcome.Stopped => new(
                "stopped", false, Strings.SetupInstallStoppedTitle, Strings.SetupInstallStoppedCause,
                lines, lines.Count > 0 ? Strings.SetupInstallLastLines : null, retry, retryKind, null),
            ServerInstallOutcome.Busy => new("busy", false, null, Strings.SetupInstallBusy, [], null, null, null, null),
            _ => new(
                "notStarted", false, offer.Kind == ServerInstallKind.Start ? Strings.SetupStartTitle : Strings.SetupInstallFailedTitle,
                Strings.SetupInstallNotStarted(program, result.Detail ?? ""), [], null, retry, retryKind, Strings.SetupInstallYourself),
        };
    }
}
