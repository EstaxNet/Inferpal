using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;

namespace Inferpal.ToolWindow;

/// <summary>
/// The first run's server install, in Visual Studio: when no model server answers, a card in the conversation offers to
/// install Ollama (winget, for this account) or to start an installed one — the command and what it does shown before the
/// click — then its progress, then the first run goes on to the models (<see cref="ServerInstall"/>).
/// </summary>
internal partial class InferpalToolWindowData
{
    /// <summary>The first run offered the card already: it resumes at every start until a model is chosen, and offers it
    /// once per Visual Studio.</summary>
    private int _serverInstallOffered;

    /// <summary>The Stop of the install running now.</summary>
    private CancellationTokenSource? _serverInstallStop;

    /// <summary>
    /// Offers this machine's install or start in the conversation, under <paramref name="lead"/> (what each place answered,
    /// when the first run said nothing else). Nothing when the machine offers nothing Inferpal runs (no winget, not
    /// Windows), or when a card still waits.
    /// </summary>
    /// <param name="once">The automatic first run: once per Visual Studio.</param>
    private async Task OfferServerInstallAsync(string? lead, bool once)
    {
        var offer = ServerInstall.OfferFor(null);
        if (!offer.Runs) return;
        if (once && Interlocked.Exchange(ref _serverInstallOffered, 1) == 1) return;
        var card = ServerInstallCard.For(offer, null);
        await RunOnVMContextAsync(() =>
        {
            if (Messages.Any(m => m.Role == "serverinstall" && !m.IsAnswered)) return;
            if (lead is not null)
            {
                var notice = ChatMessageItem.NoticeMsg(lead);
                ApplyItemTheme(notice);
                Messages.Insert(Messages.Count - 2, notice);
            }
            ChatMessageItem? item = null;
            item = ChatMessageItem.ServerInstallMsg(card,
                onPrimary:   () => RunServerInstallAsync(item!),
                onLookAgain: () => RunSetupDiscoveryAsync(FirstRunPresentAsync, CancellationToken.None),
                onStop:      () => _serverInstallStop?.Cancel());
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });
    }

    /// <summary>
    /// Runs the card's install or start. ⚠ The machine's offer is read again at the click, never taken from the card: an
    /// install runs only from a card that showed its command. Then, a server that answers sends the first run on to the
    /// models; anything else stays on the card with its cause — and is said in the conversation when the card left it (a
    /// new conversation started meanwhile): an ending nobody sees is the silence this card exists to end.
    /// </summary>
    private async Task RunServerInstallAsync(ChatMessageItem card)
    {
        var asked = card.InstallKind == "install" ? ServerInstallKind.Install : ServerInstallKind.Start;
        var offer = ServerInstall.OfferFor(null);
        ServerInstallReport report;
        try
        {
            if (offer.Kind != asked || ServerInstallCard.RunningFor(offer.Kind) is not { } running)
            {
                await RunOnVMContextAsync(() => card.InstallEnded(ServerInstallReport.Changed));
                await RunSetupDiscoveryAsync(FirstRunPresentAsync, CancellationToken.None);
                return;
            }

            using var stop = new CancellationTokenSource();
            _serverInstallStop = stop;
            await RunOnVMContextAsync(() => card.InstallRunning(running));
            ServerInstallResult result;
            try
            {
                result = await ServerInstall.RunAsync(offer, line => Post(() => card.InstallSaid(running.Says, line)), stop.Token);
            }
            finally
            {
                if (ReferenceEquals(_serverInstallStop, stop)) _serverInstallStop = null;
            }
            report = ServerInstallReport.Of(result, offer, startsNow: ServerInstall.OfferFor(null).Kind == ServerInstallKind.Start);
        }
        catch (OperationCanceledException) { report = ServerInstallReport.Changed; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ServerInstall.Run", ex);
            // The offer that ran, never the machine's now: a start card must not come back offering an install.
            report = ServerInstallReport.Of(new ServerInstallResult(ServerInstallOutcome.NotStarted, Detail: ex.Message), offer,
                                            startsNow: false);
        }
        var shown = false;
        await RunOnVMContextAsync(() =>
        {
            card.InstallEnded(report);
            shown = Messages.Contains(card);
            if (shown || report.Answers || report.Cause is null) return;
            var notice = ChatMessageItem.NoticeMsg(string.Join("\n\n",
                new[] { report.Title is null ? null : $"**{report.Title}**", report.Cause }.Where(t => t is not null)));
            ApplyItemTheme(notice);
            Messages.Insert(Messages.Count - 2, notice);
            ScrollToBottom();
        });
        if (report.Answers) await RunSetupDiscoveryAsync(FirstRunPresentAsync, CancellationToken.None);
    }
}
