using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A CPU profile already recorded reads as where the time goes (pari 150): functions by self time, then total time, then
/// the hottest paths, each with its unit and share; V8's .cpuprofile, Chrome's trace and speedscope alike.
/// </summary>
public class ProfileTextTests
{
    // V8's shape (node --cpu-prof): main calls work (self 30 ms) and parse (self 10 ms); 1 ms between samples.
    private const string CpuProfile = """
        { "nodes": [
            { "id": 1, "callFrame": { "functionName": "(root)", "url": "", "lineNumber": -1, "columnNumber": -1 }, "children": [2, 5] },
            { "id": 2, "callFrame": { "functionName": "main", "url": "file:///app/server.js", "lineNumber": 9, "columnNumber": 0 }, "children": [3, 4] },
            { "id": 3, "callFrame": { "functionName": "work", "url": "file:///app/server.js", "lineNumber": 19, "columnNumber": 2 } },
            { "id": 4, "callFrame": { "functionName": "parse", "url": "file:///app/parse.js", "lineNumber": 4, "columnNumber": 0 } },
            { "id": 5, "callFrame": { "functionName": "(garbage collector)", "url": "", "lineNumber": -1, "columnNumber": -1 } } ],
          "startTime": 0, "endTime": 50000,
          "samples": [SAMPLES], "timeDeltas": [DELTAS] }
        """;

    private static string Profile()
    {
        var samples = Enumerable.Repeat(3, 30).Concat(Enumerable.Repeat(4, 10)).Concat(Enumerable.Repeat(2, 5)).Concat(Enumerable.Repeat(5, 5)).ToList();
        return CpuProfile.Replace("SAMPLES", string.Join(",", samples), StringComparison.Ordinal)
                         .Replace("DELTAS", string.Join(",", samples.Select(_ => 1000)), StringComparison.Ordinal);
    }

    [Fact]
    public void ACpuProfile_ReadsItsFunctionsBySelfTime_ThenTotal_ThenPaths()
    {
        var text = ProfileText.Summarize("server.cpuprofile", Profile(), 20_000)!;

        Assert.StartsWith("V8 CPU profile: 50.0 ms sampled, 50 samples.", text, StringComparison.Ordinal);
        Assert.Contains("  1. work — server.js:20 — 30.0 ms (60.0%)", text, StringComparison.Ordinal);
        Assert.Contains("  2. parse — parse.js:5 — 10.0 ms (20.0%)", text, StringComparison.Ordinal);
        Assert.Contains("  1. main — server.js:10 — 45.0 ms (90.0%)", text[text.IndexOf("Total time", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("60.0% (30.0 ms)  main › work", text, StringComparison.Ordinal);
        Assert.Contains("Not a function of the program: (garbage collector) 5.0 ms (10.0%).", text, StringComparison.Ordinal);
        var (frame, share) = ProfileText.Hottest(ProfileText.Read("x.cpuprofile", Profile())!)!.Value;
        Assert.Equal(("work", 0.6), (frame.Name, Math.Round(share, 3)));
    }

    [Fact]
    public void AChromeTrace_ReadsTheProfileItsChunksCarry()
    {
        var trace = """
            { "traceEvents": [
              { "name": "Profile", "ph": "P", "pid": 7, "id": "0x1", "args": { "data": { "startTime": 100 } } },
              { "name": "ProfileChunk", "ph": "P", "pid": 7, "id": "0x1", "args": { "data": {
                "cpuProfile": { "nodes": [ { "id": 1, "callFrame": { "functionName": "(root)" } },
                                           { "id": 2, "parent": 1, "callFrame": { "functionName": "render", "url": "https://localhost/app.js", "lineNumber": 41 } } ],
                                "samples": [2, 2, 2] }, "timeDeltas": [1000, 1000, 1000] } } },
              { "name": "ProfileChunk", "ph": "P", "pid": 7, "id": "0x1", "args": { "data": {
                "cpuProfile": { "nodes": [ { "id": 3, "parent": 1, "callFrame": { "functionName": "layout", "url": "https://localhost/app.js", "lineNumber": 80 } } ],
                                "samples": [3, 2] }, "timeDeltas": [1000, 1000] } } } ] }
            """;

        var profile = ProfileText.Read("Trace-20261122.json", trace)!;

        Assert.Equal("Chrome performance trace", profile.Kind);
        Assert.Equal("render", ProfileText.Hottest(profile)!.Value.Frame.Name);
        Assert.Contains("render — app.js:42", ProfileText.Summarize("Trace-20261122.json", trace, 20_000)!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpeedscopeFile_ReadsItsSampledAndEventedProfiles()
    {
        var sampled = """
            { "$schema": "https://www.speedscope.app/file-format-schema.json",
              "shared": { "frames": [ { "name": "main", "file": "app.py", "line": 3 }, { "name": "slow_query", "file": "db.py", "line": 12 } ] },
              "profiles": [ { "type": "sampled", "unit": "milliseconds", "samples": [[0, 1], [0, 1], [0]], "weights": [10, 10, 5] } ] }
            """;
        var evented = """
            { "$schema": "https://www.speedscope.app/file-format-schema.json",
              "shared": { "frames": [ { "name": "main" }, { "name": "resize" } ] },
              "profiles": [ { "type": "evented", "unit": "microseconds", "startValue": 0, "endValue": 100,
                "events": [ { "type": "O", "at": 0, "frame": 0 }, { "type": "O", "at": 10, "frame": 1 },
                            { "type": "C", "at": 90, "frame": 1 }, { "type": "C", "at": 100, "frame": 0 } ] } ] }
            """;

        var (slow, slowShare) = ProfileText.Hottest(ProfileText.Read("run.speedscope.json", sampled)!)!.Value;
        var (resize, resizeShare) = ProfileText.Hottest(ProfileText.Read("ui.speedscope.json", evented)!)!.Value;

        Assert.Equal(("slow_query", 0.8), (slow.Name, Math.Round(slowShare, 3)));
        Assert.Equal(("resize", 0.8), (resize.Name, Math.Round(resizeShare, 3)));
        Assert.Contains("slow_query — db.py:12 — 20.0 ms (80.0%)", ProfileText.Summarize("run.speedscope.json", sampled, 20_000)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnonymousFunction_IsCode_ItsPseudoFramesAreNot()
    {
        var anonymous = Profile().Replace("\"functionName\": \"work\"", "\"functionName\": \"\"", StringComparison.Ordinal);

        var (frame, _) = ProfileText.Hottest(ProfileText.Read("x.cpuprofile", anonymous)!)!.Value;

        Assert.Equal("", frame.Name);
        Assert.Contains("(anonymous) — server.js:20", ProfileText.Summarize("x.cpuprofile", anonymous, 20_000)!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFunctionsOnOneMinifiedLine_StayTwo_ToldApartByTheirColumn()
    {
        var bundle = """
            { "nodes": [ { "id": 1, "callFrame": { "functionName": "(root)" }, "children": [2, 3] },
                { "id": 2, "callFrame": { "functionName": "", "url": "https://localhost/main.abc.js", "lineNumber": 0, "columnNumber": 100 } },
                { "id": 3, "callFrame": { "functionName": "", "url": "https://localhost/main.abc.js", "lineNumber": 0, "columnNumber": 9000 } } ],
              "startTime": 0, "endTime": 4000, "samples": [2, 2, 3, 3], "timeDeltas": [1000, 1000, 1000, 1000] }
            """;

        var text = ProfileText.Summarize("bundle.cpuprofile", bundle, 20_000)!;

        Assert.Contains("(anonymous) — main.abc.js:1 col 101 — 2.0 ms (50.0%)", text, StringComparison.Ordinal);
        Assert.Contains("(anonymous) — main.abc.js:1 col 9001 — 2.0 ms (50.0%)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplesWrittenOutOfOrder_AreTimedInTimeOrder()
    {
        // Sample at 3 ms written before the one at 2 ms (a negative delta): 4 ms of profile, never more.
        var shuffled = """
            { "nodes": [ { "id": 1, "callFrame": { "functionName": "(root)" }, "children": [2] },
                { "id": 2, "callFrame": { "functionName": "work", "url": "a.js", "lineNumber": 0 } } ],
              "startTime": 0, "endTime": 4000, "samples": [2, 2, 2, 2], "timeDeltas": [1000, 2000, -1000, 2000] }
            """;

        var profile = ProfileText.Read("s.cpuprofile", shuffled)!;

        Assert.Equal(4000, profile.Samples.Sum(s => s.Micros));
    }

    [Fact]
    public void AFileOfSeveralProfiles_ReadsTheOneWhoseCodeRanLongest_AndNamesTheOthers()
    {
        var trace = """
            [ { "name": "ProfileChunk", "pid": 1, "id": "0x1", "args": { "data": {
                  "cpuProfile": { "nodes": [ { "id": 1, "callFrame": { "functionName": "(root)" } },
                                             { "id": 2, "parent": 1, "callFrame": { "functionName": "(idle)" } },
                                             { "id": 3, "parent": 1, "callFrame": { "functionName": "onClick" } } ],
                                  "samples": [2, 2, 2, 2, 2, 2, 3] }, "timeDeltas": [1000, 1000, 1000, 1000, 1000, 1000, 1000] } } },
              { "name": "ProfileChunk", "pid": 1, "id": "0x2", "args": { "data": {
                  "cpuProfile": { "nodes": [ { "id": 1, "callFrame": { "functionName": "(root)" } },
                                             { "id": 2, "parent": 1, "callFrame": { "functionName": "decode" } } ],
                                  "samples": [2, 2, 2] }, "timeDeltas": [1000, 1000, 1000] } } } ]
            """;

        var profile = ProfileText.Read("Trace.json", trace)!;

        Assert.Equal("decode", ProfileText.Hottest(profile)!.Value.Frame.Name);
        Assert.Contains("This file holds 2 profiles", ProfileText.Summarize("Trace.json", trace, 20_000)!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpeedscopeProfileOfBytes_IsNoTime()
    {
        var memory = """
            { "$schema": "https://www.speedscope.app/file-format-schema.json", "shared": { "frames": [ { "name": "alloc" } ] },
              "profiles": [ { "type": "sampled", "unit": "bytes", "samples": [[0]], "weights": [1048576] } ] }
            """;

        Assert.Null(ProfileText.Read("heap.speedscope.json", memory));
    }

    [Fact]
    public void ASmallBudget_CutsBetweenLines_AndSaysSo()
    {
        var text = ProfileText.Summarize("server.cpuprofile", Profile(), 300)!;

        Assert.True(text.Length <= 300, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("[cut: read_file with start_line pages the raw JSON]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package.json", """{ "name": "app", "scripts": { "start": "node ." } }""")]
    [InlineData("trace.json", """{ "traceEvents": [ { "name": "Layout", "ph": "X" } ] }""")]
    [InlineData("notes.txt", "nodes samples timeDeltas")]
    public void AnyOtherFile_ReadsAsItsLines(string name, string text) =>
        Assert.Null(ProfileText.Summarize(name, text, 20_000));
}
