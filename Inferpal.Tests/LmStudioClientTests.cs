using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers LM Studio's native-payload parsing that the OpenAI-compatible base can't do — specifically
// extracting the *loaded* context window (n_ctx the running instance was loaded with), which feeds the
// proactive context-fit guard. The model's max_context_length is its capability, not the loaded n_ctx,
// so a model can be loaded well below what it supports (the root of the LM Studio context-overflow bug).
public class LmStudioClientTests
{
    private static List<JsonElement> Instances(string json)
        => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    [Fact]
    public void LoadedContextFromInstances_ReadsNestedConfigContextLength()
    {
        // v1 nests the loaded n_ctx under loaded_instances[].config.context_length.
        var instances = Instances("""
            [{ "instance_id": "qwen/qwen3-27b", "config": { "context_length": 8192, "flash_attention": true } }]
            """);
        Assert.Equal(8192, LmStudioClient.LoadedContextFromInstances(instances));
    }

    [Fact]
    public void LoadedContextFromInstances_ToleratesFlatShapes()
    {
        Assert.Equal(16384, LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "context_length": 16384 }]""")));
        Assert.Equal(4096, LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "loaded_context_length": 4096 }]""")));
    }

    [Fact]
    public void LoadedContextFromInstances_NoneOrEmpty_ReturnsNull()
    {
        Assert.Null(LmStudioClient.LoadedContextFromInstances(null));
        Assert.Null(LmStudioClient.LoadedContextFromInstances([]));
        // An instance with no context field at all → unknown, not zero (must not block requests).
        Assert.Null(LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "instance_id": "x", "config": { "flash_attention": true } }]""")));
        // A zero/garbage value is rejected (treated as unknown).
        Assert.Null(LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "context_length": 0 }]""")));
    }
}
