using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace WinUIDesigner.Core;

/// <summary>Thin wrapper around <see cref="XamlReader"/> for building the design-time live preview.</summary>
public static class XamlPreviewLoader
{
    public static XamlLoadResult TryLoad(string xamlText)
    {
        try
        {
            var loaded = XamlReader.Load(xamlText);
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
