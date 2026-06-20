using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Inferpal.GhostText;

/// <summary>
/// Declares the WPF adornment layer that ghost-text TextBlocks are placed on.
/// Ordered after the caret so ghost text renders above the cursor line.
/// </summary>
internal static class GhostTextAdornmentLayerDefinition
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(GhostTextAdornment.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
#pragma warning disable CS0649
    internal static AdornmentLayerDefinition? LayerDefinition;
#pragma warning restore CS0649
}
