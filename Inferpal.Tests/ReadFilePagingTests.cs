using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Every tool result enters the model's context cut to <see cref="AgentOrchestrator.MaxToolResultCharsInContext"/>
/// characters, and <c>read_file</c> took no range. On a file longer than that — a third of the source
/// files of an ordinary repository — the agent saw the beginning, a marker saying how much was cut,
/// and NO way to read the rest: asking again returned the same beginning.
/// </summary>
public class ReadFilePagingTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"inferpal-readpage-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_root, name), content);
        return name;
    }

    private static string Lines(int count)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++) sb.Append("line ").Append(i.ToString("D4")).Append(" of a long source file\n");
        return sb.ToString();
    }

    private Task<string> Read(object args) =>
        new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private static readonly Regex Continue = new(@"start_line=(\d+)");

    [Fact]
    public async Task ALongFile_ComesBackInPages_ThatTheContextCapNeverCuts_AndThatRebuildTheFile()
    {
        var content = Lines(500);                      // ~16,000 characters
        var name    = Write("Long.cs", content);

        var rebuilt = new StringBuilder();
        var start   = 0;
        for (var page = 0; page < 50; page++)
        {
            var answer = await Read(start == 0 ? new { path = name } : new { path = name, start_line = start });
            Assert.Equal(answer, AgentOrchestrator.CapForContext(answer));      // fits: the loop does not cut it

            var footer = answer.LastIndexOf("\n[", StringComparison.Ordinal);
            var next   = Continue.Match(answer);
            if (!next.Success)
            {
                rebuilt.Append(footer >= 0 && answer.EndsWith(']') ? answer[..(footer + 1)] : answer);
                break;
            }
            rebuilt.Append(answer[..(footer + 1)]);
            start = int.Parse(next.Groups[1].Value);
        }

        Assert.Equal(content, rebuilt.ToString());
    }

    [Fact]
    public async Task AShortFile_IsReturnedWhole_WithNothingAdded()
    {
        // Reference arm: two thirds of the files. A footer on every read would be noise.
        var content = Lines(40);
        Assert.Equal(content, await Read(new { path = Write("Short.cs", content) }));
    }

    [Fact]
    public async Task ARangeAsked_IsTheRangeGiven()
    {
        var name   = Write("Range.cs", Lines(500));
        var answer = await Read(new { path = name, start_line = 10, end_line = 12 });

        Assert.StartsWith("line 0010 of a long source file\nline 0011 of a long source file\nline 0012 of a long source file\n", answer);
        Assert.Contains("lines 10–12 of 500", answer);
    }

    [Fact]
    public async Task AStartPastTheEnd_SaysHowLongTheFileIs()
    {
        var answer = await Read(new { path = Write("Small.cs", Lines(40)), start_line = 90 });

        Assert.Contains("40 lines", answer);
    }

    [Fact]
    public async Task SlashRead_StillAttachesTheWholeFile()
    {
        // `/read <path>` goes through the same tool to attach the file as a chip: it must not get a page.
        var content = Lines(500);
        var name    = Write("Whole.cs", content);
        var action  = Assert.IsType<SlashToolAction>(
            SlashCommandRouter.Route("/read " + Path.Combine(_root, name), []));

        var answer = await new ReadFileTool(() => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(action.Args), CancellationToken.None);

        Assert.Equal(content, answer);
    }
}
