using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Documents;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>read_file</c> on a picture of the workspace: the image is seen, not read as text. A tool result is text (LM Studio
/// refuses an image in a <c>tool</c> message), so the loop puts the image in a message of its own after the batch's
/// results — only to a model that reads images; any other is told it was not sent. An image is counted by its pixels.
/// </summary>
public sealed class ToolReadImageTests : IDisposable
{
    // A 1×1 PNG.
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-toolimage-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Picture(string name = "mockup.png")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Convert.FromBase64String(Png1x1));
        return path;
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ReadFile_OnAnImage_LeavesItForTheLoop_AndSaysWhatItIs()
    {
        var inbox = new ToolImageInbox();
        var tool = new ReadFileTool(() => _root, images: inbox);

        var said = await tool.ExecuteAsync(Args(new { path = Picture() }), default);

        Assert.Contains("'mockup.png' is an image (image/png, 1×1)", said);
        var taken = inbox.Take();
        Assert.Equal("mockup.png", Assert.Single(taken).Name);
        Assert.Empty(inbox.Take());   // taken once
    }

    [Fact]
    public async Task ReadFile_WithNoInbox_StillNamesTheFileBinary()
    {
        var said = await new ReadFileTool(() => _root).ExecuteAsync(Args(new { path = Picture() }), default);

        Assert.Contains("is a binary file", said);
    }

    [Fact]
    public async Task AnImageOverTheSizeLimit_IsNeitherLoadedNorShown()
    {
        var path = Picture("huge.png");
        using (var f = new FileStream(path, FileMode.Open)) f.SetLength(ChatImages.MaxBytes + 1);
        var inbox = new ToolImageInbox();

        var said = await new ReadFileTool(() => _root, images: inbox).ExecuteAsync(Args(new { path }), default);

        Assert.Contains("larger than", said);
        Assert.Empty(inbox.Take());
    }

    private static ChatImage Image() => new("mockup.png", Png1x1, "image/png", 1, 1, 70);

    [Fact]
    public async Task AModelThatReadsImages_GetsThemInAMessageOfTheLoops()
    {
        var messages = new List<ChatMessageDto> { new("tool", "'mockup.png' is an image") };

        await ToolImages.AppendAsync(messages, [Image()], "m", _ => Task.FromResult<bool?>(true), default);

        Assert.Equal("assistant", messages[1].Role);   // after a tool result, never a user message straight away
        var added = messages[^1];
        Assert.Equal("user", added.Role);
        Assert.True(added.IsScaffolding);
        Assert.Equal(Png1x1, Assert.Single(added.Images!));
        Assert.Contains("mockup.png", added.Content);
    }

    [Theory]
    [InlineData(false, "does not read images")]
    [InlineData(null, "does not say whether")]
    public async Task AnyOtherModel_IsToldTheImageWasNotSent_AndGetsNone(bool? sees, string why)
    {
        var messages = new List<ChatMessageDto> { new("tool", "'mockup.png' is an image") };

        await ToolImages.AppendAsync(messages, [Image()], "granite", _ => Task.FromResult(sees), default);

        var added = messages[^1];
        Assert.Null(added.Images);
        Assert.Contains("were not sent", added.Content);
        Assert.Contains(why, added.Content);
        Assert.Contains("granite", added.Content);
    }

    [Fact]
    public async Task NoImageRead_AddsNothing()
    {
        var messages = new List<ChatMessageDto> { new("tool", "text") };
        await ToolImages.AppendAsync(messages, [], "m", _ => throw new InvalidOperationException("never asked"), default);
        Assert.Single(messages);
    }

    [Fact]
    public void AnImage_IsCountedByItsPixels_AndAtMostTheCeiling()
    {
        Assert.Equal(1 * 1 / 1024 + 16, ChatImages.EstimatedTokens(Png1x1));
        Assert.Equal(ChatImages.TokensPerImageCeiling, ChatImages.EstimatedTokens("not an image at all, no header"));
        var withImage = new List<ChatMessageDto> { new("user", "q") { Images = [Png1x1] } };
        Assert.Equal(AgentOrchestrator.EstimateChars([new("user", "q")]) + ChatImages.EstimatedTokens(Png1x1) * 4, AgentOrchestrator.EstimateChars(withImage));
    }

    // ── The loop puts the image in front of the model, after the results of the batch ───────────────────────────────

    private sealed class ImageRegistry : IToolRegistry
    {
        private readonly ToolImageInbox _inbox = new();
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new ToolDefinition("function", new ToolFunction("read_file", "read_file", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            _inbox.Put(Image());
            return Task.FromResult("'mockup.png' is an image (image/png, 1×1); it follows this result.");
        }
        public IReadOnlyList<ChatImage> TakeReadImages() => _inbox.Take();
    }

    [Fact]
    public void EveryRegistryThatWrapsAnother_PassesTheImagesOn()
    {
        // A decorator that answers by default loses the image the tool said it read; the next run would get it instead.
        var wrappers = new[] { typeof(ToolRegistry).Assembly, typeof(Inferpal.Host.HostServer).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface && typeof(IToolRegistry).IsAssignableFrom(t)
                        && t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IToolRegistry))))
            .ToList();
        Assert.True(wrappers.Count >= 4, $"witness: the wrapping registries are found ({wrappers.Count})");
        var missing = wrappers.Where(t => t.GetMethod(nameof(IToolRegistry.TakeReadImages),
                          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly) is null)
                      .Select(t => t.FullName).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public async Task AnImageLeftByAnEarlierRun_IsNotSentToTheNextOne()
    {
        const string plan = """{"goal":"answer","steps":[{"i":1,"desc":"answer","tool":"none"}]}""";
        var seen = new List<List<ChatMessageDto>>();
        var turn = 0;
        var fake = new FakeInferenceProvider { Installed = [new InstalledModelInfo("m", 1, SeesImages: true)] };
        fake.OnChatRequest = (_, messages, _, _) =>
        {
            seen.Add([.. messages]);
            return Task.FromResult(turn++ switch
            {
                0 => new ChatTurnResult(plan, null, 0, 0),
                1 => new ChatTurnResult(string.Empty,
                        [new ToolCallDto(new ToolCallFunction("list_files", JsonSerializer.SerializeToElement(new { path = "." })))], 0, 0),
                _ => new ChatTurnResult("Done.", null, 0, 0),
            });
        };
        var tools = new LeftoverRegistry();

        await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 4 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "List the files.")], tools: tools,
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null,
                      onStreamReset: null, ct: CancellationToken.None);

        Assert.True(seen.Count >= 3, "witness: a request followed the tool");
        Assert.DoesNotContain(seen.SelectMany(s => s), m => m.Images is { Count: > 0 });
    }

    private sealed class LeftoverRegistry : IToolRegistry
    {
        private readonly ToolImageInbox _inbox = new();
        public LeftoverRegistry() => _inbox.Put(Image());   // read by a run that never sent it
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new ToolDefinition("function", new ToolFunction("list_files", "list_files", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("a.cs");
        public IReadOnlyList<ChatImage> TakeReadImages() => _inbox.Take();
    }

    [Fact]
    public void RunCompaction_TakesAnOldImageOutFirst_AndSaysHowToSeeItAgain()
    {
        var big = new string('x', 3_000);
        var messages = new List<ChatMessageDto>
        {
            new("system", "s"), new("user", "task"),
            new("assistant", "") { },
            new("tool", "'a.png' is an image"),
            new("user", "[The image(s) read_file read: a.png.]") { IsScaffolding = true, Images = [Png1x1x2000()] },
            new("assistant", ""),
            new("tool", big),
        };
        var before = AgentOrchestrator.EstimateChars(messages) / 4;

        AgentOrchestrator.CompactRunContext(messages, anchorCount: 2, budget: before - 500);

        Assert.Null(messages[4].Images);
        Assert.Contains("read the file again", messages[4].Content);
        Assert.Equal(big, messages[6].Content);   // the text of the last batch was not needed
    }

    // A PNG header declaring 2000×1000: weighed at its pixels, never decoded.
    private static string Png1x1x2000()
    {
        var bytes = Convert.FromBase64String(Png1x1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 2000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 1000);
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public async Task TheOrchestrator_SendsTheImage_RightAfterTheToolResult_ToAModelThatReadsImages()
    {
        const string plan = """{"goal":"match the mockup","steps":[{"i":1,"desc":"look at the mockup","tool":"read_file"}]}""";
        var seen = new List<List<ChatMessageDto>>();
        var turn = 0;
        var fake = new FakeInferenceProvider { Installed = [new InstalledModelInfo("m", 1, SeesImages: true)] };
        fake.OnChatRequest = (_, messages, _, _) =>
        {
            seen.Add([.. messages]);
            return Task.FromResult(turn++ switch
            {
                0 => new ChatTurnResult(plan, null, 0, 0),
                1 => new ChatTurnResult(string.Empty,
                        [new ToolCallDto(new ToolCallFunction("read_file", JsonSerializer.SerializeToElement(new { path = "mockup.png" })))], 0, 0),
                _ => new ChatTurnResult("The form has two fields.", null, 0, 0),
            });
        };

        await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 4 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Match docs/mockup.png.")], tools: new ImageRegistry(),
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null,
                      onStreamReset: null, ct: CancellationToken.None);

        var after = seen[2];
        var tool = after.FindLastIndex(m => m.Role == "tool");
        Assert.True(tool >= 0, "witness: the tool ran");
        var image = after.FindIndex(tool, m => m.Images is { Count: > 0 });
        Assert.Equal("assistant", after[tool + 1].Role);   // the turn strict chat templates need between a tool result and a user message
        Assert.Equal(tool + 2, image);
        Assert.True(after[image].IsScaffolding);
    }
}
