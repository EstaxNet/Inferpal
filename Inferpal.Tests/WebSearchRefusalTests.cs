using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Measured against the live engine: DuckDuckGo answers a second search made within a few seconds
/// with HTTP 202 and its anti-bot page — even for a query it answered with ten results just before —
/// and keeps doing so for minutes. That page holds no result, so it parsed as "No results.", which the
/// model reads as a fact about the web: every search of the session after the first "found nothing".
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class WebSearchRefusalTests
{
    // The identifying markup of the page as served (2026-09), not the page itself.
    private const string Challenge =
        "<html><body><div class=\"anomaly-modal__mask\"><div class=\"anomaly-modal__modal\" data-testid=\"anomaly-modal\">" +
        "<div class=\"anomaly-modal__title\">Unfortunately, bots use DuckDuckGo too.</div>" +
        "<div class=\"anomaly-modal__description\">Please complete the following challenge to confirm this search was made by a human.</div>" +
        "</div></div><form id=\"challenge-form\" action=\"//duckduckgo.com/anomaly.js?sv=html\" method=\"POST\"></form></body></html>";

    private const string OneResult =
        "<div class=\"result\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Flearn.microsoft.com%2Fdotnet&amp;rut=x\">HttpClient</a>" +
        "<a class=\"result__snippet\" href=\"#\">Follows redirects by default.</a></div>";

    [Fact]
    public void TheAntiBotPage_IsARefusal_NotNoResults()
    {
        var answer = WebSearchTool.Render(202, Challenge, 5, null);

        Assert.Equal(Strings.WebSearchRefused, answer);
        Assert.NotEqual(Strings.NoResults, answer);
    }

    [Fact]
    public void TheAntiBotPage_IsRecognisedByItsMarkup_WhateverTheStatus()
    {
        // The status is the first tell, the markup the second: a 200 carrying the challenge is no answer either.
        Assert.Equal(Strings.WebSearchRefused, WebSearchTool.Render(200, Challenge, 5, null));
    }

    [Fact]
    public void AnOrdinaryAnswer_StillListsItsResults()
    {
        // Reference arm: the check must not swallow a real answer.
        var answer = WebSearchTool.Render(200, OneResult, 5, null);

        Assert.Contains("1. HttpClient", answer);
        Assert.Contains("https://learn.microsoft.com/dotnet", answer);
    }

    [Fact]
    public void AnAnswerWithNothingInIt_IsStillNoResults()
    {
        // Reference arm: a 200 page that is not the challenge and holds no result keeps its sentence.
        Assert.Equal(Strings.NoResults, WebSearchTool.Render(200, "<html><body><div class=\"no-results\">No results.</div></body></html>", 5, null));
    }
}
