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

    [Fact]
    public void SteadyConnected_NeverReannounces()
    {
        var p = new ConnectionStatusPresenter();
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
        Assert.Equal(ConnectionTransition.None, p.Evaluate(true).Transition);
    }
}
