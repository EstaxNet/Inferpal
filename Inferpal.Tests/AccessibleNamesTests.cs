using System.IO;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ What a screen reader says for a control of the Visual Studio windows is its UI Automation name, and WPF gives one
/// only from an <c>AutomationProperties.Name</c>, a text <c>Content</c>, or a single <c>TextBlock</c> for content: a check
/// box whose content is a label and a hint, a button whose text lives in its template, a combo box, say nothing without
/// one. And an item of a list of objects without a name is read as its proxy type,
/// <c>Microsoft.VisualStudio.PlatformUI.RemoteUI.Internals.ObjectProxy</c> — every message of the chat, for one.
/// </summary>
public class AccessibleNamesTests
{
    private static readonly string[] Controls = ["Button", "CheckBox", "RadioButton", "ToggleButton"];

    // Not a control one aims at: the transparent pane behind the header menus, which closes them on a click outside.
    private const string ClickOutsidePane = "{Binding CloseMenusCommand}";

    private static IEnumerable<(string File, XElement Element)> Xaml()
    {
        var dir = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var files = Directory.GetFiles(dir, "*.xaml");
        Assert.True(files.Length >= 3, $"the Visual Studio XAML was not found under {dir}");
        foreach (var file in files)
            foreach (var e in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants())
                yield return (Path.GetFileName(file), e);
    }

    private static string Where((string File, XElement Element) x) => $"{x.File}:{((IXmlLineInfo)x.Element).LineNumber}";

    private static bool HasName(XElement e) =>
        e.Attribute("AutomationProperties.Name") is not null
        // A name its own style sets for every state (the Send button's, that a trigger turns into Stop): a name set only
        // under a trigger leaves the other states unnamed.
        || e.Elements().Where(p => p.Name.LocalName == e.Name.LocalName + ".Style")
               .Elements().Where(s => s.Name.LocalName == "Style").Elements()
               .Any(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == "AutomationProperties.Name");

    /// <summary>A mode is chosen by what it is for and what it may do: without its help text, the two lines under its name
    /// never reach a screen reader.</summary>
    [Fact]
    public void AModeRow_IsReadWithAllItShows()
    {
        var row = Xaml().Single(x => (string?)x.Element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ModeMenuRow");
        Assert.Equal("{Binding Help}", (string?)row.Element.Elements().Single().Attribute("AutomationProperties.HelpText"));

        Task Nothing(CancellationToken _) => Task.CompletedTask;
        Assert.Equal("reads the code · qwen · no writes · review.md",
            new ToolWindow.ModeMenuItem("Review", "reads the code", "qwen · no writes · review.md", false, "", "", "", Nothing).Help);
        Assert.Equal("", new ToolWindow.ModeMenuItem("Agent", "", "", false, "", "", "", Nothing).Help);
    }

    // A Segoe MDL2 glyph (private use area) is drawn, not read: it names nothing.
    private static bool IsGlyph(string? text) => text is { Length: > 0 } && text[0] >= '\uE000' && text[0] <= '\uF8FF';

    private static bool NamedByItsContent(XElement e)
    {
        if ((string?)e.Attribute("Content") is { Length: > 0 } text && !IsGlyph(text)) return true;
        var content = e.Elements().Where(c => !c.Name.LocalName.StartsWith(e.Name.LocalName + ".", StringComparison.Ordinal)).ToList();
        return content.Count == 1 && content[0].Name.LocalName == "TextBlock" && !IsGlyph((string?)content[0].Attribute("Text"))
               && !((string?)content[0].Attribute("FontFamily") ?? "").Contains("MDL2", StringComparison.Ordinal);
    }

    [Fact]
    public void EveryControlOfTheVisualStudioWindows_HasAName()
    {
        var controls = Xaml().Where(x => Controls.Contains(x.Element.Name.LocalName)).ToList();
        // Witness: the reading is alive (kept under the real count, which only grows).
        Assert.True(controls.Count >= 150, $"only {controls.Count} controls read");

        var unnamed = controls
            .Where(x => !HasName(x.Element) && !NamedByItsContent(x.Element)
                        && (string?)x.Element.Attribute("Command") != ClickOutsidePane)
            .Select(x => $"{Where(x)} <{x.Element.Name.LocalName} {(string?)x.Element.Attribute("Command") ?? (string?)x.Element.Attribute("IsChecked")}>")
            .ToList();
        Assert.True(unnamed.Count == 0, "Controls a screen reader cannot name:\n" + string.Join("\n", unnamed));
    }

    [Fact]
    public void EveryComboBox_HasAName()
    {
        var boxes = Xaml().Where(x => x.Element.Name.LocalName == "ComboBox").ToList();
        Assert.True(boxes.Count >= 10, $"only {boxes.Count} combo boxes read");
        var unnamed = boxes.Where(x => !HasName(x.Element)).Select(Where).ToList();
        Assert.True(unnamed.Count == 0, "Combo boxes a screen reader cannot name: " + string.Join(", ", unnamed));
    }

    [Fact]
    public void EveryItemOfAList_IsReadByWhatItShows_NotByItsProxyType()
    {
        var lists = Xaml().Where(x => x.Element.Name.LocalName == "ListBox").ToList();
        Assert.True(lists.Count >= 2, $"only {lists.Count} lists read");
        // A name for EVERY item, set by the style itself: one only under a trigger (a role) leaves the others unnamed.
        var unnamed = lists
            .Where(x => !x.Element.Elements().Where(p => p.Name.LocalName == "ListBox.ItemContainerStyle")
                           .Elements().Where(s => s.Name.LocalName == "Style").Elements()
                           .Any(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == "AutomationProperties.Name"))
            .Select(x => $"{Where(x)} {(string?)x.Element.Attribute("ItemsSource")}")
            .ToList();
        Assert.True(unnamed.Count == 0, "Lists whose items read as their proxy type: " + string.Join(", ", unnamed));
    }
}
