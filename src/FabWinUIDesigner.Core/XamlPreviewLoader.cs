using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace FabWinUIDesigner.Core;

/// <summary>Thin wrapper around <see cref="XamlReader"/> for building the design-time live preview.</summary>
public static class XamlPreviewLoader
{
    /// <summary>Attempts to load XAML text into a live visual tree via <see cref="XamlReader.Load(string)"/>, catching any exception instead of letting it propagate - a failed attempt is expected/handled by callers (see <see cref="XamlPreviewSanitizer"/>), not exceptional.</summary>
    /// <param name="xamlText">XAML text to load - normally already sanitized by <see cref="XamlPreviewSanitizer"/>, since raw designer-authored XAML (with x:Class, event handlers) reliably fails.</param>
    /// <returns>A success result wrapping the loaded root, or a failure result wrapping whatever went wrong.</returns>
    public static XamlLoadResult TryLoad(string xamlText)
    {
        try
        {
            var loaded = XamlReader.Load(xamlText);
            // XamlReader.Load returns object (it can load non-visual things like a ResourceDictionary),
            // but the design surface only ever hosts a UIElement.
            if (loaded is UIElement root)
            {
                return XamlLoadResult.Success(root);
            }

            return XamlLoadResult.Failure(new InvalidOperationException(
                $"Loaded object of type '{loaded.GetType()}' is not a UIElement."));
        }
        catch (Exception ex)
        {
            return XamlLoadResult.Failure(ex);
        }
    }
}
