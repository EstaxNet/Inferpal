using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

internal static class EditorContextMenu
{
    [VisualStudioContribution]
    internal static MenuConfiguration AskInferpalSubmenu { get; } = new("%ContextMenuAskInferpal%")
    {
        Id = "Inferpal.Commands.EditorContextMenu.AskInferpalSubmenu",
        Children =
        [
            MenuChild.Command<InlineEditSelectionCommand>(),
            MenuChild.Separator,
            MenuChild.Command<ExplainSelectionCommand>(),
            MenuChild.Command<RefactorSelectionCommand>(),
            MenuChild.Command<AddTestsSelectionCommand>(),
            MenuChild.Command<FixSelectionCommand>(),
            MenuChild.Command<AddDocstringSelectionCommand>(),
        ],
    };

    // GroupPlacement.VsctParent parents a group to a VSCT menu (IDM_VS_CTXT_CODEWIN).
    // CommandPlacement.VsctParent would require a group ID as target — not a menu ID.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration AskInferpalContextGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.CodeWindowContext, 0))
        {
            Children = [GroupChild.Menu(AskInferpalSubmenu)],
        };
}
