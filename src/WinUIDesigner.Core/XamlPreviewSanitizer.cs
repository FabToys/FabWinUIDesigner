using WinUIDesigner.Document;

namespace WinUIDesigner.Core;

/// <summary>
/// Produces design-time-safe copies of a XAML document's text for feeding to
/// <see cref="Microsoft.UI.Xaml.Markup.XamlReader"/>. x:Class and event-handler attributes
/// reference code-behind members that don't exist in the designer's own assembly, so loose
/// XamlReader.Load can fail on them - see research/02-xamlreader-event-handlers.md for what
/// actually happens and why these two strip levels exist. The original document/file is never
/// touched by this; it only ever affects the design-time preview text handed to the loader.
/// </summary>
public static class XamlPreviewSanitizer
{
    private static readonly string[] KnownEventAttributeNames =
    {
        "Click", "Checked", "Unchecked", "TextChanged", "SelectionChanged", "Loaded", "Tapped",
    };

    public static string StripClass(string xamlText)
    {
        var doc = XamlDocument.Parse(xamlText);
        doc.Root.SetAttribute(XamlNamespaces.X + "Class", null);
        return doc.ToXamlString();
    }

    public static string StripClassAndEvents(string xamlText)
    {
        var doc = XamlDocument.Parse(xamlText);
        StripRecursive(doc.Root);
        return doc.ToXamlString();
    }

    private static void StripRecursive(DesignElement element)
    {
        element.SetAttribute(XamlNamespaces.X + "Class", null);
        foreach (var eventName in KnownEventAttributeNames)
        {
            element.SetAttribute(eventName, null);
        }

        foreach (var child in element.Children)
        {
            StripRecursive(child);
        }
    }
}
