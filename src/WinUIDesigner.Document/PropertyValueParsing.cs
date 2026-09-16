using System.Globalization;

namespace WinUIDesigner.Document;

/// <summary>
/// Pure string-parsing/formatting helpers for property-grid value editors, kept UI-independent
/// (no WinUI types) so they're directly unit-testable. WinUI-specific construction (actual
/// Color/Thickness instances) happens in WinUIDesigner.Core, which wraps these.
/// </summary>
public static class PropertyValueParsing
{
    public static bool TryParseColorHex(string text, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255;
        r = 0;
        g = 0;
        b = 0;

        if (!text.StartsWith('#'))
        {
            return false;
        }

        var hex = text[1..];
        try
        {
            switch (hex.Length)
            {
                case 6:
                    r = Convert.ToByte(hex[..2], 16);
                    g = Convert.ToByte(hex[2..4], 16);
                    b = Convert.ToByte(hex[4..6], 16);
                    return true;
                case 8:
                    a = Convert.ToByte(hex[..2], 16);
                    r = Convert.ToByte(hex[2..4], 16);
                    g = Convert.ToByte(hex[4..6], 16);
                    b = Convert.ToByte(hex[6..8], 16);
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Matches XAML's own Thickness syntax: "N" (all 4 sides), "H,V", or "L,T,R,B".</summary>
    public static bool TryParseThicknessParts(string text, out double left, out double top, out double right, out double bottom)
    {
        left = top = right = bottom = 0;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        switch (values.Length)
        {
            case 1:
                left = top = right = bottom = values[0];
                return true;
            case 2:
                left = right = values[0];
                top = bottom = values[1];
                return true;
            case 4:
                left = values[0];
                top = values[1];
                right = values[2];
                bottom = values[3];
                return true;
            default:
                return false;
        }
    }

    public static string FormatThicknessParts(double left, double top, double right, double bottom) =>
        left == top && top == right && right == bottom
            ? left.ToString(CultureInfo.InvariantCulture)
            : $"{left.ToString(CultureInfo.InvariantCulture)},{top.ToString(CultureInfo.InvariantCulture)},{right.ToString(CultureInfo.InvariantCulture)},{bottom.ToString(CultureInfo.InvariantCulture)}";
}
