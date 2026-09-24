using Microsoft.UI.Xaml.Controls;
using DocumentSession = FabWinUIDesigner.Document.DocumentSession;

namespace FabWinUIDesigner.App;

/// <summary>
/// One tab of the document tab strip: the open file's <see cref="DocumentSession"/> plus the bits
/// of per-file state that live in the window rather than in the document (its tab header, and
/// XAML source text typed but not applied when the user switched away from it).
/// </summary>
internal sealed class DocumentTab
{
    /// <summary>Creates the tab and its <see cref="TabViewItem"/>, which points back to it through its <c>Tag</c>.</summary>
    /// <param name="session">The file shown in this tab.</param>
    /// <param name="untitledName">Name shown while the file has never been saved (e.g. "Untitled1").</param>
    public DocumentTab(DocumentSession session, string untitledName)
    {
        Session = session;
        UntitledName = untitledName;
        Item = new TabViewItem { Tag = this };
        UpdateHeader(isDirty: false);
    }

    /// <summary>The file shown in this tab. Replaced when the file is reloaded from disk.</summary>
    public DocumentSession Session { get; set; }

    /// <summary>This tab's item in the tab strip.</summary>
    public TabViewItem Item { get; }

    /// <summary>Name shown while the file has never been saved.</summary>
    public string UntitledName { get; }

    /// <summary>
    /// XAML source text the user typed in this tab but that wasn't applied when they switched to
    /// another tab (typing pause not over, or invalid XAML). Put back in the editor when the tab
    /// is shown again; null when there's none.
    /// </summary>
    public string? UnappliedSourceText { get; set; }

    /// <summary>Where the user was in this tab (caret, scroll positions, selected element) when they switched away from it, restored when it's shown again; null for a tab never shown yet or just reloaded.</summary>
    public TabViewState? ViewState { get; set; }

    /// <summary>File name with extension, or <see cref="UntitledName"/> for a file never saved.</summary>
    public string DisplayName => Session.FilePath is null ? UntitledName : Path.GetFileName(Session.FilePath);

    /// <summary>Unsaved changes while this tab isn't the active one. The active tab's editor state lives in the window, which has the final say for that tab.</summary>
    public bool IsDirtyWhileInactive => UnappliedSourceText is not null || Session.IsModified;

    /// <summary>Refreshes the header text (name, plus <c>*</c> when unsaved, like VS) and its full-path tooltip.</summary>
    /// <param name="isDirty">Whether the tab has unsaved changes.</param>
    public void UpdateHeader(bool isDirty)
    {
        Item.Header = isDirty ? DisplayName + "*" : DisplayName;
        ToolTipService.SetToolTip(Item, Session.FilePath ?? "(not saved yet)");
    }
}

/// <summary>A tab's view position, kept while another tab is showing.</summary>
/// <param name="CaretLine">XAML editor caret line (zero-based, as the editor reports it).</param>
/// <param name="CaretCharacter">XAML editor caret column (zero-based).</param>
/// <param name="EditorVerticalScroll">XAML editor vertical scroll position.</param>
/// <param name="EditorHorizontalScroll">XAML editor horizontal scroll position.</param>
/// <param name="DesignHorizontalOffset">Design surface horizontal scroll offset.</param>
/// <param name="DesignVerticalOffset">Design surface vertical scroll offset.</param>
/// <param name="SelectedElementIndex">Selected element's position among the document's elements in document order, or null if nothing was selected.</param>
/// <param name="EditorHadFocus">Whether the user was working in the XAML editor, so it gets keyboard focus (and a visible caret) back.</param>
internal sealed record TabViewState(
    int CaretLine,
    int CaretCharacter,
    double EditorVerticalScroll,
    double EditorHorizontalScroll,
    double DesignHorizontalOffset,
    double DesignVerticalOffset,
    int? SelectedElementIndex,
    bool EditorHadFocus);
