using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A distributed trace export (pari 171) reads as what someone chasing a slow or failing request wants: per trace, errors
/// first (status, attributes, exception), the slow path, then the spans — the same reading from OTLP (hex or base64 ids, one
/// request or one per line), Jaeger and Zipkin. Recognised by its content; a JSON that is not a trace is read as its lines.
/// </summary>
public class OtelTraceTextTests
{
    private const long T0 = 1795465210000000000;   // nanoseconds

    private static string OtlpSpan(string trace, string id, string? parent, string name, int kind, long startMs, long endMs,
                                   int code = 0, string? message = null, string events = "") =>
        "{\"traceId\":\"" + trace + "\",\"spanId\":\"" + id + "\"" + (parent is null ? "" : ",\"parentSpanId\":\"" + parent + "\"")
        + ",\"name\":\"" + name + "\",\"kind\":" + kind + ",\"startTimeUnixNano\":\"" + (T0 + startMs * 1_000_000) + "\",\"endTimeUnixNano\":\"" + (T0 + endMs * 1_000_000)
        + "\",\"attributes\":[{\"key\":\"http.response.status_code\",\"value\":{\"intValue\":\"500\"}}]"
        + ",\"status\":{\"code\":" + code + (message is null ? "" : ",\"message\":\"" + message + "\"") + "}" + events + "}";

    private static string Resource(string service, params string[] spans) =>
        "{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"" + service + "\"}}]},\"scopeSpans\":[{\"scope\":{\"name\":\"shop\"},\"spans\":["
        + string.Join(",", spans) + "]}]}";

    private const string Trace = "1cc60773f214bcd380e3186908103812";

    private static readonly string Exception =
        ",\"events\":[{\"name\":\"exception\",\"timeUnixNano\":\"1\",\"attributes\":[{\"key\":\"exception.type\",\"value\":{\"stringValue\":\"ConnectionRefusedError\"}},"
        + "{\"key\":\"exception.message\",\"value\":{\"stringValue\":\"refused\"}},{\"key\":\"exception.stacktrace\",\"value\":{\"stringValue\":\"Traceback\\n  at db.connect\\n  at cart.checkout\"}}]}]";

    private static readonly string[] Frontend =
    [
        OtlpSpan(Trace, "73fd6e9c1daf386e", null, "GET /checkout", 2, 0, 182, 2),
        OtlpSpan(Trace, "aaaaaaaaaaaaaaaa", "73fd6e9c1daf386e", "POST /cart/checkout", 3, 10, 182, 2),
    ];

    private static readonly string[] Cart =
    [
        OtlpSpan(Trace, "bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa", "pricing.compute", 1, 11, 161),
        OtlpSpan(Trace, "cccccccccccccccc", "aaaaaaaaaaaaaaaa", "SELECT orders", 3, 161, 182, 2, "connection refused", Exception),
    ];

    private static string Otlp() => "{\"resourceSpans\":[" + Resource("frontend", Frontend) + "," + Resource("cart", Cart) + "]}";

    [Fact]
    public void AnOtlpExport_ReadsAsErrorsThenTheSlowPathThenTheSpans()
    {
        var text = OtelTraceText.Summarize("trace.json", Otlp(), 30_000)!;

        Assert.StartsWith("[trace.json: a distributed trace export (OTLP JSON), 1 trace(s), 4 span(s), services: frontend, cart.", text);
        Assert.Contains("## Trace 1cc60773f214bcd380e3186908103812 — 182 ms, 4 span(s), 3 in error, services: frontend, cart\n", text);
        Assert.Contains("✗ cart  SELECT orders (CLIENT) — 21 ms, at +161 ms — status: connection refused\n"
                      + "  attributes: http.response.status_code=500\n"
                      + "  event: exception ConnectionRefusedError: refused — Traceback | at db.connect | at cart.checkout\n", text);
        // The child that ends last at each level: SELECT orders, not the longer pricing.compute that ended before it.
        Assert.Contains("frontend GET /checkout 182 ms → frontend POST /cart/checkout 172 ms → cart SELECT orders 21 ms\n", text);
        Assert.Contains("+11 ms  150 ms      cart  pricing.compute (INTERNAL)\n", text);
        Assert.True(text.IndexOf("### Spans in error", StringComparison.Ordinal) < text.IndexOf("### Slow path", StringComparison.Ordinal));
    }

    [Fact]
    public void Base64Ids_EnumNames_AndOneRequestPerLine_ReadTheSame()
    {
        static string B64(string hex) => Convert.ToBase64String(Convert.FromHexString(hex));
        var proto3 = Otlp();
        foreach (var hex in new[] { Trace, "73fd6e9c1daf386e", "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "cccccccccccccccc" })
            proto3 = proto3.Replace("\"" + hex + "\"", "\"" + B64(hex) + "\"", StringComparison.Ordinal);
        proto3 = proto3.Replace("\"kind\":2", "\"kind\":\"SPAN_KIND_SERVER\"", StringComparison.Ordinal)
                       .Replace("\"code\":2", "\"code\":\"STATUS_CODE_ERROR\"", StringComparison.Ordinal);
        var reference = Body(OtelTraceText.Summarize("trace.json", Otlp(), 30_000)!);
        Assert.Equal(reference, Body(OtelTraceText.Summarize("trace.json", proto3, 30_000)!));

        var lines = "{\"resourceSpans\":[" + Resource("frontend", Frontend) + "]}\n{\"resourceSpans\":[" + Resource("cart", Cart) + "]}\n";
        var perLine = OtelTraceText.Summarize("collector.jsonl", lines, 30_000)!;
        Assert.Contains("(OTLP JSON, 2 request(s), one per line)", perLine);
        Assert.Equal(reference, Body(perLine));
    }

    private static string Body(string text) => text[text.IndexOf("\n", StringComparison.Ordinal)..];

    [Fact]
    public void JaegerAndZipkin_ReadTheirSpans_ErrorsAndExceptions()
    {
        var jaeger = """
            {"data":[{"traceID":"1cc60773f214bcd3","spans":[
              {"traceID":"1cc60773f214bcd3","spanID":"0000000000000001","operationName":"GET /checkout","references":[],"startTime":1795465210000000,"duration":182000,"tags":[{"key":"span.kind","type":"string","value":"server"}],"logs":[],"processID":"p1"},
              {"traceID":"1cc60773f214bcd3","spanID":"0000000000000002","operationName":"SELECT orders","references":[{"refType":"CHILD_OF","traceID":"1cc60773f214bcd3","spanID":"0000000000000001"}],"startTime":1795465210161000,"duration":21000,
               "tags":[{"key":"error","type":"bool","value":true},{"key":"otel.status_description","type":"string","value":"connection refused"},{"key":"db.system","type":"string","value":"postgresql"}],
               "logs":[{"timestamp":1795465210180000,"fields":[{"key":"event","type":"string","value":"exception"},{"key":"exception.type","type":"string","value":"IOError"},{"key":"exception.message","type":"string","value":"refused"}]}],"processID":"p2"}],
              "processes":{"p1":{"serviceName":"frontend","tags":[]},"p2":{"serviceName":"orders-db","tags":[]}}}]}
            """;
        var j = OtelTraceText.Summarize("jaeger-export.json", jaeger, 30_000)!;
        Assert.Contains("(Jaeger JSON), 1 trace(s), 2 span(s), services: frontend, orders-db.", j);
        Assert.Contains("✗ orders-db  SELECT orders — 21 ms, at +161 ms — status: connection refused\n  attributes: db.system=postgresql\n  event: exception IOError: refused\n", j);
        Assert.Contains("+0.0 ms  182 ms  frontend  GET /checkout (SERVER)\n+161 ms  21 ms    ✗ orders-db  SELECT orders\n", j);

        var zipkin = """
            [[{"traceId":"1cc60773f214bcd380e3186908103812","id":"0000000000000001","name":"get /checkout","timestamp":1795465210000000,"duration":182000,"kind":"SERVER","localEndpoint":{"serviceName":"frontend"}},
              {"traceId":"1cc60773f214bcd380e3186908103812","id":"0000000000000002","parentId":"0000000000000001","name":"select orders","timestamp":1795465210161000,"duration":21000,"kind":"CLIENT",
               "localEndpoint":{"serviceName":"unknown_service"},"tags":{"service.name":"orders-db","error":"connection refused","db.system":"postgresql"},
               "annotations":[{"timestamp":1795465210180000,"value":"{\"exception\": {\"exception.type\": \"IOError\", \"exception.message\": \"refused\"}}"}]}]]
            """;
        var z = OtelTraceText.Summarize("zipkin.json", zipkin, 30_000)!;
        Assert.Contains("(Zipkin v2 JSON), 1 trace(s), 2 span(s), services: frontend, orders-db.", z);
        Assert.Contains("✗ orders-db  select orders (CLIENT) — 21 ms, at +161 ms — status: connection refused\n  attributes: db.system=postgresql\n  event: exception IOError: refused\n", z);
    }

    [Fact]
    public void AParentNotInTheFile_IsSaid_AndAShortSpanIsInMicroseconds()
    {
        var text = OtelTraceText.Summarize("part.json", "{\"resourceSpans\":[" + Resource("cart", Cart) + "]}", 30_000)!;
        Assert.Contains("(2 span(s) name a parent that is not in the file: shown as roots.)", text);
        var tiny = OtelTraceText.Summarize("t.json", "{\"resourceSpans\":[" + Resource("x",
            "{\"traceId\":\"" + Trace + "\",\"spanId\":\"0000000000000001\",\"name\":\"tick\",\"startTimeUnixNano\":\"1000\",\"endTimeUnixNano\":\"6000\"}") + "]}", 30_000)!;
        Assert.Contains("+0.0 ms  5 µs  x  tick\n", tiny);
    }

    [Fact]
    public void AFullPage_SaysWhatItLeavesOut()
    {
        var many = Enumerable.Range(0, 400).Select(i => OtlpSpan(Trace, $"{i:x16}", "73fd6e9c1daf386e", $"step {i}", 1, i, i + 1)).Append(Frontend[0]);
        var text = OtelTraceText.Summarize("big.json", "{\"resourceSpans\":[" + Resource("svc", [.. many]) + "]}", 4_000)!;
        Assert.Contains("more span(s) of this trace, not listed: the page is full.", text);
        Assert.True(text.Length < 6_000);
    }

    private static string Z(string id, string? parent, string name, long startUs = 1795465210000000, long durationUs = 1000, string extra = "") =>
        "{\"traceId\":\"1cc60773f214bcd3\",\"id\":\"" + id + "\"" + (parent is null ? "" : ",\"parentId\":\"" + parent + "\"")
        + ",\"name\":\"" + name + "\"" + (startUs > 0 ? ",\"timestamp\":" + startUs : "") + ",\"duration\":" + durationUs + ",\"localEndpoint\":{\"serviceName\":\"svc\"}" + extra + "}";

    [Fact]
    public void IdsUsedTwice_ListEachSpanOnce_AndALoopThroughThemEnds()
    {
        // Zipkin's shared spans: a client and its server span under one id, the server's child under it.
        var shared = "[" + string.Join(",", Z("000000000000000a", null, "root"), Z("000000000000000b", "000000000000000a", "client"),
                                         Z("000000000000000b", "000000000000000a", "server", extra: ",\"shared\":true"),
                                         Z("000000000000000c", "000000000000000b", "query")) + "]";
        var text = OtelTraceText.Summarize("zipkin.json", shared, 30_000)!;
        Assert.Single(text.Split('\n'), l => l.Contains("svc  query", StringComparison.Ordinal));

        // A loop through ids used twice, branching at each level: listed once, and it returns.
        var loop = "[" + string.Join(",", Z("000000000000000a", null, "r"), Z("000000000000000b", "000000000000000a", "x1"),
                                       Z("000000000000000b", "000000000000000a", "x2"), Z("000000000000000a", "000000000000000b", "y")) + "]";
        var read = Task.Run(() => OtelTraceText.Summarize("loop.json", loop, 30_000));
        Assert.True(read.Wait(TimeSpan.FromSeconds(30)));
        Assert.NotNull(read.Result);
    }

    [Fact]
    public void AKeyOrAnIdFromTheFile_NeverWritesALine()
    {
        var forged = "[" + Z("000000000000000a", null, "root", extra: ",\"kind\":\"SERVER\\n## Trace forged\",\"tags\":{\"a\\n✗ fake span\":\"1\",\"error\":\"x\"}") + "]";
        var text = OtelTraceText.Summarize("zipkin.json", forged, 30_000)!;
        Assert.DoesNotContain("\n## Trace forged", text);
        Assert.DoesNotContain("\n✗ fake span", text);
        Assert.Contains("a⏎✗ fake span=1", text);
    }

    [Fact]
    public void ALogWithTraceIds_IsNoTrace_AndASpanWithoutATime_SaysSo()
    {
        Assert.Null(OtelTraceText.Summarize("requests.json", "[{\"traceId\":\"abc\",\"duration\":12,\"message\":\"GET /\"}]", 30_000));
        var text = OtelTraceText.Summarize("zipkin.json", "[" + Z("000000000000000a", null, "root", durationUs: 182000)
                                           + "," + Z("000000000000000b", "000000000000000a", "late", startUs: 0) + "]", 30_000)!;
        Assert.Contains("— 182 ms, 2 span(s)", text);
        Assert.Contains("time unknown  duration unknown    svc  late\n", text);
    }

    [Fact]
    public void SnakeCaseOtlp_AndAHalfWrittenLastLine_AreRead()
    {
        var snake = "{\"resource_spans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"string_value\":\"cart\"}}]},\"scope_spans\":[{\"spans\":[{\"trace_id\":\""
                    + Trace + "\",\"span_id\":\"000000000000000a\",\"name\":\"checkout\",\"start_time_unix_nano\":\"" + T0 + "\",\"end_time_unix_nano\":\"" + (T0 + 5_000_000) + "\"}]}]}]}";
        Assert.Contains("+0.0 ms  5.0 ms  cart  checkout\n", OtelTraceText.Summarize("snake.json", snake, 30_000));

        var lines = "{\"resourceSpans\":[" + Resource("frontend", Frontend) + "]}\n{\"resourceSpans\":[" + Resource("cart", Cart[0]);
        var text = OtelTraceText.Summarize("collector.jsonl", lines, 30_000)!;
        Assert.Contains("(OTLP JSON, 1 request(s), one per line; 1 line(s) that are not JSON, not read)", text);
        Assert.Contains("frontend  GET /checkout", text);
    }

    [Fact]
    public void ASpanWithoutAnEnd_OrATagWithoutAValue_IsNoNonsense()
    {
        var unended = OtelTraceText.Summarize("t.json", "{\"resourceSpans\":[" + Resource("x",
            "{\"traceId\":\"" + Trace + "\",\"spanId\":\"0000000000000001\",\"name\":\"open\",\"startTimeUnixNano\":\"1700000000000000000\"}") + "]}", 30_000)!;
        Assert.Contains("— duration unknown, 1 span(s)", unended);
        Assert.Contains("+0.0 ms  duration unknown  x  open\n", unended);

        var jaeger = "{\"data\":[{\"traceID\":\"a1\",\"spans\":[{\"traceID\":\"a1\",\"spanID\":\"b2\",\"operationName\":\"op\",\"startTime\":1,\"duration\":5,\"tags\":[{\"key\":\"x\",\"type\":\"string\"}]}]}]}";
        Assert.Contains("x  op".Replace("x  ", "(no service)  ", StringComparison.Ordinal), OtelTraceText.Summarize("jaeger.json", jaeger, 30_000));

        var unfinished = OtelTraceText.Summarize("z.json", "[{\"traceId\":\"1cc60773f214bcd3\",\"id\":\"000000000000000a\",\"name\":\"pending\",\"timestamp\":1795465210000000,\"localEndpoint\":{\"serviceName\":\"svc\"}}]", 30_000)!;
        Assert.Contains("+0.0 ms  duration unknown  svc  pending\n", unfinished);
    }

    [Fact]
    public void AJsonThatIsNoTrace_IsReadAsItsLines()
    {
        Assert.Null(OtelTraceText.Summarize("package.json", "{\"name\":\"shop\",\"version\":\"1.0.0\"}", 30_000));
        Assert.Null(OtelTraceText.Summarize("spans.txt", Otlp(), 30_000));                       // not a .json
        Assert.Null(OtelTraceText.Summarize("broken.json", "{\"resourceSpans\": [ {", 30_000));  // unreadable: its lines
        Assert.Null(OtelTraceText.Summarize("empty.json", "{\"resourceSpans\":[]}", 30_000));    // no span: nothing to say
    }
}
