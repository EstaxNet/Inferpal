using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// Remote UI content of the tool-approval dialog. Template resolved by convention from
/// the embedded resource <c>Inferpal.ToolWindow.ApprovalDialogControl.xaml</c>.
/// </summary>
internal sealed class ApprovalDialogControl : RemoteUserControl
{
    public ApprovalDialogControl(ApprovalDialogData data)
        : base(data)
    {
    }
}
