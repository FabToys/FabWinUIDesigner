using System.Xml;

namespace FabWinUIDesigner.Document;

/// <summary>
/// Maps positions in a XAML document's text to the elements they fall in - used to make the
/// caret in the XAML source view drive the design-surface selection.
/// </summary>
public static class XamlSourcePositions
{
    /// <summary>Converts a 1-based (line, column) position, as reported by <see cref="IXmlLineInfo"/>, to a 0-based character offset into <paramref name="text"/>.</summary>
    /// <param name="text">The text the position is within.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>The matching 0-based character offset, clamped to <paramref name="text"/>'s length.</returns>
    public static int OffsetOf(string text, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;
        while (currentLine < line)
        {
            var newlineIndex = text.IndexOf('\n', offset);
            if (newlineIndex < 0)
            {
                return text.Length;
            }

            offset = newlineIndex + 1;
            currentLine++;
        }

        return Math.Min(text.Length, offset + column - 1);
    }

    /// <summary>
    /// Finds the innermost element whose full extent - from its start tag's <c>&lt;</c> to the
    /// end of its end tag (or of its <c>/&gt;</c> if self-closing) - contains
    /// <paramref name="caretOffset"/>. So a caret inside <c>&lt;/StackPanel&gt;</c>, or in the
    /// whitespace between two children, gives the StackPanel rather than one of its children.
    /// A caret right after an element's final <c>&gt;</c> still counts as inside it, so clicking
    /// at the end of a <c>&lt;Button ... /&gt;</c> line picks that Button.
    /// </summary>
    /// <param name="text">Well-formed XAML text. Malformed text throws <see cref="XmlException"/>.</param>
    /// <param name="caretOffset">0-based character offset of the caret.</param>
    /// <returns>The element's zero-based index in document (pre-)order - the same order as <c>DescendantsAndSelf()</c> on the root - or -1 if the caret is outside every element.</returns>
    public static int EnclosingElementIndex(string text, int caretOffset)
    {
        var bestIndex = -1;
        var bestStart = -1;
        var nextIndex = 0;

        // Open, non-empty elements awaiting their end tag: (document-order index, start offset).
        var open = new Stack<(int Index, int Start)>();

        void Consider(int index, int start, int end)
        {
            // Among spans containing the caret, the one starting latest is the innermost (spans
            // nest), and on a shared boundary (<A/><B/> with the caret between) it's the later one.
            if (start <= caretOffset && caretOffset <= end && start > bestStart)
            {
                bestStart = start;
                bestIndex = index;
            }
        }

        // XDocument's line info only covers start tags, so the text is walked with an XmlReader
        // instead: it also reports where each end tag is.
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var lineInfo = (IXmlLineInfo)reader;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    // Line info points at the name, just after the '<'.
                    var start = OffsetOf(text, lineInfo.LineNumber, lineInfo.LinePosition) - 1;
                    var index = nextIndex++;
                    if (reader.IsEmptyElement)
                    {
                        Consider(index, start, TagEnd(text, start));
                    }
                    else
                    {
                        open.Push((index, start));
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                {
                    var (index, start) = open.Pop();
                    // Line info points at the name, just after the "</"; an end tag has no
                    // attributes, so its first '>' closes it.
                    var endTagStart = OffsetOf(text, lineInfo.LineNumber, lineInfo.LinePosition) - 2;
                    Consider(index, start, TagEnd(text, endTagStart));
                    break;
                }
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Offset just past the <c>&gt;</c> that closes the tag starting at <paramref name="tagStart"/>,
    /// skipping any <c>&gt;</c> inside a quoted attribute value (legal in XML, unlike <c>&lt;</c>).
    /// </summary>
    /// <param name="text">The document text.</param>
    /// <param name="tagStart">Offset of the tag's <c>&lt;</c>.</param>
    /// <returns>The offset after the closing <c>&gt;</c>, or the text's length if there is none.</returns>
    private static int TagEnd(string text, int tagStart)
    {
        char? quote = null;
        for (var i = tagStart; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        return text.Length;
    }
}
