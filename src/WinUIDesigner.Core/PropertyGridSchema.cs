namespace WinUIDesigner.Core;

public enum PropertyEditorKind
{
    Text,
    Number,
    Bool,
    Enum,
    Brush,
    Thickness,
}

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

    public static IReadOnlyList<PropertyDescriptor> GetProperties(string controlTypeName)
    {
        var specific = ByControlType.TryGetValue(controlTypeName, out var list) ? list : CommonLayout;
        return [new PropertyDescriptor("Name", PropertyEditorKind.Text), .. specific];
    }
}
