using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>max_chars</c> went up to 50 000 while the agent loop cuts every result to
/// <see cref="AgentOrchestrator.MaxToolResultCharsInContext"/> before the model reads it: the middle of a
/// long documentation page was lost whatever was asked, and no argument could reach it — the shape
/// <c>read_file</c> had before it learnt to page.
/// </summary>
public class FetchUrlWindowTests
{
    private static readonly Regex Next = new(@"start_char=(\d+)");

    private static string Page(int words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++) sb.Append("word").Append(i).Append(i % 12 == 11 ? '\n' : ' ');
        return sb.ToString();
    }

    [Fact]
    public void ALongPage_ComesBackInWindows_ThatTheContextCapNeverCuts_AndThatRebuildThePage()
    {
        var text  = Page(4000);                                  // ~30,000 characters
        var built = new StringBuilder();
        var start = 0;
        for (var i = 0; i < 20; i++)
        {
            var answer = FetchUrlTool.Window(text, start, FetchUrlTool.MaxWindow);
            Assert.Equal(answer, AgentOrchestrator.CapForContext(answer));   // reaches the model whole

            var footer = answer.LastIndexOf("\n\n[... characters ", StringComparison.Ordinal);
            Assert.True(footer >= 0, "every window of a long page says where it stands");
            built.Append(answer[..footer]);

            var next = Next.Match(answer);
            if (!next.Success) break;
            start = int.Parse(next.Groups[1].Value);
        }

        Assert.Equal(text, built.ToString());
    }

    [Fact]
    public void AShortPage_IsReturnedWhole_WithNothingAdded()
    {
        // Reference arm.
        var text = Page(200);
        Assert.Equal(text, FetchUrlTool.Window(text, 0, FetchUrlTool.MaxWindow));
    }

    [Fact]
    public void AStartPastTheEnd_SaysHowLongThePageIs()
    {
        Assert.Contains("1200 characters", FetchUrlTool.Window(new string('a', 1200), 5000, FetchUrlTool.MaxWindow));
    }
}
