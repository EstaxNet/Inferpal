using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Presentation;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Unload" is refused while ANY Inferpal on the machine is answering — not only this process.
/// </summary>
/// <remarks>
/// The refusal read the GPU lease of the process that asked: the settings of Visual Studio unloaded the model VS Code (or
/// a Zed thread's agent) was answering with, the answer breaking mid-stream. The machine-wide channel already existed —
/// the ghost text reads it to stay quiet during any chat.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class UnloadWhileAnotherEditorAnswersTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private const int OtherEditor = 424242;

    public void Dispose() => _scratch.Dispose();

    private static FakeInferenceProvider Server(List<string> unloaded) => new()
    {
        Running = [new RunningModelInfo("qwen3:27b", 1L << 30, "")],
        OnUnload = name => { unloaded.Add(name); return true; },
    };

    [Fact]
    public async Task AnAnswerInAnotherEditor_RefusesTheUnload()
    {
        SignalFile._isProcessAliveOverride = pid => pid == OtherEditor || pid == Environment.ProcessId;
        File.WriteAllText(Path.Combine(_scratch.Path, $"chat_busy.{OtherEditor}.json"),
            JsonSerializer.Serialize(new ChatBusyState(OtherEditor, SignalFile.Now.ToUnixTimeMilliseconds())));
        var unloaded = new List<string>();

        var said = await ModelUnloader.UnloadAsync(Server(unloaded), new InferpalConfig(), null, CancellationToken.None);

        Assert.Equal(Strings.UnloadWhileAnswering, said);
        Assert.Empty(unloaded);
    }

    [Fact]
    public async Task NoAnswerAnywhere_Unloads()
    {
        // Reference arm.
        var unloaded = new List<string>();

        await ModelUnloader.UnloadAsync(Server(unloaded), new InferpalConfig(), null, CancellationToken.None);

        Assert.Equal(["qwen3:27b"], unloaded);
    }
}
