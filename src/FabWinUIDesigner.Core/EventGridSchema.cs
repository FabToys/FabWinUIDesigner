namespace FabWinUIDesigner.Core;

/// <summary>One event-grid row: the XAML event name and which category group it shows under.</summary>
/// <param name="Name">The event name - used both as the attribute name and to look up the live event via reflection (see MainWindow.ApplyEventEdit).</param>
/// <param name="Category">Display group, e.g. "Action"/"Selection" - same reasoning as <see cref="PropertyDescriptor.Category"/> (research/46): curated, since WinUI exposes no equivalent metadata to reflect this from.</param>
public sealed record EventDescriptor(string Name, string Category);

/// <summary>
/// The curated per-control-type "common events" list for the MVP property grid's Events section
/// (M7 - research/43-m7-event-codegen-plan.md), loaded from the loose
/// <c>Metadata/EventMetadata.json</c> next to the app's .exe (research/46/47). Same reasoning as
/// <see cref="PropertyGridSchema"/>: WinUI controls carry no design-time metadata that could
/// drive this automatically, so it's an explicit list. Deliberately narrower than "every event
/// this type has" - just the ones a WinForms/WPF designer would typically surface by default.
/// </summary>
public static class EventGridSchema
{
    private static readonly Dictionary<string, EventDescriptor[]> ByControlType =
        ControlMetadataLoader.Load<Dictionary<string, EventDescriptor[]>>("EventMetadata.json");

    /// <summary>Gets the event list to show in the property grid's Events section for one control type.</summary>
    /// <param name="controlTypeName">The element's XAML type name, e.g. "Button".</param>
    /// <returns>The type's curated events, or an empty list for a type with none (e.g. <c>TextBlock</c>).</returns>
    public static IReadOnlyList<EventDescriptor> GetEvents(string controlTypeName) =>
        ByControlType.TryGetValue(controlTypeName, out var list) ? list : [];
}
