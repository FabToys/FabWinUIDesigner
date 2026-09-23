using FabWinUIDesigner.Document;

namespace FabWinUIDesigner.Document.Tests;

[TestClass]
public class DocumentSessionTests
{
    private const string Xaml =
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n" +
        "    <Canvas Width=\"400\" />\n" +
        "</Page>";

    private static DocumentSession NewSession() => new(XamlDocument.Parse(Xaml), filePath: null);

    private static DesignElement Canvas(DocumentSession session) => session.Document.Root.Children.First();

    /// <summary>An in-place edit through the Begin/Commit pair, like a move or a property change.</summary>
    private static void SetWidth(DocumentSession session, string width)
    {
        session.BeginChange();
        Canvas(session).SetAttribute("Width", width);
        session.CommitChange();
    }

    [TestMethod]
    public void NewSession_IsUnmodified_WithNoHistory()
    {
        var session = NewSession();

        Assert.IsFalse(session.IsModified);
        Assert.IsFalse(session.CanUndo);
        Assert.IsFalse(session.CanRedo);
        Assert.IsNull(session.FilePath);
    }

    [TestMethod]
    public void Edit_ThenUndo_ThenRedo_RoundTrips()
    {
        var session = NewSession();
        SetWidth(session, "500");
        Assert.IsTrue(session.IsModified);
        Assert.IsTrue(session.CanUndo);

        Assert.IsTrue(session.Undo());
        Assert.AreEqual("400", Canvas(session).GetAttribute("Width"));
        Assert.IsFalse(session.IsModified, "Undoing back to the saved state counts as unmodified.");
        Assert.IsTrue(session.CanRedo);

        Assert.IsTrue(session.Redo());
        Assert.AreEqual("500", Canvas(session).GetAttribute("Width"));
        Assert.IsTrue(session.IsModified);
    }

    [TestMethod]
    public void NewEdit_ClearsRedo()
    {
        var session = NewSession();
        SetWidth(session, "500");
        session.Undo();

        SetWidth(session, "600");

        Assert.IsFalse(session.CanRedo);
    }

    [TestMethod]
    public void CommitWithoutBegin_RecordsNothing()
    {
        var session = NewSession();

        Assert.IsFalse(session.CommitChange());
        Assert.IsFalse(session.CanUndo);
    }

    [TestMethod]
    public void UndoRedo_OnEmptyHistory_ReturnFalse()
    {
        var session = NewSession();

        Assert.IsFalse(session.Undo());
        Assert.IsFalse(session.Redo());
    }

    [TestMethod]
    public void ReplaceDocument_IsOneUndoStep()
    {
        var session = NewSession();

        session.ReplaceDocument(XamlDocument.Parse(Xaml.Replace("400", "700")));

        Assert.AreEqual("700", Canvas(session).GetAttribute("Width"));
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("400", Canvas(session).GetAttribute("Width"));
        Assert.IsFalse(session.CanUndo);
    }

    [TestMethod]
    public void Save_WritesFile_SetsPath_AndClearsModified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DocumentSessionTests-{Guid.NewGuid():N}.xaml");
        try
        {
            var session = NewSession();
            SetWidth(session, "500");

            session.Save(path);

            Assert.AreEqual(path, session.FilePath);
            Assert.IsFalse(session.IsModified);
            Assert.AreEqual("500", Canvas(DocumentSession.Open(path)).GetAttribute("Width"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Save_WithoutPath_OnANewDocument_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => NewSession().Save());
    }

    /// <summary>Runs <paramref name="test"/> against a session opened on a fresh temp file, deleting it afterwards.</summary>
    private static void WithSavedFile(Action<DocumentSession, string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DocumentSessionTests-{Guid.NewGuid():N}.xaml");
        try
        {
            File.WriteAllText(path, Xaml);
            test(DocumentSession.Open(path), path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void CheckDisk_AfterOpen_IsUnchanged()
    {
        WithSavedFile((session, _) => Assert.AreEqual(DiskState.Unchanged, session.CheckDisk()));
    }

    [TestMethod]
    public void CheckDisk_AfterOutsideEdit_IsChanged_UntilAccepted()
    {
        WithSavedFile((session, path) =>
        {
            File.WriteAllText(path, Xaml.Replace("400", "900"));
            Assert.AreEqual(DiskState.Changed, session.CheckDisk());

            session.AcceptDiskState();
            Assert.AreEqual(DiskState.Unchanged, session.CheckDisk());
        });
    }

    [TestMethod]
    public void CheckDisk_FileOnlyTouched_IsUnchanged()
    {
        WithSavedFile((session, path) =>
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.AreEqual(DiskState.Unchanged, session.CheckDisk());
        });
    }

    [TestMethod]
    public void CheckDisk_AfterOwnSave_IsUnchanged()
    {
        WithSavedFile((session, _) =>
        {
            SetWidth(session, "500");
            session.Save();
            Assert.AreEqual(DiskState.Unchanged, session.CheckDisk());
        });
    }

    [TestMethod]
    public void CheckDisk_FileDeleted_IsMissing_UntilAccepted()
    {
        WithSavedFile((session, path) =>
        {
            File.Delete(path);
            Assert.AreEqual(DiskState.Missing, session.CheckDisk());

            session.AcceptDiskState();
            Assert.AreEqual(DiskState.Unchanged, session.CheckDisk());
        });
    }

    [TestMethod]
    public void CheckDisk_NeverSavedDocument_IsUnchanged()
    {
        Assert.AreEqual(DiskState.Unchanged, NewSession().CheckDisk());
    }

    [TestMethod]
    public void MarkModified_StaysModified_UntilSave()
    {
        WithSavedFile((session, _) =>
        {
            session.MarkModified();
            Assert.IsTrue(session.IsModified);

            session.Save();
            Assert.IsFalse(session.IsModified);
        });
    }
}
