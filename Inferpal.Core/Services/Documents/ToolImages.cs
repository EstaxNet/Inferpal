using System.Collections.Concurrent;
using Inferpal.Models;

namespace Inferpal.Services.Documents;

/// <summary>
/// The images a tool read during a batch of calls (<c>read_file</c> on a PNG of the workspace), waiting for the loop to put
/// them in front of the model.
/// </summary>
/// <remarks>
/// ⚠ A tool result is text: an OpenAI-compatible server (LM Studio) refuses an image in a <c>tool</c> message with a 400, and
/// the OpenAI protocol has no place for one there. The image goes in a <c>user</c> message of the loop's own, right after the
/// tool results (<see cref="ToolImages.AppendAsync"/>), the only shape both protocols take — measured in docs/probes/pari90.
/// Calls of one batch may run in parallel: the queue is concurrent.
/// </remarks>
internal sealed class ToolImageInbox
{
    private readonly ConcurrentQueue<ChatImage> _images = new();

    /// <summary>Keeps an image a tool read, for the loop to send after the batch.</summary>
    public void Put(ChatImage image) => _images.Enqueue(image);

    /// <summary>The images read since the last call, and empties the inbox.</summary>
    public IReadOnlyList<ChatImage> Take()
    {
        var taken = new List<ChatImage>();
        while (_images.TryDequeue(out var image)) taken.Add(image);
        return taken;
    }
}

/// <summary>Puts the images a batch of tools read in front of the model, or says why they are not.</summary>
internal static class ToolImages
{
    /// <summary>
    /// After a batch's tool results: one message of the loop's own carrying <paramref name="images"/> when the model answering
    /// reads images, or a note saying it does not.
    /// </summary>
    /// <param name="sees">Whether the model answering reads images; <c>null</c>: the server does not say.</param>
    /// <remarks>
    /// ⚠ Never sent to a model the server does not declare as reading images: an image it cannot read is either refused by the
    /// server or silently answered from its name. ⚠ The message is scaffolding: it is no question of the person's
    /// (<see cref="ChatMessageDto.IsScaffolding"/>), and images of earlier turns are retired like a question's
    /// (<see cref="ChatImages.RetireEarlier"/>).
    /// </remarks>
    public static async Task AppendAsync(List<ChatMessageDto> messages, IReadOnlyList<ChatImage> images, string model,
                                         Func<CancellationToken, Task<bool?>> sees, CancellationToken ct)
    {
        if (images.Count == 0) return;
        var names = string.Join(", ", images.Select(i => i.Name));
        bool? reads;
        try { reads = await sees(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("ToolImages.Sees", ex); reads = null; }

        if (reads == true)
        {
            // ⚠ A short assistant turn between the tool results and the image: Mistral's chat template (Devstral on LM
            // Studio) refuses a user message right after a tool result with a 400 — the whole run would fail on an image.
            if (messages.Count > 0 && messages[^1].Role == "tool")
                messages.Add(new ChatMessageDto("assistant", "Looking at the image(s).") { IsScaffolding = true });
            messages.Add(new ChatMessageDto("user",
                $"[The image(s) read_file read: {names}. They are the files you asked to read — answer from them; do not read them again.]")
            {
                IsScaffolding = true,
                Images = [.. images.Select(i => i.Base64)],
            });
            return;
        }
        var why = reads == false
            ? $"the model answering, {model}, does not read images"
            : $"the server does not say whether the model answering, {model}, reads images";
        // A model that does not see: the words written in each image, read by OCR, go instead (pari 108); an image with no
        // text it finds keeps the note below.
        var read = new List<string>();
        var unread = new List<string>();
        foreach (var image in images)
        {
            if (await Task.Run(() => ImageText.Read(image, ct), ct) is { } text) read.Add(ImageText.ForModel(image.Name, text));
            else unread.Add(image.Name);
        }
        if (read.Count > 0)
        {
            messages.Add(new ChatMessageDto("user",
                $"[read_file read the image(s) {names}; they were not sent as pictures: {why}. Their text follows instead.]\n\n"
              + string.Join("\n\n", read)
              + (unread.Count > 0 ? $"\n\n[No text was found in {string.Join(", ", unread)}: do not guess what it shows.]" : string.Empty))
            { IsScaffolding = true });
            return;
        }
        messages.Add(new ChatMessageDto("user",
            $"[read_file read the image(s) {names}, but they were not sent: {why}. Do not guess what they show. Tell the user: "
          + "they can describe the image, or choose a model that reads images. To read the words written in it (an error, a value), "
          + "call the ocr tool on it.]") { IsScaffolding = true });
    }
}
