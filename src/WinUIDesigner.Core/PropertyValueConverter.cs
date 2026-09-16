using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinUIDesigner.Document;

namespace WinUIDesigner.Core;

/// <summary>
/// WinUI-specific wrapper around <see cref="PropertyValueParsing"/>'s pure string logic,
/// producing real <see cref="Color"/>/<see cref="Thickness"/> values.
/// </summary>
public static class PropertyValueConverter
{
    public static bool TryParseColor(string text, out Color color)
    {
        var trimmed = text.Trim();

        if (trimmed.StartsWith('#'))
        {
            if (PropertyValueParsing.TryParseColorHex(trimmed, out var a, out var r, out var g, out var b))
            {
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            color = default;
            return false;
        }

        // Named colors (e.g. "Red", "White") aren't parseable text - they're looked up as
        // static properties on Microsoft.UI.Colors, matching what XAML itself accepts.
        var namedColorProperty = typeof(Colors).GetProperty(trimmed, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (namedColorProperty?.GetValue(null) is Color namedColor)
        {
            color = namedColor;
            return true;
        }

        color = default;
        return false;
    }

    public static bool TryParseThickness(string text, out Thickness thickness)
    {
        if (PropertyValueParsing.TryParseThicknessParts(text, out var left, out var top, out var right, out var bottom))
        {
            thickness = new Thickness(left, top, right, bottom);
            return true;
        }

        thickness = default;
        return false;
    }

    public static string FormatThickness(Thickness thickness) =>
        PropertyValueParsing.FormatThicknessParts(thickness.Left, thickness.Top, thickness.Right, thickness.Bottom);
}
