using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A structured log — a JSON object or a logfmt line per record — reads like a text log (pari 180): <c>level</c>, <c>since</c>
/// and <c>until</c> filter its records, each shown as its time, level, message and exception; a message that repeats on the
/// page is shown once and counted. Read without a filter, the file stays its text, a line above it naming the filters.
/// </summary>
public class StructuredLogTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("inferpal-structuredlog-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { }
    }

    private Task<string> Read(string name, string content, object args)
    {
        File.WriteAllText(Path.Combine(_root.FullName, name), content);
        return new ReadFileTool(() => _root.FullName).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    private const string Socket = "System.Net.Sockets.SocketException (10061): Connection refused\\n   at Shop.Db.Open()";

    // Serilog's compact format: no @l is Information, @mt is the template, @x the exception.
    private const string Clef =
        "{\"@t\":\"2026-11-17T10:00:01.1230000Z\",\"@mt\":\"Starting {App}\",\"App\":\"Shop\"}\n" +
        "{\"@t\":\"2026-11-17T10:15:00.0000000Z\",\"@mt\":\"Connection refused to {host}\",\"@l\":\"Error\",\"host\":\"db1\",\"@x\":\"" + Socket + "\"}\n" +
        "{\"@t\":\"2026-11-17T10:15:01.0000000Z\",\"@mt\":\"Connection refused to {host}\",\"@l\":\"Error\",\"host\":\"db2\",\"@x\":\"" + Socket + "\"}\n" +
        "{\"@t\":\"2026-11-17T10:15:02.0000000Z\",\"@mt\":\"Connection refused to {host}\",\"@l\":\"Error\",\"host\":\"db3\",\"@x\":\"" + Socket + "\"}\n" +
        "{\"@t\":\"2026-11-17T10:30:00.0000000Z\",\"@mt\":\"Order {Id} failed\",\"@l\":\"Error\",\"Id\":42,\"@x\":\"System.InvalidOperationException: Stock is empty\\n   at Shop.Orders.Place()\"}\n" +
        "{\"@t\":\"2026-11-17T11:00:00.0000000Z\",\"@mt\":\"Retried\",\"@l\":\"Warning\"}\n";

    [Fact]
    public async Task ClefRecords_ReadByLevel_AsTimeLevelMessageAndException_ARepeatedMessageShownOnce()
    {
        var read = await Read("app.clef", Clef, new { path = "app.clef", level = "error" });

        Assert.Contains("2: 2026-11-17T10:15:00.0000000Z ERROR Connection refused to db1", read);
        Assert.Contains("    System.Net.Sockets.SocketException (10061): Connection refused", read);
        Assert.Contains("5: 2026-11-17T10:30:00.0000000Z ERROR Order 42 failed", read);
        Assert.Contains("    System.InvalidOperationException: Stock is empty", read);
        Assert.DoesNotContain("db2", read);   // the same message again: counted, not shown
        Assert.Contains("3 × \"Connection refused to {host}\" (shown at line 2, repeated up to line 4)", read);
        Assert.Contains("4 of 6 entries match (level error or above)", read);
        Assert.DoesNotContain("Starting", read);
        Assert.DoesNotContain("Retried", read);
    }

    [Fact]
    public async Task AClefRecordWithNoLevel_IsInformation_AndATimeRangeKeepsTheRecordsBetween()
    {
        var info = await Read("app.clef", Clef, new { path = "app.clef", level = "info" });
        var range = await Read("app.clef", Clef, new { path = "app.clef", since = "10:20", until = "11:00" });

        Assert.Contains("1: 2026-11-17T10:00:01.1230000Z INFO Starting Shop", info);
        Assert.Contains("Order 42 failed", range);
        Assert.Contains("WARN Retried", range);
        Assert.DoesNotContain("Connection refused", range);
        Assert.DoesNotContain("Starting", range);
    }

    [Fact]
    public async Task TheSameTemplateWithAnotherException_IsNotFoldedIntoIt()
    {
        var log =
            "{\"@t\":\"2026-11-17T10:15:00Z\",\"@mt\":\"Call to {api} failed\",\"@l\":\"Error\",\"api\":\"a\",\"@x\":\"System.TimeoutException: slow\"}\n" +
            "{\"@t\":\"2026-11-17T10:15:01Z\",\"@mt\":\"Call to {api} failed\",\"@l\":\"Error\",\"api\":\"b\",\"@x\":\"System.UnauthorizedAccessException: denied\"}\n";

        var read = await Read("app.clef", log, new { path = "app.clef", level = "error" });

        Assert.Contains("System.TimeoutException: slow", read);
        Assert.Contains("System.UnauthorizedAccessException: denied", read);
        Assert.DoesNotContain("each shown once", read);
    }

    [Fact]
    public async Task PinoRecords_TimedByAnEpochNumber_AreReadAsUtc_AndSaidSo()
    {
        var log =
            "{\"level\":30,\"time\":1794909601000,\"pid\":7,\"hostname\":\"h\",\"msg\":\"server listening\"}\n" +
            "{\"level\":50,\"time\":1794910500000,\"pid\":7,\"hostname\":\"h\",\"msg\":\"request failed\",\"err\":{\"type\":\"Error\",\"message\":\"ECONNREFUSED\",\"stack\":\"Error: ECONNREFUSED\\n    at TCPConnectWrap.afterConnect\"}}\n" +
            "{\"level\":40,\"time\":1794912000000,\"pid\":7,\"hostname\":\"h\",\"msg\":\"slow response\",\"ms\":2300}\n";

        var errors = await Read("app.log", log, new { path = "app.log", level = "error" });
        var after = await Read("app.log", log, new { path = "app.log", since = "10:10" });

        Assert.Contains("2: 2026-11-17 10:15:00.000Z ERROR request failed", errors);
        Assert.Contains("    Error: ECONNREFUSED", errors);
        Assert.Contains("    at TCPConnectWrap.afterConnect", errors);
        Assert.DoesNotContain("hostname", errors);
        Assert.Contains("WARN slow response  ms=2300", after);
        Assert.DoesNotContain("server listening", after);
        Assert.Contains("read as UTC", after);
    }

    [Theory]
    // zap: the error's text and the call's stack apart, the time an epoch in seconds.
    [InlineData("{\"level\":\"error\",\"ts\":1794910500.123,\"caller\":\"main.go:12\",\"msg\":\"dial failed\",\"error\":\"connection refused\",\"stacktrace\":\"main.main\\n\\t/app/main.go:12\"}",
                "ERROR dial failed  caller=main.go:12", "connection refused", "main.main")]
    // bunyan: pino's levels, an ISO time.
    [InlineData("{\"name\":\"app\",\"hostname\":\"h\",\"pid\":1,\"level\":50,\"msg\":\"db down\",\"time\":\"2026-11-17T10:25:00.000Z\",\"v\":0}",
                "2026-11-17T10:25:00.000Z ERROR db down  name=app", null, null)]
    // ECS, its error nested.
    [InlineData("{\"@timestamp\":\"2026-11-17T10:21:00.000Z\",\"log.level\":\"error\",\"message\":\"Checkout crashed\",\"ecs.version\":\"1.6.0\",\"error\":{\"type\":\"java.lang.IllegalStateException\",\"message\":\"cart empty\",\"stack_trace\":\"java.lang.IllegalStateException: cart empty\\n\\tat Cart.checkout(Cart.java:12)\"}}",
                "ERROR Checkout crashed", "java.lang.IllegalStateException: cart empty", "at Cart.checkout(Cart.java:12)")]
    // The ILogger JSON console: its level, its message, the template in its state.
    [InlineData("{\"Timestamp\":\"2026-11-17 10:22:00\",\"EventId\":0,\"LogLevel\":\"Error\",\"Category\":\"Shop.Api\",\"Message\":\"Payment failed for 7\",\"Exception\":\"System.TimeoutException: gateway\",\"State\":{\"Message\":\"Payment failed for 7\",\"order\":7,\"{OriginalFormat}\":\"Payment failed for {order}\"}}",
                "ERROR Payment failed for 7  Category=Shop.Api", "System.TimeoutException: gateway", null)]
    // Serilog's JSON formatter: the template filled from its properties, the others shown.
    [InlineData("{\"Timestamp\":\"2026-11-17T10:23:00.0000000+01:00\",\"Level\":\"Error\",\"MessageTemplate\":\"Disk {Drive} full\",\"Properties\":{\"Drive\":\"C:\",\"SourceContext\":\"Shop.Disk\"}}",
                "ERROR Disk C: full  SourceContext=Shop.Disk", null, null)]
    // winston: a stack of its own.
    [InlineData("{\"level\":\"error\",\"message\":\"Unhandled rejection\",\"timestamp\":\"2026-11-17T10:24:00.000Z\",\"stack\":\"Error: boom\\n    at main (/app/index.js:3:9)\"}",
                "ERROR Unhandled rejection", "Error: boom", "at main (/app/index.js:3:9)")]
    // structlog: the message is its event.
    [InlineData("{\"event\":\"payment declined\",\"level\":\"error\",\"timestamp\":\"2026-11-17T10:20:00.000000Z\",\"user\":\"bob\"}",
                "ERROR payment declined  user=bob", null, null)]
    // logfmt, as Go's slog and Grafana write it.
    [InlineData("time=2026-11-17T10:26:00Z level=error msg=\"cache miss storm\" keys=120", "ERROR cache miss storm  keys=120", null, null)]
    [InlineData("t=2026-11-17T10:27:00+0000 lvl=eror msg=\"Failed to send alert\" logger=alerting", "ERROR Failed to send alert  logger=alerting", null, null)]
    public async Task EachFormat_IsReadForItsLevelMessageAndException(string record, string shown, string? exception, string? stack)
    {
        var log = record.Replace("error", "info").Replace("Error", "Information").Replace("eror", "info").Replace("\"level\":50", "\"level\":30")
                  + "\n" + record + "\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("2: ", read);
        Assert.Contains(shown, read);
        if (exception is not null) Assert.Contains("    " + exception, read);
        if (stack is not null) Assert.Contains(stack, read);
        Assert.Contains("1 of 2 entries match (level error or above)", read);
    }

    [Fact]
    public async Task AJsonLinesFileThatIsNoLog_IsNotTakenForOne()
    {
        var data = "{\"prompt\":\"hi\",\"completion\":\"hello\"}\n{\"prompt\":\"error?\",\"completion\":\"none\"}\n{\"prompt\":\"x\",\"completion\":\"y\"}\n";

        var filtered = await Read("train.jsonl", data, new { path = "train.jsonl", level = "error" });
        var plain = await Read("train.jsonl", data, new { path = "train.jsonl" });

        Assert.Contains("no log entry recognised", filtered);
        Assert.DoesNotContain("log: read_file with level", plain);
    }

    [Fact]
    public async Task ReadWithoutAFilter_AStructuredLogStaysItsText_WithTheFiltersNamedAboveIt()
    {
        var read = await Read("app.clef", Clef, new { path = "app.clef" });

        Assert.StartsWith("(a JSON log: read_file with level, since or until", read);
        Assert.Contains("\"@mt\":\"Order {Id} failed\"", read);   // the text on disk, which an edit quotes
    }

    [Fact]
    public async Task AnErrorUnderTheTemplateOfAnInformation_IsNeverFoldedIntoIt()
    {
        // Serilog's request logging: one template, the 500 at Error, the 200s at Information.
        const string Request = "\"@mt\":\"HTTP {RequestMethod} {RequestPath} responded {StatusCode}\"";
        var log =
            "{\"@t\":\"2026-11-17T10:00:00Z\"," + Request + ",\"RequestMethod\":\"GET\",\"RequestPath\":\"/health\",\"StatusCode\":200}\n" +
            "{\"@t\":\"2026-11-17T10:00:01Z\"," + Request + ",\"RequestMethod\":\"GET\",\"RequestPath\":\"/health\",\"StatusCode\":200}\n" +
            "{\"@t\":\"2026-11-17T10:00:02Z\"," + Request + ",\"@l\":\"Error\",\"RequestMethod\":\"POST\",\"RequestPath\":\"/orders\",\"StatusCode\":500}\n";

        var read = await Read("app.clef", log, new { path = "app.clef", since = "10:00" });

        Assert.Contains("3: 2026-11-17T10:00:02Z ERROR HTTP POST /orders responded 500", read);
        Assert.Contains("2 × \"HTTP {RequestMethod} {RequestPath} responded {StatusCode}\" (shown at line 1, repeated up to line 2)", read);
    }

    [Fact]
    public async Task DockerRecords_WhoseMessageIsTheirLog_AreEachShown()
    {
        var log =
            "{\"log\":\"Starting app\\n\",\"stream\":\"stdout\",\"time\":\"2026-11-17T10:00:00.1Z\"}\n" +
            "{\"log\":\"Listening on 8080\\n\",\"stream\":\"stdout\",\"time\":\"2026-11-17T10:00:01.1Z\"}\n" +
            "{\"log\":\"Unhandled exception: disk full\\n\",\"stream\":\"stderr\",\"time\":\"2026-11-17T10:00:02.1Z\"}\n";

        var read = await Read("container.log", log, new { path = "container.log", since = "10:00" });

        Assert.Contains("3: 2026-11-17T10:00:02.1Z Unhandled exception: disk full  stream=stderr", read);
        Assert.Contains("Listening on 8080", read);
        Assert.DoesNotContain("each shown once", read);
    }

    [Fact]
    public async Task TheLinesARuntimeWroteUnderARepeatedRecord_AreShownWithIt()
    {
        var log =
            "{\"level\":50,\"time\":1794910500000,\"msg\":\"request failed\"}\n" +
            "{\"level\":50,\"time\":1794910501000,\"msg\":\"request failed\"}\n" +
            "TypeError: Cannot read properties of undefined (reading 'id')\n" +
            "    at handler (/app/x.js:10:5)\n" +
            "{\"level\":50,\"time\":1794910502000,\"msg\":\"request failed\"}\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("2: 2026-11-17 10:15:01.000Z ERROR request failed", read);
        Assert.Contains("3: TypeError: Cannot read properties of undefined", read);
        Assert.Contains("4:     at handler (/app/x.js:10:5)", read);
        Assert.Contains("2 × \"request failed\" (shown at line 1, repeated up to line 5)", read);
    }

    [Fact]
    public async Task ALastRecordCutShort_IsSaid_NeverFoldedIntoTheOneBefore()
    {
        var log =
            "{\"level\":30,\"time\":1794910500000,\"msg\":\"working\"}\n" +
            "{\"level\":30,\"time\":1794910501000,\"msg\":\"still working\"}\n" +
            "{\"level\":60,\"time\":1794910502000,\"msg\":\"FATAL out of mem";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("0 of 3 entries match", read);
        Assert.Contains("1 entry with no level left out", read);
    }

    [Fact]
    public async Task AFieldIsShownAsWritten_AccentsAndBackslashesUnescaped()
    {
        var log =
            "{\"level\":\"error\",\"timestamp\":\"2026-11-17T10:00:00Z\",\"message\":\"save failed\",\"detail\":\"échec de la connexion <db> & 'retry'\",\"dir\":\"C:\\\\Program Files\\\\app\"}\n" +
            "{\"level\":\"info\",\"timestamp\":\"2026-11-17T10:00:01Z\",\"message\":\"ok\"}\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("detail=\"échec de la connexion <db> & 'retry'\"", read);
        Assert.Contains("dir=\"C:\\Program Files\\app\"", read);
    }

    [Fact]
    public async Task AnEcsLevelNestedInItsLogObject_IsNotShownAgain_ItsOtherMembersAre()
    {
        var log =
            "{\"@timestamp\":\"2026-11-17T10:00:00Z\",\"log\":{\"level\":\"error\",\"logger\":\"Shop.Cart\"},\"message\":\"Checkout crashed\"}\n" +
            "{\"@timestamp\":\"2026-11-17T10:00:01Z\",\"log\":{\"level\":\"info\",\"logger\":\"Shop.Cart\"},\"message\":\"ok\"}\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("ERROR Checkout crashed  log.logger=Shop.Cart", read);
        Assert.DoesNotContain("log={", read);
    }

    [Fact]
    public async Task NumbersGluedToAWord_AreNotSetAside_TwoDigestsAreTwoMessages()
    {
        var log =
            "{\"level\":\"error\",\"timestamp\":\"2026-11-17T10:00:00Z\",\"message\":\"sha256 mismatch\"}\n" +
            "{\"level\":\"error\",\"timestamp\":\"2026-11-17T10:00:01Z\",\"message\":\"sha1 mismatch\"}\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("sha256 mismatch", read);
        Assert.Contains("sha1 mismatch", read);
    }

    [Fact]
    public void AMessageOfAThousandsDigits_DoesNotRunTheTemplateOutOfItsBudget()
    {
        var digits = new string('7', 20_000);
        var log = "{\"level\":50,\"time\":1794910500000,\"msg\":\"" + digits + "\"}\n{\"level\":50,\"time\":1794910501000,\"msg\":\"" + digits + "\"}\n";

        var read = LogFilter.Apply(log, "app.log", "error", null, null, 0, 0, 200_000);

        Assert.Contains("2 of 2 entries match", read);
    }

    [Fact]
    public void APageCutAfterRepeats_SaysHowManyWereShown_AndHowManyCounted()
    {
        var log = string.Concat(Enumerable.Range(0, 6).Select(i => $"{{\"level\":50,\"time\":{1794910500000 + i},\"msg\":\"same\"}}\n"))
                + string.Concat(Enumerable.Range(0, 20).Select(i => $"{{\"level\":50,\"time\":{1794910600000 + i},\"msg\":\"distinct message {new string((char)('a' + i), 3)} {new string('x', 60)}\"}}\n"));

        var read = LogFilter.Apply(log, "app.log", "error", null, null, 0, 0, 600);

        Assert.Contains("5 repeat(s) counted below", read);
        Assert.Contains("more follow", read);
    }

    [Theory]
    // A text log quoting a JSON body under an entry: the body and the stack stay that entry's lines.
    [InlineData("[10:15:00 ERR] Webhook rejected, body:\n{\"timestamp\":\"2026-11-17T09:00:00Z\",\"event\":\"payment.failed\",\"level\":\"info\"}\n   at Shop.Hook()\n",
                "2: {\"timestamp\"", "3:    at Shop.Hook()")]
    // Go: a panic and its frames stay under the timed entry they follow.
    [InlineData("2026-11-17 10:00:01 ERROR handler crashed\npanic: runtime error: index out of range\ngoroutine 1 [running]:\npanic({0x4a6e20, 0xc000012345})\n",
                "2: panic: runtime error", "4: panic({0x4a6e20")]
    public async Task ATextLog_KeepsItsLinesUnderTheirEntry(string log, string kept, string alsoKept)
    {
        var read = await Read("app.log", log, new { path = "app.log", level = "error", since = "10:00" });

        Assert.Contains(kept, read);
        Assert.Contains(alsoKept, read);
        Assert.Contains("1 of 1 entries match", read);
    }

    [Theory]
    [InlineData("{\"course\":\"French\",\"level\":20}\n{\"course\":\"Maths\",\"level\":30}\n")]   // a level with no message is data
    [InlineData("{\"name\":\"sort\",\"time\":2300000000}\n{\"name\":\"merge\",\"time\":2400000000}\n")]   // durations, no dates
    public async Task JsonDataThatLooksLikeALog_IsNotTakenForOne(string data)
    {
        var read = await Read("data.jsonl", data, new { path = "data.jsonl", level = "trace" });

        Assert.Contains("no log entry recognised", read);
    }

    [Theory]
    // GELF: a syslog severity, 0 (emergency) to 7 (debug), under "level", only beside its short_message.
    [InlineData(2, "FATAL")]
    [InlineData(3, "ERROR")]
    [InlineData(4, "WARN")]
    [InlineData(6, "INFO")]
    [InlineData(7, "DEBUG")]
    public async Task AGelfRecord_ReadsItsSyslogSeverity(int severity, string shown)
    {
        var log = $"{{\"version\":\"1.1\",\"host\":\"web1\",\"short_message\":\"disk check\",\"timestamp\":1794910500.5,\"level\":{severity}}}\n"
                + "{\"version\":\"1.1\",\"host\":\"web1\",\"short_message\":\"heartbeat\",\"timestamp\":1794910501.5,\"level\":6}\n";

        var read = await Read("gelf.log", log, new { path = "gelf.log", level = "trace" });

        Assert.Contains($"1: 2026-11-17 10:15:00.500Z {shown} disk check  version=1.1 host=web1", read);
    }

    [Fact]
    public async Task ATextLog_IsReadAsBefore()
    {
        var log = "[10:00:01 INF] Starting\n[10:15:00 ERR] Order failed\nSystem.Exception: boom\n   at Shop.Run()\n";

        var read = await Read("app.log", log, new { path = "app.log", level = "error" });

        Assert.Contains("2: [10:15:00 ERR] Order failed", read);
        Assert.Contains("4:    at Shop.Run()", read);
        Assert.DoesNotContain("each shown once", read);
    }
}
