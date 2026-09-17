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

    /// <summary>The document's root element, wrapped. Throws if the underlying <see cref="XDocument"/> somehow has no root - shouldn't happen for any document that parsed successfully.</summary>
    public DesignElement Root => new(_xdoc.Root ?? throw new InvalidOperationException("XAML document has no root element."));

    /// <summary>Loads a document from a `.xaml` file on disk.</summary>
    /// <param name="path">Path of the file to load.</param>
    /// <returns>The loaded document.</returns>
    public static XamlDocument Load(string path) => new(XDocument.Load(path, LoadOptions.PreserveWhitespace));

    /// <summary>Parses a document from XAML text already in memory (e.g. a blank-document template, or a snapshot popped off the undo stack).</summary>
    /// <param name="xaml">XAML text to parse.</param>
    /// <returns>The parsed document.</returns>
    public static XamlDocument Parse(string xaml) => new(XDocument.Parse(xaml, LoadOptions.PreserveWhitespace));

    /// <summary>Writes the document to a `.xaml` file, overwriting it if it already exists.</summary>
    /// <param name="path">Destination file path.</param>
    public void Save(string path)
    {
        using var writer = XmlWriter.Create(path, WriterSettings);
        _xdoc.Save(writer);
    }

    /// <summary>Serializes the document to a XAML string, using the same formatting <see cref="Save"/> writes to disk.</summary>
    /// <returns>The document's current XAML text.</returns>
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
