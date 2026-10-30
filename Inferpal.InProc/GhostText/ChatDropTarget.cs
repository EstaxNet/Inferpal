using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Inferpal.GhostText;

/// <summary>
/// Files and folders dropped on the chat window become chips, as <c>@file</c> and <c>@folder</c> make them.
/// </summary>
/// <remarks>
/// <para>
/// The window is Remote UI: its XAML lives in devenv, where the out-of-process view model has no hand on a drop. Its root
/// (<see cref="RootTag"/>) already accepts drops — <c>AllowDrop</c> is inherited from Visual Studio — and, left alone, a
/// file dropped there is opened by Visual Studio as a document. This class handles the root's PREVIEW events, before
/// anything below or above it: it takes the drag (Copy), shows the window's own overlay (<see cref="OverlayTag"/>, its
/// text translated by the view model, the names set here), and on the drop marks it handled — Visual Studio then opens
/// nothing — and writes the paths to <see cref="ChatDropSignal"/>.
/// </para>
/// <para>
/// Only a drag that carries files (<c>FileDrop</c>: Windows Explorer, a file or folder of Solution Explorer) or Solution
/// Explorer projects (<see cref="ProjectsFormat"/>, said and never attached: a project has no path to attach) is taken.
/// Anything else — text dragged from the editor onto the prompt — goes on as before.
/// </para>
/// </remarks>
internal static class ChatDropTarget
{
    /// <summary>Must match the <c>Tag</c> of the root Grid in <c>InferpalToolWindowContent.xaml</c>.</summary>
    internal const string RootTag = "InferpalChatRoot";
    /// <summary>The overlay shown while a file is dragged over the window.</summary>
    internal const string OverlayTag = "InferpalDropOverlay";
    /// <summary>The overlay's line that names what is dragged.</summary>
    internal const string NameTag = "InferpalDropName";
    /// <summary>Solution Explorer's project references: a project node carries this and no <c>FileDrop</c>.</summary>
    internal const string ProjectsFormat = "CF_VSREFPROJECTS";

    private static bool _registered;
    private static Func<string?> _solutionDirectory = () => null;

    /// <summary>Registers the class handlers once. Must be called on the VS UI thread.</summary>
    /// <param name="solutionDirectory">The open solution's folder, read on the UI thread at each drag: a project's name is
    /// read from its file there, in the case it has on disk.</param>
    internal static void Initialize(Func<string?> solutionDirectory)
    {
        if (_registered) return;
        _registered = true;
        _solutionDirectory = solutionDirectory;
        EventManager.RegisterClassHandler(typeof(Grid), UIElement.PreviewDragEnterEvent, new DragEventHandler(OnEnter), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Grid), UIElement.PreviewDragOverEvent,  new DragEventHandler(OnOver),  handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Grid), UIElement.PreviewDragLeaveEvent, new DragEventHandler(OnLeave), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Grid), UIElement.PreviewDropEvent,      new DragEventHandler(OnDrop),  handledEventsToo: true);
    }

    private static bool IsChatRoot(object sender) => sender is Grid grid && Equals(grid.Tag, RootTag);

    private static void OnEnter(object sender, DragEventArgs e)
    {
        if (!Take(sender, e)) return;
        // Leaving one child for another inside the window raises Leave then Enter in the same input pass: no flicker.
        ShowOverlay((Grid)sender, Names(e.Data));
    }

    private static void OnOver(object sender, DragEventArgs e) => Take(sender, e);

    private static void OnLeave(object sender, DragEventArgs e)
    {
        if (IsChatRoot(sender)) HideOverlay((Grid)sender);
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (!IsChatRoot(sender) || !Carries(e.Data) || Effect(e.AllowedEffects) == DragDropEffects.None) return;
        HideOverlay((Grid)sender);
        // ⚠ Handled only once the window has it: a drop marked handled and never written would vanish in silence —
        // left alone, Visual Studio opens the file as it always did.
        if (!Write(e.Data)) return;
        e.Effects = Effect(e.AllowedEffects);
        e.Handled = true;
    }

    private static bool Write(IDataObject data)
    {
        try
        {
            var paths    = data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
            var projects = Projects(data);
            return (paths.Length > 0 || projects.Length > 0) && ChatDropSignal.Write(paths, projects);
        }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Drop", ex); return false; }
    }

    /// <summary>On the chat root, a drag this window attaches: takes it (Copy, handled) and says so.</summary>
    private static bool Take(object sender, DragEventArgs e)
    {
        if (!IsChatRoot(sender) || !Carries(e.Data)) return false;
        var effect = Effect(e.AllowedEffects);
        if (effect == DragDropEffects.None) return false;   // a source that only moves: never promised, never taken
        e.Effects = effect;
        e.Handled = true;
        return true;
    }

    private static DragDropEffects Effect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : allowed & DragDropEffects.Link;

    /// <summary>Files, or projects whose names can be read: a project reference that does not decode is not taken, so it
    /// is never accepted and then attached nowhere.</summary>
    private static bool Carries(IDataObject? data)
    {
        try { return data is not null && (data.GetDataPresent(DataFormats.FileDrop) || Projects(data).Length > 0); }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Carries", ex); return false; }
    }

    private static string[] Projects(IDataObject data) =>
        data.GetDataPresent(ProjectsFormat) && data.GetData(ProjectsFormat) is MemoryStream stream
            ? ProjectNames(stream.ToArray(), _solutionDirectory())
            : Array.Empty<string>();

    /// <summary>What the overlay names: the first three names, then an ellipsis.</summary>
    private static string Names(IDataObject data)
    {
        try
        {
            // A drive root has no file name: its path is its name.
            var names = (data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
                .Select(p => Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : p)
                .ToList();
            if (names.Count == 0) names.AddRange(Projects(data));
            return string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : "");
        }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Names", ex); return string.Empty; }
    }

    /// <summary>
    /// The project names in Solution Explorer's <see cref="ProjectsFormat"/>: a <c>DROPFILES</c> header (the offset of the
    /// list in its first four bytes, "wide" at byte 16) followed by null-separated project references,
    /// <c>{guid}|path\Project.csproj</c>. Empty when the bytes are not that.
    /// </summary>
    /// <param name="solutionDirectory">The folder the paths are relative to. ⚠ Visual Studio writes them in lower case:
    /// the name is the project file's as it is on disk there, else the path's.</param>
    internal static string[] ProjectNames(byte[] bytes, string? solutionDirectory)
    {
        if (bytes.Length < 20) return Array.Empty<string>();
        var offset = BitConverter.ToInt32(bytes, 0);
        if (offset < 20 || offset >= bytes.Length) return Array.Empty<string>();
        var wide = BitConverter.ToInt32(bytes, 16) != 0;
        var text = wide ? Encoding.Unicode.GetString(bytes, offset, (bytes.Length - offset) / 2 * 2)
                        : Encoding.Default.GetString(bytes, offset, bytes.Length - offset);
        return text.Split('\0')
                   .Select(reference => reference.Substring(reference.LastIndexOf('|') + 1).Trim())
                   .Where(file => file.Length > 0 && file.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                   .Select(file => Path.GetFileNameWithoutExtension(OnDisk(file, solutionDirectory)))
                   .Where(name => !string.IsNullOrEmpty(name))
                   .Distinct()
                   .ToArray();
    }

    /// <summary>The file name of <paramref name="file"/> as the disk spells it, when it is found there.</summary>
    private static string OnDisk(string file, string? solutionDirectory)
    {
        try
        {
            var full = Path.IsPathRooted(file) ? file : solutionDirectory is null ? null : Path.Combine(solutionDirectory, file);
            if (full is not null && Path.GetDirectoryName(full) is { } folder && Directory.Exists(folder))
            {
                var found = new DirectoryInfo(folder).GetFiles(Path.GetFileName(full)).FirstOrDefault();
                if (found is not null) return found.Name;
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.ProjectName", ex); }
        return Path.GetFileName(file);
    }

    private static void ShowOverlay(Grid root, string names)
    {
        if (Overlay(root) is not { } overlay) return;
        if (Find(overlay, NameTag) is TextBlock line) line.Text = names;
        overlay.Visibility = Visibility.Visible;
    }

    private static void HideOverlay(Grid root)
    {
        if (Overlay(root) is { } overlay) overlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>The overlay, a direct child of the root: never a walk through the whole conversation.</summary>
    private static FrameworkElement? Overlay(Grid root) =>
        root.Children.OfType<FrameworkElement>().FirstOrDefault(child => Equals(child.Tag, OverlayTag));

    /// <summary>The first element under <paramref name="from"/> whose <c>Tag</c> is <paramref name="tag"/>, the hidden
    /// ones included.</summary>
    private static FrameworkElement? Find(DependencyObject from, string tag)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(from); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
            if (Find(child, tag) is { } found) return found;
        }
        return null;
    }
}
