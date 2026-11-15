using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Presentation;

/// <summary>An attached image as its chip draws it: its name, its size line, and what is wrong with it for the model that
/// will answer (empty when nothing is).</summary>
internal sealed record ImageChipModel(string Name, string Detail, string Warning);

/// <summary>
/// The card shown before sending when the model that will answer does not see images: why, what would happen, and the two
/// ways on — another model of the server that sees (when there is one), or sending without the images.
/// </summary>
/// <param name="OtherModel">The model the first button asks; empty when no model of the server sees images (no first button).</param>
internal sealed record ImageNotSeenCardModel(
    string Title, string Subtitle, string Body, string OtherModel, string AskOtherLabel, string AskOtherHint,
    string SendWithoutLabel, string VisionModels);

/// <summary>Builds what both editors draw for images: the chip, the composer's line, the badge, the not-seen card.</summary>
internal static class ImageCards
{
    /// <summary>The chip of an image read by <see cref="ChatImages.Read"/>; <paramref name="pasted"/> for one pasted from
    /// the clipboard (it has no file).</summary>
    /// <param name="seenBy">The model that will answer, when it does not see images: the chip says so.</param>
    public static ImageChipModel Chip(ChatImage image, bool pasted, string? seenBy = null)
    {
        var size = image.Width > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{image.Width} × {image.Height} · ")
            : string.Empty;
        var kb = Math.Max(1, (int)Math.Round(image.Bytes / 1024.0)).ToString(CultureInfo.CurrentCulture) + " KB";
        var detail = size + kb + (pasted ? " · " + Strings.ImagePasted : string.Empty);
        return new ImageChipModel(image.Name, detail, seenBy is null ? string.Empty : Strings.ImageNotSeenChip(seenBy));
    }

    /// <summary>The badge beside the model's name: it sees images, it does not, or nothing when the server does not say.</summary>
    public static string Badge(bool? seesImages) => seesImages switch
    {
        true  => Strings.ImageSeesBadge,
        false => Strings.ImageNoBadge,
        _     => string.Empty,
    };

    /// <summary>The composer's line while images are attached: where they go.</summary>
    public static string GoesTo(string model) => Strings.ImageGoesTo(model);

    /// <summary>The formats and the limit, under the attach button.</summary>
    public static string FormatsHint => Strings.ImageFormatsHint;

    /// <summary>What is said when more than <see cref="ChatImages.MaxPerQuestion"/> images are attached.</summary>
    public static string TooMany => Strings.ImageTooMany(ChatImages.MaxPerQuestion);

    /// <summary>How many of the server's models that see are named on the card; the rest are counted.</summary>
    private const int NamedVisionModels = 6;

    /// <summary>The card for <paramref name="model"/>, which does not see the images named <paramref name="images"/>, on a
    /// server whose models that see are <paramref name="visionModels"/>.</summary>
    /// <param name="preferred">The models the person's settings name (chat, agent…): the first of them that sees is the
    /// one offered — never the server's first in listing order, a model nobody chose.</param>
    public static ImageNotSeenCardModel NotSeen(string model, IReadOnlyList<string> images, IReadOnlyList<string> visionModels,
                                                IReadOnlyList<string>? preferred = null)
    {
        var others = visionModels.Where(m => !Inference.ModelCatalog.SameModelName(m, model)).ToList();
        var other = (preferred ?? []).Select(p => others.FirstOrDefault(m => Inference.ModelCatalog.SameModelName(m, p)))
                                     .FirstOrDefault(m => m is not null)
                    ?? others.FirstOrDefault() ?? string.Empty;
        var named = visionModels.Take(NamedVisionModels).ToList();
        var list = string.Join(", ", named) + (visionModels.Count > named.Count
            ? string.Create(CultureInfo.InvariantCulture, $", +{visionModels.Count - named.Count}") : string.Empty);
        return new ImageNotSeenCardModel(
            Strings.ImageNotSeenTitle(model), Strings.ImageNotSeenSubtitle, Strings.ImageNotSeenBody(string.Join(", ", images)),
            other, other.Length > 0 ? Strings.ImageAskOther(other) : string.Empty, other.Length > 0 ? Strings.ImageAskOtherHint : string.Empty,
            Strings.ImageSendWithout,
            visionModels.Count > 0 ? Strings.ImageVisionModels(list) : Strings.ImageNoVisionModel);
    }
}
