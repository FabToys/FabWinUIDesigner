using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Document.Tests;

[TestClass]
public class XamlSourcePositionsTests
{
    // Document-order indexes: 0 Page, 1 Canvas, 2 StackPanel, 3 Button (in StackPanel),
    // 4 CheckBox (in StackPanel), 5 TextBlock (after the StackPanel).
    private const string Xaml =
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n" +
        "    <Canvas>\n" +
        "        <StackPanel Width=\"150\">\n" +
        "            <Button Content=\"a>b\" />\n" +
        "            <CheckBox Content='c' />\n" +
        "        </StackPanel>\n" +
        "        <TextBlock Text=\"x\"></TextBlock>\n" +
        "    </Canvas>\n" +
        "</Page>";

    /// <summary>Offset of the first occurrence of <paramref name="marker"/>, plus <paramref name="delta"/>.</summary>
    private static int At(string marker, int delta = 0) => Xaml.IndexOf(marker, StringComparison.Ordinal) + delta;

    [TestMethod]
    public void CaretInClosingTag_SelectsThatElement_NotItsLastChild()
    {
        Assert.AreEqual(2, XamlSourcePositions.EnclosingElementIndex(Xaml, At("</StackPanel>", 5)));
    }

    [TestMethod]
    public void CaretInsideStartTag_SelectsThatElement()
    {
        Assert.AreEqual(2, XamlSourcePositions.EnclosingElementIndex(Xaml, At("<StackPanel", 3)));
        Assert.AreEqual(3, XamlSourcePositions.EnclosingElementIndex(Xaml, At("<Button", 3)));
    }

    [TestMethod]
    public void CaretBetweenChildren_SelectsTheContainer()
    {
        // Start of the CheckBox's line: after the Button's newline, in the indentation.
        Assert.AreEqual(2, XamlSourcePositions.EnclosingElementIndex(Xaml, At("<CheckBox", -4)));
    }

    [TestMethod]
    public void CaretRightAfterSelfClosingTag_StillSelectsIt()
    {
        Assert.AreEqual(3, XamlSourcePositions.EnclosingElementIndex(Xaml, At("\"a>b\" />", 8)));
    }

    [TestMethod]
    public void GreaterThanInsideQuotedValue_DoesNotEndTheTag()
    {
        // Just after the '>' inside "a>b" - still within the Button's start tag.
        Assert.AreEqual(3, XamlSourcePositions.EnclosingElementIndex(Xaml, At("a>b", 2)));
    }

    [TestMethod]
    public void ElementWithSeparateEndTag_AfterAContainer_IsFound()
    {
        Assert.AreEqual(5, XamlSourcePositions.EnclosingElementIndex(Xaml, At("</TextBlock>", 3)));
    }

    [TestMethod]
    public void CaretAtCanvasClosingTag_SelectsCanvas_AndAtPageClosingTag_SelectsPage()
    {
        Assert.AreEqual(1, XamlSourcePositions.EnclosingElementIndex(Xaml, At("</Canvas>", 3)));
        Assert.AreEqual(0, XamlSourcePositions.EnclosingElementIndex(Xaml, At("</Page>", 3)));
    }

    [TestMethod]
    public void OffsetOf_ConvertsOneBasedLineAndColumn()
    {
        Assert.AreEqual(At("<Canvas>"), XamlSourcePositions.OffsetOf(Xaml, 2, 5));
    }
}
