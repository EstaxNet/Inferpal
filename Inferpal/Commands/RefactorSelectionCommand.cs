using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class RefactorSelectionCommand : InPlaceCodeActionBase
{
    public RefactorSelectionCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config, IInferenceProvider client)
        : base(extensibility, contextHolder, config, client) { }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuRefactor%")
    {
        Icon = new(ImageMoniker.KnownValues.Refactoring, IconSettings.IconAndText),
    };

    protected override string Instruction     => InPlaceCodeActionPrompts.RefactorInstruction;
    protected override string SystemPrompt     => InPlaceCodeActionPrompts.RefactorSystem;
    protected override string NoChangeMessage  => Strings.RefactorNoChange;
}
