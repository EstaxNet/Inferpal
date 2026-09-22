using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When the chat webview itself throws, the user is told — a log line is not a message.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>clientError</c> is the channel the webview uses to report its OWN crashes: it is posted from
/// <c>window.onerror</c>, from <c>unhandledrejection</c>, and when a resource fails to load. It is
/// the one channel whose entire purpose is to report failure — and it only reached
/// <c>this.log(…)</c>, the output channel.
/// </para>
/// <para>
/// ⭐ The rule is written in that very file, for its neighbours: <i>a bare
/// <c>if (!host?.isRunning) return;</c> is a button that does nothing, and a line in the output
/// channel is not a message</i>. An unhandled error in the chat view leaves a UI that has quietly
/// stopped updating — from the outside, a button that does nothing, which is exactly what the rule
/// says is indistinguishable from a button that did something.
/// </para>
/// <para>
/// ⚠ Said ONCE, and re-armed on <c>ready</c> — a fresh webview. A render that throws on every
/// message would otherwise turn the notice into the noise nobody reads, and "once" without a
/// re-arm would become "once in the lifetime of the window".
/// </para>
/// <para>
/// ⚠ Not executable from this suite: the path lives in the VS Code extension host. So this is a
/// source scan, and it carries its witnesses — the file, the channel, and the handler must all
/// still be found, or "no offender" would mean "nothing was read".
/// </para>
/// </remarks>
public sealed class WebviewCrashSilenceTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Inferpal.sln not found above the test assembly.");
        return dir!;
    }

    private static string ChatViewProvider() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts"));

    /// <summary>The <c>case 'clientError':</c> arm, up to its <c>return</c>.</summary>
    private static string ClientErrorArm(string source)
    {
        var start = source.IndexOf("case 'clientError':", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "chatViewProvider.ts no longer handles 'clientError': the webview's crash channel has "
            + "been renamed or removed, and this test is reading nothing.");

        var end = source.IndexOf("return;", start, StringComparison.Ordinal);
        Assert.True(end > start, "the 'clientError' arm no longer ends in a return: shape changed.");
        return source[start..end];
    }

    [Fact]
    public void TheWebviewCrashChannel_SaysSomething_NotJustLogs()
    {
        var arm = ClientErrorArm(ChatViewProvider());

        // WITNESS: the arm really is the one that logs — so "it also speaks" is an addition, not a
        // test that wandered into some other branch.
        Assert.Contains("this.log(", arm, StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(arm, @"sayOnce\s*\("),
            "the webview's crash channel only writes to the output channel. A log line is not a "
            + "message: an unhandled error in the chat view leaves a UI that silently stopped "
            + "updating, and the user sees a button that does nothing.");
    }

    [Fact]
    public void ItIsSaidOnce_AndReArmedWhenTheWebviewLoadsAgain()
    {
        var source = ChatViewProvider();
        var arm    = ClientErrorArm(source);

        var channel = Regex.Match(arm, @"sayOnce\s*\(\s*'([^']+)'");
        Assert.True(channel.Success, "the notice no longer goes through sayOnce with a named channel.");

        // ⚠ Without the re-arm, "once" becomes "once in the lifetime of the view": a webview that
        // reloads and breaks again would never say so. `ready` is the signal that it is fresh.
        var readyArm = source[source.IndexOf("case 'ready':", StringComparison.Ordinal)..];
        readyArm = readyArm[..readyArm.IndexOf("return;", StringComparison.Ordinal)];

        Assert.Contains($"saidOnce.delete('{channel.Groups[1].Value}')", readyArm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSentence_NamesTheViewAndAGesture()
    {
        // REFERENCE ARM for the sentence itself: a notice that says only "an error occurred" sends
        // the reader nowhere. It must name what broke and something they can do.
        var arm = ClientErrorArm(ChatViewProvider());
        var text = Regex.Match(arm, @"t\(\s*'([^']+)'");

        Assert.True(text.Success, "the notice is not localised through t().");
        Assert.Contains("chat view", text.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reload", text.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }
}
