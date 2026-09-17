using System.Xml.Linq;
using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Document.Tests;

[TestClass]
public class XamlDocumentTests
{
    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);

    /// <summary>
    /// Structural comparison, not raw text equality: whitespace *between attributes* in a
    /// start tag isn't part of the XML object model (only whitespace between elements is),
    /// so it can't survive a save regardless of how careful the writer is. See
    /// research/01-xml-roundtrip-formatting.md.
    /// </summary>
    private static void AssertSemanticallyEqual(string expectedXaml, string actualXaml)
    {
        var expected = XDocument.Parse(expectedXaml, LoadOptions.PreserveWhitespace);
        var actual = XDocument.Parse(actualXaml, LoadOptions.PreserveWhitespace);
        Assert.IsTrue(XNode.DeepEquals(expected, actual), "Expected and actual XAML are not structurally equivalent.");
    }

    [TestMethod]
    public void Load_SimplePage_ExposesRootAndChildren()
    {
        var doc = XamlDocument.Load(SamplePath("SimplePage.xaml"));

        Assert.AreEqual("Page", doc.Root.LocalName);

        var canvas = doc.Root.Children.Single();
        Assert.AreEqual("Canvas", canvas.LocalName);
        Assert.AreEqual("400", canvas.GetAttribute("Width"));

        // Not an exact count/order: SimplePage.xaml is also the app's own default sample, so
        // manual testing (e.g. via the M5 toolbox) legitimately adds more children over time.
        // What must always hold is that the two originally-authored elements are still there.
        var canvasChildren = canvas.Children.ToList();
        Assert.IsTrue(canvasChildren.Count >= 2);
        Assert.IsTrue(canvasChildren.Any(e => e.Name == "TitleText"));
        Assert.IsTrue(canvasChildren.Any(e => e.Name == "OkButton"));
    }

    [TestMethod]
    public void Load_NestedContainer_WalksNestedChildren()
    {
        var doc = XamlDocument.Load(SamplePath("NestedContainer.xaml"));

        var canvas = doc.Root.Children.Single();
        var stackPanel = canvas.Children.First(e => e.LocalName == "StackPanel");

        var buttons = stackPanel.Children.Where(e => e.LocalName == "Button").ToList();
        Assert.AreEqual(2, buttons.Count);
        Assert.AreEqual("YesButton", buttons[0].Name);
        Assert.AreEqual("NoButton", buttons[1].Name);
    }

    [TestMethod]
    public void RoundTrip_UnmodifiedDocument_IsSemanticallyEquivalent()
    {
        var originalText = File.ReadAllText(SamplePath("SimplePage.xaml"));

        var doc = XamlDocument.Parse(originalText);
        var roundTrippedText = doc.ToXamlString();

        AssertSemanticallyEqual(originalText, roundTrippedText);
    }

    [TestMethod]
    public void RoundTrip_PreservesInterElementIndentation_ButCollapsesMultiLineAttributeLists()
    {
        var doc = XamlDocument.Load(SamplePath("SimplePage.xaml"));
        var roundTripped = doc.ToXamlString();

        // Known, documented limitation: attribute-to-attribute whitespace inside a start tag
        // isn't part of the XML object model, so the multi-line xmlns block collapses.
        StringAssert.Contains(roundTripped, "<Page x:Class=\"FabWinUIDesigner.Samples.SimplePage\" xmlns=");

        // But whitespace *between* elements (indentation, blank lines) is real text content
        // and round-trips exactly.
        StringAssert.Contains(roundTripped, "\n\n    <Canvas Width=\"400\" Height=\"300\" Background=\"White\">\n        <TextBlock");
    }

    [TestMethod]
    public void ToFormattedXamlString_AddsConsistentIndentation()
    {
        // Deliberately messy - mirrors the real "elements crammed onto one line" state AddChild
        // used to produce before it inserted a newline (see research/25); ToFormattedXamlString
        // should clean this up regardless of how the input got that way.
        const string messy =
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "<Canvas Width=\"400\" Height=\"300\"><TextBlock Text=\"A\" /><Button Content=\"B\" />\n" +
            "    <CheckBox Content=\"C\" /></Canvas>\n" +
            "</Page>";

        var doc = XamlDocument.Parse(messy);
        var formatted = doc.ToFormattedXamlString();

        StringAssert.Contains(formatted, "\n    <Canvas");
        StringAssert.Contains(formatted, "\n        <TextBlock");
        StringAssert.Contains(formatted, "\n        <Button");
        StringAssert.Contains(formatted, "\n        <CheckBox");

        // Formatting must only ever change whitespace, never the document's actual content -
        // unlike AssertSemanticallyEqual (whitespace-sensitive, for the round-trip tests above,
        // where whitespace differences ARE the thing being checked for), this comparison must
        // deliberately ignore whitespace, since reformatting it is the whole point here.
        var expectedNoWhitespace = XDocument.Parse(messy);
        var actualNoWhitespace = XDocument.Parse(formatted);
        Assert.IsTrue(XNode.DeepEquals(expectedNoWhitespace, actualNoWhitespace), "Formatting changed the document's actual content, not just its whitespace.");
    }

    [TestMethod]
    public void SetAttribute_ChangesOnlyTheTargetedAttribute()
    {
        var doc = XamlDocument.Load(SamplePath("SimplePage.xaml"));
        var canvas = doc.Root.Children.Single();
        var button = canvas.Children.Single(e => e.Name == "OkButton");

        button.SetAttribute("Width", "150");

        Assert.AreEqual("150", button.GetAttribute("Width"));
        Assert.AreEqual("32", button.GetAttribute("Height")); // untouched

        var reparsed = XamlDocument.Parse(doc.ToXamlString());
        var reparsedButton = reparsed.Root.Children.Single().Children.Single(e => e.Name == "OkButton");
        Assert.AreEqual("150", reparsedButton.GetAttribute("Width"));
    }

    [TestMethod]
    public void SetAttribute_NullValue_RemovesAttribute()
    {
        var doc = XamlDocument.Parse("<Button xmlns=\"ns\" Width=\"100\" Height=\"32\" />");
        var button = doc.Root;

        button.SetAttribute("Height", null);

        Assert.IsNull(button.GetAttribute("Height"));
        Assert.AreEqual("100", button.GetAttribute("Width"));
    }

    [TestMethod]
    public void AddChild_AppearsInChildrenAndSerializedOutput()
    {
        var doc = XamlDocument.Load(SamplePath("SimplePage.xaml"));
        var canvas = doc.Root.Children.Single();
        var countBefore = canvas.Children.Count();

        var newCheckBox = canvas.AddChild("CheckBox");
        newCheckBox.Name = "AgreeCheck";
        newCheckBox.SetAttribute("Content", "I agree");

        Assert.AreEqual(countBefore + 1, canvas.Children.Count());

        var reparsed = XamlDocument.Parse(doc.ToXamlString());
        var reparsedCanvas = reparsed.Root.Children.Single();
        var reparsedCheckBox = reparsedCanvas.Children.Last();
        Assert.AreEqual("CheckBox", reparsedCheckBox.LocalName);
        Assert.AreEqual("AgreeCheck", reparsedCheckBox.Name);
        Assert.AreEqual("I agree", reparsedCheckBox.GetAttribute("Content"));
    }

    [TestMethod]
    public void Remove_DropsElementFromParentAndSerializedOutput()
    {
        var doc = XamlDocument.Load(SamplePath("NestedContainer.xaml"));
        var canvas = doc.Root.Children.Single();
        var textBox = canvas.Children.Single(e => e.LocalName == "TextBox");

        textBox.Remove();

        Assert.IsFalse(canvas.Children.Any(e => e.LocalName == "TextBox"));

        var reparsed = XamlDocument.Parse(doc.ToXamlString());
        var reparsedCanvas = reparsed.Root.Children.Single();
        Assert.IsFalse(reparsedCanvas.Children.Any(e => e.LocalName == "TextBox"));
    }
}
