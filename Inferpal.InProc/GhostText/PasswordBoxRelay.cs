using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Inferpal.GhostText;

/// <summary>
/// The password of a protected document's card, typed in the chat's <see cref="PasswordBox"/>, made visible to its binding.
/// </summary>
/// <remarks>
/// The chat is Remote UI: its XAML runs in devenv, its view model out of process, and <c>PasswordBox.Password</c> is no
/// dependency property, so nothing binds it. This class handler copies what is typed into the card's text box beside it
/// (hidden until Show, its text bound two-way to the field) and into the box's <c>Tag</c>. ⚠ Only a box whose automation id
/// is <see cref="BoxUid"/> — the card's — is read: every other password box of Visual Studio (a sign-in, a credential
/// prompt) is never touched.
/// </remarks>
internal static class PasswordBoxRelay
{
    /// <summary>Must match the literal <c>AutomationProperties.AutomationId</c> on the card's PasswordBox in <c>InferpalToolWindowContent.xaml</c>.</summary>
    internal const string BoxUid = "InferpalPasswordBox";

    /// <summary>The automation status a relayed box shows: what a probe reads to know the relay ran (never the text).</summary>
    internal const string Relayed = "relayed";

    private static bool _registered;

    /// <summary>Registers the class handler once. Must be called on the VS UI thread.</summary>
    internal static void Initialize()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(OnPasswordChanged), handledEventsToo: true);
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || AutomationProperties.GetAutomationId(box) != BoxUid) return;
        var password = box.Password;
        // SetCurrentValue keeps the two-way bindings: the value goes to the card's field.
        box.SetCurrentValue(FrameworkElement.TagProperty, password);
        if (FieldTextBox(box) is { } text) text.SetCurrentValue(TextBox.TextProperty, password);
        AutomationProperties.SetItemStatus(box, Relayed);
    }

    /// <summary>The field's text box: the first <see cref="TextBox"/> under the field's panel, the box's grandparent.</summary>
    private static TextBox? FieldTextBox(DependencyObject box)
    {
        var field = VisualTreeHelper.GetParent(box) is { } dock ? VisualTreeHelper.GetParent(dock) : null;
        return field is null ? null : First<TextBox>(field);
    }

    private static T? First<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (child is PasswordBox) continue;
            if (First<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
