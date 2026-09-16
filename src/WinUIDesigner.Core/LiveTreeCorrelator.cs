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
    public static IReadOnlyDictionary<UIElement, DesignElement> Correlate(UIElement liveRoot, DesignElement documentRoot)
    {
        var map = new Dictionary<UIElement, DesignElement>();
        Walk(liveRoot, documentRoot, map);
        return map;
    }

    private static void Walk(UIElement liveElement, DesignElement designElement, Dictionary<UIElement, DesignElement> map)
    {
        map[liveElement] = designElement;

        var liveChildren = GetChildren(liveElement).ToList();
        var designChildren = designElement.Children.ToList();

        var count = Math.Min(liveChildren.Count, designChildren.Count);
        for (var i = 0; i < count; i++)
        {
            Walk(liveChildren[i], designChildren[i], map);
        }
    }

    private static IEnumerable<UIElement> GetChildren(UIElement element) => element switch
    {
        Panel panel => panel.Children,
        Page page => page.Content is UIElement pageContent ? [pageContent] : [],
        ContentControl contentControl => contentControl.Content is UIElement content ? [content] : [],
        _ => [],
    };
}
