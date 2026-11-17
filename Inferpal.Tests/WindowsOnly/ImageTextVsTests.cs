using System.IO;
using System.Reflection;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's chat window with an image for a model that does not see (pari 108, mockups OcrReady and OcrSent): the
/// banner over the composer, the text sent instead of the image and folded under the question, or — asked on the banner —
/// the question handed to the server's model that sees, with the image.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class ImageTextVsTests
{
    private static string Screenshot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "scans", "error-screenshot.png");
    }

    private static VsWindowHarness Window()
    {
        var fake = new FakeInferenceProvider
        {
            // Listed too: a server that lists none has the window adopt a model of its own.
            ModelNames = ["granite-4.2-8b", "qwen3.8-27b"],
            Installed = [new("granite-4.2-8b", 1, SeesImages: false), new("qwen3.8-27b", 1, SeesImages: true)],
        };
        // Saved: the window follows the settings file before each question.
        var config = new InferpalConfig { DefaultModel = "granite-4.2-8b", IsFirstRun = false, RagEnabled = false };
        config.Save();
        return new VsWindowHarness(fake, config: config);
    }

    /// <summary>The model each question went to, and the question as the model got it.</summary>
    private static List<(string Model, ChatMessageDto Question)> Asked(VsWindowHarness vs) =>
        [.. vs.Fake.AgentRuns.Select(r => (r.Model, r.History.Last(m => m.Role == "user")))];

    private static Task AttachAsync(VsWindowHarness vs, string path) =>
        (Task)typeof(InferpalToolWindowData).GetMethod("AttachImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vs.Window, [path, false, CancellationToken.None])!;

    [Fact]
    public async Task TheTextGoesInsteadOfTheImage_SaidOnTheBanner_AndFoldedUnderTheQuestion()
    {
        using var vs = Window();
        await AttachAsync(vs, Screenshot());

        Assert.True(vs.Window.HasImageTextBanner);
        Assert.True(vs.Window.HasImageAskOther);
        Assert.Contains("qwen3.8-27b", vs.Window.ImageAskOtherLabel);

        await vs.AskAsync("why does the build fail?");

        var (model, question) = Assert.Single(Asked(vs));
        Assert.Equal("granite-4.2-8b", model);
        Assert.Null(question.Images);
        Assert.Contains("CS0246", question.Content);
        var bubble = vs.Window.Messages.First(m => m.Role == "user");
        Assert.True(bubble.HasImageTexts);
        Assert.Contains("CS0246", Assert.Single(bubble.ImageTexts).Text);
        Assert.False(vs.Window.HasImageTextBanner);   // the chips went with the question, the banner too
    }

    [Fact]
    public async Task AskedOnTheBanner_TheModelThatSees_TakesTheQuestion_WithTheImage()
    {
        using var vs = Window();
        await AttachAsync(vs, Screenshot());

        await vs.Window.ImageAskOtherCommand.ExecuteAsync(null, null, CancellationToken.None);
        Assert.False(vs.Window.HasImageAskOther);
        await vs.AskAsync("why does the build fail?");

        var (model, question) = Assert.Single(Asked(vs));
        Assert.Equal("qwen3.8-27b", model);
        Assert.NotNull(question.Images);
        Assert.False(vs.Window.HasImageTextBanner);
    }

    [Fact]
    public async Task TheLastChipRemoved_TakesTheChoiceMadeOnTheBanner_WithIt()
    {
        using var vs = Window();
        await AttachAsync(vs, Screenshot());
        await vs.Window.ImageAskOtherCommand.ExecuteAsync(null, null, CancellationToken.None);
        await Assert.Single(vs.Window.Attachments).RemoveCommand.ExecuteAsync(null, null, CancellationToken.None);
        await VsWindowHarness.UntilAsync(() => vs.Window.Attachments.Count == 0 && !vs.Window.HasImageTextBanner);

        await AttachAsync(vs, Screenshot());
        Assert.True(vs.Window.HasImageAskOther);   // offered afresh, not taken from the chip that went
        await vs.AskAsync("why does the build fail?");
        Assert.Equal("granite-4.2-8b", Assert.Single(Asked(vs)).Model);
    }
}
