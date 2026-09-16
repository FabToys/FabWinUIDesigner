using System.Xml.Linq;

namespace WinUIDesigner.Document;

/// <summary>
/// Thin wrapper over an <see cref="XElement"/> giving the designer's editing operations a
/// stable, XAML-flavored surface (x:Name, unprefixed attributes, element children) instead of
/// callers reaching into raw LINQ-to-XML everywhere.
/// </summary>
public sealed class DesignElement
{
    public DesignElement(XElement element)
    {
        Element = element;
    }

    public XElement Element { get; }

    public string LocalName => Element.Name.LocalName;

    public string? Name
    {
        get => GetAttribute(XamlNamespaces.X + "Name");
        set => SetAttribute(XamlNamespaces.X + "Name", value);
    }

    public string? GetAttribute(string localName) => Element.Attribute(localName)?.Value;

    public string? GetAttribute(XName name) => Element.Attribute(name)?.Value;

    public void SetAttribute(string localName, string? value) => SetAttribute((XName)localName, value);

    public void SetAttribute(XName name, string? value)
    {
        if (value is null)
        {
            Element.Attribute(name)?.Remove();
        }
        else
        {
            Element.SetAttributeValue(name, value);
        }
    }

    public IEnumerable<DesignElement> Children => Element.Elements().Select(e => new DesignElement(e));

    public DesignElement? Parent => Element.Parent is null ? null : new DesignElement(Element.Parent);

    /// <summary>Adds a new child element (in the same default namespace as this element) and returns its wrapper.</summary>
    public DesignElement AddChild(string localName)
    {
        var child = new XElement(Element.Name.Namespace + localName);
        Element.Add(child);
        return new DesignElement(child);
    }

    public void Remove() => Element.Remove();
}
