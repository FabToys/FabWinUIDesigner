namespace FabWinUIDesigner.Core;

/// <summary>One event-grid row: the XAML event name and which category group it shows under.</summary>
/// <param name="Name">The event name - used both as the attribute name and to look up the live event via reflection (see MainWindow.ApplyEventEdit).</param>
/// <param name="Category">Display group, e.g. "Action"/"Selection" - same reasoning as <see cref="PropertyDescriptor.Category"/>: curated, since WinUI exposes no equivalent metadata to reflect this from.</param>
public sealed record EventDescriptor(string Name, string Category);

/// <summary>
/// The curated per-control-type "common events" list for the MVP property grid's Events section,
/// loaded from the loose
/// <c>Metadata/EventMetadata.json</c> next to the app's .exe. Same reasoning as
/// <see cref="PropertyGridSchema"/>: WinUI controls carry no design-time metadata that could
/// drive this automatically, so it's an explicit list. Deliberately narrower than "every event
/// this type has" - just the ones a WinForms/WPF designer would typically surface by default.
/// The user can switch to a file of their own at runtime (<see cref="Use"/>).
/// </summary>
public static class EventGridSchema
{
    /// <summary>File name of the built-in metadata file, next to the app's .exe under <c>Metadata\</c>.</summary>
    public const string BuiltInFileName = "EventMetadata.json";

    // Replaced as a whole by Use, never mutated.
    private static Dictionary<string, EventDescriptor[]> ByControlType = Parse(ControlMetadataLoader.BuiltInPath(BuiltInFileName));

    /// <summary>Checks that a file would load as event metadata, without switching to it.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <exception cref="InvalidDataException">It wouldn't; the message says why.</exception>
    public static void Validate(string path) => Parse(path);

    /// <summary>Switches to another event metadata file. The file is fully loaded before anything is replaced, so a bad file leaves the current metadata in place.</summary>
    /// <param name="path">Full path of the file, or null for the built-in one.</param>
    /// <exception cref="InvalidDataException">The file couldn't be loaded; the message says why.</exception>
    public static void Use(string? path) =>
        ByControlType = Parse(path ?? ControlMetadataLoader.BuiltInPath(BuiltInFileName));

    /// <summary>Loads an event metadata file: an object of control type name -> array of events.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <exception cref="InvalidDataException">The file is missing, isn't valid JSON, or has an entry that isn't an array.</exception>
    private static Dictionary<string, EventDescriptor[]> Parse(string path)
    {
        var byControlType = ControlMetadataLoader.LoadFile<Dictionary<string, EventDescriptor[]>>(path);
        var badEntry = byControlType.FirstOrDefault(kvp => kvp.Value is null).Key;
        if (badEntry is not null)
        {
            throw new InvalidDataException($"'{path}': \"{badEntry}\" should be an array of events.");
        }

        return byControlType;
    }

    /// <summary>Gets the event list to show in the property grid's Events section for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>The type's curated events, or an empty list for a type with none (e.g. <c>TextBlock</c>).</returns>
    public static IReadOnlyList<EventDescriptor> GetEvents(string controlTypeName) =>
        ByControlType.TryGetValue(controlTypeName, out var list) ? list : [];
}
