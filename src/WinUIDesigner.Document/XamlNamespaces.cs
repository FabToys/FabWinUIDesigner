using System.Xml.Linq;

namespace WinUIDesigner.Document;

public static class XamlNamespaces
{
    /// <summary>The x: namespace (x:Class, x:Name, x:FieldModifier, ...).</summary>
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The default WinUI presentation namespace (unprefixed elements/attributes).</summary>
    public static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
}
