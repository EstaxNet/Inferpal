using System.IO;
using System.Text;
using System.Xml.Linq;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The in-process half of a drop on Visual Studio's chat window (<see cref="ChatDropTarget"/>): a Solution Explorer
/// project named from its reference, and the Tags it finds the window's root, overlay and name line by.
/// </summary>
public sealed class ChatDropTargetTests
{
    /// <summary>Solution Explorer's project references as a drop carries them: a <c>DROPFILES</c> header (offset of the
    /// list, then "wide") and null-separated <c>{guid}|path</c> strings.</summary>
    private static byte[] DropFiles(bool wide, params string[] references)
    {
        var list = string.Join("\0", references) + "\0\0";
        var body = wide ? Encoding.Unicode.GetBytes(list) : Encoding.ASCII.GetBytes(list);
        var bytes = new byte[20 + body.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);
        BitConverter.GetBytes(wide ? 1 : 0).CopyTo(bytes, 16);
        body.CopyTo(bytes, 20);
        return bytes;
    }

    [Fact]
    public void ADroppedProject_IsNamedFromItsReference()
    {
        var bytes = DropFiles(wide: true, "{B85AB777-05B4-43C7-B1AA-07AAB5B8DAE8}|Billing\\Shop.Billing.csproj",
                                          "{2150E333-8FDC-42A3-9474-1A3956D46DE8}|App\\App.vbproj");

        Assert.Equal(["Shop.Billing", "App"], ChatDropTarget.ProjectNames(bytes, null));
        Assert.Equal(["Shop.Billing"], ChatDropTarget.ProjectNames(DropFiles(wide: false, "{B85AB777-05B4-43C7-B1AA-07AAB5B8DAE8}|Billing\\Shop.Billing.csproj"), null));
    }

    /// <summary>Visual Studio writes the reference in lower case; the notice names the project as Solution Explorer shows
    /// it, from its file on disk.</summary>
    [Fact]
    public void AProjectsName_IsSpelledAsItsFileOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-drop-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Billing"));
            File.WriteAllText(Path.Combine(dir, "Billing", "Shop.Billing.csproj"), "<Project />");
            var bytes = DropFiles(wide: true, "{B85AB777-05B4-43C7-B1AA-07AAB5B8DAE8}|billing\\shop.billing.csproj");

            Assert.Equal(["Shop.Billing"], ChatDropTarget.ProjectNames(bytes, dir));
            Assert.Equal(["shop.billing"], ChatDropTarget.ProjectNames(bytes, null));   // no folder to look in: Visual Studio's case
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BytesThatAreNotProjectReferences_NameNothing()
    {
        Assert.Empty(ChatDropTarget.ProjectNames([], null));
        Assert.Empty(ChatDropTarget.ProjectNames(new byte[19], null));
        var badOffset = DropFiles(wide: true, "{B85AB777-05B4-43C7-B1AA-07AAB5B8DAE8}|A\\A.csproj");
        BitConverter.GetBytes(badOffset.Length + 4).CopyTo(badOffset, 0);
        Assert.Empty(ChatDropTarget.ProjectNames(badOffset, null));
    }

    /// <summary>The in-process half finds the window's root, its overlay and the overlay's name line by their Tags: a Tag
    /// renamed on one side only, and the drop is opened by Visual Studio again, in silence.</summary>
    [Fact]
    public void TheChatXaml_CarriesTheTagsTheInProcHalfLooksFor()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml");
        var xaml = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var root = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "root");
        Assert.Equal(ChatDropTarget.RootTag, (string?)root.Attribute("Tag"));

        // A direct child of the root (looked for there, never by a walk through the conversation), collapsed, and its
        // visibility bound to nothing: a binding would put it back over the shown overlay, or hide it mid-drag.
        var overlay = root.Elements().Single(e => (string?)e.Attribute("Tag") == ChatDropTarget.OverlayTag);
        Assert.Equal("Collapsed", (string?)overlay.Attribute("Visibility"));
        Assert.Single(overlay.Descendants(), e => (string?)e.Attribute("Tag") == ChatDropTarget.NameTag);
    }
}
