using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Core;

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
    // Not exhaustive - just the handful of events the MVP toolbox controls actually wire up
    // (see MainWindow.AddControl/ApplyDefaultAttributes). A control type added later with an
    // event not in this list would need it added here too, or StripClassAndEvents would leave
    // that attribute behind and the second-fallback preview load would fail the same way as
    // the first, unstripped attempt.
    private static readonly string[] KnownEventAttributeNames =
    {
        "Click", "Checked", "Unchecked", "TextChanged", "SelectionChanged", "Loaded", "Tapped",
    };

    /// <summary>Removes just the root element's x:Class attribute (the first, lighter-weight fallback - see the class doc).</summary>
    /// <param name="xamlText">Original XAML text.</param>
    /// <returns>The same XAML with x:Class removed from the root element only.</returns>
    public static string StripClass(string xamlText)
    {
        var doc = XamlDocument.Parse(xamlText);
        doc.Root.SetAttribute(XamlNamespaces.X + "Class", null);
        return doc.ToXamlString();
    }

    /// <summary>Removes x:Class and all known event-handler attributes from every element in the tree (the second, more aggressive fallback - see the class doc).</summary>
    /// <param name="xamlText">Original XAML text.</param>
    /// <returns>The same XAML with x:Class and known event attributes stripped from every element.</returns>
    public static string StripClassAndEvents(string xamlText)
    {
        var doc = XamlDocument.Parse(xamlText);
        StripRecursive(doc.Root);
        return doc.ToXamlString();
    }

    /// <summary>Recursively strips x:Class and known event attributes from <paramref name="element"/> and every descendant.</summary>
    /// <param name="element">Subtree root to strip.</param>
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
