using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A log read by level and time (<c>read_file</c> with <c>level</c>, <c>since</c>, <c>until</c>): an entry keeps the lines
/// under it (a stack trace), each line comes under its number in the file, and what the filter could not judge is said —
/// never dropped in silence, which would let the model conclude an error is absent.
/// </summary>
public class ReadLogFilterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("inferpal-logfilter-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { }
    }

    private Task<string> Read(string name, string content, object args)
    {
        File.WriteAllText(Path.Combine(_root.FullName, name), content);
        return new ReadFileTool(() => _root.FullName).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    private const string Serilog =
        "[10:00:01 INF] Starting\n" +
        "[10:00:02 WRN] Cache cold\n" +
        "[10:15:00 ERR] Order failed\n" +
        "System.Collections.Generic.KeyNotFoundException: The given key 'kettle' was not present\n" +
        "   at Shop.Program.PriceOf(String item)\n" +
        "[10:30:00 INF] Retried\n" +
        "[11:00:00 FTL] Gave up\n";

    [Fact]
    public async Task ALevel_KeepsItsEntries_WithTheirStackTraces_UnderTheirLineNumbers()
    {
        var read = await Read("app.log", Serilog, new { path = "app.log", level = "error" });

        Assert.Contains("3: [10:15:00 ERR] Order failed", read);
        Assert.Contains("4: System.Collections.Generic.KeyNotFoundException", read);
        Assert.Contains("5:    at Shop.Program.PriceOf(String item)", read);
        Assert.Contains("7: [11:00:00 FTL] Gave up", read);
        Assert.DoesNotContain("Starting", read);
        Assert.DoesNotContain("Retried", read);
        Assert.Contains("2 of 5 entries match (level error or above)", read);
    }

    [Fact]
    public async Task ATimeRange_KeepsTheEntriesBetween()
    {
        var read = await Read("app.log", Serilog, new { path = "app.log", since = "10:10", until = "10:30" });

        Assert.Contains("Order failed", read);
        Assert.Contains("Retried", read);
        Assert.DoesNotContain("Cache cold", read);
        Assert.DoesNotContain("Gave up", read);
    }

    [Fact]
    public async Task DatedEntries_AreComparedWithTheirDate()
    {
        const string nlog =
            "2026-11-16 23:59:00.1234|ERROR|Shop|Yesterday's failure\n" +
            "2026-11-17 00:01:00.0000|ERROR|Shop|Today's failure\n";
        var read = await Read("nlog.txt", nlog, new { path = "nlog.txt", level = "error", since = "2026-11-17 00:00" });

        Assert.Contains("Today's failure", read);
        Assert.DoesNotContain("Yesterday's failure", read);
    }

    [Fact]
    public async Task TheILoggerConsoleFormat_IsRead()
    {
        const string console =
            "info: Shop.Worker[0]\n" +
            "      Started\n" +
            "fail: Shop.Worker[0]\n" +
            "      Order failed\n";
        var read = await Read("console.log", console, new { path = "console.log", level = "error" });

        Assert.Contains("3: fail: Shop.Worker[0]", read);
        Assert.Contains("4:       Order failed", read);
        Assert.DoesNotContain("Started", read);
    }

    [Fact]
    public async Task WhatTheFilterCouldNotJudge_IsSaid()
    {
        const string mixed =
            "Log opened by the service\n" +
            "10:00:00 something with no level\n" +
            "[10:00:01 ERR] Failed\n";
        var read = await Read("mixed.log", mixed, new { path = "mixed.log", level = "error" });

        Assert.Contains("Failed", read);
        Assert.Contains("1 line(s) before the first entry not judged", read);
        Assert.Contains("1 entry with no level left out", read);
    }

    [Fact]
    public async Task AFileWithNoEntry_SaysTheFormatWasNotRecognised()
    {
        var read = await Read("notes.txt", "just\nsome\ntext\n", new { path = "notes.txt", level = "error" });

        Assert.Contains("no log entry recognised", read);
    }

    [Fact]
    public async Task AnUnknownLevel_OrAnUnreadableTime_IsRefusedBeforeAnythingIsRead()
    {
        var level = await Read("app.log", Serilog, new { path = "app.log", level = "severe-ish" });
        var time = await Read("app.log", Serilog, new { path = "app.log", since = "yesterday" });

        Assert.Contains("Unknown level 'severe-ish'", level);
        Assert.Contains("trace, debug, info, warn, error, fatal", level);
        Assert.Contains("'yesterday' is not a time since can take", time);
    }

    [Fact]
    public async Task AStartInsideAnEntry_IsJudgedFromItsHeader_SoTheRestOfItsStackTraceIsKept()
    {
        var read = await Read("app.log", Serilog, new { path = "app.log", level = "error", start_line = 5 });

        Assert.Contains("5:    at Shop.Program.PriceOf(String item)", read);
        Assert.DoesNotContain("3: [10:15:00 ERR]", read);
        Assert.DoesNotContain("before the first entry", read);
        Assert.Contains("7: [11:00:00 FTL] Gave up", read);
    }

    [Fact]
    public async Task APage_IsCutBetweenTwoEntries_AndTheNextOneTakesTheWholeEntry()
    {
        var trace = string.Concat(Enumerable.Range(0, 40).Select(i => $"   at Shop.Frame{i:D2}() in Program.cs:line {i}\n"));
        var sb = new StringBuilder();
        for (var e = 0; e < 60; e++) sb.Append($"[10:{e:00}:00 ERR] failure {e:D2}\n").Append(trace);
        var log = sb.ToString();

        var first = await Read("traces.log", log, new { path = "traces.log", level = "error" });
        var next = int.Parse(Regex.Match(first, @"start_line=(\d+)").Groups[1].Value);
        var second = await Read("traces.log", log, new { path = "traces.log", level = "error", start_line = next });

        Assert.Matches($@"^{next}: \[10:\d\d:00 ERR\] failure", second);
        Assert.EndsWith($"   at Shop.Frame39() in Program.cs:line 39", first.Split("\n[traces.log")[0].TrimEnd('\n'));
    }

    [Fact]
    public async Task EndLine_IsHonoured()
    {
        var read = await Read("app.log", Serilog, new { path = "app.log", level = "error", end_line = 5 });

        Assert.Contains("Order failed", read);
        Assert.DoesNotContain("Gave up", read);
        Assert.Contains("line 5 reached", read);
    }

    [Fact]
    public async Task TimesThatGoBackPastMidnight_AreSaid()
    {
        const string night = "[23:59:58 INF] late\n[00:00:03 ERR] boom\n";
        var read = await Read("night.log", night, new { path = "night.log", since = "23:00" });

        Assert.Contains("go back past midnight at line 2", read);
    }

    [Fact]
    public async Task GosStandardLog_IsRead_AndUntilAMinute_TakesTheWholeMinute()
    {
        const string go = "2026/11/17 10:30:45 ERROR payment refused\n2026/11/17 10:31:10 ERROR later\n";
        var read = await Read("go.log", go, new { path = "go.log", until = "2026-11-17 10:30" });

        Assert.Contains("1: 2026/11/17 10:30:45 ERROR payment refused", read);
        Assert.DoesNotContain("later", read);
    }

    [Fact]
    public async Task ALineLargerThanAPage_IsCut_AndSaid()
    {
        var read = await Read("huge.log", "[10:00:00 ERR] " + new string('x', ReadFileTool.PageChars * 2) + "\n[10:00:01 ERR] next\n",
                              new { path = "huge.log", level = "error" });

        Assert.True(read.Length <= ReadFileTool.PageChars + 600, $"{read.Length} characters");
        Assert.Contains("line 1 cut", read);
        Assert.Contains("start_line=2", read);
    }

    [Fact]
    public async Task AnEmptyFilter_IsThePlainRead()
    {
        var read = await Read("app.log", Serilog, new { path = "app.log", level = "" });

        Assert.StartsWith("[10:00:01 INF] Starting", read);
    }

    [Fact]
    public async Task ALongSelection_IsPaged_AndTheNextPageGoesOn()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 4000; i++) sb.Append($"[10:{i / 60 % 60:00}:{i % 60:00} ERR] failure number {i:D4} with some padding to make the line long enough\n");
        var log = sb.ToString();

        var first = await Read("big.log", log, new { path = "big.log", level = "error" });
        var next = int.Parse(Regex.Match(first, @"start_line=(\d+)").Groups[1].Value);
        var second = await Read("big.log", log, new { path = "big.log", level = "error", start_line = next });

        Assert.True(first.Length <= ReadFileTool.PageChars + 400, $"page of {first.Length} characters");
        Assert.Contains("failure number 0000", first);
        Assert.Contains($"{next}: [", second);
        Assert.DoesNotContain("failure number 0000", second);
    }
}
