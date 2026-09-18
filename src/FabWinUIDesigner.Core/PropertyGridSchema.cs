namespace FabWinUIDesigner.Core;

/// <summary>Which property-grid editor control a property gets - see MainWindow.CreatePropertyEditor for how each kind maps to a control, and MainWindow.ApplyPropertyEdit for how each kind's text is parsed back into a live value. Resolved by reflection at grid-build time (<see cref="PropertyKindResolver"/>), never stored - see research/46.</summary>
public enum PropertyEditorKind
{
    Text,
    Number,
    Bool,
    Enum,
    Brush,
    Thickness,
}

/// <summary>One property-grid row: a property's name and which category group it shows under.</summary>
/// <param name="Name">The CLR/XAML property name, e.g. "Width" - used both as the attribute name and to look up the live property via reflection.</param>
/// <param name="Category">Display group, e.g. "Layout"/"Appearance" - see research/46 for where these come from (WinUI has no reflectable equivalent of WPF's <c>CategoryAttribute</c>, so this is curated, inspired by WPF's real category values).</param>
public sealed record PropertyDescriptor(string Name, string Category);

/// <summary>
/// The JSON shape of <c>Metadata/PropertyMetadata.json</c>: which control types the Toolbox
/// offers, in display order, plus each control type's curated property list.
/// </summary>
/// <param name="ToolboxOrder">XAML type names, in the order Toolbox buttons should appear - deliberately separate from <see cref="ControlTypes"/>' key order, which JSON/dictionary ordering shouldn't be relied on for (and which includes types like "Canvas" that aren't Toolbox-addable).</param>
/// <param name="ControlTypes">Control type name -> its curated property list (plus the special <c>_default</c> fallback for a type with no entry).</param>
internal sealed record PropertyMetadataFile(string[] ToolboxOrder, Dictionary<string, PropertyDescriptor[]> ControlTypes);

/// <summary>
/// The curated per-control-type property list for the MVP property grid, loaded from the loose
/// <c>Metadata/PropertyMetadata.json</c> next to the app's .exe (research/46/47) - WinUI controls
/// carry none of the WinForms/WPF-style design-time attributes that could drive this
/// automatically (see research/07-property-grid-design-time-attributes.md), so an explicit
/// include-list per type is still required. Unlike the original in-code table, no editor "Kind"
/// is stored here anymore - that's always reflected off the live element's actual CLR property
/// type (<see cref="PropertyKindResolver"/>), so it can never drift from reality. Also doubles as
/// the source for the Toolbox's control list (<see cref="ToolboxControlTypes"/>) - one file to
/// edit for "add a control type", instead of a separate list to keep in sync.
/// </summary>
public static class PropertyGridSchema
{
    private const string CommonPropertiesCategory = "Common Properties";

    private static readonly PropertyMetadataFile Metadata = ControlMetadataLoader.Load<PropertyMetadataFile>("PropertyMetadata.json");

    /// <summary>XAML type names the Toolbox should offer, in display order (research/47) - e.g. "Button", "TextBlock", ... Excludes types like "Canvas" that exist in the property grid (as the document root) but aren't meant to be added from the Toolbox.</summary>
    public static IReadOnlyList<string> ToolboxControlTypes => Metadata.ToolboxOrder;

    /// <summary>Gets the property list to show in the property grid for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>"Name" (x:Name - every element gets this first) followed by the type's specific properties, or just the <c>_default</c> layout-only set for an unrecognized type.</returns>
    public static IReadOnlyList<PropertyDescriptor> GetProperties(string controlTypeName)
    {
        var specific = Metadata.ControlTypes.TryGetValue(controlTypeName, out var list) ? list : Metadata.ControlTypes["_default"];
        return [new PropertyDescriptor("Name", CommonPropertiesCategory), .. specific];
    }
}
