using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Inferpal.GhostText;

/// <summary>
/// Declares the WPF adornment layer the inline diff preview draws on. Ordered after the
/// text so the removed-line tints sit over the code, below the caret layer.
/// (Same sealed-type/static-field MEF pattern as <see cref="GhostTextAdornmentLayerDefinition"/>.)
/// </summary>
internal static class InlineDiffAdornmentLayerDefinition
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(InlineDiffAdornment.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Text)]
#pragma warning disable CS0649
    internal static AdornmentLayerDefinition? LayerDefinition;
#pragma warning restore CS0649
}
