using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinUIDesigner.Document;

/// <summary>
/// The designer's source of truth for a `.xaml` file: an <see cref="XDocument"/> loaded with
/// whitespace preserved, so that saving back out without further edits reproduces the
/// original text (see research/01-xml-roundtrip-formatting.md for how far that holds).
/// </summary>
public sealed class XamlDocument
{
    private readonly XDocument _xdoc;

    private XamlDocument(XDocument xdoc)
    {
        _xdoc = xdoc;
    }

    public DesignElement Root => new(_xdoc.Root ?? throw new InvalidOperationException("XAML document has no root element."));

    public static XamlDocument Load(string path) => new(XDocument.Load(path, LoadOptions.PreserveWhitespace));

    public static XamlDocument Parse(string xaml) => new(XDocument.Parse(xaml, LoadOptions.PreserveWhitespace));

    public void Save(string path)
    {
        using var writer = XmlWriter.Create(path, WriterSettings);
        _xdoc.Save(writer);
    }

    public string ToXamlString()
    {
        var stringWriter = new StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, WriterSettings))
        {
            _xdoc.Save(writer);
        }
        return stringWriter.ToString();
    }

    // No XML declaration (none of the .xaml fixtures/templates carry one) and no line-ending
    // normalization, so an unmodified document round-trips to byte-identical text.
    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = true,
        NewLineHandling = NewLineHandling.None,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };
}
