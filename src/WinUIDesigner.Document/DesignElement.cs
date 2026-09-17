using System.Xml.Linq;

namespace WinUIDesigner.Document;

/// <summary>
/// Thin wrapper over an <see cref="XElement"/> giving the designer's editing operations a
/// stable, XAML-flavored surface (x:Name, unprefixed attributes, element children) instead of
/// callers reaching into raw LINQ-to-XML everywhere.
/// </summary>
public sealed class DesignElement
{
    /// <summary>Wraps an existing XML element - does not copy it, so edits through this wrapper mutate the underlying document.</summary>
    /// <param name="element">The element to wrap.</param>
    public DesignElement(XElement element)
    {
        Element = element;
    }

    /// <summary>The underlying XML element, for callers that need raw LINQ-to-XML access this wrapper doesn't cover.</summary>
    public XElement Element { get; }

    /// <summary>The element's XAML type name, e.g. "Button" (without namespace prefix).</summary>
    public string LocalName => Element.Name.LocalName;

    /// <summary>The element's x:Name (its identity/handle for the designer, not a plain "Name" attribute - see MainWindow.ApplyPropertyEdit for why that distinction matters). Null if unset.</summary>
    public string? Name
    {
        get => GetAttribute(XamlNamespaces.X + "Name");
        set => SetAttribute(XamlNamespaces.X + "Name", value);
    }

    /// <summary>Reads an unprefixed (default-namespace) attribute, e.g. "Width".</summary>
    /// <param name="localName">Attribute name.</param>
    /// <returns>The attribute's value, or null if it isn't set.</returns>
    public string? GetAttribute(string localName) => Element.Attribute(localName)?.Value;

    /// <summary>Reads a namespaced attribute, e.g. x:Name.</summary>
    /// <param name="name">Qualified attribute name.</param>
    /// <returns>The attribute's value, or null if it isn't set.</returns>
    public string? GetAttribute(XName name) => Element.Attribute(name)?.Value;

    /// <summary>Sets or removes an unprefixed (default-namespace) attribute.</summary>
    /// <param name="localName">Attribute name.</param>
    /// <param name="value">New value, or null to remove the attribute entirely.</param>
    public void SetAttribute(string localName, string? value) => SetAttribute((XName)localName, value);

    /// <summary>Sets or removes a namespaced attribute.</summary>
    /// <param name="name">Qualified attribute name.</param>
    /// <param name="value">New value, or null to remove the attribute entirely.</param>
    public void SetAttribute(XName name, string? value)
    {
        // null means "remove", not "set to empty string" - a null-but-still-present attribute
        // isn't a state XAML has, and leaving one behind (e.g. Width="") would render/parse
        // differently than not having it at all.
        if (value is null)
        {
            Element.Attribute(name)?.Remove();
        }
        else
        {
            Element.SetAttributeValue(name, value);
        }
    }

    /// <summary>This element's direct child elements, each wrapped.</summary>
    public IEnumerable<DesignElement> Children => Element.Elements().Select(e => new DesignElement(e));

    /// <summary>This element's parent, wrapped, or null if this is the document root.</summary>
    public DesignElement? Parent => Element.Parent is null ? null : new DesignElement(Element.Parent);

    /// <summary>Adds a new child element (in the same default namespace as this element) and returns its wrapper.</summary>
    /// <param name="localName">The new child's XAML type name, e.g. "Button".</param>
    /// <returns>The newly added child, wrapped.</returns>
    public DesignElement AddChild(string localName)
    {
        var child = new XElement(Element.Name.Namespace + localName);
        Element.Add(InferChildIndent(), child);
        return new DesignElement(child);
    }

    /// <summary>
    /// Finds an existing newline-containing whitespace-only text node between this element's
    /// current children to reuse for a newly appended one, so it lands on its own line matching
    /// the surrounding style instead of getting jammed onto the end of the previous line's
    /// closing tag. Falls back to a plain newline (no indentation) if none is found - e.g. every
    /// existing child was itself appended without one, before this fix existed.
    /// </summary>
    private XText InferChildIndent()
    {
        var existing = Element.Nodes()
            .OfType<XText>()
            .FirstOrDefault(t => t.Value.Contains('\n') && string.IsNullOrWhiteSpace(t.Value));

        return new XText(existing?.Value ?? "\n");
    }

    /// <summary>Removes this element from its parent.</summary>
    public void Remove() => Element.Remove();
}
