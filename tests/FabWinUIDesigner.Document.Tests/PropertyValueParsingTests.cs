using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Document.Tests;

[TestClass]
public class PropertyValueParsingTests
{
    [TestMethod]
    public void TryParseColorHex_SixDigits_ParsesAsOpaque()
    {
        var ok = PropertyValueParsing.TryParseColorHex("#FF8800", out var a, out var r, out var g, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(255, a);
        Assert.AreEqual(0xFF, r);
        Assert.AreEqual(0x88, g);
        Assert.AreEqual(0x00, b);
    }

    [TestMethod]
    public void TryParseColorHex_EightDigits_ParsesAlphaFirst()
    {
        var ok = PropertyValueParsing.TryParseColorHex("#80FF8800", out var a, out var r, out var g, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(0x80, a);
        Assert.AreEqual(0xFF, r);
        Assert.AreEqual(0x88, g);
        Assert.AreEqual(0x00, b);
    }

    [TestMethod]
    [DataRow("FF8800")] // missing '#'
    [DataRow("#FF88")] // wrong length
    [DataRow("#GGGGGG")] // not hex
    public void TryParseColorHex_InvalidInput_ReturnsFalse(string input)
    {
        var ok = PropertyValueParsing.TryParseColorHex(input, out _, out _, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryParseThicknessParts_SingleValue_AppliesToAllSides()
    {
        var ok = PropertyValueParsing.TryParseThicknessParts("10", out var l, out var t, out var r, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(10, l);
        Assert.AreEqual(10, t);
        Assert.AreEqual(10, r);
        Assert.AreEqual(10, b);
    }

    [TestMethod]
    public void TryParseThicknessParts_TwoValues_ApplyHorizontalAndVertical()
    {
        var ok = PropertyValueParsing.TryParseThicknessParts("10,20", out var l, out var t, out var r, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(10, l);
        Assert.AreEqual(20, t);
        Assert.AreEqual(10, r);
        Assert.AreEqual(20, b);
    }

    [TestMethod]
    public void TryParseThicknessParts_FourValues_ApplyLeftTopRightBottom()
    {
        var ok = PropertyValueParsing.TryParseThicknessParts("1,2,3,4", out var l, out var t, out var r, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, l);
        Assert.AreEqual(2, t);
        Assert.AreEqual(3, r);
        Assert.AreEqual(4, b);
    }

    [TestMethod]
    public void TryParseThicknessParts_EmptyString_ReturnsZeroThickness()
    {
        var ok = PropertyValueParsing.TryParseThicknessParts("", out var l, out var t, out var r, out var b);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, l);
        Assert.AreEqual(0, t);
        Assert.AreEqual(0, r);
        Assert.AreEqual(0, b);
    }

    [TestMethod]
    [DataRow("1,2,3")] // 3 values isn't a valid form
    [DataRow("abc")]
    public void TryParseThicknessParts_InvalidInput_ReturnsFalse(string input)
    {
        var ok = PropertyValueParsing.TryParseThicknessParts(input, out _, out _, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void FormatThicknessParts_UniformSides_FormatsAsSingleNumber()
    {
        Assert.AreEqual("10", PropertyValueParsing.FormatThicknessParts(10, 10, 10, 10));
    }

    [TestMethod]
    public void FormatThicknessParts_NonUniformSides_FormatsAsFourNumbers()
    {
        Assert.AreEqual("1,2,3,4", PropertyValueParsing.FormatThicknessParts(1, 2, 3, 4));
    }
}
