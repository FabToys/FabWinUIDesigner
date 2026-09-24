using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;

namespace FabWinUIDesigner.Core;

/// <summary>
/// Maps a XAML element name (e.g. "Button") to its WinUI CLR type, and a type to its inheritance
/// chain. The metadata files list properties/events per class, base classes included, and a
/// control gets the entries of every class it inherits from; the chain comes from the real WinUI
/// types, so it can't drift from WinUI the way a hand-written list in the JSON could.
/// </summary>
public static class ControlTypeResolver
{
    // Every Microsoft.UI.Xaml.* type lives in the same projection assembly as UIElement.
    private static readonly Assembly WinUiAssembly = typeof(UIElement).Assembly;

    // Where an unprefixed XAML element name can come from. Microsoft.UI.Xaml itself holds the
    // base classes (UIElement, FrameworkElement), which the metadata also names.
    private static readonly string[] Namespaces =
    [
        "Microsoft.UI.Xaml.Controls",
        "Microsoft.UI.Xaml.Controls.Primitives",
        "Microsoft.UI.Xaml.Shapes",
        "Microsoft.UI.Xaml",
    ];

    // Reflection lookups are cached: the types never change while the app runs.
    private static readonly ConcurrentDictionary<string, Type?> ByName = new(StringComparer.Ordinal);

    /// <summary>Finds the WinUI element type for an unprefixed XAML element name.</summary>
    /// <param name="xamlName">The element's local name, e.g. "Button" or "Rectangle".</param>
    /// <returns>The type, or null if no WinUI <see cref="UIElement"/> has that name (a typo, a custom control, or a non-visual type such as RowDefinition).</returns>
    public static Type? Resolve(string xamlName) => ByName.GetOrAdd(xamlName, name =>
        name.Contains('.')
            ? null
            : Namespaces
                .Select(ns => WinUiAssembly.GetType($"{ns}.{name}"))
                .FirstOrDefault(type => type is not null && typeof(UIElement).IsAssignableFrom(type)));

    /// <summary>The class names from <see cref="UIElement"/> down to <paramref name="type"/> itself, e.g. UIElement, FrameworkElement, Control, ContentControl, ButtonBase, Button.</summary>
    /// <param name="type">A <see cref="UIElement"/> type.</param>
    public static IReadOnlyList<string> ClassChainBaseFirst(Type type)
    {
        var chain = new List<string>();
        for (var current = type; current is not null && typeof(UIElement).IsAssignableFrom(current); current = current.BaseType)
        {
            chain.Add(current.Name);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Builds a control type's list from per-class metadata entries: the entries of every class
    /// in its inheritance chain, base class first. When a name appears at several levels, the
    /// most derived entry wins, at its own position (so a control can re-categorize an inherited
    /// property). A name that doesn't resolve to a WinUI type uses its own entry if it has one.
    /// </summary>
    /// <typeparam name="TDescriptor">A property or event descriptor.</typeparam>
    /// <param name="entriesByClass">Class name -> its entries, as in the metadata file.</param>
    /// <param name="xamlName">The element's XAML name, e.g. "Button".</param>
    /// <param name="getName">Gets a descriptor's property/event name.</param>
    /// <returns>The merged list, or null if the name doesn't resolve and has no entry of its own (the caller decides the fallback).</returns>
    internal static List<TDescriptor>? MergeEntries<TDescriptor>(
        IReadOnlyDictionary<string, TDescriptor[]> entriesByClass,
        string xamlName,
        Func<TDescriptor, string> getName)
    {
        var type = Resolve(xamlName);
        if (type is null)
        {
            return entriesByClass.TryGetValue(xamlName, out var own) ? [.. own] : null;
        }

        var merged = new List<TDescriptor>();
        foreach (var className in ClassChainBaseFirst(type))
        {
            if (!entriesByClass.TryGetValue(className, out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                merged.RemoveAll(existing => getName(existing) == getName(entry));
                merged.Add(entry);
            }
        }

        return merged;
    }
}
