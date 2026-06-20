using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure decision logic of the backend auto-detection probe (which signature endpoint
// responded → which provider), plus the base-URL root derivation. No network.
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
}
