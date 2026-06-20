using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class AddDocstringSelectionCommand : InPlaceCodeActionBase
{
    public AddDocstringSelectionCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config, IInferenceProvider client)
        : base(extensibility, contextHolder, config, client) { }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuAddDocstring%")
    {
        Icon = new(ImageMoniker.KnownValues.CommentCode, IconSettings.IconAndText),
    };

    protected override string Instruction     => InPlaceCodeActionPrompts.DocstringInstruction;
    protected override string SystemPrompt     => InPlaceCodeActionPrompts.DocstringSystem;
    protected override string NoChangeMessage  => Strings.DocNoChange;
}
