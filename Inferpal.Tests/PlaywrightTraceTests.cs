using System.IO;
using System.IO.Compression;
using System.Text;
using Inferpal.Services.Documents;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Playwright trace (pari 170) reads as the test it records: the failure first (its step and line, the message without
/// colours, the page's accessibility snapshot and the test's lines from error-context.md), then the steps, the console, the
/// page's errors and the network; the picture of the page is the last one before the failure. Recognised by what the zip
/// holds, never by its name; what is decompressed is bounded. The lines below are Playwright 1.65's, paths made neutral.
/// </summary>
public class PlaywrightTraceTests
{
    private static string Zip(Dictionary<string, string> textEntries, Dictionary<string, byte[]>? binary = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"inferpal-trace-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in textEntries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(text);
            foreach (var (name, bytes) in binary ?? [])
                using (var s = zip.CreateEntry(name).Open()) s.Write(bytes);
        }
        return path;
    }

    private const string Stack = "\"stack\":[{\"file\":\"C:\\\\work\\\\shop\\\\node_modules\\\\x.js\",\"line\":1},{\"file\":\"C:\\\\work\\\\shop\\\\cart.spec.js\",\"line\":LINE,\"column\":5}]";

    private static string At(int line) => Stack.Replace("LINE", line.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static readonly string RunnerTrace = string.Join('\n',
        "{\"version\":10,\"type\":\"context-options\",\"origin\":\"testRunner\",\"browserName\":\"\",\"playwrightVersion\":\"1.65.1\",\"options\":{},\"platform\":\"linux\",\"wallTime\":1795462252819,\"monotonicTime\":759.0}",
        "{\"type\":\"before\",\"callId\":\"a@1\",\"startTime\":775.0,\"class\":\"Test\",\"method\":\"hook\",\"title\":\"Before Hooks\",\"params\":{},\"stack\":[]}",
        "{\"type\":\"before\",\"callId\":\"a@36\",\"parentId\":\"a@1\",\"startTime\":783.0,\"class\":\"Test\",\"method\":\"fixture\",\"title\":\"Fixture \\\"browser\\\"\",\"params\":{},\"stack\":[]}",
        "{\"type\":\"before\",\"callId\":\"a@37\",\"parentId\":\"a@36\",\"startTime\":784.0,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"Launch browser\",\"params\":{},\"stack\":[]}",
        "{\"type\":\"after\",\"callId\":\"a@37\",\"endTime\":963.0}",
        "{\"type\":\"after\",\"callId\":\"a@36\",\"endTime\":964.0}",
        "{\"type\":\"after\",\"callId\":\"a@1\",\"endTime\":1103.0}",
        "{\"type\":\"before\",\"callId\":\"a@55\",\"startTime\":1104.0,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"Navigate\",\"subtitle\":\"shop.test/\",\"params\":{\"url\":\"http://shop.test/\"}," + At(21) + "}",
        "{\"type\":\"after\",\"callId\":\"a@55\",\"endTime\":1151.0}",
        "{\"type\":\"before\",\"callId\":\"a@59\",\"startTime\":1153.0,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"Click\",\"subtitle\":\"getByRole('button', { name: 'Checkout' })\",\"params\":{}," + At(22) + "}",
        "{\"type\":\"after\",\"callId\":\"a@59\",\"endTime\":1254.0}",
        "{\"type\":\"before\",\"callId\":\"a@63\",\"startTime\":1258.0,\"class\":\"Test\",\"method\":\"expect\",\"title\":\"Expect \\\"toHaveText\\\"\",\"subtitle\":\"locator('#total')\",\"params\":{}," + At(23) + "}",
        "{\"type\":\"after\",\"callId\":\"a@63\",\"endTime\":3267.0,\"error\":{\"name\":\"\",\"message\":\"Error: expect\"}}",
        "{\"type\":\"error\",\"message\":\"Error: \\u001b[2mexpect(\\u001b[22m\\u001b[31mlocator\\u001b[39m\\u001b[2m).\\u001b[22mtoHaveText\\u001b[2m(\\u001b[22m\\u001b[32mexpected\\u001b[39m\\u001b[2m)\\u001b[22m failed\\n\\nLocator:  locator('#total')\\nExpected: \\u001b[32m\\\"108.00\\\"\\u001b[39m\\nReceived: \\u001b[31m\\\"\\\"\\u001b[39m\\n\\nCall log:\\n  - waiting for locator('#total')\"}",
        "{\"type\":\"before\",\"callId\":\"a@80\",\"startTime\":3300.0,\"class\":\"Test\",\"method\":\"test.attach\",\"title\":\"Attach \\\"error-context\\\"\",\"params\":{},\"stack\":[]}",
        "{\"type\":\"after\",\"callId\":\"a@80\",\"endTime\":3301.0,\"attachments\":[{\"name\":\"error-context\",\"contentType\":\"text/markdown\",\"file\":\"attachments/85c9\"}]}");

    private static readonly string LibraryTrace = string.Join('\n',
        "{\"version\":10,\"type\":\"context-options\",\"origin\":\"library\",\"browserName\":\"chromium\",\"playwrightVersion\":\"1.65.1\",\"options\":{},\"platform\":\"linux\",\"wallTime\":1795462253039,\"monotonicTime\":979.0}",
        "{\"type\":\"frame-snapshot\",\"snapshot\":{\"html\":[\"HTML\",{},\"" + new string('x', 300_000) + "\"]}}",
        "{\"type\":\"console\",\"messageType\":\"log\",\"text\":\"cart page ready\",\"location\":{\"url\":\"http://shop.test/\",\"lineNumber\":3},\"time\":1143.0}",
        "{\"type\":\"log\",\"callId\":\"a@63\",\"time\":1261.0,\"message\":\"waiting for locator('#total')\"}",
        "{\"type\":\"console\",\"messageType\":\"error\",\"text\":\"total failed: 500\",\"location\":{\"url\":\"http://shop.test/\",\"lineNumber\":6},\"time\":1268.4}",
        "{\"type\":\"event\",\"time\":1270.0,\"class\":\"BrowserContext\",\"method\":\"pageError\",\"params\":{\"error\":{\"error\":{\"message\":\"cart is undefined\",\"name\":\"Error\"}}}}",
        "{\"type\":\"screencast-frame\",\"pageId\":\"page@1\",\"file\":\"screencast/before.jpeg\",\"width\":1280,\"height\":720,\"timestamp\":1426.0}",
        "{\"type\":\"screencast-frame\",\"pageId\":\"page@1\",\"file\":\"screencast/after.jpeg\",\"width\":1280,\"height\":720,\"timestamp\":3290.0}");

    private const string Network =
        "{\"type\":\"resource-snapshot\",\"snapshot\":{\"startedDateTime\":\"2026-11-23T19:30:53.178Z\",\"time\":6.9,\"request\":{\"method\":\"GET\",\"url\":\"http://shop.test/\"},\"response\":{\"status\":200,\"statusText\":\"OK\"}}}\n"
      + "{\"type\":\"resource-snapshot\",\"snapshot\":{\"startedDateTime\":\"2026-11-23T19:30:53.308Z\",\"time\":5.3,\"request\":{\"method\":\"GET\",\"url\":\"http://shop.test/api/total\"},\"response\":{\"status\":500,\"statusText\":\"Internal Server Error\"}}}\n";

    private const string ErrorContext = """
        # Instructions

        - Following Playwright test failed.
        - Explain why, be concise, respect Playwright best practices.

        # Error details

        ```
        Error: expect(locator).toHaveText(expected) failed
        ```

        ```yaml
        - heading "Cart" [level=1]
        - button "Checkout"
        - text: ""
        ```

        # Test source

        ```ts
          21 |   await page.goto('http://shop.test/');
          22 |   await page.getByRole('button', { name: 'Checkout' }).click();
        > 23 |   await expect(page.locator('#total')).toHaveText('108.00');
             |                                        ^ Error: expect(locator).toHaveText(expected) failed
          24 | });
        ```
        """;

    private static string RunnerZip() => Zip(
        new() { ["test.trace"] = RunnerTrace, ["0-trace.trace"] = "{\"version\":10,\"type\":\"context-options\",\"origin\":\"library\",\"title\":\"cart.spec.js:20 \u203a checkout shows the total\"}",
                ["1-trace.trace"] = LibraryTrace, ["1-trace.network"] = Network, ["attachments/85c9"] = ErrorContext },
        new() { ["screencast/before.jpeg"] = [1, 2, 3], ["screencast/after.jpeg"] = [4, 5, 6] });

    [Fact]
    public void ATestRunnersTrace_ReadsAsItsFailure_ThenStepsConsoleErrorsAndNetwork()
    {
        var path = RunnerZip();
        try
        {
            Assert.True(PlaywrightTrace.IsTrace(path));
            var reading = PlaywrightTrace.Read(path, CancellationToken.None);
            var text = reading.Text;

            Assert.StartsWith("[Playwright trace: cart.spec.js:20 › checkout shows the total — failed (Playwright 1.65.1, chromium, linux).", text);
            Assert.Contains("A picture of the page at +0.651 s (the last before the failure) is in the trace.", text);
            Assert.Equal(new byte[] { 1, 2, 3 }, reading.Frame);   // the frame before the failure ended, not the one after

            var failure = text[text.IndexOf("## Failure", StringComparison.Ordinal)..text.IndexOf("## Steps", StringComparison.Ordinal)];
            Assert.Contains("Expect \"toHaveText\" locator('#total')  (cart.spec.js:23), at +0.483 s", failure);
            Assert.Contains("Error: expect(locator).toHaveText(expected) failed\n\nLocator:  locator('#total')\nExpected: \"108.00\"", failure);
            Assert.DoesNotContain('\u001b', text);   // a char: a string is compared by culture, where it is ignorable
            Assert.DoesNotContain("Call log, its end", failure);   // the runner's message carries it already
            Assert.Contains("  - button \"Checkout\"", failure);
            Assert.Contains("  > 23 |   await expect(page.locator('#total')).toHaveText('108.00');", failure);
            Assert.DoesNotContain("Explain why", text);            // its instructions to a model are not passed on

            Assert.Contains("+0.000 s  Before Hooks  328 ms", text);
            Assert.DoesNotContain("Launch browser", text);         // a fixture's inner steps fold into it
            Assert.Contains("+0.329 s  Navigate shop.test/  47 ms  (cart.spec.js:21)", text);
            Assert.Contains("+0.483 s  ✗ Expect \"toHaveText\" locator('#total')  2.01 s  (cart.spec.js:23)  — failed", text);
            Assert.Contains("+0.368 s  log  cart page ready  (http://shop.test/:4)", text);
            Assert.Contains("+0.493 s  error  total failed: 500  (http://shop.test/:7)", text);
            Assert.Contains("## Errors in the page\n+0.495 s  cart is undefined", text);
            Assert.Contains("GET http://shop.test/api/total → 500 Internal Server Error (5 ms)  ← failed", text);
            Assert.Contains("GET http://shop.test/ → 200 OK (7 ms)\n", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ALibrarysTrace_ReadsItsCalls_AndTheEndOfTheFailingCallsLog()
    {
        var trace = string.Join('\n',
            "{\"version\":10,\"type\":\"context-options\",\"origin\":\"library\",\"browserName\":\"chromium\",\"playwrightVersion\":\"1.65.1\",\"wallTime\":1795462253039,\"monotonicTime\":500.0}",
            "{\"type\":\"before\",\"callId\":\"c@1\",\"startTime\":500.0,\"class\":\"Frame\",\"method\":\"goto\",\"params\":{\"url\":\"http://shop.test/\"}}",
            "{\"type\":\"after\",\"callId\":\"c@1\",\"endTime\":533.0}",
            "{\"type\":\"before\",\"callId\":\"c@2\",\"startTime\":540.0,\"class\":\"Frame\",\"method\":\"click\",\"params\":{\"selector\":\"#pay\"}}",
            "{\"type\":\"log\",\"callId\":\"c@2\",\"time\":541.0,\"message\":\"waiting for locator('#pay')\"}",
            "{\"type\":\"log\",\"callId\":\"c@2\",\"time\":542.0,\"message\":\"  locator resolved to <button disabled>\"}",
            "{\"type\":\"log\",\"callId\":\"c@2\",\"time\":543.0,\"message\":\"  element is not enabled\"}",
            "{\"type\":\"log\",\"callId\":\"c@2\",\"time\":544.0,\"message\":\"  locator resolved to <button disabled>\"}",
            "{\"type\":\"log\",\"callId\":\"c@2\",\"time\":545.0,\"message\":\"  element is not enabled\"}",
            "{\"type\":\"after\",\"callId\":\"c@2\",\"endTime\":1550.0,\"error\":{\"message\":\"Timeout 1000ms exceeded.\"}}");
        var path = Zip(new() { ["trace.trace"] = trace, ["trace.network"] = string.Empty });
        try
        {
            var text = PlaywrightTrace.Read(path, CancellationToken.None).Text;
            Assert.StartsWith("[Playwright trace — failed (Playwright 1.65.1, chromium).", text);
            Assert.Contains("Frame.click #pay, at +0.040 s\nTimeout 1000ms exceeded.\nCall log, its end:\n"
                          + "  waiting for locator('#pay')\n  locator resolved to <button disabled>\n"
                          + "  element is not enabled  (these two lines 2 times)\n", text);
            Assert.Contains("## Calls\n+0.000 s  Frame.goto http://shop.test/  33 ms\n+0.040 s  ✗ Frame.click #pay  1.01 s  — failed", text);
            Assert.Contains("## Network\nnone", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnotherZip_IsNoTrace_WhateverItsName()
    {
        var other = Zip(new() { ["trace.json"] = "{}", ["readme.txt"] = "hello" });
        var renamed = Path.Combine(Path.GetDirectoryName(other)!, $"trace-{Guid.NewGuid():N}.zip");
        File.Move(other, renamed);
        var notAZip = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.zip");
        File.WriteAllText(notAZip, "not a zip");
        try
        {
            Assert.False(PlaywrightTrace.IsTrace(renamed));
            Assert.False(PlaywrightTrace.IsTrace(notAZip));
        }
        finally { File.Delete(renamed); File.Delete(notAZip); }
    }

    [Fact]
    public async Task AnAttachedTrace_IsItsText()
    {
        var path = RunnerZip();
        try
        {
            Assert.Null(MentionController.BinaryRefusal(path));
            var text = await MentionController.FileMentionTextAsync(path, null, CancellationToken.None);
            Assert.StartsWith("[Playwright trace: cart.spec.js:20", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WhatIsDecompressed_IsBounded_AndTheStopSaid()
    {
        // A line of zeros that inflates past the bound: reading stops, what was read is said.
        var path = Path.Combine(Path.GetTempPath(), $"inferpal-bomb-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("trace.trace").Open()))
                w.Write("{\"version\":10,\"type\":\"context-options\",\"playwrightVersion\":\"1.65.1\"}\n");
            using var s = zip.CreateEntry("trace.network", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i <= PlaywrightTrace.MaxRead / zeros.Length; i++) s.Write(zeros);
        }
        try
        {
            Assert.True(new FileInfo(path).Length < 1024 * 1024);   // witness: small on disk
            var text = PlaywrightTrace.Read(path, CancellationToken.None).Text;
            Assert.Contains("Reading stopped after 64 MB decompressed in trace.network", text);
            Assert.Contains("(Playwright 1.65.1)", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StepsThatNameEachOtherTheirParent_DoNotHangTheReading()
    {
        var path = Zip(new()
        {
            ["test.trace"] = string.Join('\n',
                "{\"type\":\"before\",\"callId\":\"a\",\"parentId\":\"b\",\"startTime\":1,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"A\"}",
                "{\"type\":\"before\",\"callId\":\"b\",\"parentId\":\"a\",\"startTime\":2,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"B\"}",
                "{\"type\":\"before\",\"callId\":\"c\",\"parentId\":\"c\",\"startTime\":3,\"class\":\"Test\",\"method\":\"pw:api\",\"title\":\"C\"}",
                "{\"type\":\"after\",\"callId\":\"c\",\"endTime\":4,\"error\":{\"message\":\"boom\"}}"),
        });
        try
        {
            var read = Task.Run(() => PlaywrightTrace.Read(path, CancellationToken.None));
            Assert.True(read.Wait(TimeSpan.FromSeconds(30)));   // a loop would never return
            Assert.Contains("✗ C", read.Result.Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADuplicatedEntry_OrADamagedOne_IsNoFailureOfTheWholeReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inferpal-trace-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var text in new[] { "{\"version\":10,\"type\":\"context-options\",\"playwrightVersion\":\"1.65.1\"}", "{}" })
                using (var w = new StreamWriter(zip.CreateEntry("trace.trace").Open())) w.Write(text);   // twice: the first is read
            using (var w = new StreamWriter(zip.CreateEntry("trace.network").Open())) w.Write(Network);
        }
        try
        {
            var text = PlaywrightTrace.Read(path, CancellationToken.None).Text;
            Assert.Contains("(Playwright 1.65.1)", text);
            Assert.Contains("GET http://shop.test/api/total → 500", text);
        }
        finally { File.Delete(path); }

        // A damaged entry: what came before it is read, and the damage said.
        var damaged = Zip(new() { ["trace.trace"] = "{\"version\":10,\"type\":\"context-options\",\"playwrightVersion\":\"1.65.1\"}", ["trace.network"] = Network });
        var bytes = File.ReadAllBytes(damaged);
        var at = Encoding.ASCII.GetBytes("trace.network");
        var local = bytes.AsSpan().IndexOf(at);              // the network entry's local header
        for (var i = local + at.Length + 4; i < local + at.Length + 40; i++) bytes[i] ^= 0x5A;   // its deflated data
        File.WriteAllBytes(damaged, bytes);
        try
        {
            var text = PlaywrightTrace.Read(damaged, CancellationToken.None).Text;
            Assert.Contains("(Playwright 1.65.1)", text);
            Assert.Contains("Damaged, not read whole: trace.network.", text);
            Assert.Contains("## Network\nnone read (reading stopped: see the first line)", text);
        }
        finally { File.Delete(damaged); }
    }

    [Fact]
    public void TheNetwork_IsReadWhateverTheSnapshotsSpent_AndAMissingOneIsNotNone()
    {
        // Snapshots that alone would fill the budget: the network has its own.
        var snapshots = new StringBuilder("{\"version\":10,\"type\":\"context-options\",\"playwrightVersion\":\"1.65.1\"}\n");
        var line = "{\"type\":\"frame-snapshot\",\"snapshot\":\"" + new string('x', 200_000) + "\"}\n";
        while (snapshots.Length < PlaywrightTrace.MaxRead + 1_000_000) snapshots.Append(line);
        var path = Zip(new() { ["trace.trace"] = snapshots.ToString(), ["trace.network"] = Network });
        try
        {
            var text = PlaywrightTrace.Read(path, CancellationToken.None).Text;
            Assert.Contains("Reading stopped after 64 MB decompressed in trace.trace", text);
            Assert.Contains("GET http://shop.test/api/total → 500", text);
            Assert.Contains("## Console\nnone read (reading stopped: see the first line)", text);
        }
        finally { File.Delete(path); }

        var noNetwork = Zip(new() { ["trace.trace"] = "{\"version\":10,\"type\":\"context-options\",\"playwrightVersion\":\"1.65.1\"}" });
        try { Assert.Contains("## Network\nnot recorded in this trace", PlaywrightTrace.Read(noNetwork, CancellationToken.None).Text); }
        finally { File.Delete(noNetwork); }
    }

    [Fact]
    public void ARequestWithoutAStart_HasNoTime_NeverANonsenseOne()
    {
        var path = Zip(new()
        {
            ["trace.trace"] = "{\"version\":10,\"type\":\"context-options\",\"wallTime\":1795462253039,\"monotonicTime\":500.0}",
            ["trace.network"] = "{\"type\":\"resource-snapshot\",\"snapshot\":{\"request\":{\"method\":\"GET\",\"url\":\"http://x/\"},\"response\":{\"status\":200}}}",
        });
        try { Assert.Contains("(no time)  GET http://x/ → 200", PlaywrightTrace.Read(path, CancellationToken.None).Text); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARepeatedLine_OrPairOfLines_IsOneWithItsCount()
    {
        Assert.Equal(["a  (3 times)", "b"], PlaywrightTrace.Collapse(["a", "a", "a", "b"]));
        Assert.Equal(["x", "y  (these two lines 3 times)", "z"], PlaywrightTrace.Collapse(["x", "y", "x", "y", "x", "y", "z"]));
    }
}
