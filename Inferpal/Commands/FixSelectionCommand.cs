using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class FixSelectionCommand : InPlaceCodeActionBase
{
    public FixSelectionCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config, IInferenceProvider client)
        : base(extensibility, contextHolder, config, client) { }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuFix%")
    {
        Icon = new(ImageMoniker.KnownValues.EditBug, IconSettings.IconAndText),
    };

    protected override string Instruction     => InPlaceCodeActionPrompts.FixInstruction;
    protected override string SystemPrompt     => InPlaceCodeActionPrompts.FixSystem;
    protected override string NoChangeMessage  => Strings.FixNoChange;
}
