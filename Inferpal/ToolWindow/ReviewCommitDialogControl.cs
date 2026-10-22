using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// Remote UI content of the "Review a commit" dialog. Template resolved by convention from the embedded resource
/// <c>Inferpal.ToolWindow.ReviewCommitDialogControl.xaml</c>.
/// </summary>
internal sealed class ReviewCommitDialogControl : RemoteUserControl
{
    public ReviewCommitDialogControl(ReviewCommitDialogData data)
        : base(data)
    {
    }
}
