namespace FabWinUIDesigner.Document;

/// <summary>How the file on disk compares with what a <see cref="DocumentSession"/> last loaded or saved.</summary>
public enum DiskState
{
    /// <summary>Same content as last loaded/saved (or the document has never been saved).</summary>
    Unchanged,

    /// <summary>Another program changed the file's content.</summary>
    Changed,

    /// <summary>The file was deleted or renamed.</summary>
    Missing,
}

/// <summary>
/// One open XAML file: its in-memory <see cref="XamlDocument"/>, where it lives on disk, its
/// undo/redo history, and whether it has unsaved changes. The designer's view state (the live
/// preview tree, the selection) deliberately isn't here - only the active file is rendered, so
/// that belongs to the window, not to each open file.
/// </summary>
/// <remarks>
/// Undo/Redo keeps whole-document text snapshots rather than a command pattern - simple, and
/// cheap enough at our document sizes. <see cref="BeginChange"/> takes the snapshot right before
/// a mutation and <see cref="CommitChange"/> records it right after it succeeds, so an edit
/// that's validated and rejected (e.g. bad property input) never pollutes the undo stack.
/// </remarks>
public sealed class DocumentSession
{
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private string? _pendingUndoSnapshot;

    // Compared against the document text as of the last load/save, not a simple bool, so undoing
    // back to that exact state counts as unmodified again.
    private string _lastSavedXaml;

    // Set by MarkModified (e.g. the file was changed or deleted on disk and the user kept this
    // version): unsaved regardless of the text comparison, until the next Save.
    private bool _forcedModified;

    // What the file looked like on disk as of the last load/save/AcceptDiskState, for CheckDisk.
    // A null _diskWriteTimeUtc means it was missing then.
    private DateTime? _diskWriteTimeUtc;
    private long _diskLength;
    private string? _diskText;

    /// <summary>Starts a session on an already-parsed document, with no history and nothing unsaved.</summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="filePath">Where it's saved, or null for a new file that hasn't been saved yet.</param>
    public DocumentSession(XamlDocument document, string? filePath)
    {
        Document = document;
        FilePath = filePath;
        _lastSavedXaml = document.ToXamlString();
        AcceptDiskState();
    }

    /// <summary>Loads a `.xaml` file into a new session.</summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <returns>A session on the loaded document.</returns>
    public static DocumentSession Open(string path) => new(XamlDocument.Load(path), path);

    /// <summary>The current in-memory document. Replaced (not mutated) by <see cref="ReplaceDocument"/>, <see cref="Undo"/> and <see cref="Redo"/>.</summary>
    public XamlDocument Document { get; private set; }

    /// <summary>Where the document is saved, or null for a new file that hasn't been saved yet.</summary>
    public string? FilePath { get; private set; }

    /// <summary>True when the document differs from what was last loaded or saved.</summary>
    public bool IsModified => _forcedModified || Document.ToXamlString() != _lastSavedXaml;

    /// <summary>True when <see cref="Undo"/> has something to restore.</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>True when <see cref="Redo"/> has something to restore.</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Call right before mutating <see cref="Document"/> in place. Paired with <see cref="CommitChange"/>.</summary>
    public void BeginChange() => _pendingUndoSnapshot = Document.ToXamlString();

    /// <summary>
    /// Call right after a mutation succeeds: records the snapshot taken by
    /// <see cref="BeginChange"/> as one undo step and clears redo (a fresh edit invalidates
    /// whatever redo history existed). Does nothing if Begin wasn't called first.
    /// </summary>
    /// <returns>True if an undo step was recorded.</returns>
    public bool CommitChange()
    {
        if (_pendingUndoSnapshot is null)
        {
            return false;
        }

        _undoStack.Push(_pendingUndoSnapshot);
        _redoStack.Clear();
        _pendingUndoSnapshot = null;
        return true;
    }

    /// <summary>Swaps in a whole new document (e.g. re-parsed from edited source text) as one undo step.</summary>
    /// <param name="document">The replacement document.</param>
    public void ReplaceDocument(XamlDocument document)
    {
        BeginChange();
        Document = document;
        CommitChange();
    }

    /// <summary>Restores the document to the top of the undo stack, pushing the current state onto redo first.</summary>
    /// <returns>False if there was nothing to undo.</returns>
    public bool Undo() => Restore(from: _undoStack, pushCurrentOnto: _redoStack);

    /// <summary>Restores the document to the top of the redo stack, pushing the current state onto undo first.</summary>
    /// <returns>False if there was nothing to redo.</returns>
    public bool Redo() => Restore(from: _redoStack, pushCurrentOnto: _undoStack);

    /// <summary>Saves the document and marks it unmodified.</summary>
    /// <param name="path">Where to save, becoming the new <see cref="FilePath"/> (Save As), or null to save to the current <see cref="FilePath"/>.</param>
    /// <exception cref="InvalidOperationException">No <paramref name="path"/> was given and the document has never been saved.</exception>
    public void Save(string? path = null)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("A new document needs a path for its first save.");
        Document.Save(target);
        FilePath = target;
        _lastSavedXaml = Document.ToXamlString();
        _forcedModified = false;
        AcceptDiskState();
    }

    /// <summary>Marks the document as having unsaved changes until the next <see cref="Save"/>, even if its text matches what was last saved.</summary>
    public void MarkModified() => _forcedModified = true;

    /// <summary>
    /// Compares the file on disk with what this session last loaded, saved or accepted: first its
    /// last-write time and length, then - only if those differ - its text, so a file that was just
    /// touched (new time, same content) still counts as unchanged. A read that fails (e.g. the file
    /// is locked mid-write by the other program) also counts as unchanged, so the caller simply
    /// checks again later.
    /// </summary>
    /// <returns>The file's state; always <see cref="DiskState.Unchanged"/> for a document never saved.</returns>
    public DiskState CheckDisk()
    {
        if (FilePath is null)
        {
            return DiskState.Unchanged;
        }

        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists)
            {
                return _diskWriteTimeUtc is null ? DiskState.Unchanged : DiskState.Missing;
            }

            if (file.LastWriteTimeUtc == _diskWriteTimeUtc && file.Length == _diskLength)
            {
                return DiskState.Unchanged;
            }

            if (File.ReadAllText(FilePath) == _diskText)
            {
                AcceptDiskState();
                return DiskState.Unchanged;
            }

            return DiskState.Changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskState.Unchanged;
        }
    }

    /// <summary>Records the file's current state on disk as the new baseline for <see cref="CheckDisk"/>, e.g. after the user chose to keep this version over an outside change.</summary>
    public void AcceptDiskState()
    {
        _diskWriteTimeUtc = null;
        _diskLength = 0;
        _diskText = null;
        if (FilePath is null)
        {
            return;
        }

        try
        {
            var file = new FileInfo(FilePath);
            if (file.Exists)
            {
                _diskText = File.ReadAllText(FilePath);
                _diskWriteTimeUtc = file.LastWriteTimeUtc;
                _diskLength = file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now - leave the baseline empty; the next check sees a change and asks.
        }
    }

    private bool Restore(Stack<string> from, Stack<string> pushCurrentOnto)
    {
        if (from.Count == 0)
        {
            return false;
        }

        pushCurrentOnto.Push(Document.ToXamlString());
        Document = XamlDocument.Parse(from.Pop());
        return true;
    }
}
