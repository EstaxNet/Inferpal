using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Lighthouse report reads first as what it found (pari 155): the scores and the metrics, then the audits not passed, the
/// largest estimated gain first, each with the elements it names; from its JSON, its HTML report or a PageSpeed answer.
/// </summary>
public class LighthouseTextTests
{
    // The shape Lighthouse 12 writes with --output=json (fields as documented), cut to what the reader uses and a few it skips.
    private const string Report = """
        {
          "lighthouseVersion": "12.8.2",
          "requestedUrl": "http://localhost:8080/", "finalDisplayedUrl": "http://localhost:8080/shop",
          "fetchTime": "2026-11-22T10:00:00.000Z",
          "configSettings": { "formFactor": "mobile" },
          "runWarnings": [],
          "categories": {
            "performance": { "id": "performance", "title": "Performance", "score": 0.54, "auditRefs": [
              { "id": "first-contentful-paint", "weight": 10, "group": "metrics", "acronym": "FCP" },
              { "id": "largest-contentful-paint", "weight": 25, "group": "metrics", "acronym": "LCP" },
              { "id": "unused-javascript", "weight": 0, "group": "diagnostics" },
              { "id": "render-blocking-resources", "weight": 0, "group": "diagnostics" },
              { "id": "uses-optimized-images", "weight": 0, "group": "diagnostics" },
              { "id": "font-display", "weight": 0, "group": "diagnostics" },
              { "id": "mainthread-work-breakdown", "weight": 0, "group": "diagnostics" },
              { "id": "final-screenshot", "weight": 0, "group": "hidden" },
              { "id": "hidden-failure", "weight": 0, "group": "hidden" } ] },
            "accessibility": { "id": "accessibility", "title": "Accessibility", "score": 0.78, "auditRefs": [
              { "id": "image-alt", "weight": 10, "group": "a11y-names-labels" },
              { "id": "button-name", "weight": 10, "group": "a11y-names-labels" },
              { "id": "color-contrast", "weight": 7, "group": "a11y-color-contrast" },
              { "id": "video-caption", "weight": 0 },
              { "id": "logical-tab-order", "weight": 0 } ] },
            "best-practices": { "id": "best-practices", "title": "Best Practices", "score": 1, "auditRefs": [
              { "id": "errors-in-console", "weight": 1 }, { "id": "deprecations", "weight": 5 } ] },
            "seo": { "id": "seo", "title": "SEO", "score": null, "auditRefs": [ { "id": "robots-txt", "weight": 1 } ] }
          },
          "audits": {
            "first-contentful-paint": { "id": "first-contentful-paint", "title": "First Contentful Paint", "score": 0.4,
              "scoreDisplayMode": "numeric", "displayValue": "3.1 s", "numericValue": 3100 },
            "largest-contentful-paint": { "id": "largest-contentful-paint", "title": "Largest Contentful Paint", "score": 0.1,
              "scoreDisplayMode": "numeric", "displayValue": "6.2 s", "numericValue": 6200 },
            "unused-javascript": { "id": "unused-javascript", "title": "Reduce unused JavaScript", "score": 0,
              "scoreDisplayMode": "metricSavings", "displayValue": "Est savings of 880 KiB", "metricSavings": { "LCP": 1950, "FCP": 300 },
              "details": { "type": "opportunity", "overallSavingsMs": 1950, "overallSavingsBytes": 901120,
                "items": [ { "url": "http://localhost:8080/big.js", "totalBytes": 937786, "wastedBytes": 901120, "wastedPercent": 96 } ] } },
            "render-blocking-resources": { "id": "render-blocking-resources", "title": "Eliminate render-blocking resources",
              "score": 0.5, "scoreDisplayMode": "metricSavings", "metricSavings": { "FCP": 450, "LCP": 450 },
              "details": { "type": "opportunity", "overallSavingsMs": 450,
                "items": [ { "url": "http://localhost:8080/big.js", "totalBytes": 937786, "wastedMs": 450 } ] } },
            "uses-optimized-images": { "id": "uses-optimized-images", "title": "Efficiently encode images", "score": 0.5,
              "scoreDisplayMode": "metricSavings", "metricSavings": { "LCP": 0 },
              "details": { "type": "opportunity", "overallSavingsMs": 0, "overallSavingsBytes": 204800,
                "items": [ { "url": "http://localhost:8080/hero.png", "totalBytes": 446626, "wastedBytes": 204800 } ] } },
            "font-display": { "id": "font-display", "title": "All text remains visible during webfont loads", "score": 1,
              "scoreDisplayMode": "metricSavings" },
            "image-alt": { "id": "image-alt", "title": "Image elements do not have `[alt]` attributes", "score": 0,
              "scoreDisplayMode": "binary",
              "details": { "type": "table", "items": [
                { "node": { "type": "node", "selector": "body > img", "snippet": "<img src=\"hero.png\" width=\"400\" height=\"200\">",
                            "nodeLabel": "body > img" } } ] } },
            "button-name": { "id": "button-name", "title": "Buttons do not have an accessible name", "score": 0,
              "scoreDisplayMode": "binary",
              "details": { "type": "table", "items": [
                { "node": { "type": "node", "selector": "body > button.icon", "snippet": "<button class=\"icon\">" } } ] } },
            "color-contrast": { "id": "color-contrast", "title": "Background and foreground colors do not have a sufficient contrast ratio.",
              "score": 0, "scoreDisplayMode": "binary",
              "details": { "type": "table", "items": [
                { "node": { "type": "node", "selector": "p.faint", "snippet": "<p class=\"faint\">",
                            "explanation": "Element has insufficient color contrast of 1.92" } } ] } },
            "video-caption": { "id": "video-caption", "title": "Videos have captions", "score": null, "scoreDisplayMode": "notApplicable" },
            "logical-tab-order": { "id": "logical-tab-order", "title": "The page has a logical tab order", "score": null,
              "scoreDisplayMode": "manual" },
            "errors-in-console": { "id": "errors-in-console", "title": "No browser errors logged to the console", "score": 1,
              "scoreDisplayMode": "binary" },
            "mainthread-work-breakdown": { "id": "mainthread-work-breakdown", "title": "Minimize main-thread work", "score": null,
              "scoreDisplayMode": "informative", "displayValue": "2.4 s" },
            "deprecations": { "id": "deprecations", "title": "Uses deprecated APIs", "score": 0, "scoreDisplayMode": "binary",
              "details": { "type": "table",
                "headings": [ { "key": "value", "valueType": "text", "label": "Deprecation / Warning" },
                              { "key": "source", "valueType": "source-location", "label": "Source" } ],
                "items": [ { "value": "Synchronous XMLHttpRequest on the main thread is deprecated.",
                             "source": { "type": "source-location", "url": "http://localhost:8080/big.js", "urlProvider": "network", "line": 9, "column": 4 } },
                           { "unknown": 1 } ] } },
            "robots-txt": { "id": "robots-txt", "title": "robots.txt is valid", "score": null, "scoreDisplayMode": "error",
              "errorMessage": "Lighthouse was unable to download a robots.txt file" },
            "final-screenshot": { "id": "final-screenshot", "title": "Final Screenshot", "score": null,
              "scoreDisplayMode": "informative", "details": { "type": "screenshot", "data": "data:image/jpeg;base64,AAAA" } },
            "hidden-failure": { "id": "hidden-failure", "title": "An audit no category references", "score": 0,
              "scoreDisplayMode": "binary" }
          }
        }
        """;

    [Fact]
    public void AReport_ReadsItsScoresAndMetrics_ThenWhatFailed_LargestGainFirst()
    {
        var text = LighthouseText.Summarize("report.json", Report, 20_000)!;

        Assert.StartsWith("Lighthouse report (Lighthouse 12.8.2) for http://localhost:8080/shop, mobile", text, StringComparison.Ordinal);
        Assert.Contains("Scores (out of 100): Performance 54, Accessibility 78, Best Practices 100, SEO n/a.", text, StringComparison.Ordinal);
        Assert.Contains("Metrics: First Contentful Paint 3.1 s (poor), Largest Contentful Paint 6.2 s (poor).", text, StringComparison.Ordinal);
        Assert.Contains("7 audits not passed; 2 passed, 1 not applicable, 1 to check by hand, 1 informative (not scored, not listed), 1 errored.",
                        text, StringComparison.Ordinal);
        // Time saved first, then bytes saved, then the accessibility audits by weight.
        var order = new[] { "Reduce unused JavaScript", "Eliminate render-blocking", "Efficiently encode images",
                            "Image elements do not have", "Buttons do not have", "Background and foreground" }
            .Select(t => text.IndexOf(t, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains("[Performance] Reduce unused JavaScript — Est savings of 880 KiB (saves ~2.0 s, saves ~880 KiB)", text, StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080/big.js — 880 KiB wasted", text, StringComparison.Ordinal);
        Assert.Contains("body > img  <img src=\"hero.png\" width=\"400\" height=\"200\">", text, StringComparison.Ordinal);
        Assert.Contains("p.faint", text, StringComparison.Ordinal);
        Assert.Contains("robots.txt is valid (Lighthouse was unable to download a robots.txt file)", text, StringComparison.Ordinal);
        // A table is read by its columns, a location counted from 1, and a row of another shape still counted.
        Assert.Contains("Synchronous XMLHttpRequest on the main thread is deprecated. · http://localhost:8080/big.js:10:5", text, StringComparison.Ordinal);
        Assert.Contains("[1 row of a shape not read here", text, StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080/hero.png — 200 KiB wasted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportWithInsights_ListsThemOnce_WithoutTheDiagnosticsTheyReplace()
    {
        var report = """
            { "lighthouseVersion": "12.8.2", "finalDisplayedUrl": "http://localhost/",
              "categories": { "performance": { "title": "Performance", "score": 0.5, "auditRefs": [
                { "id": "render-blocking-insight", "weight": 0, "group": "insights" },
                { "id": "render-blocking-resources", "weight": 0, "group": "diagnostics" } ] } },
              "audits": {
                "render-blocking-insight": { "title": "Render blocking requests", "score": 0, "scoreDisplayMode": "metricSavings",
                  "metricSavings": { "FCP": 300 } },
                "render-blocking-resources": { "title": "Eliminate render-blocking resources", "score": 0, "scoreDisplayMode": "metricSavings",
                  "metricSavings": { "FCP": 300 } } } }
            """;

        var text = LighthouseText.Summarize("lhr.json", report, 20_000)!;

        Assert.Contains("Render blocking requests", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Eliminate render-blocking resources", text, StringComparison.Ordinal);
        Assert.Contains("1 audit not passed", text, StringComparison.Ordinal);
        Assert.Contains("The older diagnostics the insights replace are left out.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PassedHiddenAndMetricAudits_AreNotListed()
    {
        var text = LighthouseText.Summarize("report.json", Report, 20_000)!;

        Assert.DoesNotContain("webfont", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No browser errors", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Videos have captions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An audit no category references", text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[Performance] First Contentful Paint", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHtmlReport_ReadsAsItsJson()
    {
        // The HTML report carries its JSON in one script, every "<" written as its JSON escape.
        var html = "<!doctype html><html><head><title>Lighthouse Report</title></head><body><script>window.__LIGHTHOUSE_JSON__ = "
                 + Report.Replace("<", "\\u003c", StringComparison.Ordinal) + ";</script><script>/* renderer */</script></body></html>";

        var text = LighthouseText.Summarize("localhost_8080-20261122.report.html", html, 20_000)!;

        Assert.Contains("Scores (out of 100): Performance 54", text, StringComparison.Ordinal);
        Assert.Contains("<button class=\"icon\">", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APageSpeedAnswer_ReadsItsLighthouseResult_AndTheRealUsersFigures()
    {
        // CrUX writes the layout shift times 100, every other metric in milliseconds.
        var psi = """
            { "id": "https://example.org/", "loadingExperience": { "overall_category": "SLOW", "origin_fallback": true, "metrics": {
                "LARGEST_CONTENTFUL_PAINT_MS": { "percentile": 4200, "category": "SLOW" },
                "CUMULATIVE_LAYOUT_SHIFT_SCORE": { "percentile": 5, "category": "FAST" } } }, "lighthouseResult":
            """ + Report + "}";

        var text = LighthouseText.Summarize("psi.json", psi, 20_000)!;

        Assert.Contains("Real users (Chrome UX Report, 75th percentile), overall SLOW — for the whole site: this page has too few "
                      + "visits of its own: LARGEST_CONTENTFUL_PAINT_MS 4.2 s (SLOW), CUMULATIVE_LAYOUT_SHIFT_SCORE 0.05 (FAST).",
                        text, StringComparison.Ordinal);
        Assert.Contains("Reduce unused JavaScript", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRun_SaysItsScoresMeasureNothing()
    {
        var failed = Report.Replace("\"runWarnings\": []",
            "\"runWarnings\": [], \"runtimeError\": { \"code\": \"NO_FCP\", \"message\": \"The page did not paint any content.\" }",
            StringComparison.Ordinal);

        var text = LighthouseText.Summarize("report.json", failed, 20_000)!;

        Assert.Contains("⚠ The run failed (NO_FCP: The page did not paint any content.): its scores do not measure the page.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheBudgetLeavesOut_IsCounted()
    {
        var text = LighthouseText.Summarize("report.json", Report, 1_000)!;

        Assert.Contains("Reduce unused JavaScript", text, StringComparison.Ordinal);
        Assert.Matches(@"\[\d+ more audits? not passed, not shown: read_file with start_line pages the raw report\]", text);
        Assert.True(text.Length < 1_000, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("package.json", """{ "name": "shop", "version": "1.0.0" }""")]
    [InlineData("index.html", "<html><body><h1>Shop</h1></body></html>")]
    [InlineData("notes.json", """{ "lighthouseVersion": "12", "text": "no audits here" }""")]
    [InlineData("report.txt", Report)]
    public void AnyOtherFile_ReadsAsItsLines(string name, string text) =>
        Assert.Null(LighthouseText.Summarize(name, text, 20_000));

    [Fact]
    public async Task ReadFile_GivesTheReport_AndItsLinesWhenAsked()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-lighthouse-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "lighthouse.json"), Report);
            var tool = new Inferpal.Services.Tools.ReadFileTool(() => dir);

            var report = await tool.ExecuteAsync(System.Text.Json.JsonDocument.Parse("""{ "path": "lighthouse.json" }""").RootElement, default);
            var lines = await tool.ExecuteAsync(System.Text.Json.JsonDocument.Parse("""{ "path": "lighthouse.json", "start_line": 1, "end_line": 3 }""").RootElement, default);

            Assert.Contains("Scores (out of 100)", report, StringComparison.Ordinal);
            Assert.Contains("\"lighthouseVersion\"", lines, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
