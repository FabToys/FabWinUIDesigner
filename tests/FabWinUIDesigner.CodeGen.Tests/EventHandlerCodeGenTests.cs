using FabWinUIDesigner.CodeGen;

namespace FabWinUIDesigner.CodeGen.Tests;

[TestClass]
public class EventHandlerCodeGenTests
{
    private static readonly EventHandlerStub ClickStub =
        new("Click", "OkButton_Click", "System.Object", "Microsoft.UI.Xaml.RoutedEventArgs");

    [TestMethod]
    public void EnsureEventHandlers_NoExistingFile_SynthesizesMinimalPartialClass()
    {
        var result = EventHandlerCodeGen.EnsureEventHandlers(null, "FabWinUIDesigner.Samples.SimplePage", [ClickStub]);

        StringAssert.Contains(result, "namespace FabWinUIDesigner.Samples;");
        StringAssert.Contains(result, "public sealed partial class SimplePage");
        StringAssert.Contains(result, "private void OkButton_Click(System.Object sender, Microsoft.UI.Xaml.RoutedEventArgs e)");
    }

    [TestMethod]
    public void EnsureEventHandlers_RerunOnOwnOutput_IsIdempotent()
    {
        var first = EventHandlerCodeGen.EnsureEventHandlers(null, "FabWinUIDesigner.Samples.SimplePage", [ClickStub]);
        var second = EventHandlerCodeGen.EnsureEventHandlers(first, "FabWinUIDesigner.Samples.SimplePage", [ClickStub]);

        Assert.AreEqual(first, second, "Re-running with the same stub must not duplicate the method or change anything else.");
    }

    [TestMethod]
    public void EnsureEventHandlers_HandlerAlreadyExists_LeavesItCompletelyUntouched()
    {
        var existing = """
            public sealed partial class SimplePage
            {
                private int _counter = 0;

                private void OkButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
                {
                    _counter++;
                }
            }
            """;

        var result = EventHandlerCodeGen.EnsureEventHandlers(existing, "SimplePage", [ClickStub]);

        Assert.AreEqual(existing, result, "An existing handler (matched by name) and unrelated code must be preserved byte-for-byte, including formatting.");
    }

    [TestMethod]
    public void EnsureEventHandlers_AddsNewHandlerAlongsideExistingOne()
    {
        var existing = """
            public sealed partial class SimplePage
            {
                private int _counter = 0;

                private void OkButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
                {
                    _counter++;
                }
            }
            """;
        var checkedStub = new EventHandlerStub("Checked", "MyCheckBox_Checked", "System.Object", "Microsoft.UI.Xaml.RoutedEventArgs");

        var result = EventHandlerCodeGen.EnsureEventHandlers(existing, "SimplePage", [ClickStub, checkedStub]);

        StringAssert.Contains(result, "_counter++;", "The existing handler's body must survive untouched.");
        StringAssert.Contains(result, "private void MyCheckBox_Checked(System.Object sender, Microsoft.UI.Xaml.RoutedEventArgs e)");
    }

    [TestMethod]
    public void EnsureEventHandlers_ClassInWrongNamespace_ThrowsRatherThanCorrupting()
    {
        // A same-named class in a *different* namespace than requested must not be treated as
        // a match - silently appending a second class (and, for a file-scoped namespace, a
        // second `namespace` declaration - a compile error) would corrupt an unrelated file.
        var existing = """
            namespace SomeOtherNamespace;

            public sealed partial class SimplePage
            {
            }
            """;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EventHandlerCodeGen.EnsureEventHandlers(existing, "FabWinUIDesigner.Samples.SimplePage", [ClickStub]));
    }
}
