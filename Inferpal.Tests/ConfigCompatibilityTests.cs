using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A user upgrades with the <c>config.json</c> they have: the one the last published version wrote. Read by this version
/// it keeps every key and its value, saves back without loss — what it does not know included —, each key added since
/// says what such a configuration gets, and a factory value changed since says why.
/// </summary>
/// <remarks>
/// The two fixtures are files the last published version itself wrote — its own <c>Save</c>, run at its tag, bytes as
/// they are (BOM, line breaks) —, not files written to look like them: <c>config-X.json</c> for a user who changed EVERY
/// setting (a key that stops being read then shows: no value in it is the factory one), <c>config-X-factory.json</c> for
/// one who changed none. A new release adds its own pair and moves <see cref="Release"/> to it.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class ConfigCompatibilityTests : IDisposable
{
    /// <summary>The last published version, whose files the fixtures are.</summary>
    private const string Release = "1.8.3";

    /// <summary>
    /// The keys added since <see cref="Release"/>, each with the value a configuration written before it gets — the
    /// factory value — and why that keeps what the user had. ⚠ A key added without a line here fails the suite: say what
    /// the configurations already out there get before adding it.
    /// </summary>
    private static readonly Dictionary<string, (string Json, string Why)> AddedSince = new(StringComparer.Ordinal)
    {
        ["inlineCompletionRunsOn"]   = ("\"chat\"", "completions stay on the chat's server, as before"),
        ["inlineCompletionBaseUrl"]  = ("\"\"", "read only when completions run on another server"),
        ["inlineCompletionProvider"] = ("\"\"", "read only when completions run on another server"),
        ["inlineCompletionApiKey"]   = ("\"\"", "read only when completions run on another server"),
        ["ragEmbeddingRunsOn"]       = ("\"chat\"", "code search stays on the chat's server, as before"),
        ["ragEmbeddingBaseUrl"]      = ("\"\"", "read only when code search runs on another server"),
        ["ragEmbeddingProvider"]     = ("\"\"", "read only when code search runs on another server"),
        ["ragEmbeddingApiKey"]       = ("\"\"", "read only when code search runs on another server"),
        ["inlineCompletionTrigger"]  = ("\"auto\"", "a suggestion after each pause in typing, as before"),
        ["contextWindowSizeAuto"]    = ("0", "no window of the product's: a window other than the factory one stays the person's"),
    };

    /// <summary>
    /// The factory values changed since <see cref="Release"/>, each with why. A user who saved once keeps the old value
    /// (every key is written), one whose file predates the key gets the new one: ⚠ a change without a line here fails the
    /// suite — say who gets what before changing it.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultChangedSince = new(StringComparer.Ordinal);

    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"compat-{Guid.NewGuid():N}.json");

    public ConfigCompatibilityTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private static string Fixture(string suffix = "") =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", $"config-{Release}{suffix}.json");

    private static JsonObject Parse(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>The release's file, its bytes as it wrote them, read by this version.</summary>
    private InferpalConfig LoadReleaseFile()
    {
        File.Copy(Fixture(), _path, overwrite: true);
        return InferpalConfig.Load();
    }

    /// <summary>Every key the last version wrote is still a setting, with the value it wrote: none renamed, dropped or
    /// read in another type.</summary>
    [Fact]
    public void EveryKeyTheLastReleaseWrote_IsReadWithItsValue()
    {
        var file    = Parse(Fixture());
        var factory = Parse(Fixture("-factory"));
        // Witnesses: the file is a whole release's, and none of its values is the factory one — read or ignored, a key
        // would otherwise look the same.
        Assert.True(file.Count >= 50, $"the fixture holds {file.Count} keys: it is not a file {Release} wrote");
        Assert.Empty(file.Where(kv => JsonNode.DeepEquals(kv.Value, factory[kv.Key])).Select(kv => kv.Key));

        var read = LoadReleaseFile().SnapshotNow();

        foreach (var (key, value) in file)
        {
            Assert.True(read.ContainsKey(key), $"'{key}', written by {Release}, is no longer read: a user who set it loses it");
            Assert.True(JsonNode.DeepEquals(value, read[key]),
                        $"'{key}' written by {Release} as {value?.ToJsonString()} is read as {read[key]?.ToJsonString()}");
        }
    }

    /// <summary>Saved by this version after a change, the file keeps every other key and value — and a key this version
    /// does not know (a later version's, or a hand-written one).</summary>
    [Fact]
    public void SavedAgain_TheFileLosesNothing()
    {
        var file = Parse(Fixture());
        file["aKeyOfALaterVersion"] = "kept";
        // Written as the release writes: BOM and Windows line breaks.
        File.WriteAllText(_path, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n"),
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var config = InferpalConfig.Load();

        config.ContextWindowSize = 16_384;
        config.Save();

        var written = Parse(_path);
        Assert.Equal(16_384, written["contextWindowSize"]!.GetValue<int>());                                  // witness
        foreach (var (key, value) in file.Where(kv => kv.Key != "contextWindowSize"))
            Assert.True(JsonNode.DeepEquals(value, written[key]),
                        $"'{key}' was {value?.ToJsonString()}, saved as {written[key]?.ToJsonString()}");
    }

    /// <summary>Every key added since the last version is declared with what the configurations already out there get —
    /// and they get exactly that.</summary>
    [Fact]
    public void EveryKeyAddedSinceTheLastRelease_SaysWhatExistingConfigurationsGet()
    {
        var file  = Parse(Fixture());
        var now   = new InferpalConfig().SnapshotNow();
        var added = now.Select(kv => kv.Key).Where(k => !file.ContainsKey(k)).ToHashSet(StringComparer.Ordinal);

        Assert.True(added.SetEquals(AddedSince.Keys),
                    "keys added since " + Release + " without a line in AddedSince: "
                    + string.Join(", ", added.Except(AddedSince.Keys)) + " — say what existing configurations get"
                    + (AddedSince.Keys.Except(added).Any() ? "; declared but not added: " + string.Join(", ", AddedSince.Keys.Except(added)) : ""));

        var read = LoadReleaseFile().SnapshotNow();
        foreach (var (key, (json, why)) in AddedSince)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), read[key]),
                        $"'{key}' gives a {Release} configuration {read[key]?.ToJsonString()}, not {json} ({why})");
    }

    /// <summary>A factory value is the behaviour of everyone who never touched the setting: changed since the last
    /// version, it is declared with why.</summary>
    [Fact]
    public void EveryFactoryValueChangedSinceTheLastRelease_IsDeclared()
    {
        var factory = Parse(Fixture("-factory"));
        var now     = new InferpalConfig().SnapshotNow();

        var changed = factory.Where(kv => now.ContainsKey(kv.Key) && !JsonNode.DeepEquals(kv.Value, now[kv.Key]))
                             .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        Assert.True(changed.SetEquals(DefaultChangedSince.Keys),
                    "factory values changed since " + Release + " without a line in DefaultChangedSince: "
                    + string.Join(", ", changed.Except(DefaultChangedSince.Keys).Select(k => $"{k} ({factory[k]?.ToJsonString()} → {now[k]?.ToJsonString()})"))
                    + (DefaultChangedSince.Keys.Except(changed).Any() ? "; declared but unchanged: " + string.Join(", ", DefaultChangedSince.Keys.Except(changed)) : ""));
    }

    /// <summary>
    /// A behaviour no key holds: completions became possible on an OpenAI-compatible server. A configuration of the last
    /// version chatting on one — completions on by default, no completion model — gets none it did not have: the chat
    /// model there is often a large one on a shared server. A completion model chosen turns them on.
    /// </summary>
    [Fact]
    public void AnOpenAiCompatibleServer_GetsNoCompletionsItDidNotHave()
    {
        var file = Parse(Fixture("-factory"));
        file["provider"] = "openai-compatible";
        file["baseUrl"]  = "http://192.168.1.20:8080";
        file["defaultModel"] = "qwen3-coder-30b";
        File.WriteAllText(_path, file.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var config = InferpalConfig.Load();
        Assert.True(config.InlineCompletionEnabled);                                                       // witness

        Assert.Null(RoleServers.CompletionModel(config, null));

        config.InlineCompletionModel = "qwen2.5-coder:1.5b";
        Assert.Equal("qwen2.5-coder:1.5b", RoleServers.CompletionModel(config, null));
        // Reference arm: Ollama completed with the chat model in the last version, and still does.
        config.InlineCompletionModel = "";
        config.Provider = "ollama";
        Assert.Equal("qwen3-coder-30b", RoleServers.CompletionModel(config, null));
    }

    /// <summary>What the last version's settings did, they still do: completions and code search on the chat's server
    /// with its key, the end of a long run announced or not as the user chose.</summary>
    [Fact]
    public void TheLastReleasesSettings_KeepTheirMeaning()
    {
        var config = LoadReleaseFile();

        Assert.Equal((config.Provider, config.BaseUrl, config.ApiKey), RoleServers.Of(config, ServerRole.Completion));
        Assert.Equal((config.Provider, config.BaseUrl, config.ApiKey), RoleServers.Of(config, ServerRole.Search));
        Assert.Equal(config.InlineCompletionModel, RoleServers.CompletionModel(config, null));
        // The fixture's user turned the sound off: nothing announces a long run in either editor.
        Assert.False(config.RunEndSoundEnabled);
        Assert.False(Services.Agent.ChatTurnPolicy.AnnouncesEnd(config, TimeSpan.FromMinutes(5)));
    }
}
