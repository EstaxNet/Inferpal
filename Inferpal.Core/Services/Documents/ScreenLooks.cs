using System.Security.Cryptography;
using Inferpal.Localization;

namespace Inferpal.Services.Documents;

/// <summary>
/// One look the agent took at what an app shows (<c>view_page</c> at one width, <c>view_window</c> on one window), with how
/// many of the run's writes came before it.
/// </summary>
internal sealed record ScreenLook(string Target, ChatImage Image, int WritesBefore, DateTime At);

/// <summary>
/// What the app showed before the run changed anything and after its last change: the agent's own first and last look at
/// the same target, as the run's result bar shows them side by side.
/// </summary>
/// <param name="Title">"On screen · …", in the person's language.</param>
/// <param name="Before">The first look's thumbnail, a PNG data URL.</param>
/// <param name="After">The last look's thumbnail, a PNG data URL.</param>
/// <param name="BeforeFile">The first look full size, a file of Inferpal's own; <c>null</c> when it could not be written.</param>
/// <param name="AfterFile">The last look full size, a file of Inferpal's own; <c>null</c> when it could not be written.</param>
internal sealed record RunScreen(string Title, string Before, string After, string BeforeCaption, string AfterCaption,
                                 string? BeforeFile, string? AfterFile, string Hint);

/// <summary>Pairs a run's looks into its <see cref="RunScreen"/>.</summary>
internal static class ScreenLooks
{
    /// <summary>The suite's own folder in place of the account's (TestConfigIsolation).</summary>
    internal static string? HomeOverride { get; set; }

    /// <summary>Where looks are kept full size: a folder of this process's own, under the account's local data (never a
    /// shared temp folder: outside Windows another account's), the ones earlier processes left a day ago removed.</summary>
    private static readonly Lazy<string> Folder = new(() =>
    {
        var home = HomeOverride ?? Path.Combine(UserFolders.Of(Environment.SpecialFolder.LocalApplicationData), "Inferpal", "looks");
        try
        {
            if (Directory.Exists(home))
                foreach (var old in new DirectoryInfo(home).EnumerateDirectories())
                    if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                        try { old.Delete(recursive: true); } catch { /* in use by another process: left for later */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("ScreenLooks.Sweep", ex); }
        return Directory.CreateDirectory(Path.Combine(home, Guid.NewGuid().ToString("N")[..12])).FullName;
    });

    /// <summary>
    /// The first look at a target taken before any write of the run, and the last look at the same target taken after the
    /// run's last write; <c>null</c> when the run wrote nothing, or looked only once, or not on both sides of its writes.
    /// ⚠ Never an "before" made up afterwards: both pictures are the ones the model saw. Of several targets paired, the one
    /// looked at last.
    /// </summary>
    internal static RunScreen? Pair(IReadOnlyList<(ScreenLook First, ScreenLook Last)> looks, int writes)
    {
        if (writes <= 0) return null;
        string Time(DateTime at) => at.ToString("t", Strings.UiCulture);
        foreach (var (first, last) in looks
                     .Where(l => !ReferenceEquals(l.First, l.Last) && l.First.WritesBefore == 0 && l.Last.WritesBefore == writes)
                     .OrderByDescending(l => l.Last.At))
        {
            // A picture no thumbnail can be made of gives way to the next pair, never the whole strip.
            if (ChatImages.Thumbnail(first.Image) is not { } before || ChatImages.Thumbnail(last.Image) is not { } after) continue;
            return new(Strings.ScreenOnScreen(first.Target), before, after,
                       Strings.ScreenBefore(Time(first.At)), Strings.ScreenAfter(Time(last.At)),
                       Save(first.Image), Save(last.Image), Strings.ScreenOpenHint);
        }
        return null;
    }

    /// <summary>The look full size in a file of Inferpal's own, named by its content: opened when its thumbnail is.</summary>
    private static string? Save(ChatImage image)
    {
        try
        {
            var bytes = Convert.FromBase64String(image.Base64);
            var extension = image.MediaType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".png" };
            var path = Path.Combine(Folder.Value, Convert.ToHexString(SHA256.HashData(bytes))[..32] + extension);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Diagnostics.Swallow("ScreenLooks.Save", ex);
            return null;
        }
    }
}
