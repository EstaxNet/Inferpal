using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A chat gesture never waits on the editor to open a file. Visual Studio's answer to an open can wait on the window's
/// own context, which the gesture holds until it returns: then it never comes, and every later gesture — Send, New
/// conversation — queues behind it, the chat frozen without a word (seen with "See the text read"). The open is started
/// and left running, bounded; the gesture returns at once. Windows only: the window is Visual Studio's.
/// </summary>
[Collection(SignalCollection.Name)]   // the window writes the active solution's signal
public sealed class EditorOpenNeverBlocksTheChatTests
{
    /// <summary>An editor that is asked to open a file and never answers.</summary>
    private static TaskCompletionSource<Uri> NeverAnswering(VsWindowHarness vs)
    {
        var asked = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        vs.Window.OpenDocumentForTests = (uri, _) =>
        {
            asked.TrySetResult(uri);
            return new TaskCompletionSource().Task;
        };
        return asked;
    }

    private static async Task ReturnsWithinTheBudget(Task gesture, string what)
    {
        var first = await Task.WhenAny(gesture, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(first == gesture, $"{what} is still waiting on the editor: the chat would be frozen behind it.");
        await gesture;
    }

    [Fact]
    public async Task SeeTheTextRead_ReturnsAtOnce_AndTheNextQuestionIsAnswered_ThoughTheEditorNeverAnswers()
    {
        var fake = new FakeInferenceProvider
        {
            ModelNames = ["granite-4.2-8b"],
            Installed = [new("granite-4.2-8b", 1, SeesImages: false)],
        };
        var config = new InferpalConfig { DefaultModel = "granite-4.2-8b", IsFirstRun = false, RagEnabled = false };
        config.Save();
        using var vs = new VsWindowHarness(fake, config: config);
        var asked = NeverAnswering(vs);
        await (Task)typeof(InferpalToolWindowData).GetMethod("AttachImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vs.Window, [Screenshot(), false, CancellationToken.None])!;
        Assert.True(vs.Window.HasImageTextBanner);   // witness: the banner that offers "See the text read" is up

        await ReturnsWithinTheBudget(vs.Window.ImageSeeTextCommand.ExecuteAsync(null, null, CancellationToken.None),
                                     "\"See the text read\"");
        Assert.EndsWith("text-read-by-ocr.txt", (await asked.Task).LocalPath);   // the open was asked all the same

        await ReturnsWithinTheBudget(vs.AskAsync("why does the build fail?"), "The next question");
        Assert.Single(vs.Fake.AgentRuns);
    }

    [Fact]
    public async Task ACommandThatCreatesAFile_ReturnsAtOnce_AndTheNextQuestionIsAnswered_ThoughTheEditorNeverAnswers()
    {
        using var vs = new VsWindowHarness();
        var asked = NeverAnswering(vs);

        await ReturnsWithinTheBudget(vs.AskAsync("/rules init"), "/rules init");
        Assert.True(File.Exists((await asked.Task).LocalPath));   // the file was written, and its open asked

        await ReturnsWithinTheBudget(vs.AskAsync("hello"), "The next question");
        Assert.Single(vs.Fake.AgentRuns);
    }

    /// <summary>
    /// The property, on the source: a gesture of Visual Studio's chat or settings window never awaits Visual Studio to open
    /// a document or show a window — it goes through <c>EditorCalls.Start</c>. The editor commands (<c>Commands/</c>) run
    /// outside those windows and Add Tests edits the document it opens: they are not gestures of a window.
    /// </summary>
    [Fact]
    public void NoGestureOfTheWindows_AwaitsVisualStudio()
    {
        var calls = 0;
        var awaited = new List<string>();
        var files = ConventionCoverageTests.ViewModelSources()
            .Append(Path.Combine(Path.GetDirectoryName(ConventionCoverageTests.ViewModelSources()[0])!, "..", "InferpalSettingsToolWindow.cs"));
        foreach (var file in files)
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            calls += Regex.Matches(code, @"(OpenTextDocumentAsync|ShowToolWindowAsync)[<(]").Count;
            foreach (Match m in Regex.Matches(code, @"await\s+[\w.()]*(OpenTextDocumentAsync|ShowToolWindowAsync)[<(]"))
                awaited.Add($"{Path.GetFileName(file)}: {m.Value}");
        }
        Assert.True(calls >= 6, $"Only {calls} call(s) to open a document or show a window found: the scan reads nothing.");   // WITNESS
        Assert.True(awaited.Count == 0, "Awaited where a gesture would wait on Visual Studio: " + string.Join("; ", awaited));
    }

    private static string Screenshot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "scans", "error-screenshot.png");
    }
}
