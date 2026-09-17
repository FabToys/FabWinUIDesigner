using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIDesigner.Document;

namespace WinUIDesigner.Core;

/// <summary>
/// Maps each live control instantiated by <see cref="XamlPreviewLoader"/> back to the
/// <see cref="DesignElement"/> it came from, by walking both trees in parallel (they're built
/// from the same source, in the same order, so this holds as long as every design element that
/// produces a child *live* element does so in document order - true for the MVP control set:
/// Panels via Children, single-content hosts like Page/ContentControl via Content).
/// </summary>
public static class LiveTreeCorrelator
{
    /// <summary>Builds a live-element → design-element lookup by walking both trees together from their roots.</summary>
    /// <param name="liveRoot">Root of the just-loaded live visual tree (from <see cref="XamlPreviewLoader"/>).</param>
    /// <param name="documentRoot">Root of the document's element tree the live tree was loaded from.</param>
    /// <returns>A map from every correlated live element to its source <see cref="DesignElement"/>, used by the design surface's hit-testing to go from "what was clicked" to "what to edit".</returns>
    public static IReadOnlyDictionary<UIElement, DesignElement> Correlate(UIElement liveRoot, DesignElement documentRoot)
    {
        var map = new Dictionary<UIElement, DesignElement>();
        Walk(liveRoot, documentRoot, map);
        return map;
    }

    /// <summary>Maps one live/design pair, then recurses into their children in parallel, index by index.</summary>
    /// <param name="liveElement">Current live element.</param>
    /// <param name="designElement">Its corresponding design element.</param>
    /// <param name="map">Map being built; this call adds one entry plus whatever its descendants add.</param>
    private static void Walk(UIElement liveElement, DesignElement designElement, Dictionary<UIElement, DesignElement> map)
    {
        map[liveElement] = designElement;

        var liveChildren = GetChildren(liveElement).ToList();
        var designChildren = designElement.Children.ToList();

        // Should always be equal (both trees come from the same source) - Math.Min is just a
        // defensive guard against index-out-of-range if that assumption is ever violated.
        var count = Math.Min(liveChildren.Count, designChildren.Count);
        for (var i = 0; i < count; i++)
        {
            Walk(liveChildren[i], designChildren[i], map);
        }
    }

    /// <summary>Returns the live child elements worth correlating for one live element - <see cref="Panel"/>'s children, or a single-content host's content, or none for a leaf control.</summary>
    /// <param name="element">The live element to get children from.</param>
    private static IEnumerable<UIElement> GetChildren(UIElement element) => element switch
    {
        Panel panel => panel.Children,
        Page page => page.Content is UIElement pageContent ? [pageContent] : [],
        ContentControl contentControl => contentControl.Content is UIElement content ? [content] : [],
        _ => [],
    };
}
