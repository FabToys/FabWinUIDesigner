namespace FabWinUIDesigner.Core;

/// <summary>Which property-grid editor control a property gets - see MainWindow.CreatePropertyEditor for how each kind maps to a control, and MainWindow.ApplyPropertyEdit for how each kind's text is parsed back into a live value. Resolved by reflection at grid-build time (<see cref="PropertyKindResolver"/>), never stored.</summary>
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
/// <param name="Category">Display group, e.g. "Layout"/"Appearance" (WinUI has no reflectable equivalent of WPF's <c>CategoryAttribute</c>, so this is curated, inspired by WPF's real category values).</param>
public sealed record PropertyDescriptor(string Name, string Category);

/// <summary>One Toolbox group, e.g. "Common" or "Layout".</summary>
/// <param name="Name">Group header text.</param>
/// <param name="Controls">XAML type names in the group, in display order. A control can be in several groups (e.g. "Common" and its own category), like VS's Toolbox.</param>
/// <param name="ExpandedByDefault">Whether the group starts expanded, before the user has expanded or collapsed any group. Optional in the JSON; groups start collapsed otherwise.</param>
public sealed record ToolboxGroup(string Name, string[] Controls, bool ExpandedByDefault = false);

/// <summary>
/// The JSON shape of <c>Metadata/PropertyMetadata.json</c>: the Toolbox's groups of control
/// types, plus each control type's curated property list.
/// </summary>
/// <param name="ToolboxGroups">Toolbox groups, in display order - an array rather than an object keyed by name, since JSON/dictionary key order shouldn't be relied on. Separate from <see cref="ControlTypes"/>, which also includes types like "Canvas" that aren't Toolbox-addable.</param>
/// <param name="ControlTypes">Control type name -> its curated property list (plus the special <c>_default</c> fallback for a type with no entry).</param>
internal sealed record PropertyMetadataFile(ToolboxGroup[] ToolboxGroups, Dictionary<string, PropertyDescriptor[]> ControlTypes);

/// <summary>
/// The curated per-control-type property list for the MVP property grid, loaded from the loose
/// <c>Metadata/PropertyMetadata.json</c> next to the app's .exe - WinUI controls
/// carry none of the WinForms/WPF-style design-time attributes that could drive this
/// automatically, so an explicit
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

    /// <summary>The Toolbox's groups, in display order.</summary>
    public static IReadOnlyList<ToolboxGroup> ToolboxGroups => Metadata.ToolboxGroups;

    /// <summary>Every XAML type name the Toolbox offers, each once, sorted by name - the Toolbox's alphabetical view. Excludes types like "Canvas" that exist in the property grid (as the document root) but aren't meant to be added from the Toolbox.</summary>
    public static IReadOnlyList<string> ToolboxControlTypes { get; } =
        Metadata.ToolboxGroups.SelectMany(g => g.Controls).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Gets the property list to show in the property grid for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>"Name" (x:Name - every element gets this first) followed by the type's specific properties, or just the <c>_default</c> layout-only set for an unrecognized type.</returns>
    public static IReadOnlyList<PropertyDescriptor> GetProperties(string controlTypeName)
    {
        var specific = Metadata.ControlTypes.TryGetValue(controlTypeName, out var list) ? list : Metadata.ControlTypes["_default"];
        return [new PropertyDescriptor("Name", CommonPropertiesCategory), .. specific];
    }
}
