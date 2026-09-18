using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FabWinUIDesigner.Core;

/// <summary>
/// Resolves a <see cref="PropertyEditorKind"/> (and, for an enum property, its allowed values)
/// straight off the live control's actual CLR property type - see research/46. This is what lets
/// <see cref="PropertyGridSchema"/>'s JSON stay to just Name/Category: which editor a property
/// needs was never real "metadata" to curate, it's already fully determined by the property's own
/// .NET type.
/// </summary>
public static class PropertyKindResolver
{
    /// <summary>Resolves the editor kind for one property.</summary>
    /// <param name="propertyInfo">The live element's reflected property, or <c>null</c> if the curated property name doesn't exist on this particular type (falls back to a plain text editor).</param>
    /// <returns>The editor kind, and - only for <see cref="PropertyEditorKind.Enum"/> - the enum's member names in declaration order (which matches every hand-written list this replaced, e.g. HorizontalAlignment's Left/Center/Right/Stretch).</returns>
    public static (PropertyEditorKind Kind, string[]? EnumValues) Resolve(PropertyInfo? propertyInfo)
    {
        if (propertyInfo is null)
        {
            return (PropertyEditorKind.Text, null);
        }

        var type = propertyInfo.PropertyType;

        if (type == typeof(bool))
        {
            return (PropertyEditorKind.Bool, null);
        }

        if (type.IsEnum)
        {
            return (PropertyEditorKind.Enum, Enum.GetNames(type));
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(int))
        {
            return (PropertyEditorKind.Number, null);
        }

        if (type == typeof(Brush))
        {
            return (PropertyEditorKind.Brush, null);
        }

        if (type == typeof(Thickness))
        {
            return (PropertyEditorKind.Thickness, null);
        }

        return (PropertyEditorKind.Text, null);
    }
}
