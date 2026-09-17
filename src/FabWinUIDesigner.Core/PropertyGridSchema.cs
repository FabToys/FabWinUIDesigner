namespace FabWinUIDesigner.Core;

/// <summary>Which property-grid editor control a property gets - see MainWindow.CreatePropertyEditor for how each kind maps to a control, and MainWindow.ApplyPropertyEdit for how each kind's text is parsed back into a live value.</summary>
public enum PropertyEditorKind
{
    Text,
    Number,
    Bool,
    Enum,
    Brush,
    Thickness,
}

/// <summary>One property-grid row: a property's name, which editor kind to show for it, and (for <see cref="PropertyEditorKind.Enum"/>) the values to offer.</summary>
/// <param name="Name">The CLR/XAML property name, e.g. "Width" - used both as the attribute name and to look up the live property via reflection.</param>
/// <param name="Kind">Which editor control to show.</param>
/// <param name="EnumValues">Allowed values for an <see cref="PropertyEditorKind.Enum"/> property; unused otherwise.</param>
public sealed record PropertyDescriptor(string Name, PropertyEditorKind Kind, string[]? EnumValues = null);

/// <summary>
/// The curated per-control-type property list for the MVP property grid. WinUI controls carry
/// none of the WinForms/WPF-style design-time attributes that could drive this automatically
/// (see research/07-property-grid-design-time-attributes.md), so this has to be an explicit
/// table. Kept as a single in-code declarative table rather than scattered per-type logic, so
/// swapping the source to a JSON file later (see research/08-future-json-driven-metadata.md)
/// stays a small, isolated change instead of a rewrite.
/// </summary>
public static class PropertyGridSchema
{
    // Shared by every control type below via the `.. CommonLayout` spread - every WinUI
    // FrameworkElement has these, so listing them once here avoids repeating the same 6 lines
    // in every entry of ByControlType.
    private static readonly PropertyDescriptor[] CommonLayout =
    [
        new("Width", PropertyEditorKind.Number),
        new("Height", PropertyEditorKind.Number),
        new("Margin", PropertyEditorKind.Thickness),
        new("HorizontalAlignment", PropertyEditorKind.Enum, ["Left", "Center", "Right", "Stretch"]),
        new("VerticalAlignment", PropertyEditorKind.Enum, ["Top", "Center", "Bottom", "Stretch"]),
        new("Visibility", PropertyEditorKind.Enum, ["Visible", "Collapsed"]),
    ];

    private static readonly Dictionary<string, PropertyDescriptor[]> ByControlType = new()
    {
        ["Button"] = [
            new("Content", PropertyEditorKind.Text),
            .. CommonLayout,
            new("Foreground", PropertyEditorKind.Brush),
            new("Background", PropertyEditorKind.Brush),
            new("FontSize", PropertyEditorKind.Number),
            new("IsEnabled", PropertyEditorKind.Bool),
        ],
        ["CheckBox"] = [
            new("Content", PropertyEditorKind.Text),
            .. CommonLayout,
            new("Foreground", PropertyEditorKind.Brush),
            new("Background", PropertyEditorKind.Brush),
            new("FontSize", PropertyEditorKind.Number),
            new("IsEnabled", PropertyEditorKind.Bool),
        ],
        ["TextBlock"] = [
            new("Text", PropertyEditorKind.Text),
            .. CommonLayout,
            new("Foreground", PropertyEditorKind.Brush),
            new("FontSize", PropertyEditorKind.Number),
        ],
        ["TextBox"] = [
            new("Text", PropertyEditorKind.Text),
            .. CommonLayout,
            new("Foreground", PropertyEditorKind.Brush),
            new("Background", PropertyEditorKind.Brush),
            new("FontSize", PropertyEditorKind.Number),
            new("IsEnabled", PropertyEditorKind.Bool),
        ],
        ["ComboBox"] = [
            .. CommonLayout,
            new("Foreground", PropertyEditorKind.Brush),
            new("Background", PropertyEditorKind.Brush),
            new("FontSize", PropertyEditorKind.Number),
            new("IsEnabled", PropertyEditorKind.Bool),
        ],
        ["Image"] = CommonLayout,
        ["StackPanel"] = [.. CommonLayout, new("Background", PropertyEditorKind.Brush)],
        ["Grid"] = [.. CommonLayout, new("Background", PropertyEditorKind.Brush)],
        ["Canvas"] = [.. CommonLayout, new("Background", PropertyEditorKind.Brush)],
    };

    /// <summary>Gets the property list to show in the property grid for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>"Name" (x:Name - every element gets this first) followed by the type's specific properties, or just <see cref="CommonLayout"/> for an unrecognized type.</returns>
    public static IReadOnlyList<PropertyDescriptor> GetProperties(string controlTypeName)
    {
        var specific = ByControlType.TryGetValue(controlTypeName, out var list) ? list : CommonLayout;
        return [new PropertyDescriptor("Name", PropertyEditorKind.Text), .. specific];
    }
}
