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
/// types, the attributes a control starts with when added from the Toolbox, plus each control
/// type's curated property list.
/// </summary>
/// <param name="ToolboxGroups">Toolbox groups, in display order - an array rather than an object keyed by name, since JSON/dictionary key order shouldn't be relied on. Separate from <see cref="ControlTypes"/>, which also includes types like "Canvas" that aren't Toolbox-addable.</param>
/// <param name="DefaultAttributes">Control type name -> attribute name -> XAML value set on a control added from the Toolbox (plus the special <c>_default</c> fallback for a type with no entry). Missing from the file = none.</param>
/// <param name="ControlTypes">Class name -> its curated property list. Base classes (e.g. "FrameworkElement", "Control") are entries too: a control gets the entries of every class it inherits from (<see cref="ControlTypeResolver"/>). The special <c>_default</c> entry is for a name that isn't a WinUI type.</param>
internal sealed record PropertyMetadataFile(
    ToolboxGroup[] ToolboxGroups,
    Dictionary<string, Dictionary<string, string>>? DefaultAttributes,
    Dictionary<string, PropertyDescriptor[]> ControlTypes);

/// <summary>
/// The curated per-control-type property list for the MVP property grid, loaded from the loose
/// <c>Metadata/PropertyMetadata.json</c> next to the app's .exe - WinUI controls
/// carry none of the WinForms/WPF-style design-time attributes that could drive this
/// automatically, so an explicit
/// include-list per type is still required. Unlike the original in-code table, no editor "Kind"
/// is stored here anymore - that's always reflected off the live element's actual CLR property
/// type (<see cref="PropertyKindResolver"/>), so it can never drift from reality. Also doubles as
/// the source for the Toolbox's control list (<see cref="ToolboxControlTypes"/>) - one file to
/// edit for "add a control type", instead of a separate list to keep in sync. The user can
/// switch to a file of their own at runtime (<see cref="Use"/>).
/// </summary>
public static class PropertyGridSchema
{
    private const string CommonPropertiesCategory = "Common Properties";

    /// <summary>File name of the built-in metadata file, next to the app's .exe under <c>Metadata\</c>.</summary>
    public const string BuiltInFileName = "PropertyMetadata.json";

    // Replaced as a whole by Use, never mutated, so readers always see one consistent file.
    private static PropertyMetadataFile Metadata = Parse(ControlMetadataLoader.BuiltInPath(BuiltInFileName));
    private static IReadOnlyList<string> _toolboxControlTypes = ListToolboxControlTypes(Metadata);

    /// <summary>The Toolbox's groups, in display order.</summary>
    public static IReadOnlyList<ToolboxGroup> ToolboxGroups => Metadata.ToolboxGroups;

    /// <summary>Every XAML type name the Toolbox offers, each once, sorted by name - the Toolbox's alphabetical view. Excludes types like "Canvas" that exist in the property grid (as the document root) but aren't meant to be added from the Toolbox.</summary>
    public static IReadOnlyList<string> ToolboxControlTypes => _toolboxControlTypes;

    /// <summary>Checks that a file would load as property metadata, without switching to it.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <exception cref="InvalidDataException">It wouldn't; the message says why.</exception>
    public static void Validate(string path) => Parse(path);

    /// <summary>Switches to another property metadata file. The file is fully loaded and checked before anything is replaced, so a bad file leaves the current metadata in place.</summary>
    /// <param name="path">Full path of the file, or null for the built-in one.</param>
    /// <exception cref="InvalidDataException">The file couldn't be loaded; the message says why.</exception>
    public static void Use(string? path)
    {
        var metadata = Parse(path ?? ControlMetadataLoader.BuiltInPath(BuiltInFileName));
        Metadata = metadata;
        _toolboxControlTypes = ListToolboxControlTypes(metadata);
    }

    /// <summary>Loads a property metadata file and checks the parts the app relies on are there - JSON deserialization alone leaves a missing section as null.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <exception cref="InvalidDataException">The file is missing, isn't valid JSON, or lacks a required part.</exception>
    private static PropertyMetadataFile Parse(string path)
    {
        var metadata = ControlMetadataLoader.LoadFile<PropertyMetadataFile>(path);
        if (metadata.ToolboxGroups is null || metadata.ToolboxGroups.Any(g => g?.Name is null || g.Controls is null))
        {
            throw new InvalidDataException($"'{path}' needs a \"ToolboxGroups\" array whose groups each have a \"Name\" and a \"Controls\" array.");
        }

        if (metadata.ControlTypes is null || !metadata.ControlTypes.ContainsKey("_default"))
        {
            throw new InvalidDataException($"'{path}' needs a \"ControlTypes\" object with a \"_default\" entry (the property list for an element that isn't a WinUI type and has no entry of its own).");
        }

        return metadata;
    }

    private static List<string> ListToolboxControlTypes(PropertyMetadataFile metadata) =>
        metadata.ToolboxGroups.SelectMany(g => g.Controls).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Gets the attributes a control of this type starts with when added from the Toolbox:
    /// placeholder content and a size, so it isn't invisible or zero-sized on the design surface.
    /// Containers such as StackPanel/Grid also get <c>Background="Transparent"</c>: a Panel with
    /// no Background isn't hit-testable over its empty area, so a freshly added (still empty)
    /// container couldn't be clicked; Transparent is a real, if invisible, brush.
    /// </summary>
    /// <param name="controlTypeName">The XAML type name, e.g. "Button".</param>
    /// <returns>Attribute name -> XAML value, in the JSON's order: the type's own entry (an empty one means none), else the <c>_default</c> entry, else nothing.</returns>
    public static IReadOnlyDictionary<string, string> GetDefaultAttributes(string controlTypeName)
    {
        var defaults = Metadata.DefaultAttributes;
        if (defaults is null)
        {
            return new Dictionary<string, string>();
        }

        return defaults.TryGetValue(controlTypeName, out var attributes) || defaults.TryGetValue("_default", out attributes)
            ? attributes
            : new Dictionary<string, string>();
    }

    /// <summary>Gets the property list to show in the property grid for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>"Name" (x:Name - every element gets this first) followed by the properties listed for the type and its base classes, base class first (see <see cref="ControlTypeResolver.MergeEntries"/>); for a name that isn't a WinUI type and has no entry of its own, the <c>_default</c> set.</returns>
    public static IReadOnlyList<PropertyDescriptor> GetProperties(string controlTypeName)
    {
        var specific = ControlTypeResolver.MergeEntries(Metadata.ControlTypes, controlTypeName, d => d.Name)
            ?? [.. Metadata.ControlTypes["_default"]];
        return [new PropertyDescriptor("Name", CommonPropertiesCategory), .. specific.Where(d => d.Name != "Name")];
    }
}
