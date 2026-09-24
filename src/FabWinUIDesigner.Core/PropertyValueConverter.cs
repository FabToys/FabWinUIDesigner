using System.Globalization;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Core;

/// <summary>
/// WinUI-specific wrapper around <see cref="PropertyValueParsing"/>'s pure string logic,
/// producing real <see cref="Color"/>/<see cref="Thickness"/> values.
/// </summary>
public static class PropertyValueConverter
{
    /// <summary>Parses a color from either hex ("#RRGGBB"/"#AARRGGBB") or a named color (e.g. "Red"), matching what XAML itself accepts for a Brush property.</summary>
    /// <param name="text">Text to parse.</param>
    /// <param name="color">The parsed color, or <c>default</c> on failure.</param>
    /// <returns>True if parsing succeeded.</returns>
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

    /// <summary>Parses a Thickness from XAML's own syntax ("N", "H,V", or "L,T,R,B"), via <see cref="PropertyValueParsing.TryParseThicknessParts"/>.</summary>
    /// <param name="text">Text to parse.</param>
    /// <param name="thickness">The parsed thickness, or <c>default</c> on failure.</param>
    /// <returns>True if parsing succeeded.</returns>
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

    /// <summary>
    /// Formats a live property value the way it would be written in XAML, to show the value in
    /// effect when the XAML doesn't set the property (the property grid's grey placeholder).
    /// </summary>
    /// <param name="value">The value read from the live element.</param>
    /// <returns>The text, or null for a value with no useful XAML text (null, a non-solid brush, an arbitrary object) - better nothing than a CLR type name.</returns>
    public static string? FormatForDisplay(object? value) => value switch
    {
        double d when double.IsNaN(d) => "Auto",
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => s,
        Thickness t => FormatThickness(t),
        SolidColorBrush b => $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
        _ => null,
    };

    /// <summary>Formats a Thickness back to XAML attribute text, collapsing to a single number when all 4 sides are equal.</summary>
    /// <param name="thickness">The thickness to format.</param>
    /// <returns>The formatted attribute text.</returns>
    public static string FormatThickness(Thickness thickness) =>
        PropertyValueParsing.FormatThicknessParts(thickness.Left, thickness.Top, thickness.Right, thickness.Bottom);
}
