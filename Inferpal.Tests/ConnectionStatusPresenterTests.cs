using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// The connection-guard's edge logic (first-check optimism, lost↔restored transitions) lived
// untested inside the VM's heartbeat loop. These pin it. Status TEXT is localized (the suite runs
// under FR culture), so assertions use the culture-independent colours / flags / transition enum.
public class ConnectionStatusPresenterTests
{
    private const string ConnectedColor    = "#4EC94E";
    private const string UnreachableColor   = "#F44747";
    private const string SendActiveColor    = "#7C4DFF";
    private const string SendOfflineColor   = "#555555";

    [Fact]
    public void FirstSuccess_IsSilent_AndShowsConnected()
    {
        var s = new ConnectionStatusPresenter().Evaluate(ok: true);

        Assert.Equal(ConnectedColor, s.StatusColor);
        Assert.Equal(SendActiveColor, s.SendButtonColor);
        Assert.False(s.ShowRetry);
        Assert.Equal(ConnectionTransition.None, s.Transition); // optimistic start → no "reconnected"
    }

    [Fact]
    public void FirstFailure_AnnouncesOutage()
    {
        var s = new ConnectionStatusPresenter().Evaluate(ok: false);

        Assert.Equal(UnreachableColor, s.StatusColor);
        Assert.Equal(SendOfflineColor, s.SendButtonColor);
        Assert.True(s.ShowRetry);
        Assert.Equal(ConnectionTransition.Lost, s.Transition);
    }

    [Fact]
    public void DownThenUp_AnnouncesRestored_Once()
    {
        var p = new ConnectionStatusPresenter();
        p.Evaluate(ok: false);                       // Lost
        var up = p.Evaluate(ok: true);               // edge up
        Assert.Equal(ConnectionTransition.Restored, up.Transition);

        var stillUp = p.Evaluate(ok: true);          // steady state
        Assert.Equal(ConnectionTransition.None, stillUp.Transition);
    }

    [Fact]
    public void UpThenDown_AnnouncesLost_Once()
    {
        var p = new ConnectionStatusPresenter();
        p.Evaluate(ok: true);                        // silent first success
        var down = p.Evaluate(ok: false);            // edge down
        Assert.Equal(ConnectionTransition.Lost, down.Transition);

        var stillDown = p.Evaluate(ok: false);       // steady outage
        Assert.Equal(ConnectionTransition.None, stillDown.Transition);
    }

    /// <summary>
    /// Nothing chosen yet: the first run says what answers where, and "cannot reach Ollama, start it" before it reached
    /// someone with LM Studio, who has no Ollama to start. The badge keeps the state: only the sentence is left to it.
    /// </summary>
    [Fact]
    public void AFirstRunThatSpoke_TakesTheFirstFailedCheck_AndTheBadgeStillSaysIt()
    {
        var p = new ConnectionStatusPresenter();

        var first = p.Evaluate(ok: false, firstRunSpoke: true);

        Assert.Equal(ConnectionTransition.None, first.Transition);
        Assert.Equal(UnreachableColor, first.StatusColor);
        Assert.True(first.ShowRetry);
        Assert.Equal(ConnectionTransition.None, p.Evaluate(ok: false).Transition);
    }

    /// <summary>Only its first check: a server that answered and then dropped is an outage, first run or not.</summary>
    [Fact]
    public void AFirstRunThatSpoke_LeavesLaterEdgesAlone()
    {
        var p = new ConnectionStatusPresenter();
        p.Evaluate(ok: true, firstRunSpoke: true);

        Assert.Equal(ConnectionTransition.Lost, p.Evaluate(ok: false, firstRunSpoke: true).Transition);
    }

    /// <summary>
    /// A server started after the first run spoke was never connected: "Reconnected" under a card that says none runs reads
    /// wrong. It is an ordinary server from then on: its next outage is said, and so is its return.
    /// </summary>
    [Fact]
    public void AServerThatAppearsAfterTheFirstRunSpoke_IsNoReconnection()
    {
        var p = new ConnectionStatusPresenter();
        p.Evaluate(ok: false, firstRunSpoke: true);

        Assert.Equal(ConnectionTransition.None, p.Evaluate(ok: true).Transition);
        Assert.Equal(ConnectionTransition.Lost, p.Evaluate(ok: false).Transition);
        Assert.Equal(ConnectionTransition.Restored, p.Evaluate(ok: true).Transition);
    }

    /// <summary>A server that refused is running: the first run adopts nothing there, so the edge says it.</summary>
    [Fact]
    public void AFirstRunThatSpoke_StillSaysARefusal()
    {
        var s = new ConnectionStatusPresenter().Evaluate(ok: false, refusal: "401", firstRunSpoke: true);

        Assert.Equal(ConnectionTransition.Lost, s.Transition);
    }

    [Fact]
    public void SteadyConnected_NeverReannounces()
    {
        var p = new ConnectionStatusPresenter();
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
    }
}
