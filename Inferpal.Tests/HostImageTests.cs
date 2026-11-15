using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An image attached in VS Code goes through the host: <c>image/read</c> reads it with the Core's one reader, <c>image/check</c>
/// says whether the model the question asks sees it (and the card when it does not), and <c>chat/send</c> puts it on the
/// question the model receives — never on the text.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public async Task AnImage_IsRead_Checked_AndRidesTheQuestionToTheModel()
    {
        using var h = CreateHarness();
        {
            await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var s = h.Server.CurrentSession!;
            s.ToolsEnabled = false;
            s.Config.DefaultModel = "granite-4.2-8b";
            h.Fake.Installed = [new("granite-4.2-8b", 1, SeesImages: false), new("qwen3.8-27b", 1, SeesImages: true)];

            var read = await h.Server.ImageReadAsync(
                new ImageReadParams("capture.png", Convert.ToBase64String(ChatImagesTests.Png(640, 360)), Pasted: true), CancellationToken.None);
            Assert.NotNull(read.Image);
            Assert.Equal("image/png", read.Image!.MediaType);
            Assert.EndsWith(Strings.ImagePasted, read.Detail);
            var fifth = await h.Server.ImageReadAsync(new ImageReadParams("five.png", Convert.ToBase64String(ChatImagesTests.Png(8, 8)), Already: 4), CancellationToken.None);
            Assert.Equal(Services.Presentation.ImageCards.TooMany, fifth.Refusal);

            var notImage = await h.Server.ImageReadAsync(new ImageReadParams("notes.png", Convert.ToBase64String("hello"u8.ToArray())), CancellationToken.None);
            Assert.Null(notImage.Image);
            Assert.Equal(Strings.ImageFormatNotSent("notes.png", "PNG"), notImage.Refusal);

            // Granite does not see: the card offers the model of the server that does.
            var check = await h.Server.ImageCheckAsync(new ImageCheckParams(["capture.png"], "granite-4.2-8b", AgentMode: false), CancellationToken.None);
            Assert.False(check.Sees);
            Assert.Equal("qwen3.8-27b", check.Card!.OtherModel);
            Assert.Equal(Strings.ImageNotSeenChip("granite-4.2-8b"), check.ChipWarning);
            var seen = await h.Server.ImageCheckAsync(new ImageCheckParams(["capture.png"], "qwen3.8-27b", AgentMode: false), CancellationToken.None);
            Assert.True(seen.Sees);
            Assert.Null(seen.Card);

            List<ChatMessageDto>? sent = null;
            h.Fake.OnChatRequest = (_, messages, _, _) =>
            {
                sent = [.. messages];
                return Task.FromResult(new ChatTurnResult("CS1002", null, 3, 5));
            };
            var turn = await h.Server.ChatSendAsync(new ChatSendParams("what does this error say?", Model: "qwen3.8-27b", AgentMode: false,
                                                                       ModelOverridesRoles: true, Images: [read.Image]), CancellationToken.None)
                                     .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.True(turn.QuestionKept);
            var question = Assert.Single(sent!, m => m.Role == "user");
            Assert.Equal([read.Image.Data], question.Images);
            Assert.DoesNotContain(read.Image.Data, question.Content);
        }
    }
}
