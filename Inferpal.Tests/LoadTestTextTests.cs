using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A load test's result (pari 174) reads as its failed thresholds, then per request its count, errors, percentiles and
/// throughput, the worst first: k6's summary (both shapes), k6's per-request output and JMeter's .jtl (CSV and XML). The
/// fixtures follow each tool's documented format.
/// </summary>
public class LoadTestTextTests
{
    private const string JmeterHeader = "timeStamp,elapsed,label,responseCode,responseMessage,threadName,dataType,success,failureMessage,bytes,sentBytes,grpThreads,allThreads,URL,Latency,IdleTime,Connect\n";

    private static string JmeterCsv()
    {
        var rows = new System.Text.StringBuilder(JmeterHeader);
        long t = 1_700_000_000_000;
        for (var i = 0; i < 100; i++)
            rows.Append($"{t + i * 100},{40 + i},GET /products,200,OK,Users 1-1,text,true,,512,120,10,10,http://shop/products,{30 + i},0,2\n");
        for (var i = 0; i < 20; i++)
            rows.Append(i < 5
                ? $"{t + i * 500},{900 + i},POST /checkout,500,Internal Server Error,Users 1-2,text,false,\"Assertion failed, expected 200\",120,300,10,10,http://shop/checkout,880,0,3\n"
                : $"{t + i * 500},{300 + i},POST /checkout,200,OK,Users 1-2,text,true,,120,300,10,10,http://shop/checkout,280,0,3\n");
        return rows.ToString();
    }

    [Fact]
    public void AJmeterCsv_ReadsPerRequest_TheMostFailedFirst()
    {
        var text = LoadTestText.Summarize("results.jtl", JmeterCsv(), 30_000)!;

        Assert.StartsWith("[results.jtl: a load test result (JMeter CSV), 120 requests, 5 failed (4.17 %), over 10 s: 12 requests/s.", text);
        var checkout = text.IndexOf("- POST /checkout — 20 req", StringComparison.Ordinal);
        Assert.True(checkout > 0 && checkout < text.IndexOf("- GET /products — 100 req", StringComparison.Ordinal));
        Assert.Contains("5 failed (25 %)", text);
        Assert.Contains("  ✗ 5× 500 Assertion failed, expected 200\n", text);
        Assert.Contains("- GET /products — 100 req (10/s), 0 failed (0 %) · p50 89 ms · p95 134 ms · p99 138 ms · max 139 ms", text);
    }

    [Fact]
    public void AJmeterXml_CountsTopLevelSamples_NotTheirSubRequests()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <testResults version="1.2">
            <httpSample t="120" lt="100" ts="1700000000000" s="true" lb="GET /home" rc="200" rm="OK" tn="Users 1-1" by="900">
              <httpSample t="30" lt="30" ts="1700000000010" s="true" lb="GET /home-0" rc="302" rm="Found"/>
            </httpSample>
            <httpSample t="2500" lt="2400" ts="1700000001000" s="false" lb="GET /search" rc="504" rm="Gateway Timeout" tn="Users 1-1">
              <assertionResult><name>Response Assertion</name><failure>true</failure><error>false</error><failureMessage>Test failed: code expected 200</failureMessage></assertionResult>
            </httpSample>
            </testResults>
            """;
        var text = LoadTestText.Summarize("results.xml", xml, 30_000)!;
        Assert.StartsWith("[results.xml: a load test result (JMeter XML), 2 requests, 1 failed (50 %)", text);
        Assert.Contains("- GET /search — 1 req", text);
        Assert.Contains("  ✗ 1× 504 Test failed: code expected 200\n", text);
        Assert.DoesNotContain("home-0", text);
    }

    [Fact]
    public void K6Output_JoinsEachRequestsFailure_ToItsTags()
    {
        static string Point(string metric, double value, string name, string status, int ms) =>
            $"{{\"type\":\"Point\",\"metric\":\"{metric}\",\"data\":{{\"time\":\"2026-11-23T10:00:{ms / 1000:00}.{ms % 1000:000}Z\",\"value\":{value},\"tags\":{{\"method\":\"GET\",\"name\":\"{name}\",\"status\":\"{status}\",\"url\":\"http://shop{name}\"}}}}}}\n";
        var lines = "{\"type\":\"Metric\",\"data\":{\"name\":\"http_req_duration\",\"type\":\"trend\",\"contains\":\"time\"},\"metric\":\"http_req_duration\"}\n";
        for (var i = 0; i < 10; i++)
        {
            var failed = i < 3;
            lines += Point("http_req_duration", failed ? 1200 + i : 50 + i, "/api/quote", failed ? "503" : "200", i * 1000)
                   + Point("http_req_failed", failed ? 1 : 0, "/api/quote", failed ? "503" : "200", i * 1000);
        }
        var text = LoadTestText.Summarize("out.json", lines, 30_000)!;
        Assert.StartsWith("[out.json: a load test result (k6 output), 10 requests, 3 failed (30 %)", text);
        Assert.Contains("- GET /api/quote — 10 req", text);
        Assert.Contains("  ✗ 3× 503\n", text);
    }

    [Fact]
    public void AK6SummaryExport_SaysTheCrossedThresholds_AndEachTaggedRequest()
    {
        const string summary = """
            {
              "root_group": { "name": "", "path": "", "id": "d41d8cd98f00b204e9800998ecf8427e", "groups": {},
                "checks": { "status is 200": { "name": "status is 200", "path": "::status is 200", "id": "x", "passes": 950, "fails": 50 } } },
              "metrics": {
                "http_reqs": { "count": 1000, "rate": 33.2 },
                "http_req_failed": { "passes": 50, "fails": 950, "thresholds": { "rate<0.01": true }, "value": 0.05 },
                "http_req_duration": { "avg": 210.5, "min": 12.1, "med": 95.2, "max": 3020.7, "p(90)": 480.3, "p(95)": 812.9,
                                       "thresholds": { "p(95)<500": true, "p(99)<3000": false } },
                "http_req_duration{name:/api/checkout}": { "avg": 900, "min": 300, "med": 850, "max": 3020.7, "p(90)": 1500, "p(95)": 2100 },
                "http_req_duration{name:/api/products}": { "avg": 80, "min": 12.1, "med": 70, "max": 400, "p(90)": 150, "p(95)": 210 },
                "http_req_failed{name:/api/checkout}": { "passes": 50, "fails": 150, "value": 0.25 }
              }
            }
            """;
        var text = LoadTestText.Summarize("summary.json", summary, 30_000)!;
        Assert.StartsWith("[summary.json: a k6 summary, 1000 requests, 33.2 requests/s, 5 % failed.", text);
        Assert.Contains("## Thresholds crossed (2, 1 held)\n✗ http_req_failed: rate<0.01\n✗ http_req_duration: p(95)<500\n", text);
        Assert.Contains("All requests: p50 95.2 ms · p90 480 ms · p95 813 ms · max 3021 ms", text);
        Assert.True(text.IndexOf("name:/api/checkout", StringComparison.Ordinal) < text.IndexOf("name:/api/products", StringComparison.Ordinal));
        Assert.Contains("· 25 % failed", text);
        Assert.Contains("✗ status is 200: 50 failed, 950 passed", text);
    }

    [Fact]
    public void AHandleSummaryJson_ReadsItsValues_AndItsThresholdObjects()
    {
        const string data = """
            {"metrics":{
              "http_reqs":{"type":"counter","contains":"default","values":{"count":200,"rate":20}},
              "http_req_failed":{"type":"rate","contains":"default","values":{"rate":0,"passes":0,"fails":200},"thresholds":{"rate<0.01":{"ok":true}}},
              "http_req_duration":{"type":"trend","contains":"time","values":{"avg":40,"min":10,"med":35,"max":90,"p(90)":60,"p(95)":70},"thresholds":{"p(95)<500":{"ok":true}}}},
             "root_group":{"name":"","path":"","id":"x","groups":[],"checks":[{"name":"ok","path":"::ok","id":"y","passes":200,"fails":0}]},
             "options":{"summaryTrendStats":["avg","min","med","max","p(90)","p(95)"]},
             "state":{"isStdOutTTY":false,"isStdErrTTY":false,"testRunDurationMs":10000}}
            """;
        var text = LoadTestText.Summarize("summary.json", data, 30_000)!;
        Assert.StartsWith("[summary.json: a k6 summary, 200 requests, 20 requests/s, 0 % failed.", text);
        Assert.Contains("Every threshold held (2).", text);
        Assert.Contains("No per-request figures", text);
        Assert.DoesNotContain("Checks that failed", text);
    }

    [Fact]
    public void WithoutASuccessColumn_FailuresAreUnknown_NeverEveryRequest()
    {
        var csv = "timeStamp,elapsed,label,responseCode\n1700000000000,10,GET /a,200\n1700000000100,12,GET /a,200\n";
        var text = LoadTestText.Summarize("results.jtl", csv, 30_000)!;
        Assert.Contains("2 requests, 0 failed", text);
        Assert.Contains("their failures are unknown, not zero", text);
    }

    [Fact]
    public void AMultiLineMessage_StaysInItsRow()
    {
        var csv = JmeterHeader + "1700000000000,10,GET /a,500,Error,t,text,false,\"Expected 200\nbut got 500\",1,1,1,1,u,1,0,0\n"
                               + "1700000000100,12,GET /a,200,OK,t,text,true,,1,1,1,1,u,1,0,0\n";
        var text = LoadTestText.Summarize("results.jtl", csv, 30_000)!;
        Assert.StartsWith("[results.jtl: a load test result (JMeter CSV), 2 requests, 1 failed (50 %)", text);
        Assert.Contains("  ✗ 1× 500 Expected 200 but got 500\n", text);
        Assert.DoesNotContain("not read", text);
    }

    [Fact]
    public void ManyCrossedThresholds_AreCut_AndTheCutSaid()
    {
        var metrics = string.Join(",", Enumerable.Range(0, 500).Select(i =>
            $"\"http_req_duration{{name:/r{i:D3}}}\":{{\"med\":1,\"p(95)\":2,\"thresholds\":{{\"p(95)<1\":true}}}}"));
        var text = LoadTestText.Summarize("summary.json", "{\"metrics\":{\"http_reqs\":{\"count\":500,\"rate\":1}," + metrics + "}}", 6_000)!;
        Assert.Contains("more crossed threshold(s), not listed.", text);
        Assert.True(text.Length < 12_000, text.Length.ToString());
    }

    [Fact]
    public void ManyRequests_AreCut_AndTheCutSaid()
    {
        var rows = new System.Text.StringBuilder(JmeterHeader);
        for (var i = 0; i < 60; i++) rows.Append($"{1_700_000_000_000 + i},{i + 1},GET /r{i:D2},200,OK,t,text,true,,1,1,1,1,u,1,0,0\n");
        var text = LoadTestText.Summarize("results.csv", rows.ToString(), 30_000)!;
        Assert.Contains($"… {60 - LoadTestText.MaxRequests} more request(s), not listed.", text);
    }

    [Fact]
    public void ALabelCannotWriteALineOfItsOwn()
    {
        var csv = JmeterHeader + "1700000000000,10,\"GET /a\n## Thresholds crossed\",200,OK,t,text,true,,1,1,1,1,u,1,0,0\n";
        var text = LoadTestText.Summarize("results.jtl", csv, 30_000);
        Assert.DoesNotContain("\n## Thresholds crossed", text ?? "");
    }

    [Theory]
    [InlineData("package.json", "{\"name\":\"shop\",\"metrics\":{}}")]
    [InlineData("data.csv", "id,name\n1,a\n")]
    [InlineData("pom.xml", "<project><modelVersion>4.0.0</modelVersion></project>")]
    public void AFileThatIsNoLoadTest_IsReadAsItsLines(string name, string text)
    {
        Assert.Null(LoadTestText.Summarize(name, text, 30_000));
    }

    [Fact]
    public void Percentile_IsTheNearestRank()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(95, LoadTestText.Percentile(sorted, 95));
        Assert.Equal(1, LoadTestText.Percentile([1.0], 99));
    }
}
