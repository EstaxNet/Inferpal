using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Off Windows, a page or a file is opened by the opener program with streams of its own: by shell execute, the opener
/// and the browser it launches inherit the ACP agent's stdin and stdout — its protocol pipes.
/// </summary>
public class SystemOpenerTests
{
    [Fact]
    public void ThePageIsOpened_WithoutLendingThisProcesssStreams()
    {
        var start = SystemOpener.StartInfoFor("https://example.com/");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(start.UseShellExecute);   // Windows' shell execute inherits nothing
            return;
        }
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(OperatingSystem.IsMacOS() ? "open" : "xdg-open", start.FileName);
        Assert.Equal(["https://example.com/"], start.ArgumentList);
    }
}
