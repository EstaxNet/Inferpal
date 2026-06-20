using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

internal static class InferpalMenu
{
    [VisualStudioContribution]
    internal static MenuConfiguration ToolsMenuEntry { get; } = new("%InferpalMenu%")
    {
        Id = "Inferpal.Commands.InferpalMenu.ToolsMenuEntry",
        Children =
        [
            MenuChild.Command<OpenInferpalWindowCommand>(),
            MenuChild.Command<OpenSettingsCommand>(),
        ],
    };

    // Own group in Tools menu so VS renders separators around the submenu.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration ToolsMenuGroup { get; } =
        new(GroupPlacement.KnownPlacements.ToolsMenu)
        {
            Children = [GroupChild.Menu(ToolsMenuEntry)],
        };
}
