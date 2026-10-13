using System.ComponentModel;
using System.Diagnostics;
using Inferpal.Services;
using Inferpal.Services.Presentation;

namespace Inferpal.ToolWindow;

/// <summary>The one way the chat opens a web address in the system's browser: the documentation, a link in an answer.</summary>
internal static class WebLink
{
    /// <summary><c>false</c> when <paramref name="url"/> is not a web address, or no browser could be started.</summary>
    /// <remarks>⚠ Web addresses only (<see cref="MarkdownParser.WebUrl"/>): a link in an answer is written by the model, and
    /// the system's application for another scheme — <c>file:</c>, a protocol handler — runs a program. The browser
    /// outlives Visual Studio: never bound to it.</remarks>
    public static bool Open(string url)
    {
        if (MarkdownParser.WebUrl(url) is not { } web) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Diagnostics.Swallow("WebLink.Open", ex);
            return false;
        }
    }
}
