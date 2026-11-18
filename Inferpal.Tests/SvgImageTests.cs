using System.Text;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An SVG attached (pari 111): drawn for a model that sees images, by the browser <c>view_page</c> uses; its markup for a
/// model that does not, or when no browser draws it — the chip's line says which.
/// </summary>
public class SvgImageTests
{
    private static byte[] Svg(string attributes, string body = "") =>
        Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" {attributes}>{body}</svg>");

    [Theory]
    [InlineData("width=\"1200\" height=\"800\"", 1200, 800)]                 // its own size, within the range
    [InlineData("viewBox=\"0 0 400 160\"", 800, 320)]                         // no size: its viewBox, a small one brought to 800
    [InlineData("width=\"24\" height=\"24\"", 192, 192)]                      // an icon: at most 8 times larger
    [InlineData("width=\"3840\" height=\"1000\"", 1920, 500)]                 // too wide: brought to 1920
    [InlineData("width=\"100%\" viewBox=\"0 0 1000 500\"", 1000, 500)]       // a percentage is no size: the viewBox
    [InlineData("width=\"600pt\" height=\"300pt\"", 800, 400)]               // points, as a browser reads them
    [InlineData("width=\"1000\" height=\"20000\"", 205, 4096)]               // too tall: brought to 4096 high
    [InlineData("", 800, 400)]                                               // nothing: a browser's 300 × 150
    public void ItIsDrawnAtItsOwnSize_KeptWithinWhatAModelReads(string attributes, int width, int height)
    {
        Assert.Equal((width, height), SvgImage.DrawnSize(Svg(attributes)));
    }

    [Fact]
    public void ADoctype_IsNoDanger_AndNoObstacle()
    {
        var svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">"
                                         + "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"400\"></svg>");

        Assert.Equal((1000, 400), SvgImage.DrawnSize(svg));
    }

    [Fact]
    public void AnIllustratorExport_ItsEntitiesInTheRoot_KeepsItsSize()
    {
        // Illustrator declares entities in an internal DTD and names the root's namespace with one.
        var svg = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY ns_svg "http://www.w3.org/2000/svg">]>"""
                                         + """<svg xmlns="&ns_svg;" width="1200" height="2400"></svg>""");

        Assert.Equal((1200, 2400), SvgImage.DrawnSize(svg));
    }

    [Fact]
    public async Task AnSvgPastTheAttachmentsSize_IsNeverDrawn_ItsMarkupGoes()
    {
        var svg = Svg("""width="100" height="100" """, new string(' ', (int)ChatImages.MaxBytes + 1));

        var (drawn, detail) = await SvgImage.AttachAsync(svg, "huge.svg", sees: true, CancellationToken.None);

        Assert.Null(drawn);
        Assert.Equal(SvgImage.MarkupChip(svg.Length), detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AModelThatDoesNotSee_OrNotKnownTo_GetsItsMarkup_AndTheChipSaysSo(bool? sees)
    {
        var svg = Svg("width=\"400\" height=\"200\"", new string(' ', 3000));

        var (drawn, detail) = await SvgImage.AttachAsync(svg, "plan.svg", sees, CancellationToken.None);

        Assert.Null(drawn);
        Assert.Equal(Strings.SvgMarkupChip(3), detail);
    }

    [Fact]
    public async Task AModelThatSees_GetsItDrawn_OrTheChipSaysWhyNot()
    {
        var svg = Svg("viewBox=\"0 0 400 160\"", "<rect x=\"10\" y=\"10\" width=\"160\" height=\"60\" fill=\"#3B6FD8\"/>");

        var (drawn, detail) = await SvgImage.AttachAsync(svg, "plan.svg", sees: true, CancellationToken.None);

        if (PageCapture.FindAll().Any())
        {
            // A browser here draws it: the drawing is the file's, its rectangle where the file puts it (scaled twice).
            Assert.NotNull(drawn);
            Assert.Equal(("plan.svg", "image/png"), (drawn!.Name, drawn.MediaType));
            Assert.Equal((800, 320), (drawn.Width, drawn.Height));
            Assert.Equal(Strings.SvgDrawnChip(800, 320), detail);
            var pixels = StbImageSharp.ImageResult.FromMemory(Convert.FromBase64String(drawn.Base64),
                                                              StbImageSharp.ColorComponents.RedGreenBlueAlpha).Data;
            var at = (60 * 800 + 100) * 4;
            Assert.Equal(new byte[] { 0x3B, 0x6F, 0xD8 }, pixels[at..(at + 3)]);
        }
        else
        {
            Assert.Null(drawn);
            Assert.Equal(Strings.SvgNoBrowserChip, detail);
        }
    }

    [Theory]
    [InlineData("<svg width=\"100\" height=\"40\"><rect width=\"10\" height=\"10\"/></svg>")]                          // copied from HTML: no xmlns
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><use xlink:href=\"#a\"/></svg>")]                                  // undeclared prefix
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><text>Tom & Jerry</text></svg>")]                                   // unescaped &
    public async Task AnSvgABrowserShowsAsABrokenImage_GoesAsItsMarkup(string markup)
    {
        var svg = Encoding.UTF8.GetBytes(markup);

        var (drawn, detail) = await SvgImage.AttachAsync(svg, "broken.svg", sees: true, CancellationToken.None);

        Assert.False(SvgImage.IsDrawable(svg));
        Assert.Null(drawn);
        Assert.Equal(Strings.SvgNotDrawnChip, detail);
    }

    [Fact]
    public void AnInfiniteSize_IsNoSize_TheBrowsersDefaultIsTaken() =>
        Assert.Equal((800, 400), SvgImage.DrawnSize(Svg("width=\"1e999\" height=\"10\"")));

    [Fact]
    public async Task ADrawingThatShowsNothing_IsNotSent_ItsMarkupGoes()
    {
        // A sprite file: symbols only, nothing drawn until a page uses them.
        var svg = Svg("width=\"100\" height=\"40\"", "<symbol id=\"a\"><rect width=\"10\" height=\"10\"/></symbol>");

        var (drawn, detail) = await SvgImage.AttachAsync(svg, "sprite.svg", sees: true, CancellationToken.None);

        Assert.Null(drawn);
        Assert.Equal(PageCapture.FindAll().Any() ? Strings.SvgNotDrawnChip : Strings.SvgNoBrowserChip, detail);
    }
}

/// <summary>The host's <c>svg/read</c>: the same choice, made for the model the next question answers with.</summary>
public partial class HostServerTests
{
    [Fact]
    public async Task AnSvg_ToAModelThatDoesNotSee_IsItsMarkup_WithTheChipsLine()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var s = h.Server.CurrentSession!;
        s.Config.DefaultModel = "granite-4.2-8b";
        h.Fake.Installed = [new("granite-4.2-8b", 1, SeesImages: false)];
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><text>Order API</text></svg>";

        var read = await h.Server.SvgReadAsync(new SvgReadParams(Name: "plan.svg", Data: Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)),
                                                                 Model: "granite-4.2-8b", AgentMode: false), CancellationToken.None);

        Assert.Null(read.Image);
        Assert.Equal(Strings.SvgMarkupChip(1), read.Detail);
        Assert.Equal(svg, read.Text);                                         // dropped bytes: the markup comes back to attach
    }
}
