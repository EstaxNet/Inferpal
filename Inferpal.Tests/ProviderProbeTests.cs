using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure decision logic of the backend auto-detection probe (which signature endpoint
// responded → which provider), plus the base-URL root derivation. No network.
[Collection("Diagnostics")]
public class ProviderProbeTests
{
    [Theory]
    [InlineData(true,  false, false, InferenceProviderFactory.Ollama)]
    [InlineData(false, true,  false, InferenceProviderFactory.LmStudio)]
    [InlineData(false, false, true,  InferenceProviderFactory.OpenAiCompatible)]
    [InlineData(false, false, false, null)]
    // Priority: Ollama's /api/tags wins even if a generic /v1 also answers (Ollama exposes both).
    [InlineData(true,  false, true,  InferenceProviderFactory.Ollama)]
    // LM Studio's native endpoint wins over the generic /v1 it also exposes.
    [InlineData(false, true,  true,  InferenceProviderFactory.LmStudio)]
    public void Classify_PicksByEndpointPriority(bool ollama, bool lmStudio, bool openAi, string? expected)
        => Assert.Equal(expected, ProviderProbe.Classify(ollama, lmStudio, openAi));

    [Theory]
    [InlineData("http://localhost:11434",    "http://localhost:11434")]
    [InlineData("http://localhost:1234/v1",  "http://localhost:1234")]
    [InlineData("http://localhost:1234/v1/", "http://localhost:1234")]
    [InlineData("http://host/",              "http://host")]
    [InlineData("",                          "")]
    [InlineData(null,                        "")]
    public void RootOf_StripsV1AndTrailingSlash(string? raw, string expected)
        => Assert.Equal(expected, ProviderProbe.RootOf(raw));

    [Theory]
    // Real backend shapes — the discriminating root property is present.
    [InlineData("{\"models\":[]}",            "models", true)]
    [InlineData("{\"data\":[],\"object\":\"list\"}", "data", true)]
    // A reverse proxy that returns 200 with an HTML page (or anything non-JSON) must NOT pass.
    [InlineData("<!DOCTYPE html><html></html>", "models", false)]
    [InlineData("",                           "models", false)]
    [InlineData(null,                         "models", false)]
    // Valid JSON but the wrong shape (e.g. Ollama-style body probed for an OpenAI property).
    [InlineData("{\"models\":[]}",            "data",   false)]
    // A JSON array, not an object, is not a models listing.
    [InlineData("[]",                         "data",   false)]
    public void HasRootProperty_RequiresJsonObjectWithProperty(string? body, string property, bool expected)
        => Assert.Equal(expected, ProviderProbe.HasRootProperty(body, property));

    // The connection badge applies the same rule as the detection.
    //
    // It was written here all along ("a bare status code is not enough") and the badge did not apply
    // it: CheckConnectionAsync concluded "connected" from IsSuccessStatusCode alone. The body below
    // is not invented - it is the MEASURED answer of an LM Studio instance behind a reverse proxy,
    // queried on Ollama's native endpoint.

    [Theory]
    // What a real backend returns, on the endpoint that signs it.
    [InlineData("{\"models\":[]}", "models", true)]
    [InlineData("{\"object\":\"list\",\"data\":[{\"id\":\"devstral\"}]}", "data", true)]
    // The measurement: HTTP 200, an error body, no "models" property. A green badge until now.
    [InlineData("{\"error\":\"Unexpected endpoint or method. (GET /api/tags)\"}", "models", false)]
    // A reverse proxy serving its landing page on every unknown route.
    [InlineData("<!DOCTYPE html><html><body>nginx</body></html>", "models", false)]
    [InlineData("", "data", false)]
    public void ConfirmsBackendPayload_RefusesA2xxThatIsNotTheConfiguredBackend(string body, string property, bool expected)
        => Assert.Equal(expected,
            InferenceProviderBase.ConfirmsBackendPayload("http://srv/api/tags", body, property, "Test", "ollama"));

    /// <summary>
    /// A refusal must leave something to investigate: the recorded line names the endpoint probed,
    /// the expected property and the body received - never a cause the code cannot know.
    /// </summary>
    [Fact]
    public void ConfirmsBackendPayload_RecordsWhatWasObserved()
    {
        Diagnostics.Clear();

        Assert.False(InferenceProviderBase.ConfirmsBackendPayload(
            "http://srv/api/tags",
            "{\"error\":\"Unexpected endpoint or method. (GET /api/tags)\"}",
            "models",
            "Ollama.CheckConnection",
            "ollama"));

        var entry = Assert.Single(Diagnostics.Snapshot(), e => e.Context == "Ollama.CheckConnection");
        Assert.Contains("http://srv/api/tags", entry.Detail);
        Assert.Contains("models", entry.Detail);
        Assert.Contains("Unexpected endpoint or method", entry.Detail);

        // Issue #8: the line NAMES the configured backend. Without it, "not as the configured
        // backend" sends people to look at the SERVER, when the cause can be an Ollama client
        // pointed at an LM Studio URL because the `provider` setting is not the one they think
        // they picked. The reporter had no way to know which of the two was misconfigured.
        Assert.Contains("ollama", entry.Detail);

        // And a success records nothing: a channel that speaks on the ordinary path stops being read.
        Diagnostics.Clear();
        Assert.True(InferenceProviderBase.ConfirmsBackendPayload(
            "http://srv/api/tags", "{\"models\":[]}", "models", "Ollama.CheckConnection", "ollama"));
        Assert.Empty(Diagnostics.Snapshot());
    }
}
