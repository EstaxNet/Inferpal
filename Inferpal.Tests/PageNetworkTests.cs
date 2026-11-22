using System.Text.Json;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A page's requests, read from its browser's DevTools events (pari 153): what failed and what was slowest, each with its
/// method, address, status and time; what the browser does not reach by construction never told as the app's failure.
/// </summary>
public class PageNetworkTests
{
    private const string Origin = "http://localhost:5173";

    private static void Feed(PageNetwork network, string name, object args) =>
        network.OnEvent(name, JsonSerializer.SerializeToElement(args));

    private static void Request(PageNetwork n, string id, string method, string url, double start, double end,
                                int? status = null, string statusText = "", string? failure = null, bool canceled = false)
    {
        Feed(n, "Network.requestWillBeSent", new { requestId = id, timestamp = start, request = new { method, url } });
        if (status is not null) Feed(n, "Network.responseReceived", new { requestId = id, response = new { status, statusText } });
        if (failure is null) Feed(n, "Network.loadingFinished", new { requestId = id, timestamp = end });
        else Feed(n, "Network.loadingFailed", new { requestId = id, timestamp = end, errorText = failure, canceled });
    }

    [Fact]
    public void TheFailedRequests_AreSaidFirst_ThenTheSlowest_AndElsewhereIsNotTheAppsFailure()
    {
        var n = new PageNetwork();
        Request(n, "1", "GET", "http://localhost:5173/", 10.0, 10.05, 200, "OK");
        Request(n, "2", "GET", "http://localhost:5173/api/orders?page=2", 10.1, 10.22, 500, "Internal Server Error");
        Request(n, "3", "POST", "http://localhost:5173/api/login", 10.1, 10.15, failure: "net::ERR_CONNECTION_RESET");
        Request(n, "4", "GET", "https://fonts.example.com/inter.css", 10.0, 10.01, failure: "net::ERR_PROXY_CONNECTION_FAILED");
        Request(n, "5", "GET", "http://localhost:5173/api/report", 10.2, 12.5, 200, "OK");
        Request(n, "6", "GET", "http://localhost:5173/cancelled.js", 10.2, 10.3, failure: "net::ERR_ABORTED", canceled: true);
        Feed(n, "Network.requestWillBeSent", new { requestId = "7", timestamp = 12.6, request = new { method = "GET", url = "http://localhost:5173/live" } });
        Feed(n, "Network.requestWillBeSent", new { requestId = "8", timestamp = 12.6, request = new { method = "GET", url = "data:image/png;base64,AAAA" } });

        var text = string.Join("\n", n.Lines(Origin));

        Assert.StartsWith("Network (6 requests, 2 failed, 1 still under way;", text, StringComparison.Ordinal);
        Assert.Contains("  GET /api/orders?page=2 → 500 Internal Server Error (120 ms)", text, StringComparison.Ordinal);
        Assert.Contains("  POST /api/login → failed: net::ERR_CONNECTION_RESET (50 ms)", text, StringComparison.Ordinal);
        Assert.Contains("Not reached — this browser reaches only the page's own address, so this says nothing of the app: https://fonts.example.com (1)",
                        text, StringComparison.Ordinal);
        Assert.Contains("Slowest: GET /api/report 200 OK (2.3 s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled.js →", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatWasRead_IsNotSaidAgain_ButARequestStillUnderWayIs()
    {
        var n = new PageNetwork();
        Request(n, "1", "GET", "http://localhost:5173/a", 1, 1.1, 404, "Not Found");
        Feed(n, "Network.requestWillBeSent", new { requestId = "2", timestamp = 1.0, request = new { method = "GET", url = "http://localhost:5173/slow" } });

        var first = string.Join("\n", n.Lines(Origin));
        Feed(n, "Network.responseReceived", new { requestId = "2", response = new { status = 503, statusText = "Service Unavailable" } });
        Feed(n, "Network.loadingFinished", new { requestId = "2", timestamp = 4.0 });
        var second = string.Join("\n", n.Lines(Origin));

        Assert.Contains("GET /a → 404 Not Found", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/a →", second, StringComparison.Ordinal);
        Assert.Contains("GET /slow → 503 Service Unavailable (3.0 s)", second, StringComparison.Ordinal);
        Assert.Empty(n.Lines(Origin));
    }

    [Fact]
    public void ACredentialInAnAddress_IsMasked()
    {
        var n = new PageNetwork();
        Request(n, "1", "GET", "http://localhost:5173/api/data?api_key=sk-live-0123456789abcdef0123456789", 1, 1.2, 401, "Unauthorized");

        var text = string.Join("\n", n.Lines(Origin));

        Assert.Contains("GET /api/data?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789abcdef0123456789", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureAfterHundredsOfModuleLoads_IsKept()
    {
        // A dev server's unbundled modules come first; the API call that fails comes after them.
        var n = new PageNetwork();
        for (var i = 0; i < 700; i++) Request(n, $"m{i}", "GET", $"http://localhost:5173/src/m{i}.js", 1, 1.01, 200, "OK");
        Request(n, "api", "GET", "http://localhost:5173/api/orders", 2, 2.2, 500, "Internal Server Error");

        var text = string.Join("\n", n.Lines(Origin));

        Assert.Contains("GET /api/orders → 500 Internal Server Error", text, StringComparison.Ordinal);
        Assert.Contains("701 requests, 1 failed", text, StringComparison.Ordinal);
        Assert.Contains("201 that ended well not kept to make room", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestOfThePageRedirectedElsewhere_IsSaidAsTheAppsOwn()
    {
        var n = new PageNetwork();
        Feed(n, "Network.requestWillBeSent", new { requestId = "1", timestamp = 1.0, request = new { method = "GET", url = "http://localhost:5173/login" } });
        Feed(n, "Network.requestWillBeSent", new { requestId = "1", timestamp = 1.1, request = new { method = "GET", url = "https://auth.example.com/authorize" } });
        Feed(n, "Network.loadingFailed", new { requestId = "1", timestamp = 1.2, errorText = "net::ERR_PROXY_CONNECTION_FAILED" });

        var text = string.Join("\n", n.Lines(Origin));

        Assert.Contains("GET /login redirected to https://auth.example.com, which this browser does not reach", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedShape_IsIgnored_NeverThrown()
    {
        var n = new PageNetwork();
        n.OnEvent("Network.requestWillBeSent", JsonSerializer.SerializeToElement(new { requestId = 4 }));
        n.OnEvent("Network.loadingFinished", JsonSerializer.SerializeToElement("text"));
        n.OnEvent("Network.responseReceived", JsonSerializer.SerializeToElement(new { requestId = "x", response = new { status = "bad" } }));

        Assert.Empty(n.Lines(Origin));
    }
}
