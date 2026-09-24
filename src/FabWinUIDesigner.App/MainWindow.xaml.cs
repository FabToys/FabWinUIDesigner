using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TextControlBoxNS;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using FabWinUIDesigner.CodeGen;
using FabWinUIDesigner.Core;
using DesignElement = FabWinUIDesigner.Document.DesignElement;
using XamlDocument = FabWinUIDesigner.Document.XamlDocument;
using XamlSourcePositions = FabWinUIDesigner.Document.XamlSourcePositions;
using DocumentSession = FabWinUIDesigner.Document.DocumentSession;
using DiskState = FabWinUIDesigner.Document.DiskState;

namespace FabWinUIDesigner.App;

/// <summary>
/// The designer's single window: file browser, toolbox, design surface (with selection/move/resize
/// adorners), XAML source view, and property grid, all wired directly in code-behind. Several
/// files can be open in tabs (<see cref="DocumentTab"/>); the panels show the active tab's
/// in-memory <see cref="XamlDocument"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string SpikeLogPath = Path.Combine(Path.GetTempPath(), "FabWinUIDesigner", "m2-xamlreader-spike.log");
    private static readonly string SnapshotPath = Path.Combine(Path.GetTempPath(), "FabWinUIDesigner", "preview-snapshot.png");
    private static readonly string LayoutConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabWinUIDesigner", "layout.json");
    private static readonly string RecentConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabWinUIDesigner", "recent.json");

    private const double MinElementSize = 8;
    private const double HandleSize = 7;

    // Tools → Options choices (settings.json). Read once at startup, replaced as a whole on OK.
    private AppSettings _settings = AppSettings.Load();

    // Snap-to-grid; its spacing is _settings.GridSize.
    private bool _snapToGridEnabled;

    // Property/Events grid view toggle - one
    // flag drives both grids, since they share BuildCategorizedGrid.
    private bool _alphabeticalPropertyView;

    // Toolbox view: grouped by category (default) or one alphabetical list, plus which groups are
    // expanded (by name). Both are kept in layout.json. Expanded rather than collapsed groups are
    // stored, so a group added to the JSON later starts collapsed. Until layout.json has a list,
    // the groups marked ExpandedByDefault in the metadata are the expanded ones - worked out on
    // first use, so from whichever metadata file is loaded by then (possibly a custom one).
    private bool _alphabeticalToolboxView;
    private HashSet<string>? _expandedToolboxGroupsSaved;

    private HashSet<string> ExpandedToolboxGroups => _expandedToolboxGroupsSaved ??= new(
        PropertyGridSchema.ToolboxGroups.Where(g => g.ExpandedByDefault).Select(g => g.Name),
        StringComparer.Ordinal);

    // The tab shown on the design surface and in the XAML editor, or null when no file is open.
    // _session is its file (document, path, undo/redo, saved state); CurrentDocument/CurrentFilePath
    // are shorthands for that file's two most-used parts.
    private DocumentTab? _activeTab;
    private DocumentSession? _session => _activeTab?.Session;
    private XamlDocument? CurrentDocument => _session?.Document;
    private string? CurrentFilePath => _session?.FilePath;
    private IReadOnlyDictionary<UIElement, DesignElement> _liveToDesign = new Dictionary<UIElement, DesignElement>();
    private UIElement? _selectedLiveElement;
    private DesignElement? _selectedDesignElement;

    // TextControlBox (unlike TextBox) doesn't route keyboard focus through a plain TextBox
    // instance, so RootGrid_KeyDown's "is a TextBox focused?" guard can't see it by type -
    // tracked explicitly via this control's own GotFocus/LostFocus instead (see the constructor).
    private bool _xamlSourceViewHasFocus;

    // Apply-after-a-pause for typing in the XAML source view: each keystroke restarts
    // _sourceEditTimer, and when it fires the typed text is applied (TryApplyXamlSourceEdit).
    // _sourceEditPending is true while the editor holds typed text not applied yet - it also
    // counts as "unsaved", so Save lights up while typing. _keepEditorTextOnRefresh stops that
    // apply's design-surface refresh from rewriting the editor under the user's caret. The pause
    // length is _settings.XamlApplyDelayMs.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _sourceEditTimer;
    private bool _sourceEditPending;
    private bool _keepEditorTextOnRefresh;

    // TextControlBox.LoadText resets the caret to the start of the document as a side effect,
    // which raises SelectionChanged - the same failure mode already fixed twice before for the
    // old TextBox: a programmatic reload's caret-reset gets misread by
    // XamlSourceView_SelectionChanged as "the user moved the caret", re-selecting whatever
    // element now sits at offset 0. This time it's worse than a wrong selection - re-selecting
    // tears down and rebuilds the property grid (BuildPropertyGrid clears PropertyGridPanel),
    // which crashes if triggered while a property-grid control's own event handler (e.g. a text
    // field's LostFocus, which is what calls SetXamlSourceText via RefreshXamlSourceView in the
    // first place) is still executing on the call stack - the visual tree gets mutated out from
    // under a control mid-dispatch. Set around every SetXamlSourceText call so its caret reset
    // can never drive a reselect; real user caret movement is unaffected.
    private bool _suppressSourceSelectionSync;

    // A caret position our own code put there (loading text, restoring a tab). The editor can
    // report it again later through SelectionChanged - on its next cursor redraw, which only
    // happens while it has focus - long after _suppressSourceSelectionSync was reset. That report
    // isn't the user moving the caret, so XamlSourceView_SelectionChanged ignores the caret while
    // it's still at this position; the first move elsewhere clears it.
    private (int Line, int Character)? _caretSetByCode;

    // Move drag state (set while a PointerPressed-on-an-element -> PointerMoved -> PointerReleased
    // sequence is in progress on DesignSurfaceHost).
    private UIElement? _moveElement;
    private DesignElement? _moveDesignElement;
    private Point _moveStartPointerPosition;
    private double _moveStartLeft;
    private double _moveStartTop;

    // Resize drag state (set while dragging one of the adorner's resize handles).
    private string? _resizeDirection;
    private UIElement? _resizeElement;
    private DesignElement? _resizeDesignElement;
    private Point _resizeStartPointerPosition;
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private double _resizeStartWidth;
    private double _resizeStartHeight;

    /// <summary>Wires up splitters, hover cursors, design-surface pointer routing, and loads the recent-files/folders menu. Starts with no document open.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Title = AppTitle;

        // Before anything reads the metadata (the Toolbox, below).
        var metadataProblem = UseMetadataFiles(_settings);
        ApplyEditorAppearance();

        LoadPanelLayout();
        SnapToGridToggle.IsChecked = _snapToGridEnabled;
        AlphabeticalViewToggle.IsChecked = _alphabeticalPropertyView;
        AlphabeticalToolboxToggle.IsChecked = _alphabeticalToolboxView;
        // Set directly too: the toggles' Checked/Unchecked handlers (which also sync the menu)
        // don't fire when the loaded value equals the default.
        SnapToGridMenuItem.IsChecked = _snapToGridEnabled;
        AlphabeticalViewMenuItem.IsChecked = _alphabeticalPropertyView;
        AlphabeticalToolboxMenuItem.IsChecked = _alphabeticalToolboxView;

        // After LoadPanelLayout, which says which view to build and which groups are collapsed.
        BuildToolbox();
        DesignSurfaceHost.SizeChanged += (_, _) =>
        {
            GridOverlay.Width = DesignSurfaceHost.ActualWidth;
            GridOverlay.Height = DesignSurfaceHost.ActualHeight;
            RenderGridOverlay();
        };

        AttachColumnSplitter(ToolboxSplitter, ToolboxColumn, minWidth: 100, maxWidth: 400, SavePanelLayout);
        // invert:true here - XamlSourceRow is the row *after* this splitter (DesignSurfaceRow,
        // splitter, XamlSourceRow), so dragging up should grow it, unlike FilePropertiesSplitter
        // below where the controlled row (FilePanelRow) comes *before* the splitter.
        AttachRowSplitter(DesignXamlSplitter, XamlSourceRow, minHeight: 80, maxHeight: 600, SavePanelLayout, invert: true);
        AttachRowSplitter(FilePropertiesSplitter, FilePanelRow, minHeight: 80, maxHeight: 600, SavePanelLayout);
        Closed += (_, _) => SavePanelLayout();
        Activated += MainWindow_Activated;
        AppWindow.Closing += AppWindow_Closing;

        // Reopening last session's tabs waits for the window's content to be loaded, so their
        // first render (and its deferred layout/snapshot work) runs against a live window.
        void RestoreTabsOnce(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= RestoreTabsOnce;
            RestoreOpenTabs();
        }

        RootGrid.Loaded += RestoreTabsOnce;

        if (metadataProblem is not null)
        {
            SetStatus(metadataProblem);
        }

        // Hover cursor: a plain <Grid> can't show one (UIElement.ProtectedCursor is protected),
        // hence SplitterThumb/ResizeHandle - see their doc comment.
        ToolboxSplitter.SetCursor(InputSystemCursorShape.SizeWestEast);
        DesignXamlSplitter.SetCursor(InputSystemCursorShape.SizeNorthSouth);
        FilePropertiesSplitter.SetCursor(InputSystemCursorShape.SizeNorthSouth);

        HandleNW.SetCursor(InputSystemCursorShape.SizeNorthwestSoutheast);
        HandleSE.SetCursor(InputSystemCursorShape.SizeNorthwestSoutheast);
        HandleNE.SetCursor(InputSystemCursorShape.SizeNortheastSouthwest);
        HandleSW.SetCursor(InputSystemCursorShape.SizeNortheastSouthwest);
        HandleN.SetCursor(InputSystemCursorShape.SizeNorthSouth);
        HandleS.SetCursor(InputSystemCursorShape.SizeNorthSouth);
        HandleW.SetCursor(InputSystemCursorShape.SizeWestEast);
        HandleE.SetCursor(InputSystemCursorShape.SizeWestEast);

        // Interactive controls (Button, CheckBox, ...) mark PointerPressed/Moved/Released as
        // handled once they start tracking their own press state, so they never bubble to a
        // normal XAML event handler here. Capturing the pointer on DesignSurfaceHost does NOT
        // change this - capture only guarantees continued delivery to the capturing element,
        // it doesn't take routing priority away from whatever is actually under the pointer,
        // so the hit control still gets first crack and can still mark events Handled. All
        // three need handledEventsToo:true to fire regardless (found via real interactive
        // testing: PointerReleased on a Button was being swallowed the same way, leaving the
        // move-drag "stuck" since our plain-XAML-wired release handler never ran).
        DesignSurfaceHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DesignSurfaceHost_PointerPressed), true);
        DesignSurfaceHost.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(DesignSurfaceHost_PointerMoved), true);
        DesignSurfaceHost.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(DesignSurfaceHost_PointerReleased), true);
        // PointerCaptureLost can fire instead of PointerReleased (e.g. the pointer leaves the
        // window mid-drag) - without also resetting state here, a drag could get "stuck" and
        // silently keep applying stale deltas to unrelated future pointer movement. Reusing the
        // same handler is safe: it already guards on _moveElement being non-null.
        DesignSurfaceHost.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(DesignSurfaceHost_PointerReleased), true);

        LoadRecentLists();
        RefreshRecentMenu();

        UpdateXamlPaneTabButtonVisuals();
        ShowPropertyPaneTab(PropertyPaneTab.Properties);

        // The built-in XML mode (SelectSyntaxHighlightingById(SyntaxHighlightID.XML)) turned out
        // to look broken on real XAML: its one regex for an opening tag greedily matches the
        // *whole* tag including every attribute, and its attribute-name and attribute-value
        // colors happen to be the exact same green in light theme - so a multi-attribute line
        // like the root Page/Canvas's xmlns block rendered as "tag name, then everything else in
        // one solid color" instead of readable syntax coloring (found in
        // interactive testing). XamlSyntaxHighlightingJson below is a custom scheme instead -
        // separate regexes for the tag name, each attribute name, and each quoted value, so they
        // never collapse into one match or share a color. Verified against real WinRT-projected
        // JSON parsing (Newtonsoft.Json under the hood - a plain JSON array for Filter, which the
        // library's own type expects as a pipe-delimited *string*, silently failed to deserialize)
        // with a throwaway probe before trusting it here.
        XamlSourceView.EnableSyntaxHighlighting = true;
        var xamlHighlighting = TextControlBox.GetSyntaxHighlightingFromJson(XamlSyntaxHighlightingJson);
        if (xamlHighlighting.Succeed)
        {
            XamlSourceView.SyntaxHighlighting = xamlHighlighting.SyntaxHighlighting;
        }

        // Wired here rather than as XAML event attributes: TextControlBox's event delegates
        // don't match TextBox's shapes (LostFocus/GotFocus pass only a sender, no EventArgs;
        // SelectionChanged's second parameter is a type specific to this control) - implicit-
        // typed lambdas bind against whatever the real delegate is without needing to name it.
        XamlSourceView.SelectionChanged += (_, _) =>
        {
            UpdateCaretStatus();
            XamlSourceView_SelectionChanged();
        };
        XamlSourceView.TextChanged += _ => XamlSourceView_TextChanged();

        // Ctrl+W closes the tab (RootGrid_KeyDown), even while typing in the editor - the editor
        // can use it to select a word instead, which would make it do both.
        XamlSourceView.ControlW_SelectWord = false;
        _sourceEditTimer = DispatcherQueue.CreateTimer();
        _sourceEditTimer.Interval = TimeSpan.FromMilliseconds(_settings.XamlApplyDelayMs);
        _sourceEditTimer.IsRepeating = false;
        _sourceEditTimer.Tick += (_, _) => ApplySourceEditAfterPause();
        XamlSourceView.LostFocus += _ =>
        {
            _xamlSourceViewHasFocus = false;
            XamlSourceView_LostFocus();
        };
        XamlSourceView.GotFocus += _ => _xamlSourceViewHasFocus = true;

        // GotFocus bubbles up from whichever element got focus, and can't be marked handled on
        // the way, so one handler on the root sees every focus change inside the window.
        RootGrid.GotFocus += RootGrid_GotFocus;
    }

    /// <summary>
    /// A custom syntax-highlighting scheme for <see cref="XamlSourceView"/>, in
    /// <c>TextControlBox.GetSyntaxHighlightingFromJson</c>'s own JSON shape (its
    /// <c>JsonSyntaxHighlighting</c> DTO - <c>Filter</c> is a pipe-delimited <em>string</em>, not
    /// a JSON array, confirmed via a throwaway probe against the real package before trusting it
    /// here). Colors follow Visual Studio's classic XML/XAML editor palette: element
    /// names maroon, attribute names red, quoted values blue, comments green - each its own
    /// regex, deliberately narrower than the built-in XML language's single whole-tag regex (see
    /// the comment in the constructor for why that one looked broken on real XAML).
    /// </summary>
    private const string XamlSyntaxHighlightingJson = """
        {
            "Name": "XAML",
            "Author": "FabWinUIDesigner",
            "Filter": ".xaml",
            "Description": "XAML palette matching Visual Studio's default XML/XAML editor colors",
            "Highlights": [
                { "Pattern": "</?([a-zA-Z_:][\\w:.-]*)", "ColorLight": "#A31515", "ColorDark": "#E06C75" },
                { "Pattern": "[a-zA-Z_:][\\w:.-]*(?==)", "ColorLight": "#FF0000", "ColorDark": "#D19A66" },
                { "Pattern": "\"[^\"\\n]*\"", "ColorLight": "#0000FF", "ColorDark": "#98C379" },
                { "Pattern": "<!--[\\s\\S]*?-->", "ColorLight": "#008000", "ColorDark": "#7F848E" }
            ]
        }
        """;

    /// <summary>A blank single-Canvas Page, same shape as the sample fixtures minus x:Class (a brand-new file has no code-behind yet), with the root Canvas in the Options' new-page background.</summary>
    private string NewDocumentXaml() =>
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\">\n" +
        $"    <Canvas Width=\"400\" Height=\"300\" Background=\"{System.Security.SecurityElement.Escape(_settings.NewPageBackground)}\" />\n" +
        "</Page>";

    /// <summary>
    /// VS-style "Save changes?" prompt before closing tabs (one tab, or all of them on exit), for
    /// those with unsaved changes. Save goes through them one at a time, showing each and asking
    /// for a location if it was never saved.
    /// </summary>
    /// <param name="tabs">The tabs about to be closed.</param>
    /// <returns>True if nothing was unsaved, the user chose Don't Save, or every save succeeded; false if they cancelled (the dialog or a Save As).</returns>
    private async Task<bool> ConfirmSaveTabsAsync(IReadOnlyList<DocumentTab> tabs)
    {
        var dirtyTabs = tabs.Where(IsTabDirty).ToList();
        if (dirtyTabs.Count == 0)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Save changes?",
            Content = dirtyTabs.Count == 1
                ? $"Save changes to {dirtyTabs[0].DisplayName}?"
                : "Save changes to the following files?\n\n" + string.Join("\n", dirtyTabs.Select(t => t.DisplayName)),
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another ContentDialog is already open (e.g. a second click on a tab's X while this
            // one is showing) - treat it as Cancel.
            return false;
        }

        if (result == ContentDialogResult.Secondary)
        {
            return true;
        }

        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        foreach (var tab in dirtyTabs)
        {
            ActivateTab(tab);
            if (!await SaveCurrentDocumentAsync())
            {
                return false;
            }
        }

        return true;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => NewFile();

    // Numbers the untitled tabs (Untitled1, Untitled2, ...) for this run of the app.
    private int _untitledCount;

    /// <summary>Opens a blank document in a new tab.</summary>
    private void NewFile()
    {
        var session = new DocumentSession(XamlDocument.Parse(NewDocumentXaml()), filePath: null);
        AddTab(new DocumentTab(session, $"Untitled{++_untitledCount}"), activate: true);
        SetStatus("New file");
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await OpenFileAsync();

    /// <summary>Prompts for a `.xaml` file via the file picker and loads it.</summary>
    private async Task OpenFileAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".xaml");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            OpenFileInTab(file.Path);
        }
    }

    /// <summary>Saves the current document, prompting for a location first ("Save As" semantics) if it doesn't have a path yet (i.e. it came from New File).</summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveCurrentDocumentAsync();

    /// <summary>File → Save As: always prompts for a location, then saves there and makes it the current file.</summary>
    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e) => await SaveCurrentDocumentAsync(saveAs: true);

    /// <summary>
    /// Commits any pending edit sitting in the XAML source view first (so Ctrl+S while typing
    /// there behaves the way it would in a real text editor - save what you just typed, not the
    /// stale on-disk state), then saves. If the pending edit is invalid XAML, the save is
    /// skipped - saving the old document while an inline error is showing would look like the
    /// edit was silently discarded.
    /// </summary>
    /// <param name="saveAs">True to always prompt for a location (Save As), even if the document already has a path.</param>
    /// <returns>True if the document was saved; false if there was none, its pending edit is invalid, or the user cancelled the location prompt.</returns>
    private async Task<bool> SaveCurrentDocumentAsync(bool saveAs = false)
    {
        if (!TryApplyXamlSourceEdit())
        {
            return false;
        }

        var session = _session;
        if (session is null)
        {
            return false;
        }

        // A document created via New File has no path yet - prompt for one, same as "Save As".
        string? newPath = null;
        if (session.FilePath is null || saveAs)
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("XAML File", new List<string> { ".xaml" });
            picker.SuggestedFileName = session.FilePath is null ? "NewPage" : Path.GetFileNameWithoutExtension(session.FilePath);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return false;
            }

            newPath = file.Path;
        }

        session.Save(newPath);
        UpdateCommandStates();
        SetStatus($"Saved {Path.GetFileName(session.FilePath)}");
        AddRecentFile(session.FilePath!);
        SyncEventHandlerStubs();

        // A first save or Save As gives the tab a (new) path to reopen next time.
        SaveOpenTabs();
        return true;
    }

    /// <summary>File → Exit: same as the window's close button.</summary>
    private async void ExitMenuItem_Click(object sender, RoutedEventArgs e) => await CloseWindowAsync();

    // Set once the user has confirmed closing the window, so the Close() that follows isn't
    // cancelled again by AppWindow_Closing. _closingWindow guards against a second close request
    // (X clicked again) while the first one's dialog is still up.
    private bool _closeConfirmed;
    private bool _closingWindow;

    /// <summary>
    /// The window's X button / Alt+F4: always cancelled here and replaced by
    /// <see cref="CloseWindowAsync"/>, since this event can't wait for a dialog - it closes the
    /// window for real itself once the user has answered.
    /// </summary>
    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        await CloseWindowAsync();
    }

    /// <summary>Closes the window after asking about every tab's unsaved changes (see <see cref="ConfirmSaveTabsAsync"/>). The open tabs are left in place, so they're reopened on the next start.</summary>
    private async Task CloseWindowAsync()
    {
        if (_closingWindow)
        {
            return;
        }

        _closingWindow = true;
        try
        {
            if (!await ConfirmSaveTabsAsync(Tabs.ToList()))
            {
                return;
            }
        }
        finally
        {
            _closingWindow = false;
        }

        _closeConfirmed = true;
        Close();
    }

    /// <summary>Tools → Options: shows the settings dialog and applies what the user confirmed.</summary>
    private async void OptionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsDialog(_settings, _snapToGridEnabled, WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            XamlRoot = Content.XamlRoot,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another ContentDialog is already open.
            return;
        }

        if (result == ContentDialogResult.Primary)
        {
            ApplySettings(dialog.Result, dialog.SnapToGrid);
        }
    }

    /// <summary>Makes new settings take effect at once, and saves them.</summary>
    /// <param name="settings">The settings confirmed in the Options dialog (already validated there).</param>
    /// <param name="snapToGrid">Snap-to-grid as set in the dialog.</param>
    private void ApplySettings(AppSettings settings, bool snapToGrid)
    {
        var metadataChanged = settings.PropertyMetadataPath != _settings.PropertyMetadataPath
            || settings.EventMetadataPath != _settings.EventMetadataPath;
        _settings = settings;
        _settings.Save();

        var status = "Options saved";
        if (metadataChanged)
        {
            // The dialog loaded the files moments ago, but they could have changed since.
            status = UseMetadataFiles(_settings) ?? "Options saved, control metadata reloaded";
            BuildToolbox();
            if (_selectedDesignElement is not null)
            {
                BuildPropertyGrid(_selectedDesignElement);
            }
        }

        // The toggle's handler redraws the dots and saves the layout, but only when the value
        // changes - the dots are redrawn here anyway, for a new grid size.
        SnapToGridToggle.IsChecked = snapToGrid;
        RenderGridOverlay();

        _sourceEditTimer.Interval = TimeSpan.FromMilliseconds(_settings.XamlApplyDelayMs);
        ApplyEditorAppearance();

        TrimRecentList(_recentFiles);
        TrimRecentList(_recentFolders);
        SaveRecentLists();
        RefreshRecentMenu();

        SetStatus(status);
    }

    /// <summary>
    /// Loads the property and event metadata files named in <paramref name="settings"/> (the
    /// built-in ones where none is set). One that won't load - e.g. edited outside the designer
    /// since it was chosen - is replaced by the built-in file rather than stopping the app.
    /// </summary>
    /// <returns>Null if every file loaded; otherwise a message for the status bar.</returns>
    private static string? UseMetadataFiles(AppSettings settings)
    {
        var problems = new List<string>();
        try
        {
            PropertyGridSchema.Use(settings.PropertyMetadataPath);
        }
        catch (InvalidDataException ex)
        {
            PropertyGridSchema.Use(null);
            problems.Add(ex.Message);
        }

        try
        {
            EventGridSchema.Use(settings.EventMetadataPath);
        }
        catch (InvalidDataException ex)
        {
            EventGridSchema.Use(null);
            problems.Add(ex.Message);
        }

        return problems.Count == 0 ? null : $"Using the built-in metadata instead: {string.Join(" ", problems)}";
    }

    /// <summary>
    /// Applies the Options' font size and background to the XAML editor. The editor takes its
    /// colours as one <see cref="TextControlBoxDesign"/> (null = its own default look), so a
    /// custom background comes with matching text, caret, selection and line-number colours:
    /// VS-like light ones, or light-on-dark ones for a dark background - which also switches the
    /// editor to its dark theme, so the syntax highlighting uses its dark palette.
    /// </summary>
    private void ApplyEditorAppearance()
    {
        XamlSourceView.FontSize = _settings.EditorFontSize;

        if (_settings.EditorBackground is not { } backgroundText || !PropertyValueConverter.TryParseColor(backgroundText, out var background))
        {
            XamlSourceView.Design = null;
            XamlSourceView.RequestedTheme = ElementTheme.Default;
            return;
        }

        // Perceived brightness (ITU-R BT.601 weights), 0..255.
        var isDark = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B) < 128;
        XamlSourceView.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        XamlSourceView.Design = isDark
            ? new TextControlBoxDesign(
                new SolidColorBrush(background),
                textColor: Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC),
                selectionColor: Color.FromArgb(0x99, 0x26, 0x4F, 0x78),
                cursorColor: Colors.White,
                lineHighlighterColor: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
                lineNumberColor: Color.FromArgb(0xFF, 0x85, 0x85, 0x85),
                lineNumberBackground: background,
                searchHighlightColor: Color.FromArgb(0x99, 0x62, 0x33, 0x15),
                invisibleCharacterColor: Color.FromArgb(0xFF, 0x50, 0x50, 0x50))
            : new TextControlBoxDesign(
                new SolidColorBrush(background),
                textColor: Colors.Black,
                selectionColor: Color.FromArgb(0x99, 0xAD, 0xD6, 0xFF),
                cursorColor: Colors.Black,
                lineHighlighterColor: Color.FromArgb(0x30, 0x00, 0x00, 0x00),
                lineNumberColor: Color.FromArgb(0xFF, 0x2B, 0x91, 0xAF),
                lineNumberBackground: background,
                searchHighlightColor: Color.FromArgb(0x99, 0xF6, 0xB9, 0x4D),
                invisibleCharacterColor: Color.FromArgb(0xFF, 0xBB, 0xBB, 0xBB));
    }

    /// <summary>Cuts a recent-files/folders list down to the Options' recent list length.</summary>
    /// <param name="list">Either <see cref="_recentFiles"/> or <see cref="_recentFolders"/>.</param>
    private void TrimRecentList(List<string> list)
    {
        if (list.Count > _settings.RecentListLength)
        {
            list.RemoveRange(_settings.RecentListLength, list.Count - _settings.RecentListLength);
        }
    }

    /// <summary>Help → About: app name and version.</summary>
    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var dialog = new ContentDialog
        {
            Title = "About FabWinUI Designer",
            Content = $"FabWinUI Designer {version}\nA visual designer for WinUI 3 XAML pages.",
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync();

    /// <summary>Prompts for a folder via the folder picker and populates the file tree from it.</summary>
    private async Task OpenFolderAsync()
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            PopulateFileTree(folder.Path);
            AddRecentFolder(folder.Path);
        }
    }

    /// <summary>The folder the Explorer is showing, or null before one is opened - kept for Refresh and the search filter, which both rebuild the tree from it.</summary>
    private string? _explorerFolderPath;

    /// <summary>Shows <paramref name="folderPath"/> in the Explorer, with an empty search filter and the panel's buttons enabled.</summary>
    /// <param name="folderPath">Absolute path of the folder to list.</param>
    private void PopulateFileTree(string folderPath)
    {
        _explorerFolderPath = folderPath;
        ExplorerRefreshButton.IsEnabled = ExplorerCollapseAllButton.IsEnabled = ExplorerSearchBox.IsEnabled = true;

        // Clearing the box fires TextChanged, which rebuilds the tree. It doesn't fire if the
        // box was already empty, hence the explicit rebuild in that case.
        if (ExplorerSearchBox.Text.Length > 0)
        {
            ExplorerSearchBox.Text = string.Empty;
        }
        else
        {
            RebuildFileTree();
        }
    }

    /// <summary>
    /// Rebuilds the Explorer tree from <see cref="_explorerFolderPath"/>: one root node for the
    /// folder, then its .xaml files (non-recursive, name + extension only - the full path is
    /// shown for whichever item is selected instead). Each .xaml node gets its paired .xaml.cs
    /// nested under it if one exists on disk, like VS's Solution Explorer file nesting - there's
    /// no code editor yet, so selecting the .cs node just shows its path rather than opening it.
    /// A non-empty search box keeps only the .xaml files whose own name, or code-behind's name,
    /// contains the search text (case-insensitive).
    /// </summary>
    private void RebuildFileTree()
    {
        FileTreeView.RootNodes.Clear();
        if (_explorerFolderPath is null)
        {
            return;
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_explorerFolderPath));
        var root = new TreeViewNode
        {
            Content = new FileTreeNodeInfo(folderName.Length > 0 ? folderName : _explorerFolderPath, _explorerFolderPath, FileTreeNodeKind.Folder),
            IsExpanded = true,
        };

        var filter = ExplorerSearchBox.Text.Trim();
        bool Matches(string path) =>
            filter.Length == 0 || Path.GetFileName(path).Contains(filter, StringComparison.OrdinalIgnoreCase);

        try
        {
            foreach (var xamlPath in Directory.EnumerateFiles(_explorerFolderPath, "*.xaml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var codeBehindPath = xamlPath + ".cs";
                var hasCodeBehind = File.Exists(codeBehindPath);
                if (!Matches(xamlPath) && !(hasCodeBehind && Matches(codeBehindPath)))
                {
                    continue;
                }

                var node = new TreeViewNode { Content = new FileTreeNodeInfo(Path.GetFileName(xamlPath), xamlPath, FileTreeNodeKind.Xaml) };
                if (hasCodeBehind)
                {
                    node.Children.Add(new TreeViewNode { Content = new FileTreeNodeInfo(Path.GetFileName(codeBehindPath), codeBehindPath, FileTreeNodeKind.CodeBehind) });
                    node.IsExpanded = true;
                }

                root.Children.Add(node);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder was deleted/renamed or became unreadable since it was opened - show the
            // root alone rather than failing; Refresh tries again.
            WriteLog($"RebuildFileTree failed: {ex}");
        }

        FileTreeView.RootNodes.Add(root);
    }

    /// <summary>Explorer → Refresh: re-reads the folder, e.g. after files were added or removed outside the designer. Keeps the current search filter.</summary>
    private void ExplorerRefreshButton_Click(object sender, RoutedEventArgs e) => RebuildFileTree();

    /// <summary>Explorer → Collapse All: collapses every file node, leaving just the folder's file list - like VS, which keeps the top node open.</summary>
    private void ExplorerCollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in FileTreeView.RootNodes)
        {
            foreach (var child in root.Children)
            {
                child.IsExpanded = false;
            }
        }
    }

    /// <summary>Filters the Explorer as the user types.</summary>
    private void ExplorerSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildFileTree();

    /// <summary>Loads the double-tapped file tree node, if it's a `.xaml` node (double-tapping the folder or a nested `.xaml.cs` node does nothing - see <see cref="FileTreeNodeInfo"/>).</summary>
    /// <remarks>
    /// Uses SelectedNode.Content, not SelectedItem - SelectedItem only reflects the selection
    /// for an ItemsSource-bound TreeView; ours is populated manually via RootNodes/TreeViewNode,
    /// so SelectedNode is the API that actually tracks selection in that mode. (Bug found via
    /// real testing: double-click silently did nothing because of this.) Single click only
    /// selects; each item's full path is in its tooltip.
    /// </remarks>
    private void FileTreeView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileTreeView.SelectedNode?.Content is FileTreeNodeInfo { Kind: FileTreeNodeKind.Xaml } info)
        {
            OpenFileInTab(info.FullPath);
        }
    }

    /// <summary>What an Explorer node stands for - decides its icon and whether double-click opens it.</summary>
    private enum FileTreeNodeKind
    {
        /// <summary>The opened folder (the tree's root).</summary>
        Folder,

        /// <summary>A `.xaml` file - double-click loads it.</summary>
        Xaml,

        /// <summary>A `.xaml.cs` code-behind file, nested under its `.xaml`. Display-only: there's no code editor yet.</summary>
        CodeBehind,
    }

    /// <summary>Content of one Explorer node. Bound to by the tree's <c>ItemTemplate</c> (icon + name).</summary>
    /// <param name="DisplayName">Name shown in the tree (name + extension, no path).</param>
    /// <param name="FullPath">Absolute path used to load the file or show its path.</param>
    /// <param name="Kind">Folder, XAML file, or code-behind file.</param>
    private sealed record FileTreeNodeInfo(string DisplayName, string FullPath, FileTreeNodeKind Kind)
    {
        // Shared brushes, in VS Solution Explorer's colors: yellow folder, blue XAML, green C#.
        private static readonly SolidColorBrush FolderBrush = new(Color.FromArgb(0xFF, 0xDC, 0xB6, 0x7A));
        private static readonly SolidColorBrush XamlBrush = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        private static readonly SolidColorBrush CodeBrush = new(Color.FromArgb(0xFF, 0x38, 0x8A, 0x34));

        /// <summary>Segoe Fluent Icons glyph for this kind: folder, document, or code.</summary>
        public string Glyph => Kind switch
        {
            FileTreeNodeKind.Folder => "",
            FileTreeNodeKind.Xaml => "",
            _ => "",
        };

        /// <summary>Icon color for this kind.</summary>
        public SolidColorBrush IconBrush => Kind switch
        {
            FileTreeNodeKind.Folder => FolderBrush,
            FileTreeNodeKind.Xaml => XamlBrush,
            _ => CodeBrush,
        };

        public override string ToString() => DisplayName;
    }

    private readonly List<string> _recentFiles = new();
    private readonly List<string> _recentFolders = new();

    /// <summary>On-disk shape of <see cref="RecentConfigPath"/>.</summary>
    /// <param name="Files">Recently opened file paths, most-recent-first.</param>
    /// <param name="Folders">Recently opened folder paths, most-recent-first.</param>
    private sealed record RecentLists(List<string> Files, List<string> Folders);

    /// <summary>Loads <see cref="_recentFiles"/>/<see cref="_recentFolders"/> from <see cref="RecentConfigPath"/>. Leaves both empty if the file is missing or unreadable.</summary>
    private void LoadRecentLists()
    {
        try
        {
            if (!File.Exists(RecentConfigPath))
            {
                return;
            }

            var lists = JsonSerializer.Deserialize<RecentLists>(File.ReadAllText(RecentConfigPath));
            if (lists is null)
            {
                return;
            }

            _recentFiles.AddRange(lists.Files);
            _recentFolders.AddRange(lists.Folders);
        }
        catch (Exception)
        {
            // Corrupt/unreadable config - just start with empty recent lists.
        }
    }

    /// <summary>Persists <see cref="_recentFiles"/>/<see cref="_recentFolders"/> to <see cref="RecentConfigPath"/>. Best-effort - a write failure is swallowed.</summary>
    private void SaveRecentLists()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentConfigPath)!);
            File.WriteAllText(RecentConfigPath, JsonSerializer.Serialize(new RecentLists(_recentFiles, _recentFolders)));
        }
        catch (Exception)
        {
            // Best-effort - failing to persist recent lists isn't fatal.
        }
    }

    /// <summary>Adds a file to the front of the recent-files list.</summary>
    /// <param name="path">Absolute path of the file just opened or saved.</param>
    private void AddRecentFile(string path) => AddRecent(_recentFiles, path);

    /// <summary>Adds a folder to the front of the recent-folders list.</summary>
    /// <param name="path">Absolute path of the folder just opened.</param>
    private void AddRecentFolder(string path) => AddRecent(_recentFolders, path);

    /// <summary>Moves <paramref name="path"/> to the front of <paramref name="list"/> (de-duplicated, case-insensitively), trims it to the Options' recent list length, then persists and re-renders the Recent menu.</summary>
    /// <param name="list">Either <see cref="_recentFiles"/> or <see cref="_recentFolders"/>.</param>
    /// <param name="path">Absolute path to add.</param>
    private void AddRecent(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        TrimRecentList(list);
        SaveRecentLists();
        RefreshRecentMenu();
    }

    /// <summary>Rebuilds the File menu's Recent Files ▸ / Recent Folders ▸ submenus from <see cref="_recentFiles"/>/<see cref="_recentFolders"/>.</summary>
    private void RefreshRecentMenu()
    {
        FillRecentSubmenu(RecentFilesMenu, _recentFiles, RecentFileItem_Click);
        FillRecentSubmenu(RecentFoldersMenu, _recentFolders, RecentFolderItem_Click);
    }

    /// <summary>Replaces <paramref name="submenu"/>'s items with one entry per path (numbered like VS's recent lists), or a disabled "(none)" placeholder if <paramref name="paths"/> is empty.</summary>
    /// <param name="submenu">The File-menu submenu to fill.</param>
    /// <param name="paths">Paths to list, most-recent-first.</param>
    /// <param name="onClick">Click handler for each entry; it reads the path back from the item's <c>Tag</c>.</param>
    private static void FillRecentSubmenu(MenuFlyoutSubItem submenu, List<string> paths, RoutedEventHandler onClick)
    {
        submenu.Items.Clear();

        if (paths.Count == 0)
        {
            submenu.Items.Add(new MenuFlyoutItem { Text = "(none)", IsEnabled = false });
            return;
        }

        // Path is stashed in Tag so the click handler knows which entry was clicked without a
        // closure per item.
        for (var i = 0; i < paths.Count; i++)
        {
            var item = new MenuFlyoutItem { Text = $"{i + 1} {paths[i]}", Tag = paths[i] };
            item.Click += onClick;
            submenu.Items.Add(item);
        }
    }

    /// <summary>Loads the clicked recent file, or prunes it from the list if it no longer exists.</summary>
    private void RecentFileItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string path })
        {
            return;
        }

        if (File.Exists(path))
        {
            OpenFileInTab(path);
        }
        else
        {
            _recentFiles.Remove(path);
            SaveRecentLists();
            RefreshRecentMenu();
        }
    }

    /// <summary>Populates the file tree from the clicked recent folder, or prunes it from the list if it no longer exists.</summary>
    private void RecentFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string path })
        {
            return;
        }

        if (Directory.Exists(path))
        {
            PopulateFileTree(path);
            AddRecentFolder(path);
        }
        else
        {
            _recentFolders.Remove(path);
            SaveRecentLists();
            RefreshRecentMenu();
        }
    }

    private static readonly string OpenTabsConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabWinUIDesigner", "open-tabs.json");

    /// <summary>The open tabs, in tab-strip order. The tab strip itself is the only list of them, so reordering by drag needs no bookkeeping.</summary>
    private IEnumerable<DocumentTab> Tabs => DocumentTabView.TabItems.OfType<TabViewItem>().Select(item => (DocumentTab)item.Tag);

    /// <summary>
    /// Shows <paramref name="path"/> in a tab: switches to its tab if it's already open, otherwise
    /// loads it into a new one (with no undo history and nothing unsaved) and adds it to Recent
    /// Files. On failure, shows the error in the status bar instead of throwing.
    /// </summary>
    /// <param name="path">Absolute path of the `.xaml` file.</param>
    private void OpenFileInTab(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.Session.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        try
        {
            AddTab(new DocumentTab(DocumentSession.Open(fullPath), untitledName: string.Empty), activate: true);
            AddRecentFile(fullPath);
            SetStatus($"Opened {Path.GetFileName(fullPath)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open {Path.GetFileName(fullPath)}: {ex.Message}");
        }
    }

    /// <summary>Adds <paramref name="tab"/> at the end of the tab strip, and shows it if <paramref name="activate"/>.</summary>
    /// <param name="tab">The new tab.</param>
    /// <param name="activate">True to make it the active tab.</param>
    private void AddTab(DocumentTab tab, bool activate)
    {
        DocumentTabView.TabItems.Add(tab.Item);
        if (activate)
        {
            ActivateTab(tab);
        }
    }

    /// <summary>Makes <paramref name="tab"/> the selected tab and shows it (does nothing if it already is).</summary>
    /// <param name="tab">The tab to show.</param>
    private void ActivateTab(DocumentTab tab)
    {
        DocumentTabView.SelectedItem = tab.Item;

        // Selecting normally lands in DocumentTabView_SelectionChanged, which switches; this
        // covers the case where the event hasn't been raised (yet) by the time we get here.
        SwitchToTab(tab);
    }

    private void DocumentTabView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SwitchToTab((DocumentTabView.SelectedItem as TabViewItem)?.Tag as DocumentTab);

    /// <summary>
    /// Makes <paramref name="tab"/> the one shown on the design surface and in the XAML editor.
    /// XAML source typed in the tab being left but not applied yet is kept on that tab (see
    /// <see cref="DocumentTab.UnappliedSourceText"/>) rather than applied or thrown away.
    /// </summary>
    /// <param name="tab">The tab to show, or null for none (no tab left).</param>
    private void SwitchToTab(DocumentTab? tab)
    {
        if (ReferenceEquals(tab, _activeTab))
        {
            return;
        }

        if (_activeTab is not null)
        {
            _activeTab.ViewState = CaptureViewState();
            _activeTab.UnappliedSourceText = _sourceEditPending ? XamlSourceView.Text : null;
            _activeTab.UpdateHeader(_activeTab.IsDirtyWhileInactive);
        }

        _activeTab = tab;
        ShowActiveTab();
        SaveOpenTabs();
    }

    /// <summary>
    /// Renders the active tab's document on the design surface and in the XAML editor (or empties
    /// both when no tab is open). A tab shown before gets its caret, scroll positions and selection
    /// back; one shown for the first time gets its root element selected, like a freshly opened file.
    /// </summary>
    private void ShowActiveTab()
    {
        SetXamlSourceErrors([]);
        if (_activeTab is null)
        {
            SetXamlSourceText(string.Empty);
            DesignSurfaceHost.Child = null;
            _liveToDesign = new Dictionary<UIElement, DesignElement>();
            ClearSelection();
            UpdateCommandStates();
            return;
        }

        RefreshDesignSurfaceFromDocument();
        if (_activeTab.UnappliedSourceText is { } typedText)
        {
            // Back to where the user left this tab: their typed text in the editor, applied now if
            // it's valid, or with its errors showing again if it isn't.
            _activeTab.UnappliedSourceText = null;
            SetXamlSourceText(typedText);
            _sourceEditPending = true;
            TryApplyXamlSourceEdit();
        }

        if (_activeTab.ViewState is { } viewState)
        {
            RestoreViewStateAfterLayout(viewState);
        }
        else
        {
            SelectDocumentRootAfterLayout();
        }

        UpdateCommandStates();
    }

    // True when the XAML editor was the last thing to have keyboard focus, not counting the tab
    // strip - clicking a tab takes focus from the editor before the switch happens, so
    // _xamlSourceViewHasFocus is already false by then. Tells the tab whether to give the editor
    // focus back (and with it, a visible caret) when it's shown again.
    private bool _xamlSourceViewWasLastFocused;

    /// <summary>Keeps <see cref="_xamlSourceViewWasLastFocused"/> up to date as focus moves around the window.</summary>
    private void RootGrid_GotFocus(object sender, RoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, XamlSourceView))
            {
                _xamlSourceViewWasLastFocused = true;
                return;
            }

            if (ReferenceEquals(element, DocumentTabView))
            {
                return;
            }
        }

        _xamlSourceViewWasLastFocused = false;
    }

    /// <summary>Records the active tab's caret, scroll positions, selected element and whether the editor had focus, for <see cref="RestoreViewStateAfterLayout"/> when the tab is shown again.</summary>
    /// <returns>The current view position.</returns>
    private TabViewState CaptureViewState()
    {
        // The selected element is remembered by its position in document order rather than by
        // reference, so it still resolves if the document object was swapped in the meantime
        // (e.g. unapplied source text applied on return).
        int? selectedIndex = null;
        if (_selectedDesignElement is not null && CurrentDocument is not null)
        {
            var index = CurrentDocument.Root.Element.DescendantsAndSelf().ToList().IndexOf(_selectedDesignElement.Element);
            selectedIndex = index >= 0 ? index : null;
        }

        var caret = XamlSourceView.CursorPosition;
        return new TabViewState(
            caret.LineNumber,
            caret.CharacterPosition,
            XamlSourceView.VerticalScroll,
            XamlSourceView.HorizontalScroll,
            DesignSurfaceScrollViewer.HorizontalOffset,
            DesignSurfaceScrollViewer.VerticalOffset,
            selectedIndex,
            _xamlSourceViewWasLastFocused);
    }

    /// <summary>
    /// Puts back a tab's caret, scroll positions and selected element once the freshly rendered
    /// document has been laid out - deferred for the same reason as
    /// <see cref="SelectDocumentRootAfterLayout"/>: scroll extents and adorner bounds only exist
    /// after layout.
    /// </summary>
    /// <param name="state">What <see cref="CaptureViewState"/> recorded when the user left the tab.</param>
    private void RestoreViewStateAfterLayout(TabViewState state)
    {
        var tab = _activeTab;
        DispatcherQueue.TryEnqueue(() =>
        {
            // Switched to another tab again before this ran - that one restores its own state.
            if (!ReferenceEquals(tab, _activeTab))
            {
                return;
            }

            DesignSurfaceHost.UpdateLayout();

            // The caret is set first with scrollIntoView, so it's visible even if the editor
            // clamps the exact scroll position that follows. Suppressed so the caret move isn't
            // read as the user clicking into an element (see _suppressSourceSelectionSync) - the
            // selection is restored separately below, and may differ from the caret's element.
            _suppressSourceSelectionSync = true;
            try
            {
                XamlSourceView.SetCursorPosition(state.CaretLine, state.CaretCharacter, scrollIntoView: true, autoClamp: true);
                XamlSourceView.VerticalScroll = state.EditorVerticalScroll;
                XamlSourceView.HorizontalScroll = state.EditorHorizontalScroll;
                var restoredCaret = XamlSourceView.CursorPosition;
                _caretSetByCode = (restoredCaret.LineNumber, restoredCaret.CharacterPosition);
            }
            finally
            {
                _suppressSourceSelectionSync = false;
            }

            UpdateCaretStatus();
            DesignSurfaceScrollViewer.ChangeView(state.DesignHorizontalOffset, state.DesignVerticalOffset, null, disableAnimation: true);

            if (state.SelectedElementIndex is int index
                && CurrentDocument?.Root.Element.DescendantsAndSelf().ElementAtOrDefault(index) is { } element)
            {
                var entry = _liveToDesign.FirstOrDefault(kvp => ReferenceEquals(kvp.Value.Element, element));
                if (entry.Key is not null)
                {
                    Select(entry.Key, entry.Value);
                }
            }

            // Last, so nothing above takes focus away again. The editor only draws its caret
            // while it has focus.
            if (state.EditorHadFocus)
            {
                XamlSourceView.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>True if <paramref name="tab"/> has unsaved changes, including XAML source typed but not applied yet.</summary>
    /// <param name="tab">Any open tab.</param>
    private bool IsTabDirty(DocumentTab tab) =>
        ReferenceEquals(tab, _activeTab)
            ? _sourceEditPending || tab.Session.IsModified
            : tab.IsDirtyWhileInactive;

    private async void DocumentTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is DocumentTab tab)
        {
            await CloseTabAsync(tab);
        }
    }

    /// <summary>File → Close: closes the active tab.</summary>
    private async void CloseMenuItem_Click(object sender, RoutedEventArgs e) => await CloseActiveTabAsync();

    /// <summary>Closes the active tab, if any (File → Close, Ctrl+W).</summary>
    private async Task CloseActiveTabAsync()
    {
        if (_activeTab is not null)
        {
            await CloseTabAsync(_activeTab);
        }
    }

    /// <summary>Closes <paramref name="tab"/> after offering to save its unsaved changes. Closing the active tab shows its right-hand neighbour (or left-hand one, for the last tab).</summary>
    /// <param name="tab">The tab to close.</param>
    private async Task CloseTabAsync(DocumentTab tab)
    {
        if (!await ConfirmSaveTabsAsync([tab]))
        {
            return;
        }

        var items = DocumentTabView.TabItems;
        var index = items.IndexOf(tab.Item);
        if (index < 0)
        {
            // Already closed while the dialog was up.
            return;
        }

        // Pick the next tab before removing this one, rather than relying on whatever the
        // TabView selects by itself when its selected item goes away.
        if (ReferenceEquals(tab, _activeTab))
        {
            var neighbour = items.Count == 1 ? null : items[index + 1 < items.Count ? index + 1 : index - 1];
            if (neighbour is TabViewItem { Tag: DocumentTab next })
            {
                ActivateTab(next);
            }
            else
            {
                SwitchToTab(null);
            }
        }

        items.Remove(tab.Item);
        SetStatus($"Closed {tab.DisplayName}");
    }

    /// <summary>Tabs added, removed or reordered: remembers the new list for the next start.</summary>
    private void DocumentTabView_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args) => SaveOpenTabs();

    /// <summary>On-disk shape of <see cref="OpenTabsConfigPath"/>.</summary>
    /// <param name="Files">Paths of the open tabs' files, in tab order. Never-saved tabs aren't included.</param>
    /// <param name="ActiveIndex">Index in <paramref name="Files"/> of the active tab, or -1 if it isn't one of them.</param>
    private sealed record OpenTabList(List<string> Files, int ActiveIndex);

    // Set while RestoreOpenTabs adds tabs, so each addition doesn't rewrite the list it's reading.
    private bool _restoringTabs;

    /// <summary>Writes the open tabs' paths to <see cref="OpenTabsConfigPath"/>, for <see cref="RestoreOpenTabs"/> on the next start. Best-effort - a write failure is swallowed.</summary>
    private void SaveOpenTabs()
    {
        if (_restoringTabs)
        {
            return;
        }

        try
        {
            var saved = Tabs.Where(t => t.Session.FilePath is not null).ToList();
            var list = new OpenTabList(saved.Select(t => t.Session.FilePath!).ToList(), _activeTab is null ? -1 : saved.IndexOf(_activeTab));
            Directory.CreateDirectory(Path.GetDirectoryName(OpenTabsConfigPath)!);
            File.WriteAllText(OpenTabsConfigPath, JsonSerializer.Serialize(list));
        }
        catch (Exception)
        {
            // Best-effort - the tabs just won't be reopened next time.
        }
    }

    /// <summary>
    /// Reopens the tabs that were open when the app was last closed, and shows the one that was
    /// active. Files that no longer exist or fail to load are skipped silently. Only the active
    /// tab is rendered; the others are rendered when switched to.
    /// </summary>
    private void RestoreOpenTabs()
    {
        if (!_settings.ReopenTabsOnStart)
        {
            return;
        }

        OpenTabList? list;
        try
        {
            list = File.Exists(OpenTabsConfigPath)
                ? JsonSerializer.Deserialize<OpenTabList>(File.ReadAllText(OpenTabsConfigPath))
                : null;
        }
        catch (Exception)
        {
            // Corrupt/unreadable - start with no tabs.
            return;
        }

        if (list is null || list.Files.Count == 0)
        {
            return;
        }

        DocumentTab? toActivate = null;
        _restoringTabs = true;
        try
        {
            for (var i = 0; i < list.Files.Count; i++)
            {
                try
                {
                    var tab = new DocumentTab(DocumentSession.Open(list.Files[i]), untitledName: string.Empty);
                    AddTab(tab, activate: false);
                    if (i == list.ActiveIndex || toActivate is null)
                    {
                        toActivate = tab;
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"RestoreOpenTabs skipped {list.Files[i]}: {ex.Message}");
                }
            }
        }
        finally
        {
            _restoringTabs = false;
        }

        if (toActivate is not null)
        {
            ActivateTab(toActivate);
        }
    }

    // Set while CheckForExternalChangesAsync runs: its own dialog closing re-activates the window,
    // which must not start a second check.
    private bool _checkingExternalChanges;

    /// <summary>When the window gets focus back, checks whether any open file was changed outside the designer.</summary>
    private async void MainWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated || _checkingExternalChanges || _activeTab is null)
        {
            return;
        }

        _checkingExternalChanges = true;
        try
        {
            // A copy: tabs aren't opened or closed while a check's dialog is up, but the list is
            // enumerated across awaits.
            foreach (var tab in Tabs.ToList())
            {
                if (!await CheckForExternalChangesAsync(tab))
                {
                    break;
                }
            }
        }
        finally
        {
            _checkingExternalChanges = false;
        }
    }

    /// <summary>
    /// VS-style handling of a change made to an open file by another program, checked when the
    /// designer window is activated rather than with a file watcher - nothing pops up while you're
    /// working in the other program, and several saves there become one question here.
    /// Changed: ask Reload / Keep my version (always asked, even with no unsaved changes, so
    /// nothing changes under you unannounced). Deleted or renamed: say so and keep the document
    /// open. Keeping either way marks the document unsaved, so the next Save deliberately writes
    /// this version over the outside change.
    /// </summary>
    /// <param name="tab">The tab whose file to check.</param>
    /// <returns>False if the dialog couldn't be shown (another one is open), so the caller stops checking for now; true otherwise.</returns>
    private async Task<bool> CheckForExternalChangesAsync(DocumentTab tab)
    {
        var session = tab.Session;
        var path = session.FilePath;
        if (path is null)
        {
            return true;
        }

        var state = session.CheckDisk();
        if (state == DiskState.Unchanged)
        {
            return true;
        }

        var name = Path.GetFileName(path);
        var dialog = state == DiskState.Changed
            ? new ContentDialog
            {
                Title = "File changed outside the designer",
                Content = IsTabDirty(tab)
                    ? $"{name} was changed by another program.\n\nReload it? Your unsaved changes in the designer will be lost."
                    : $"{name} was changed by another program.\n\nReload it?",
                PrimaryButtonText = "Reload",
                CloseButtonText = "Keep my version",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            }
            : new ContentDialog
            {
                Title = "File deleted or renamed",
                Content = $"{name} no longer exists at:\n{path}\n\nIt stays open here, marked as unsaved. Save writes it back, or use Save As to put it somewhere else.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another ContentDialog is already open (only one can be at a time). The disk state
            // wasn't accepted, so the next activation asks again.
            return false;
        }

        if (state == DiskState.Changed && result == ContentDialogResult.Primary)
        {
            ReloadTab(tab);
            return true;
        }

        session.AcceptDiskState();
        session.MarkModified();
        RefreshTabDirtyState(tab);
        SetStatus(state == DiskState.Changed ? $"Kept the designer's version of {name}" : $"{name} was deleted or renamed");
        return true;
    }

    /// <summary>Replaces <paramref name="tab"/>'s document with its file's current content on disk (no undo history, nothing unsaved), re-rendering it if it's the active tab.</summary>
    /// <param name="tab">A tab whose file exists on disk.</param>
    private void ReloadTab(DocumentTab tab)
    {
        var name = tab.DisplayName;
        try
        {
            tab.Session = DocumentSession.Open(tab.Session.FilePath!);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to reload {name}: {ex.Message}");
            return;
        }

        // The content changed under the old caret/selection, so the tab starts fresh like a newly opened file.
        tab.UnappliedSourceText = null;
        tab.ViewState = null;
        if (ReferenceEquals(tab, _activeTab))
        {
            ShowActiveTab();
        }
        else
        {
            RefreshTabDirtyState(tab);
        }

        SetStatus($"Reloaded {name}");
    }

    /// <summary>Updates the unsaved marker for <paramref name="tab"/>: its header, plus the toolbar and window title if it's the active tab.</summary>
    /// <param name="tab">Any open tab.</param>
    private void RefreshTabDirtyState(DocumentTab tab)
    {
        if (ReferenceEquals(tab, _activeTab))
        {
            UpdateCommandStates();
        }
        else
        {
            tab.UpdateHeader(tab.IsDirtyWhileInactive);
        }
    }

    /// <summary>
    /// Re-renders the design surface, XAML source pane, and live/design element correlation
    /// from <see cref="CurrentDocument"/>'s current in-memory state - used both after opening
    /// a file and after any structural edit (e.g. adding a toolbox control), since those need
    /// a full XamlReader reload rather than an incremental live-object tweak.
    /// </summary>
    private void RefreshDesignSurfaceFromDocument()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        var text = CurrentDocument.ToXamlString();
        SetXamlSourceText(text);

        var (root, status) = RenderPreview(text);
        DesignSurfaceHost.Child = root ?? new TextBlock
        {
            Text = $"Preview failed ({status}). See {SpikeLogPath}",
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap,
        };

        _liveToDesign = root is not null
            ? LiveTreeCorrelator.Correlate(root, CurrentDocument.Root)
            : new Dictionary<UIElement, DesignElement>();
        ClearSelection();

        // Deferred to the dispatcher queue so it runs after this method returns and WinUI has
        // actually laid out the newly-set DesignSurfaceHost.Child - rendering it immediately
        // here would capture stale (often zero-sized) bounds.
        DispatcherQueue.TryEnqueue(async () =>
        {
            DesignSurfaceHost.UpdateLayout();
            await SaveSnapshotAsync(DesignSurfaceHost, SnapshotPath);
        });
    }

    /// <summary>
    /// Loads a design-time preview of the document. Testing confirmed loose XamlReader.Load
    /// deterministically throws on x:Class, so we skip straight to stripping it instead of
    /// trying the raw text first - trying it anyway would throw every single time (visible as
    /// a first-chance XamlParseException that trips debugger breakpoints on thrown exceptions,
    /// even though it's caught) for no benefit. The second, more aggressive fallback is kept
    /// defensively for control types/attributes we haven't fixture-tested.
    /// </summary>
    /// <param name="xamlText">The document's current XAML text.</param>
    /// <returns>The loaded root element and a label naming which sanitizing attempt worked, or a null root and a failure status if every attempt failed.</returns>
    private static (UIElement? root, string status) RenderPreview(string xamlText)
    {
        var attempts = new (string Label, string Xaml)[]
        {
            ("x:Class stripped", XamlPreviewSanitizer.StripClass(xamlText)),
            ("x:Class + events stripped", XamlPreviewSanitizer.StripClassAndEvents(xamlText)),
        };

        var log = new StringBuilder();
        log.AppendLine($"Spike run at {DateTimeOffset.Now:O}");

        // Kept from the last attempt so a total failure can report *why* (e.g. "The property
        // 'Texte' was not found in type 'TextBlock'") instead of a generic "all attempts
        // failed" - both the design-surface fallback text and TryApplyXamlSourceEdit's inline
        // error use this.
        string? lastErrorMessage = null;

        foreach (var (label, candidateXaml) in attempts)
        {
            var result = XamlPreviewLoader.TryLoad(candidateXaml);
            log.AppendLine(result.IsSuccess
                ? $"[{label}] OK"
                : $"[{label}] FAILED: {result.Error?.GetType().FullName}: {result.Error?.Message}");

            if (result.IsSuccess)
            {
                WriteLog(log.ToString());
                return (result.Root, label);
            }

            lastErrorMessage = result.Error?.Message;
        }

        WriteLog(log.ToString());
        return (null, lastErrorMessage ?? "all attempts failed");
    }

    /// <summary>Appends a block of text to <see cref="SpikeLogPath"/>, creating its directory if needed.</summary>
    /// <param name="contents">Text to append, followed by a newline.</param>
    private static void WriteLog(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SpikeLogPath)!);
        File.AppendAllText(SpikeLogPath, contents + Environment.NewLine);
    }

    /// <summary>
    /// Fills the Toolbox from the groups in the metadata JSON (<see cref="PropertyGridSchema.ToolboxGroups"/>)
    /// - the same file that drives the property grid, so adding a control type to the app means
    /// editing one file instead of also hand-adding a XAML `Button` here. Three layouts, VS-style:
    /// while the search box has text, a flat alphabetical list of the matching controls; otherwise
    /// either one collapsible section per group, or every control once in one alphabetical list.
    /// </summary>
    private void BuildToolbox()
    {
        ToolboxItemsPanel.Children.Clear();

        var filter = ToolboxSearchBox.Text.Trim();
        if (filter.Length > 0)
        {
            var matches = PropertyGridSchema.ToolboxControlTypes
                .Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                ToolboxItemsPanel.Children.Add(new TextBlock
                {
                    Text = "No matching controls",
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 12,
                    Margin = new Thickness(4, 6, 4, 6),
                });
            }

            foreach (var controlTypeName in matches)
            {
                ToolboxItemsPanel.Children.Add(CreateToolboxItem(controlTypeName, indent: 0));
            }

            return;
        }

        if (_alphabeticalToolboxView)
        {
            foreach (var controlTypeName in PropertyGridSchema.ToolboxControlTypes)
            {
                ToolboxItemsPanel.Children.Add(CreateToolboxItem(controlTypeName, indent: 0));
            }

            return;
        }

        foreach (var group in PropertyGridSchema.ToolboxGroups.Where(g => g.Controls.Length > 0))
        {
            var collapsed = !ExpandedToolboxGroups.Contains(group.Name);
            ToolboxItemsPanel.Children.Add(CreateToolboxGroupHeader(group.Name, collapsed));
            if (collapsed)
            {
                continue;
            }

            foreach (var controlTypeName in group.Controls)
            {
                ToolboxItemsPanel.Children.Add(CreateToolboxItem(controlTypeName, indent: 12));
            }
        }
    }

    /// <summary>One clickable Toolbox entry: a flat, full-width button that adds a control of that type.</summary>
    /// <param name="controlTypeName">The XAML type name, e.g. "Button" - shown as the label and passed to <see cref="AddControl"/>.</param>
    /// <param name="indent">Extra left padding, so items read as belonging to the group header above them.</param>
    private Button CreateToolboxItem(string controlTypeName, double indent)
    {
        var button = new Button
        {
            Content = controlTypeName,
            Tag = controlTypeName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4 + indent, 4, 4, 4),
        };
        button.Click += ToolboxItem_Click;
        return button;
    }

    /// <summary>
    /// A group header row: chevron plus bold group name, on the same light gray as the property
    /// grid's category headers. Clicking it collapses or expands the group. Hand-rolled rather
    /// than an <see cref="Expander"/>, whose default chrome is too tall for a dense Toolbox.
    /// </summary>
    /// <param name="groupName">The group's name, also the key in <see cref="ExpandedToolboxGroups"/>.</param>
    /// <param name="collapsed">Whether the group is currently collapsed (chevron pointing right).</param>
    private Button CreateToolboxGroupHeader(string groupName, bool collapsed)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = collapsed ? "\uE76C" : "\uE70D", FontSize = 10 });
        content.Children.Add(new TextBlock { Text = groupName, FontWeight = FontWeights.SemiBold, FontSize = 12 });

        var header = new Button
        {
            Content = content,
            Tag = groupName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF0, 0xF0, 0xF0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 4, 0, 2),
        };
        header.Click += ToolboxGroupHeader_Click;
        return header;
    }

    /// <summary>Collapses or expands the clicked Toolbox group, and remembers it for the next start.</summary>
    private void ToolboxGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupName })
        {
            return;
        }

        if (!ExpandedToolboxGroups.Remove(groupName))
        {
            ExpandedToolboxGroups.Add(groupName);
        }

        BuildToolbox();
        SavePanelLayout();
    }

    /// <summary>Filters the Toolbox as the user types.</summary>
    private void ToolboxSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildToolbox();

    /// <summary>Switches the Toolbox between grouped and alphabetical, keeps the View menu in sync, and persists the choice.</summary>
    private void AlphabeticalToolboxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _alphabeticalToolboxView = AlphabeticalToolboxToggle.IsChecked == true;
        AlphabeticalToolboxMenuItem.IsChecked = _alphabeticalToolboxView;
        BuildToolbox();
        SavePanelLayout();
    }

    /// <summary>View → Alphabetical Toolbox: drives the Toolbox's toggle, whose handler does the actual work and keeps both in sync.</summary>
    private void AlphabeticalToolboxMenuItem_Click(object sender, RoutedEventArgs e) =>
        AlphabeticalToolboxToggle.IsChecked = AlphabeticalToolboxMenuItem.IsChecked;

    /// <summary>Adds a control of the type named by the clicked toolbox item's <c>Tag</c>.</summary>
    private void ToolboxItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string localName })
        {
            AddControl(localName);
        }
    }

    /// <summary>
    /// Adds a new element of the given type (e.g. "Button") as a child of the root Canvas,
    /// with sensible defaults, then does a full reload and selects it so the user can
    /// immediately drag it into place. v1 only supports a Canvas root, so this doesn't attempt to target nested containers.
    /// </summary>
    /// <param name="localName">The XAML element name to add, e.g. "Button". Its starting attributes come from the metadata JSON (<see cref="PropertyGridSchema.GetDefaultAttributes"/>).</param>
    private void AddControl(string localName)
    {
        if (CurrentDocument is null)
        {
            return;
        }

        var canvasElement = CurrentDocument.Root.Children.FirstOrDefault();
        if (canvasElement is null || canvasElement.LocalName != "Canvas")
        {
            return;
        }

        BeginUndoableChange();

        var name = GenerateUniqueName(localName);
        var child = canvasElement.AddChild(localName);
        child.Name = name;

        // Cascade new controls slightly so repeated adds don't land exactly on top of each other.
        var siblingCount = canvasElement.Children.Count();
        var offset = FormatLength(20 + ((siblingCount - 1) % 8 * 24));
        child.SetAttribute("Canvas.Left", offset);
        child.SetAttribute("Canvas.Top", offset);

        foreach (var (attributeName, value) in PropertyGridSchema.GetDefaultAttributes(localName))
        {
            child.SetAttribute(attributeName, value);
        }

        CommitUndoableChange();
        RefreshDesignSurfaceFromDocument();
        SelectByName(name);
    }

    /// <summary>Finds the next unused "{localName}{N}" name by scanning the whole document, so it stays unique even across files that already name things that way.</summary>
    /// <param name="localName">The element's XAML type name, e.g. "Button" - used as the name prefix.</param>
    /// <returns>A name of the form "{localName}{N}" not already used anywhere in the current document.</returns>
    private string GenerateUniqueName(string localName)
    {
        var used = new HashSet<string>();
        CollectNames(CurrentDocument!.Root, used);

        var n = 1;
        while (used.Contains($"{localName}{n}"))
        {
            n++;
        }

        return $"{localName}{n}";
    }

    /// <summary>Recursively collects every non-empty <see cref="DesignElement.Name"/> in the subtree rooted at <paramref name="element"/>.</summary>
    /// <param name="element">Subtree root to scan.</param>
    /// <param name="names">Set to add found names into.</param>
    private static void CollectNames(DesignElement element, HashSet<string> names)
    {
        if (element.Name is { Length: > 0 } name)
        {
            names.Add(name);
        }

        foreach (var child in element.Children)
        {
            CollectNames(child, names);
        }
    }

    /// <summary>Selects the live/design element pair whose design name matches, if one is found in the current live-to-design correlation.</summary>
    /// <param name="name">The <see cref="DesignElement.Name"/> (x:Name) to select.</param>
    private void SelectByName(string name)
    {
        var entry = _liveToDesign.FirstOrDefault(kvp => kvp.Value.Name == name);
        if (entry.Key is not null)
        {
            Select(entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// <see cref="SelectDocumentRoot"/>, deferred to run after WinUI has actually laid out the
    /// freshly-loaded live tree. Calling it immediately (synchronously, right after
    /// <see cref="RefreshDesignSurfaceFromDocument"/> assigns <c>DesignSurfaceHost.Child</c>)
    /// selects the right element but with a zero-sized adorner rectangle - the selection label
    /// shows fine (it sizes to its own text, not to the selected element's bounds), but the
    /// dashed border doesn't, since <c>ActualWidth</c>/<c>ActualHeight</c> are still 0 at that
    /// point. Same fix already used for the
    /// debug preview snapshot in <see cref="RefreshDesignSurfaceFromDocument"/> - force a layout
    /// pass first via <c>UpdateLayout()</c>, deferred onto the dispatcher queue since layout
    /// itself only happens asynchronously, not synchronously when a Child is assigned.
    /// </summary>
    private void SelectDocumentRootAfterLayout()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DesignSurfaceHost.UpdateLayout();
            SelectDocumentRoot();
        });
    }

    /// <summary>
    /// Selects the document's root design element (its first child - typically the root Canvas,
    /// e.g. in SimplePage.xaml) right after loading/creating a document, instead of leaving
    /// nothing selected (or, before this was added, whatever the old
    /// caret-to-end-of-text side effect happened to land on). Called (deferred - see
    /// <see cref="SelectDocumentRootAfterLayout"/>) after
    /// <see cref="RefreshDesignSurfaceFromDocument"/>.
    /// </summary>
    private void SelectDocumentRoot()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        var rootDesignElement = CurrentDocument.Root.Children.FirstOrDefault();
        if (rootDesignElement is null)
        {
            return;
        }

        var entry = _liveToDesign.FirstOrDefault(kvp => ReferenceEquals(kvp.Value.Element, rootDesignElement.Element));
        if (entry.Key is not null)
        {
            Select(entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// Whether <paramref name="designElement"/> is the document's root design element (the first
    /// child of the XAML root, typically the root Canvas) - the same element
    /// <see cref="SelectDocumentRoot"/> selects.
    /// </summary>
    /// <param name="designElement">The element to test.</param>
    private bool IsDocumentRoot(DesignElement designElement) =>
        CurrentDocument?.Root.Children.FirstOrDefault() is { } root
        && ReferenceEquals(root.Element, designElement.Element);

    /// <summary>Hit-tests the press point; if it lands on a design element, selects it and begins a move drag (unless it's the document root, which can't be moved), otherwise clears the selection.</summary>
    private void DesignSurfaceHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // FindElementsInHostCoordinates wants the point in the window root's coordinate
        // space ("host" coordinates), not relative to the subtree element passed as its
        // second argument - passing DesignSurfaceHost-local coordinates here was the bug
        // that made clicks land far to the right (off by the Toolbox column's width) while
        // Y looked roughly right (off only by DesignSurfaceHost's own small top margin).
        var hostPoint = e.GetCurrentPoint(Content).Position;
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, DesignSurfaceHost);

        // Our own move-drag math needs DesignSurfaceHost-local coordinates instead, since
        // that's the space Canvas.Left/Top live in.
        var localPoint = e.GetCurrentPoint(DesignSurfaceHost).Position;

        // Hits come back topmost-first, so the loop picks the first one that's actually one of
        // our design elements (a hit can also be some internal visual-tree piece of a control
        // that isn't in _liveToDesign, e.g. a Button's inner border) and ignores the rest.
        foreach (var hit in hits)
        {
            if (hit is UIElement hitElement && _liveToDesign.TryGetValue(hitElement, out var designElement))
            {
                Select(hitElement, designElement);

                // The root has no parent Canvas to be positioned in - dragging it would only
                // write a meaningless Canvas.Left/Top onto it - so a click just selects it.
                if (IsDocumentRoot(designElement))
                {
                    e.Handled = true;
                    return;
                }

                BeginUndoableChange();
                _moveElement = hitElement;
                _moveDesignElement = designElement;
                _moveStartPointerPosition = localPoint;
                _moveStartLeft = GetCanvasLeft(hitElement);
                _moveStartTop = GetCanvasTop(hitElement);

                DesignSurfaceHost.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }

        ClearSelection();
    }

    /// <summary>While a move drag is in progress, repositions the dragged element and its adorner to follow the pointer.</summary>
    private void DesignSurfaceHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_moveElement is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(DesignSurfaceHost).Position;
        var newLeft = Snap(_moveStartLeft + (point.X - _moveStartPointerPosition.X));
        var newTop = Snap(_moveStartTop + (point.Y - _moveStartPointerPosition.Y));

        Canvas.SetLeft(_moveElement, newLeft);
        Canvas.SetTop(_moveElement, newTop);

        UpdateAdornerToMatch(_moveElement);
    }

    /// <summary>Ends a move drag: commits the element's final position to the document (undo entry + XAML refresh) and releases pointer capture. A no-op if nothing actually moved (a plain click without dragging).</summary>
    private void DesignSurfaceHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_moveElement is not null && _moveDesignElement is not null)
        {
            var newLeft = GetCanvasLeft(_moveElement);
            var newTop = GetCanvasTop(_moveElement);

            // A plain click-to-select (press+release with no drag in between) still sets up
            // _moveElement in PointerPressed, so without this check every single click would
            // count as a "move to the same position" - pushing a no-op undo entry and, worse,
            // refreshing the XAML source view, which resets its caret to the end of the text
            // (see SetXamlSourceText) and - via the source->design caret sync - re-selects
            // whatever element that happens to land on, undoing the click's own selection.
            if (newLeft != _moveStartLeft || newTop != _moveStartTop)
            {
                _moveDesignElement.SetAttribute("Canvas.Left", FormatLength(newLeft));
                _moveDesignElement.SetAttribute("Canvas.Top", FormatLength(newTop));
                CommitUndoableChange();
                RefreshXamlSourceView();
            }
        }

        _moveElement = null;
        _moveDesignElement = null;
        DesignSurfaceHost.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Begins a resize drag from the pressed handle's <c>Tag</c> direction (e.g. "SE") against the currently selected element.</summary>
    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedLiveElement is not FrameworkElement selected || _selectedDesignElement is null)
        {
            return;
        }

        BeginUndoableChange();

        var handle = (FrameworkElement)sender;
        _resizeDirection = (string)handle.Tag;
        _resizeElement = selected;
        _resizeDesignElement = _selectedDesignElement;
        _resizeStartPointerPosition = e.GetCurrentPoint(AdornerCanvas).Position;
        _resizeStartLeft = GetCanvasLeft(selected);
        _resizeStartTop = GetCanvasTop(selected);
        _resizeStartWidth = selected.ActualWidth;
        _resizeStartHeight = selected.ActualHeight;

        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>While a resize drag is in progress, applies the pointer delta (via <see cref="ApplyResize"/>) to the resizing element and its adorner.</summary>
    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeDirection is null || _resizeElement is not FrameworkElement resizing)
        {
            return;
        }

        var point = e.GetCurrentPoint(AdornerCanvas).Position;
        var deltaX = point.X - _resizeStartPointerPosition.X;
        var deltaY = point.Y - _resizeStartPointerPosition.Y;

        var (newLeft, newTop, newWidth, newHeight) = ApplyResize(
            _resizeDirection, _resizeStartLeft, _resizeStartTop, _resizeStartWidth, _resizeStartHeight, deltaX, deltaY);

        // Snapping wraps ApplyResize's result rather than its math, so that pure function stays
        // unaffected (and its own MSTest cases don't need to know about snapping at all).
        newLeft = Snap(newLeft);
        newTop = Snap(newTop);
        newWidth = Snap(newWidth);
        newHeight = Snap(newHeight);

        Canvas.SetLeft(resizing, newLeft);
        Canvas.SetTop(resizing, newTop);
        resizing.Width = newWidth;
        resizing.Height = newHeight;

        UpdateAdornerToMatch(resizing);
    }

    /// <summary>Rounds a coordinate/length to the nearest grid line when snap-to-grid is on; returns it unchanged otherwise.</summary>
    private double Snap(double value) => _snapToGridEnabled ? Math.Round(value / _settings.GridSize) * _settings.GridSize : value;

    /// <summary>Toggles snap-to-grid, redraws the dot overlay to match, and persists the new state.</summary>
    private void SnapToGridToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _snapToGridEnabled = SnapToGridToggle.IsChecked == true;
        SnapToGridMenuItem.IsChecked = _snapToGridEnabled;
        RenderGridOverlay();
        SavePanelLayout();
    }

    /// <summary>View → Snap to Grid: drives the design toolbar's toggle, whose handler does the actual work and keeps both in sync.</summary>
    private void SnapToGridMenuItem_Click(object sender, RoutedEventArgs e) =>
        SnapToGridToggle.IsChecked = SnapToGridMenuItem.IsChecked;

    /// <summary>Repopulates <see cref="GridOverlay"/> with a dot at every grid intersection across the current page size, or clears it when snap-to-grid is off.</summary>
    private void RenderGridOverlay()
    {
        GridOverlay.Children.Clear();
        if (!_snapToGridEnabled)
        {
            return;
        }

        for (var x = 0.0; x < GridOverlay.Width; x += _settings.GridSize)
        {
            for (var y = 0.0; y < GridOverlay.Height; y += _settings.GridSize)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 2,
                    Height = 2,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, y);
                GridOverlay.Children.Add(dot);
            }
        }
    }

    /// <summary>Ends a resize drag: commits the element's final position/size to the document (undo entry + XAML refresh) and releases pointer capture. A no-op if nothing actually changed (a press+release on a handle with no drag in between).</summary>
    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeElement is FrameworkElement resizing && _resizeDesignElement is not null
            && (GetCanvasLeft(resizing) != _resizeStartLeft || GetCanvasTop(resizing) != _resizeStartTop
                || resizing.Width != _resizeStartWidth || resizing.Height != _resizeStartHeight))
        {
            _resizeDesignElement.SetAttribute("Canvas.Left", FormatLength(GetCanvasLeft(resizing)));
            _resizeDesignElement.SetAttribute("Canvas.Top", FormatLength(GetCanvasTop(resizing)));
            _resizeDesignElement.SetAttribute("Width", FormatLength(resizing.Width));
            _resizeDesignElement.SetAttribute("Height", FormatLength(resizing.Height));
            CommitUndoableChange();
            RefreshXamlSourceView();
        }

        ((FrameworkElement)sender).ReleasePointerCapture(e.Pointer);
        _resizeDirection = null;
        _resizeElement = null;
        _resizeDesignElement = null;
    }

    /// <summary>
    /// Direction is one of the 8 compass points ("NW".."SE"): N/S adjust the top edge and
    /// height, W/E adjust the left edge and width - a handle can combine one of each (e.g. "SE").
    /// </summary>
    /// <param name="direction">Compass-point drag direction, e.g. "SE" or "N".</param>
    /// <param name="startLeft">Element's <c>Canvas.Left</c> when the drag began.</param>
    /// <param name="startTop">Element's <c>Canvas.Top</c> when the drag began.</param>
    /// <param name="startWidth">Element's width when the drag began.</param>
    /// <param name="startHeight">Element's height when the drag began.</param>
    /// <param name="deltaX">Horizontal pointer movement since the drag began.</param>
    /// <param name="deltaY">Vertical pointer movement since the drag began.</param>
    /// <returns>The new left/top/width/height, each clamped to at least <see cref="MinElementSize"/>.</returns>
    private static (double Left, double Top, double Width, double Height) ApplyResize(
        string direction, double startLeft, double startTop, double startWidth, double startHeight, double deltaX, double deltaY)
    {
        var left = startLeft;
        var top = startTop;
        var width = startWidth;
        var height = startHeight;

        // Dragging the W(est) edge/corner keeps the right edge fixed: width shrinks/grows by
        // -deltaX, and left has to move by whatever width didn't (so it "eats into" the element
        // instead of sliding the whole thing). Dragging E just grows width - left never moves.
        if (direction.Contains('W'))
        {
            width = Math.Max(MinElementSize, startWidth - deltaX);
            left = startLeft + (startWidth - width);
        }
        else if (direction.Contains('E'))
        {
            width = Math.Max(MinElementSize, startWidth + deltaX);
        }

        // Same idea vertically: N(orth) keeps the bottom edge fixed, S just grows height.
        if (direction.Contains('N'))
        {
            height = Math.Max(MinElementSize, startHeight - deltaY);
            top = startTop + (startHeight - height);
        }
        else if (direction.Contains('S'))
        {
            height = Math.Max(MinElementSize, startHeight + deltaY);
        }

        return (left, top, width, height);
    }

    /// <summary>Marks the given element as selected: shows the adorner/handles around it and builds its property grid. A non-<see cref="FrameworkElement"/> hit clears the selection instead, since bounds/property reflection both need one.</summary>
    /// <param name="liveElement">The live (rendered) visual-tree element that was hit.</param>
    /// <param name="designElement">The corresponding node in the document's element tree.</param>
    private void Select(UIElement liveElement, DesignElement designElement)
    {
        if (liveElement is not FrameworkElement)
        {
            ClearSelection();
            return;
        }

        _selectedLiveElement = liveElement;
        _selectedDesignElement = designElement;
        UpdateSelectionCommandStates();

        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionLabel.Visibility = Visibility.Visible;
        SetHandlesVisibility(Visibility.Visible);

        if (IsDocumentRoot(designElement))
        {
            // Only the handles that change size alone: the others also shift Canvas.Left/Top,
            // which would move the root (see DesignSurfaceHost_PointerPressed).
            HandleNW.Visibility = Visibility.Collapsed;
            HandleN.Visibility = Visibility.Collapsed;
            HandleNE.Visibility = Visibility.Collapsed;
            HandleW.Visibility = Visibility.Collapsed;
            HandleSW.Visibility = Visibility.Collapsed;
        }

        var label = designElement.Name is { Length: > 0 } name
            ? $"{designElement.LocalName} ({name})"
            : designElement.LocalName;
        SelectionLabelText.Text = label;
        SelectionSummaryText.Text = label;

        UpdateAdornerToMatch(liveElement);
        BuildPropertyGrid(designElement);
    }

    /// <summary>Hides the selection adorner/handles and clears the property grid.</summary>
    private void ClearSelection()
    {
        _selectedLiveElement = null;
        _selectedDesignElement = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionLabel.Visibility = Visibility.Collapsed;
        SetHandlesVisibility(Visibility.Collapsed);
        SelectionSummaryText.Text = "(no selection)";
        PropertyGridPanel.Children.Clear();
        EventGridPanel.Children.Clear();
        UpdateSelectionCommandStates();
    }

    /// <summary>
    /// Escape deselects ("cancel"); Delete removes the selected control; Ctrl+S saves; Ctrl+N /
    /// Ctrl+O / Ctrl+Shift+O / Ctrl+W are New File / Open File / Open Folder / Close. Escape/
    /// Delete/Ctrl+Z/Ctrl+Y are guarded against a TextBox having focus (e.g. editing a property
    /// value or the XAML source view) so those keys edit text as expected there instead of
    /// acting on the design surface. Ctrl+S is deliberately exempt from that guard - it commits
    /// any pending XAML source edit and saves regardless of which control has focus, same as
    /// Ctrl+S would in any real text editor.
    /// </summary>
    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.S && ctrlDown)
        {
            await SaveCurrentDocumentAsync();
            e.Handled = true;
            return;
        }

        // File-level shortcuts work regardless of focus, like Ctrl+S - neither a TextBox nor the
        // XAML editor uses them for anything of its own.
        if (ctrlDown && e.Key is VirtualKey.N or VirtualKey.O or VirtualKey.W)
        {
            var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            e.Handled = true;
            if (e.Key == VirtualKey.N)
            {
                NewFile();
            }
            else if (e.Key == VirtualKey.W)
            {
                await CloseActiveTabAsync();
            }
            else if (shiftDown)
            {
                await OpenFolderAsync();
            }
            else
            {
                await OpenFileAsync();
            }

            return;
        }

        // Also guards Ctrl+Z/Y here, not just Escape/Delete - a TextBox (property grid fields)
        // or the XamlSourceView TextControlBox both have their own built-in undo for text edits,
        // and intercepting Ctrl+Z at the window level while typing in either would fight with
        // that instead of undoing the field's own typing. TextControlBox doesn't surface as a
        // TextBox to FocusManager, hence the separate _xamlSourceViewHasFocus flag (tracked via
        // its own GotFocus/LostFocus in the constructor).
        if (_xamlSourceViewHasFocus || FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                ClearSelection();
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                DeleteSelectedControl();
                e.Handled = true;
                break;

            case VirtualKey.Z when ctrlDown:
                Undo();
                e.Handled = true;
                break;

            case VirtualKey.Y when ctrlDown:
                Redo();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Removes the currently selected element from the document (undo entry + full design-surface refresh). No-op if nothing is selected.</summary>
    private void DeleteSelectedControl()
    {
        if (_selectedDesignElement is null)
        {
            return;
        }

        BeginUndoableChange();
        _selectedDesignElement.Remove();
        CommitUndoableChange();
        RefreshDesignSurfaceFromDocument();
    }

    /// <summary>Display order for property-grid categories - anything from the JSON not listed here falls back to alphabetical, after these.</summary>
    private static readonly string[] PropertyCategoryOrder = ["Common Properties", "Text", "Layout", "Appearance"];

    /// <summary>Display order for the Events tab's categories.</summary>
    private static readonly string[] EventCategoryOrder = ["Action", "Text", "Selection"];

    /// <summary>Two-column name/value layout grouped by category, matching the WPF/WinForms Properties window.</summary>
    /// <param name="designElement">The selected element whose curated properties (per <see cref="PropertyGridSchema"/>) to show editors for.</param>
    private void BuildPropertyGrid(DesignElement designElement)
    {
        var descriptors = PropertyGridSchema.GetProperties(designElement.LocalName);
        var liveType = _selectedLiveElement?.GetType();

        BuildCategorizedGrid(
            PropertyGridPanel,
            descriptors,
            PropertyCategoryOrder,
            _alphabeticalPropertyView,
            getName: d => d.Name,
            getCategory: d => d.Category,
            createEditor: d => CreatePropertyEditor(designElement, d, liveType));

        BuildEventsGrid(designElement);
    }

    /// <summary>
    /// Rebuilds the Events tab's own panel for whichever common events
    /// this element's type has (<see cref="EventGridSchema"/>) - a small "(no events)" note for
    /// a type with none, e.g. <c>TextBlock</c>, rather than leaving the previous selection's
    /// rows stale on screen. A separate panel/tab from Properties, not a sub-section of it -
    /// scrolling to find Events under a long property list was inconvenient, so they're now switchable via <see cref="PropertiesTabButton"/>/
    /// <see cref="EventsTabButton"/> instead of stacked in one scrolling list.
    /// </summary>
    /// <param name="designElement">The selected element to show events for.</param>
    private void BuildEventsGrid(DesignElement designElement)
    {
        var descriptors = EventGridSchema.GetEvents(designElement.LocalName);
        if (descriptors.Count == 0)
        {
            EventGridPanel.Children.Clear();
            EventGridPanel.Children.Add(new TextBlock { Text = "(no events)", Foreground = new SolidColorBrush(Colors.Gray) });
            return;
        }

        BuildCategorizedGrid(
            EventGridPanel,
            descriptors,
            EventCategoryOrder,
            _alphabeticalPropertyView,
            getName: d => d.Name,
            getCategory: d => d.Category,
            createEditor: d => CreateEventEditor(designElement, d));
    }

    /// <summary>
    /// Shared Properties/Events grid builder: groups <paramref name="descriptors"/>
    /// under a bold category header row per distinct <c>Category</c> - the WPF-style "Arrange by
    /// Category" view of WPF's Properties window - or, when <paramref name="alphabetical"/> is
    /// true, one flat list sorted by name with no category headers at all.
    /// Categories in
    /// <paramref name="categoryOrder"/> are shown first in that order; any other category found in
    /// the data (e.g. a typo'd JSON value) still shows, alphabetically, after - so a bad category
    /// name is visible instead of silently dropping properties.
    /// </summary>
    /// <typeparam name="TDescriptor"><see cref="PropertyDescriptor"/> or <see cref="EventDescriptor"/>.</typeparam>
    private static void BuildCategorizedGrid<TDescriptor>(
        Panel targetPanel,
        IReadOnlyList<TDescriptor> descriptors,
        string[] categoryOrder,
        bool alphabetical,
        Func<TDescriptor, string> getName,
        Func<TDescriptor, string> getCategory,
        Func<TDescriptor, FrameworkElement> createEditor)
    {
        targetPanel.Children.Clear();
        if (descriptors.Count == 0)
        {
            return;
        }

        var orderedCategories = alphabetical
            ? []
            : categoryOrder
                .Where(category => descriptors.Any(d => getCategory(d) == category))
                .Concat(descriptors.Select(getCategory).Distinct().Except(categoryOrder).OrderBy(c => c, StringComparer.Ordinal))
                .ToList();

        var rowCount = orderedCategories.Count + descriptors.Count; // one extra header row per category (0 when alphabetical)
        var grid = BuildGridLinesGrid(rowCount, out var gridLineBrush);

        void AddDescriptorRow(TDescriptor descriptor, int row, bool isLastRow)
        {
            var label = new TextBlock { Text = getName(descriptor), VerticalAlignment = VerticalAlignment.Center };
            AddGridCell(grid, label, row, column: 0, gridLineBrush, isLastRow);

            var editor = createEditor(descriptor);
            AddGridCell(grid, editor, row, column: 1, gridLineBrush, isLastRow);
        }

        var currentRow = 0;
        if (alphabetical)
        {
            foreach (var descriptor in descriptors.OrderBy(getName, StringComparer.Ordinal))
            {
                AddDescriptorRow(descriptor, currentRow, isLastRow: currentRow == rowCount - 1);
                currentRow++;
            }
        }
        else
        {
            foreach (var category in orderedCategories)
            {
                AddCategoryHeaderCell(grid, category, currentRow, gridLineBrush);
                currentRow++;

                foreach (var descriptor in descriptors.Where(d => getCategory(d) == category))
                {
                    AddDescriptorRow(descriptor, currentRow, isLastRow: currentRow == rowCount - 1);
                    currentRow++;
                }
            }
        }

        targetPanel.Children.Add(grid);
    }

    /// <summary>
    /// Creates the shared two-column grid shell (label column fixed at 90px, editor column
    /// stretches) used by both the Properties and Events tabs, with a light gray outer border -
    /// row/column divider lines are added per-cell by <see cref="AddGridCell"/>, matching the
    /// visible gridlines of a WinForms <c>PropertyGrid</c> (a plain
    /// spacing-only layout has no visible row/column separators).
    /// </summary>
    /// <param name="rowCount">Total rows the grid will hold, including category header rows.</param>
    /// <param name="gridLineBrush">Outputs the brush used for the divider lines, so callers add cells with a matching color.</param>
    /// <returns>An empty <see cref="Grid"/> with its outer border, rows, and columns already set up - cells are added by <see cref="AddGridCell"/>/<see cref="AddCategoryHeaderCell"/>.</returns>
    private static Grid BuildGridLinesGrid(int rowCount, out SolidColorBrush gridLineBrush)
    {
        gridLineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD5, 0xD5, 0xD5));

        var grid = new Grid
        {
            BorderBrush = gridLineBrush,
            BorderThickness = new Thickness(1, 1, 1, rowCount > 0 ? 0 : 1),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var row = 0; row < rowCount; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        return grid;
    }

    /// <summary>Font size used throughout the Properties/Events grids - smaller than controls' 14px default, matching the compact look of a WinForms <c>PropertyGrid</c>.</summary>
    private const double GridCellFontSize = 11;

    /// <summary>Adds one category header row (bold label, light gray background, spans both columns) - the group separators of WPF's Properties window.</summary>
    private static void AddCategoryHeaderCell(Grid grid, string category, int row, SolidColorBrush gridLineBrush)
    {
        var header = new TextBlock
        {
            Text = category,
            FontSize = GridCellFontSize,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 2, 6, 2),
        };

        var cell = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF0, 0xF0, 0xF0)),
            BorderBrush = gridLineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumnSpan(cell, 2);
        grid.Children.Add(cell);
    }

    /// <summary>
    /// Wraps one property/event grid cell's content in a bordered <see cref="Border"/> so every
    /// row gets a bottom divider line and the label column gets a right divider line (the last
    /// row skips its bottom line since the grid's own outer border already supplies it). Also
    /// shrinks whichever control was passed in to the compact row height of a VS Properties
    /// window - editors (<see cref="TextBox"/>/<see cref="ComboBox"/>/
    /// <see cref="CheckBox"/>) and the row labels (<see cref="TextBlock"/>) don't share a common
    /// "has FontSize/Padding" base, so this is a switch rather than one assignment.
    /// </summary>
    private static void AddGridCell(Grid grid, FrameworkElement content, int row, int column, SolidColorBrush gridLineBrush, bool isLastRow)
    {
        var rightLine = column == 0 ? 1 : 0;
        var bottomLine = isLastRow ? 0 : 1;

        content.VerticalAlignment = VerticalAlignment.Center;
        content.Margin = new Thickness(6, 1, 6, 1);

        switch (content)
        {
            // WinUI's default Control padding/MinHeight (TextBox/ComboBox: ~32px tall) is what
            // was making rows so much taller than VS's Properties window - trimming both here is
            // what actually shrinks the row, FontSize alone barely changes it.
            case Control control:
                control.FontSize = GridCellFontSize;
                control.Padding = new Thickness(6, 2, 6, 2);
                control.MinHeight = 24;
                break;
            case TextBlock textBlock:
                textBlock.FontSize = GridCellFontSize;
                break;
        }

        var cell = new Border
        {
            BorderBrush = gridLineBrush,
            BorderThickness = new Thickness(0, 0, rightLine, bottomLine),
            Child = content,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>Creates the property grid's editor control for one property - a <see cref="CheckBox"/>, <see cref="ComboBox"/>, or plain <see cref="TextBox"/> depending on the property's live CLR type (<see cref="PropertyKindResolver"/>) - wired to call <see cref="ApplyPropertyEdit"/> when its value changes.</summary>
    /// <param name="designElement">The selected element the property belongs to.</param>
    /// <param name="descriptor">Describes the property's name and category.</param>
    /// <param name="liveType">The selected live element's CLR type, used to reflect the actual property and so its editor kind - <c>null</c> falls back to a plain text editor (nothing selected).</param>
    /// <returns>The editor control, ready to place in the property grid.</returns>
    private FrameworkElement CreatePropertyEditor(DesignElement designElement, PropertyDescriptor descriptor, Type? liveType)
    {
        // "Name" is x:Name (a namespaced attribute, via DesignElement.Name), not a plain
        // unprefixed "Name" attribute - same special case ApplyPropertyEdit already applies on
        // the write side (see its own comment); this was missing here on the read side, so the
        // field always displayed blank regardless of what was actually set.
        var currentText = descriptor.Name == "Name"
            ? designElement.Name ?? string.Empty
            : designElement.GetAttribute(descriptor.Name) ?? string.Empty;
        var (kind, enumValues) = PropertyKindResolver.Resolve(liveType?.GetProperty(descriptor.Name));

        // Bool -> CheckBox, Enum -> ComboBox of its named values, everything else -> a plain
        // TextBox (numbers, colors, thicknesses, ... are all typed as free text and parsed in
        // ApplyPropertyEdit, same as they'd appear in real XAML).
        switch (kind)
        {
            case PropertyEditorKind.Bool:
            {
                var checkBox = new CheckBox { IsChecked = string.Equals(currentText, "True", StringComparison.OrdinalIgnoreCase) };
                checkBox.Checked += (_, _) => ApplyPropertyEdit(designElement, descriptor, "True");
                checkBox.Unchecked += (_, _) => ApplyPropertyEdit(designElement, descriptor, "False");
                return checkBox;
            }

            case PropertyEditorKind.Enum:
            {
                var values = enumValues ?? [];
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = values };
                combo.SelectedItem = values.FirstOrDefault(v => string.Equals(v, currentText, StringComparison.OrdinalIgnoreCase)) ?? values.FirstOrDefault();
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string selected)
                    {
                        ApplyPropertyEdit(designElement, descriptor, selected);
                    }
                };
                return combo;
            }

            default:
            {
                // IsSpellCheckEnabled=false: property values (colors, numbers, short content
                // strings) aren't prose, so the spell-checker's red squiggles are just noise here.
                var textBox = new TextBox { Text = currentText, IsSpellCheckEnabled = false };
                textBox.LostFocus += (_, _) => ApplyPropertyEdit(designElement, descriptor, textBox.Text);
                return textBox;
            }
        }
    }

    /// <summary>
    /// Applies one property-grid edit to both the live element (via reflection, so the design
    /// surface updates immediately) and the document (as an undo-tracked XAML attribute). Invalid
    /// input (unparsable number/color/thickness) is a silent no-op, leaving the live value and
    /// the field's own text as typed rather than reverting or throwing.
    /// </summary>
    /// <param name="designElement">The selected element whose attribute to update.</param>
    /// <param name="descriptor">Describes the property's name and category.</param>
    /// <param name="rawText">The editor's current text/value, as typed or selected.</param>
    private void ApplyPropertyEdit(DesignElement designElement, PropertyDescriptor descriptor, string rawText)
    {
        if (_selectedLiveElement is null)
        {
            return;
        }

        var propertyInfo = _selectedLiveElement.GetType().GetProperty(descriptor.Name);
        if (propertyInfo is null || !propertyInfo.CanWrite)
        {
            return;
        }

        var (kind, _) = PropertyKindResolver.Resolve(propertyInfo);

        object? liveValue;
        string attributeText;

        switch (kind)
        {
            case PropertyEditorKind.Number:
                if (rawText.Trim().Length == 0)
                {
                    liveValue = double.NaN;
                    attributeText = string.Empty;
                }
                else if (double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    liveValue = number;
                    attributeText = FormatLength(number);
                }
                else
                {
                    return; // invalid input - leave the field as typed, don't apply
                }

                break;

            case PropertyEditorKind.Bool:
                liveValue = string.Equals(rawText, "True", StringComparison.OrdinalIgnoreCase);
                attributeText = rawText;
                break;

            case PropertyEditorKind.Enum:
                liveValue = Enum.Parse(propertyInfo.PropertyType, rawText);
                attributeText = rawText;
                break;

            case PropertyEditorKind.Brush:
                if (!PropertyValueConverter.TryParseColor(rawText, out var color))
                {
                    return;
                }

                liveValue = new SolidColorBrush(color);
                attributeText = rawText;
                break;

            case PropertyEditorKind.Thickness:
                if (!PropertyValueConverter.TryParseThickness(rawText, out var thickness))
                {
                    return;
                }

                liveValue = thickness;
                attributeText = rawText.Trim().Length == 0 ? string.Empty : PropertyValueConverter.FormatThickness(thickness);
                break;

            default: // Text
                liveValue = rawText;
                attributeText = rawText;
                break;
        }

        BeginUndoableChange();
        propertyInfo.SetValue(_selectedLiveElement, liveValue);

        // The identity property is x:Name, not a plain "Name" attribute - DesignElement.Name
        // is what every other part of the tool (toolbox naming, selection lookup) reads and
        // writes, so routing it through the generic SetAttribute(string) path here would create
        // a stray plain Name="..." attribute alongside the untouched x:Name instead of renaming it.
        if (descriptor.Name == "Name")
        {
            designElement.Name = attributeText.Length == 0 ? null : attributeText;

            var label = designElement.Name is { Length: > 0 } name ? $"{designElement.LocalName} ({name})" : designElement.LocalName;
            SelectionLabelText.Text = label;
            SelectionSummaryText.Text = label;
        }
        else
        {
            designElement.SetAttribute(descriptor.Name, attributeText.Length == 0 ? null : attributeText);
        }

        CommitUndoableChange();
        RefreshXamlSourceView();
        UpdateAdornerToMatch(_selectedLiveElement);
    }

    /// <summary>Creates the Events section's editor for one event - always a plain <see cref="TextBox"/> for the handler method name, wired to call <see cref="ApplyEventEdit"/> when it loses focus.</summary>
    /// <param name="designElement">The selected element the event belongs to.</param>
    /// <param name="descriptor">Describes the event's name.</param>
    /// <returns>The editor control, ready to place in the Events section.</returns>
    private FrameworkElement CreateEventEditor(DesignElement designElement, EventDescriptor descriptor)
    {
        var currentText = designElement.GetAttribute(descriptor.Name) ?? string.Empty;
        var textBox = new TextBox { Text = currentText, IsSpellCheckEnabled = false };
        textBox.LostFocus += (_, _) => ApplyEventEdit(designElement, descriptor, textBox.Text);
        return textBox;
    }

    /// <summary>
    /// Applies one event-grid edit: sets (or, for empty text, removes) the XAML event attribute
    /// through the same undo-tracked pipeline every other edit goes through, then - unlike a
    /// property edit - also makes sure a matching stub method exists in the document's paired
    /// `.xaml.cs` file (<see cref="EnsureEventHandlerStub"/>). Deliberately does not set anything
    /// on the live design-surface element: wiring a real delegate there would make the control
    /// actually fire application logic while you're just designing it, not previewing it.
    /// </summary>
    /// <param name="designElement">The selected element whose event attribute to update.</param>
    /// <param name="descriptor">Describes the event's name.</param>
    /// <param name="rawText">The editor's current text - a handler method name, or empty to unwire.</param>
    private void ApplyEventEdit(DesignElement designElement, EventDescriptor descriptor, string rawText)
    {
        var handlerName = rawText.Trim();
        if (handlerName.Length > 0 && !IsValidIdentifier(handlerName))
        {
            return; // invalid input - leave the field as typed, don't apply (same pattern as ApplyPropertyEdit)
        }

        BeginUndoableChange();
        designElement.SetAttribute(descriptor.Name, handlerName.Length == 0 ? null : handlerName);
        CommitUndoableChange();
        RefreshXamlSourceView();

        // No per-edit codegen here - stubs are generated for the whole document at once, on
        // Save (SyncEventHandlerStubs) - code files should change at save time, not on every
        // field blur.
    }

    /// <summary>True for a string that's a valid, unqualified C# identifier - good enough for a generated method name without pulling in a full C# lexer for it.</summary>
    /// <param name="text">Candidate handler method name.</param>
    private static bool IsValidIdentifier(string text) => Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>
    /// Scans the whole document for every event attribute currently set on any element (per
    /// <see cref="EventGridSchema"/>'s curated per-type list) and makes sure a matching stub
    /// method exists for each in the document's paired `.xaml.cs` file
    /// (<see cref="EventHandlerCodeGen"/>). Called after a successful save,
    /// not on every event-field edit - generating (and writing to disk) on every field blur, before
    /// the document itself is even saved, would be surprising.
    /// Quietly does nothing if the document has no `x:Class` (hand-authored XAML with no
    /// code-behind class to generate into) or no event attributes are actually set anywhere -
    /// both are legitimate states, not errors.
    /// </summary>
    private void SyncEventHandlerStubs()
    {
        if (CurrentFilePath is null || CurrentDocument is null)
        {
            return;
        }

        var className = CurrentDocument.Root.GetAttribute(FabWinUIDesigner.Document.XamlNamespaces.X + "Class");
        if (className is null)
        {
            return;
        }

        var stubs = new List<EventHandlerStub>();
        foreach (var (liveElement, designElement) in _liveToDesign)
        {
            foreach (var descriptor in EventGridSchema.GetEvents(designElement.LocalName))
            {
                var methodName = designElement.GetAttribute(descriptor.Name);
                if (methodName is not { Length: > 0 } || !IsValidIdentifier(methodName))
                {
                    continue;
                }

                // Reflects the same way FindUnknownPropertyErrors does - the real delegate's
                // parameter types are the source of truth for the stub's signature, not a
                // hardcoded per-event table (EventHandlerCodeGen itself stays WinUI-agnostic -
                // see its own doc comment).
                var eventInfo = liveElement.GetType().GetEvent(descriptor.Name);
                var invokeParameters = eventInfo?.EventHandlerType?.GetMethod("Invoke")?.GetParameters();
                if (invokeParameters is not { Length: 2 })
                {
                    continue; // not a (sender, args)-shaped event - outside this MVP's stub generator
                }

                stubs.Add(new EventHandlerStub(descriptor.Name, methodName, invokeParameters[0].ParameterType.FullName!, invokeParameters[1].ParameterType.FullName!));
            }
        }

        if (stubs.Count == 0)
        {
            return;
        }

        var codeBehindPath = CurrentFilePath + ".cs";

        try
        {
            var existingSource = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : null;
            var updated = EventHandlerCodeGen.EnsureEventHandlers(existingSource, className, stubs);
            File.WriteAllText(codeBehindPath, updated);
        }
        catch (Exception ex)
        {
            // Best-effort - a codegen hiccup must never crash the designer or interrupt Save,
            // which has already succeeded by the time this runs.
            WriteLog($"SyncEventHandlerStubs failed: {ex}");
        }
    }

    /// <summary>Shows or hides all 8 resize handles together.</summary>
    /// <param name="visibility">Visibility to apply to every handle.</param>
    private void SetHandlesVisibility(Visibility visibility)
    {
        HandleNW.Visibility = visibility;
        HandleN.Visibility = visibility;
        HandleNE.Visibility = visibility;
        HandleW.Visibility = visibility;
        HandleE.Visibility = visibility;
        HandleSW.Visibility = visibility;
        HandleS.Visibility = visibility;
        HandleSE.Visibility = visibility;
    }

    /// <summary>Repositions the selection rectangle, label, and resize handles to match the given live element's current bounds.</summary>
    /// <param name="liveElement">The selected live element to measure and follow.</param>
    private void UpdateAdornerToMatch(UIElement liveElement)
    {
        if (liveElement is not FrameworkElement frameworkElement)
        {
            return;
        }

        // TransformToVisual/TransformBounds converts the element's own (0,0,width,height) rect
        // into DesignSurfaceHost-relative coordinates - needed because a selected element can be
        // nested inside another container, not just placed directly on the root Canvas.
        var bounds = liveElement.TransformToVisual(DesignSurfaceHost)
            .TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));

        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;

        Canvas.SetLeft(SelectionLabel, bounds.X);
        Canvas.SetTop(SelectionLabel, Math.Max(0, bounds.Y - 20));

        // Handles are centered ON the edge/corner, not inside or outside it, hence the half-size offset.
        const double half = HandleSize / 2.0;
        PositionHandle(HandleNW, bounds.Left - half, bounds.Top - half);
        PositionHandle(HandleN, bounds.Left + (bounds.Width / 2) - half, bounds.Top - half);
        PositionHandle(HandleNE, bounds.Right - half, bounds.Top - half);
        PositionHandle(HandleW, bounds.Left - half, bounds.Top + (bounds.Height / 2) - half);
        PositionHandle(HandleE, bounds.Right - half, bounds.Top + (bounds.Height / 2) - half);
        PositionHandle(HandleSW, bounds.Left - half, bounds.Bottom - half);
        PositionHandle(HandleS, bounds.Left + (bounds.Width / 2) - half, bounds.Bottom - half);
        PositionHandle(HandleSE, bounds.Right - half, bounds.Bottom - half);
    }

    /// <summary>Positions one resize handle on the adorner canvas.</summary>
    /// <param name="handle">The handle to position.</param>
    /// <param name="x">Target <c>Canvas.Left</c>.</param>
    /// <param name="y">Target <c>Canvas.Top</c>.</param>
    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    /// <summary>Reads <c>Canvas.Left</c>, treating the WinUI default of <see cref="double.NaN"/> (attached property never set) as 0.</summary>
    /// <param name="element">Element to read the attached property from.</param>
    /// <returns>The element's <c>Canvas.Left</c>, or 0 if unset.</returns>
    private static double GetCanvasLeft(UIElement element)
    {
        var value = Canvas.GetLeft(element);
        return double.IsNaN(value) ? 0 : value;
    }

    /// <summary>Reads <c>Canvas.Top</c>, treating the WinUI default of <see cref="double.NaN"/> (attached property never set) as 0.</summary>
    /// <param name="element">Element to read the attached property from.</param>
    /// <returns>The element's <c>Canvas.Top</c>, or 0 if unset.</returns>
    private static double GetCanvasTop(UIElement element)
    {
        var value = Canvas.GetTop(element);
        return double.IsNaN(value) ? 0 : value;
    }

    /// <summary>Formats a length value for a XAML attribute: rounded to 2 decimal places, culture-invariant.</summary>
    /// <param name="value">The length to format.</param>
    /// <returns>The formatted string, e.g. "123.46".</returns>
    private static string FormatLength(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Selects whichever design element the source view's caret currently sits on/in, so
    /// navigating XAML text also drives the design-surface selection - one-directional by
    /// design: an earlier version also moved the source
    /// caret to match whenever a control was selected *on the design surface*, but that made it
    /// impossible to click in the whitespace gap between two elements without the caret
    /// immediately snapping back to the preceding tag, which proved more disruptive than
    /// useful in practice - selecting on the design surface no longer touches the source pane's
    /// caret at all. Only acts while the source view's text matches the current document exactly
    /// (normalizing line endings, same as <see cref="TryApplyXamlSourceEdit"/>)
    /// - <c>TextControlBox.SelectionChanged</c> fires on every caret move, including ones caused
    /// by typing, so this deliberately does nothing while there's an uncommitted edit in progress
    /// rather than fighting the user's typing with a selection rebuild on every keystroke.
    /// </summary>
    private void XamlSourceView_SelectionChanged()
    {
        if (_suppressSourceSelectionSync || CurrentDocument is null || _xamlPaneTab != XamlPaneTab.Source)
        {
            return;
        }

        var caretNow = XamlSourceView.CursorPosition;
        if (_caretSetByCode is { } setByCode)
        {
            if (setByCode == (caretNow.LineNumber, caretNow.CharacterPosition))
            {
                return;
            }

            _caretSetByCode = null;
        }

        try
        {
            // Positions come from the editor's own text. It's either the document's text, or an
            // applied edit not normalized yet (see ApplySourceEditAfterPause) - same elements in
            // the same order either way. Typed text that isn't applied yet may not be, so skip.
            var text = NormalizeLineEndings(XamlSourceView.Text);
            if (_sourceEditPending && text != NormalizeLineEndings(CurrentDocument.ToXamlString()))
            {
                return;
            }

            // TextControlBox has no flat character-offset caret property (unlike TextBox's
            // SelectionStart) - CursorPosition gives a zero-based (line, character) pair instead,
            // converted to a flat offset via the same OffsetOf helper the Errors grid's
            // line/column positions already use (it takes 1-based line/column, hence the +1s).
            var caret = XamlSourceView.CursorPosition;
            var caretOffset = XamlSourcePositions.OffsetOf(text, caret.LineNumber + 1, caret.CharacterPosition + 1);

            var index = XamlSourcePositions.EnclosingElementIndex(text, caretOffset);
            if (index < 0)
            {
                return;
            }

            var actualElement = CurrentDocument.Root.Element.DescendantsAndSelf().ElementAtOrDefault(index);
            if (actualElement is null || ReferenceEquals(actualElement, _selectedDesignElement?.Element))
            {
                // Either nothing found, or the caret is still within the already-selected
                // element - skip the redundant Select() (and property-grid rebuild) on every
                // single caret move within the same element.
                return;
            }

            var entry = _liveToDesign.FirstOrDefault(kvp => ReferenceEquals(kvp.Value.Element, actualElement));
            if (entry.Key is not null)
            {
                Select(entry.Key, entry.Value);
            }
        }
        catch (Exception ex)
        {
            // Best-effort - a hiccup here must never disrupt normal typing/editing.
            WriteLog($"XamlSourceView_SelectionChanged failed: {ex}");
        }
    }

    /// <summary>Re-renders just the XAML source pane from the current document, without touching the design surface.</summary>
    private void RefreshXamlSourceView()
    {
        if (CurrentDocument is not null)
        {
            SetXamlSourceText(CurrentDocument.ToXamlString());
        }
    }

    /// <summary>
    /// Commits whatever's typed in the XAML source view when it loses focus, then swaps the
    /// editor text for the document's canonical form if they differ - an apply after a typing
    /// pause deliberately leaves the typed text as-is (see <see cref="ApplySourceEditAfterPause"/>),
    /// so leaving the pane is where it gets normalized.
    /// </summary>
    private void XamlSourceView_LostFocus()
    {
        if (TryApplyXamlSourceEdit() && CurrentDocument is not null)
        {
            var documentText = CurrentDocument.ToXamlString();
            if (NormalizeLineEndings(XamlSourceView.Text) != NormalizeLineEndings(documentText))
            {
                SetXamlSourceText(documentText);
            }
        }
    }

    /// <summary>
    /// On every user edit in the XAML source view: marks the edit pending (so Save lights up) and
    /// restarts the pause timer. Programmatic reloads (<see cref="SetXamlSourceText"/>) are ignored,
    /// and typing that brings the text back to the document's own clears the pending state.
    /// </summary>
    private void XamlSourceView_TextChanged()
    {
        if (_suppressSourceSelectionSync || CurrentDocument is null)
        {
            return;
        }

        _sourceEditTimer.Stop();
        _sourceEditPending = NormalizeLineEndings(XamlSourceView.Text) != NormalizeLineEndings(CurrentDocument.ToXamlString());
        if (_sourceEditPending)
        {
            _sourceEditTimer.Start();
        }

        UpdateCommandStates();
    }

    /// <summary>
    /// Fires once typing has paused for the Options' apply delay: applies the typed text
    /// (or shows its errors) without rewriting the editor under the caret, then re-selects the
    /// element the caret is in, since the design-surface refresh clears the selection.
    /// </summary>
    private void ApplySourceEditAfterPause()
    {
        if (!_sourceEditPending)
        {
            return;
        }

        _keepEditorTextOnRefresh = true;
        bool applied;
        try
        {
            applied = TryApplyXamlSourceEdit();
        }
        finally
        {
            _keepEditorTextOnRefresh = false;
        }

        if (applied)
        {
            // Deferred until after layout, for the same reason as SelectDocumentRootAfterLayout:
            // the adorner is placed from the element's laid-out bounds. Right after the refresh
            // they're not there yet, which puts the adorner in the wrong place for an element
            // nested in a container (a Button in a StackPanel), where it can't fall back on
            // Canvas.Left/Top.
            DispatcherQueue.TryEnqueue(() =>
            {
                DesignSurfaceHost.UpdateLayout();
                XamlSourceView_SelectionChanged();
            });
        }
    }

    /// <summary>
    /// Reformats the current document's XAML with consistent indentation
    /// (<see cref="XamlDocument.ToFormattedXamlString"/>) and commits it through the exact same
    /// pipeline any other source edit goes through - not a separate code path, so it's
    /// undo-tracked and re-validated for free.
    /// </summary>
    private void FormatDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null)
        {
            return;
        }

        // Commit (or reject) whatever's already pending in the box first - formatting around a
        // stale document, or silently discarding a half-typed edit, would both be surprising.
        if (!TryApplyXamlSourceEdit())
        {
            return;
        }

        // LoadText, not the Text property - Text/SetText both record a step in TextControlBox's
        // own internal undo stack, which this app never surfaces or uses (Ctrl+Z drives the
        // app-level document undo instead - see DocumentSession); LoadText resets without touching it.
        XamlSourceView.LoadText(CurrentDocument.ToFormattedXamlString(), autodetectTabsSpaces: false);
        TryApplyXamlSourceEdit();
    }

    /// <summary>
    /// If the XAML source view's text differs from the current document, tries to re-parse it
    /// and, on success, swaps it in as the current document through the same undo-tracked
    /// pipeline every other edit (move, resize, property edit, ...) goes through - not a
    /// separate code path - then does a full design-surface reload. On failure (either
    /// malformed XML, or well-formed XML that XamlReader can't actually load - e.g. a typo'd
    /// property name like "Texte" instead of "Text", which parses fine as XML but doesn't exist
    /// on the control), shows an inline error and leaves both the typed text and the in-memory
    /// document untouched, so a mid-edit mistake never crashes the app, corrupts the current
    /// document, or silently discards what was typed.
    /// </summary>
    /// <returns>True if there was nothing to commit, or the commit succeeded; false if the typed text is invalid (an error is now showing).</returns>
    private bool TryApplyXamlSourceEdit()
    {
        if (CurrentDocument is null)
        {
            return true;
        }

        // Applying now (Ctrl+S, focus loss, Format, or the pause timer itself) - a pending pause
        // tick would only re-check the same text.
        _sourceEditTimer.Stop();

        // WinUI's TextBox normalizes line endings to '\r' internally, so reading .Text back can
        // legitimately differ from ToXamlString()'s '\r\n' (or whatever the source file used)
        // even when the user hasn't typed anything - comparing normalized copies avoids treating
        // that as a real edit (which would otherwise commit a no-op change and force a full
        // design-surface reload just from focusing and then leaving the text box).
        var typedText = XamlSourceView.Text;
        var currentText = CurrentDocument.ToXamlString();
        if (NormalizeLineEndings(typedText) == NormalizeLineEndings(currentText))
        {
            SetXamlSourceErrors([]);
            MarkSourceEditApplied();
            return true;
        }

        XamlDocument parsed;
        try
        {
            parsed = XamlDocument.Parse(typedText);
        }
        catch (Exception ex)
        {
            // XmlException (unlike XamlParseException below) has structured Line/LinePosition
            // properties - no message-scraping needed for this case.
            var item = ex is XmlException xmlEx
                ? new XamlErrorListItem(
                    xmlEx.LineNumber.ToString(CultureInfo.InvariantCulture),
                    xmlEx.LinePosition.ToString(CultureInfo.InvariantCulture),
                    CleanErrorMessage(ex.Message),
                    XamlSourcePositions.OffsetOf(typedText, xmlEx.LineNumber, xmlEx.LinePosition))
                : new XamlErrorListItem(string.Empty, string.Empty, CleanErrorMessage(ex.Message), null);
            SetXamlSourceErrors([item]);
            return false;
        }

        var parsedText = parsed.ToXamlString();

        // The typed text only differs from the document in formatting the XML object model
        // doesn't keep (e.g. an editor that still holds <Button/> after the document already has
        // <Button />) - nothing to commit, and committing would add an empty undo step.
        if (parsedText == currentText)
        {
            SetXamlSourceErrors([]);
            MarkSourceEditApplied();
            return true;
        }

        // Well-formed XML isn't the same as valid WinUI XAML - check it actually loads before
        // committing it as the current document. Without this, a typo like "Texte" would parse
        // fine, get committed, and only then fail inside RefreshDesignSurfaceFromDocument, at
        // which point the design surface just shows its generic "preview failed" fallback with
        // no way back to the edit except Undo - not the inline, in-place error this pane is for.
        var (root, status) = RenderPreview(parsedText);
        if (root is null)
        {
            // FindUnknownPropertyErrors can report several mistakes at once (unlike
            // XamlReader.Load, which stops at the first) - fall back to its single message only
            // for error shapes that check doesn't cover (an unknown element, a bad value, ...).
            var errors = FindUnknownPropertyErrors(parsedText);
            if (errors.Count == 0)
            {
                var line = TryExtractLine(status);
                var offset = line is int l ? XamlSourcePositions.OffsetOf(parsedText, l, 1) : (int?)null;
                errors.Add(new XamlErrorListItem(line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, string.Empty, CleanErrorMessage(status), offset));
            }

            SetXamlSourceErrors(errors);
            return false;
        }

        _sourceEditPending = false;
        _session!.ReplaceDocument(parsed);
        UpdateCommandStates();
        RefreshDesignSurfaceFromDocument();
        SetXamlSourceErrors([]);
        MarkSourceEditApplied();
        return true;
    }

    /// <summary>The editor's text is now reflected in the document: clears the pending state and refreshes Save.</summary>
    private void MarkSourceEditApplied()
    {
        _sourceEditPending = false;
        UpdateCommandStates();
    }

    /// <summary>True for an attribute that represents a plain CLR property/event in XAML's default namespace - excludes namespace declarations (xmlns:...), namespaced attributes (x:Name, mc:Ignorable, ...), and attached properties (e.g. "Canvas.Left", which have a '.' in the local name and aren't resolved via GetProperty/GetEvent on the element's own type).</summary>
    /// <param name="attribute">Attribute to check.</param>
    private static bool IsPlainAttribute(XAttribute attribute) =>
        !attribute.IsNamespaceDeclaration
        && attribute.Name.Namespace == XNamespace.None
        && !attribute.Name.LocalName.Contains('.');

    /// <summary>
    /// Finds every unknown-property/event attribute in the document by reflecting directly
    /// against each element's real CLR type (<see cref="ControlTypeResolver.Resolve"/>) - unlike
    /// XamlReader.Load, which stops at the first such problem it hits, this checks every element
    /// in one pass, so several mistakes (e.g. more than one typo'd property name) can all be
    /// reported - and jumped to - at once. Only covers this one error shape; other kinds of
    /// invalid XAML (an unknown element name, a value that fails to convert, ...) still only
    /// ever produce the single message XamlReader.Load itself reports.
    /// </summary>
    /// <param name="text">The document's current XAML text (must already be well-formed XML).</param>
    /// <returns>Every unknown-property/event error found, in document order, each with a jump-to offset into <paramref name="text"/>.</returns>
    private static List<XamlErrorListItem> FindUnknownPropertyErrors(string text)
    {
        var errors = new List<XamlErrorListItem>();

        XDocument reparsed;
        try
        {
            reparsed = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (Exception)
        {
            return errors; // malformed XML is reported separately, by the caller's own XamlDocument.Parse
        }

        if (reparsed.Root is null)
        {
            return errors;
        }

        foreach (var element in reparsed.Root.DescendantsAndSelf())
        {
            // Only unprefixed (WinUI) elements: a custom local:Button isn't WinUI's Button.
            // Elements that aren't a WinUI UIElement (RowDefinition, property elements such as
            // Grid.RowDefinitions, custom controls) aren't checked.
            if (element.Name.Namespace != FabWinUIDesigner.Document.XamlNamespaces.Presentation
                || ControlTypeResolver.Resolve(element.Name.LocalName) is not { } type)
            {
                continue;
            }

            foreach (var attribute in element.Attributes())
            {
                if (!IsPlainAttribute(attribute))
                {
                    continue;
                }

                var name = attribute.Name.LocalName;
                if (type.GetProperty(name) is not null || type.GetEvent(name) is not null)
                {
                    continue;
                }

                var lineInfo = element as IXmlLineInfo;
                var hasLineInfo = lineInfo?.HasLineInfo() == true;
                var offset = hasLineInfo ? XamlSourcePositions.OffsetOf(text, lineInfo!.LineNumber, lineInfo.LinePosition) : (int?)null;
                var lineText = hasLineInfo ? lineInfo!.LineNumber.ToString(CultureInfo.InvariantCulture) : string.Empty;
                var columnText = hasLineInfo ? lineInfo!.LinePosition.ToString(CultureInfo.InvariantCulture) : string.Empty;
                errors.Add(new XamlErrorListItem(lineText, columnText, CleanErrorMessage($"'{name}' is not a property or event on <{element.Name.LocalName}>."), offset));
            }
        }

        return errors;
    }

    /// <summary>
    /// Tries to pull a line number out of a WinUI XamlParseException's message (its only place
    /// with this info - unlike <see cref="XmlException"/>, it has no structured Line/Position
    /// properties). Only the line, not the column: the message's position refers to whichever
    /// sanitized copy of the XAML <see cref="XamlPreviewLoader"/> actually tried (x:Class/event
    /// attributes stripped - see <see cref="XamlPreviewSanitizer"/>), not the original text shown
    /// in the editor - a stripped-earlier attribute shifts later columns on the same line, but
    /// not the line itself (this app's XAML is one element per line), so the line number can be
    /// trusted but the column can't.
    /// </summary>
    /// <param name="exceptionMessage">The exception message to scan for a line number.</param>
    /// <returns>The 1-based line number, or null if none could be found.</returns>
    private static int? TryExtractLine(string exceptionMessage)
    {
        var match = Regex.Match(exceptionMessage, @"Line:\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Strips the redundant embedded "[Line: N Position: M]" suffix some exception messages carry (now shown in the Errors grid's own Line/Column columns instead) and collapses any embedded newlines, so the message fits cleanly on one grid row.</summary>
    /// <param name="message">Raw exception message.</param>
    /// <returns>The cleaned-up, single-line message.</returns>
    private static string CleanErrorMessage(string message) =>
        Regex.Replace(message, @"\s*\[Line:\s*\d+\s*Position:\s*\d+\]\s*$", string.Empty)
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            .Trim();

    /// <summary>One row in the Errors grid.</summary>
    /// <param name="LineText">1-based line number as display text, or empty if unknown.</param>
    /// <param name="ColumnText">1-based column number as display text, or empty if unknown.</param>
    /// <param name="Message">Human-readable, single-line error text (see <see cref="CleanErrorMessage"/>).</param>
    /// <param name="Offset">0-based character offset into the XAML source to jump to on click, or null if no position could be determined.</param>
    private sealed record XamlErrorListItem(string LineText, string ColumnText, string Message, int? Offset)
    {
        public override string ToString() => Message;
    }

    /// <summary>Clicking an error jumps the source view's caret to it (when a position is known) and switches to the Source tab so it's immediately visible.</summary>
    private void XamlErrorsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not XamlErrorListItem { Offset: { } offset })
        {
            return;
        }

        ShowXamlPaneTab(XamlPaneTab.Source);
        XamlSourceView.SetSelection(offset, 0);
        XamlSourceView.Focus(FocusState.Programmatic);
    }

    /// <summary>Which of the two bottom-tab panels the XAML pane is currently showing.</summary>
    private enum XamlPaneTab
    {
        Source,
        Errors,
    }

    private XamlPaneTab _xamlPaneTab = XamlPaneTab.Source;

    /// <summary>Collapses '\r\n' and lone '\r' to '\n', so text that only differs by line-ending style compares as equal.</summary>
    /// <param name="text">Text to normalize.</param>
    /// <returns>The text with all line endings collapsed to '\n'.</returns>
    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Updates the Errors tab/warning-icon/list with the current set of errors (or clears them).
    /// Deliberately does not auto-switch to the Errors tab when errors first appear - the user
    /// is usually mid-edit in the Source tab right then (their cursor and typed text are right
    /// there), and yanking that away would be worse than just making the Errors tab and warning
    /// icon turn red so it's noticeable without being disruptive. If the Errors tab happens to
    /// already be showing when the errors clear, switches back to Source since there's nothing
    /// left to show.
    /// </summary>
    /// <param name="errors">The current errors, or an empty list to clear them.</param>
    private void SetXamlSourceErrors(IReadOnlyList<XamlErrorListItem> errors)
    {
        XamlErrorsList.ItemsSource = errors;

        var hasErrors = errors.Count > 0;
        ErrorsTabButton.IsEnabled = hasErrors;
        ErrorsTabButton.Content = hasErrors ? $"Errors ({errors.Count})" : "Errors";
        XamlSourceWarningButton.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;

        if (!hasErrors && _xamlPaneTab == XamlPaneTab.Errors)
        {
            ShowXamlPaneTab(XamlPaneTab.Source);
        }
        else
        {
            UpdateXamlPaneTabButtonVisuals();
        }
    }

    private void SourceTabButton_Click(object sender, RoutedEventArgs e) => ShowXamlPaneTab(XamlPaneTab.Source);

    private void ErrorsTabButton_Click(object sender, RoutedEventArgs e) => ShowXamlPaneTab(XamlPaneTab.Errors);

    /// <summary>Jumps straight to the Errors tab - the warning icon next to the "XAML Source" header is a shortcut for this.</summary>
    private void XamlSourceWarningButton_Click(object sender, RoutedEventArgs e) => ShowXamlPaneTab(XamlPaneTab.Errors);

    /// <summary>Switches which of the Source/Errors panels is visible and updates the tab buttons to match.</summary>
    /// <param name="tab">The panel to show.</param>
    private void ShowXamlPaneTab(XamlPaneTab tab)
    {
        _xamlPaneTab = tab;
        XamlSourceView.Visibility = tab == XamlPaneTab.Source ? Visibility.Visible : Visibility.Collapsed;
        XamlErrorsPanel.Visibility = tab == XamlPaneTab.Errors ? Visibility.Visible : Visibility.Collapsed;
        UpdateXamlPaneTabButtonVisuals();
    }

    /// <summary>
    /// Styles the two tab buttons: the selected one is bold with a highlighted background; the
    /// Errors button also turns red (text + background tint) whenever there's an error, whether
    /// or not it's the one currently selected, so it stays attention-grabbing while the user is
    /// still looking at the Source tab - being "selected" and "has an error" are independent,
    /// unlike a normal tab control's single active/inactive state.
    /// </summary>
    private void UpdateXamlPaneTabButtonVisuals()
    {
        var hasError = ErrorsTabButton.IsEnabled;

        SourceTabButton.FontWeight = _xamlPaneTab == XamlPaneTab.Source ? FontWeights.Bold : FontWeights.Normal;
        SourceTabButton.Background = _xamlPaneTab == XamlPaneTab.Source
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Colors.Transparent);

        ErrorsTabButton.FontWeight = _xamlPaneTab == XamlPaneTab.Errors ? FontWeights.Bold : FontWeights.Normal;
        ErrorsTabButton.Background = _xamlPaneTab == XamlPaneTab.Errors
            ? new SolidColorBrush(hasError ? Color.FromArgb(255, 253, 231, 233) : Colors.White)
            : new SolidColorBrush(Colors.Transparent);
        ErrorsTabButton.Foreground = new SolidColorBrush(hasError ? Colors.Red : Colors.Black);
    }

    /// <summary>Which of the right panel's two tabs is currently showing.</summary>
    private enum PropertyPaneTab
    {
        Properties,
        Events,
    }

    private PropertyPaneTab _propertyPaneTab = PropertyPaneTab.Properties;

    /// <summary>Toggles the Properties/Events grids between category-grouped and flat-alphabetical, rebuilds whichever is currently selected, and persists the choice.</summary>
    private void AlphabeticalViewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _alphabeticalPropertyView = AlphabeticalViewToggle.IsChecked == true;
        AlphabeticalViewMenuItem.IsChecked = _alphabeticalPropertyView;
        if (_selectedDesignElement is not null)
        {
            BuildPropertyGrid(_selectedDesignElement);
        }

        SavePanelLayout();
    }

    /// <summary>View → Alphabetical Properties: drives the property pane's toggle, whose handler does the actual work and keeps both in sync.</summary>
    private void AlphabeticalViewMenuItem_Click(object sender, RoutedEventArgs e) =>
        AlphabeticalViewToggle.IsChecked = AlphabeticalViewMenuItem.IsChecked;

    private void PropertiesTabButton_Click(object sender, RoutedEventArgs e) => ShowPropertyPaneTab(PropertyPaneTab.Properties);

    private void EventsTabButton_Click(object sender, RoutedEventArgs e) => ShowPropertyPaneTab(PropertyPaneTab.Events);

    /// <summary>Switches which of the Properties/Events panels is visible and updates the tab buttons to match. Deliberately doesn't reset to Properties on every new selection - if the user is looking at Events and picks a different control, staying on Events is less disruptive than snapping back.</summary>
    /// <param name="tab">The panel to show.</param>
    private void ShowPropertyPaneTab(PropertyPaneTab tab)
    {
        _propertyPaneTab = tab;

        // Toggles the ScrollViewers, not just the StackPanels they wrap - an empty but still-
        // Visible ScrollViewer sitting on top in z-order still intercepts every pointer event
        // over the whole panel even with nothing visibly inside it, which is what made the
        // Properties tab appear entirely read-only the first time this was tried.
        PropertyGridScrollViewer.Visibility = tab == PropertyPaneTab.Properties ? Visibility.Visible : Visibility.Collapsed;
        EventGridScrollViewer.Visibility = tab == PropertyPaneTab.Events ? Visibility.Visible : Visibility.Collapsed;
        UpdatePropertyPaneTabButtonVisuals();
    }

    /// <summary>Styles the two tab buttons the same bold-plus-highlighted-background way as <see cref="UpdateXamlPaneTabButtonVisuals"/> does for Source/Errors.</summary>
    private void UpdatePropertyPaneTabButtonVisuals()
    {
        PropertiesTabButton.FontWeight = _propertyPaneTab == PropertyPaneTab.Properties ? FontWeights.Bold : FontWeights.Normal;
        PropertiesTabButton.Background = _propertyPaneTab == PropertyPaneTab.Properties
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Colors.Transparent);

        EventsTabButton.FontWeight = _propertyPaneTab == PropertyPaneTab.Events ? FontWeights.Bold : FontWeights.Normal;
        EventsTabButton.Background = _propertyPaneTab == PropertyPaneTab.Events
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>Call right before a mutation of the current document starts. Paired with <see cref="CommitUndoableChange"/> (see <see cref="DocumentSession.BeginChange"/>).</summary>
    private void BeginUndoableChange() => _session?.BeginChange();

    /// <summary>Call right after a mutation succeeds: records it as one undo step (see <see cref="DocumentSession.CommitChange"/>).</summary>
    private void CommitUndoableChange()
    {
        // Most commits are followed by a source-view refresh that updates the toolbar anyway,
        // but not all of them - doing it here too keeps Undo/Redo from ever lagging behind.
        if (_session?.CommitChange() == true)
        {
            UpdateCommandStates();
        }
    }

    /// <summary>Undoes the last change to the current document and re-renders it.</summary>
    private void Undo()
    {
        if (_session?.Undo() == true)
        {
            RefreshDesignSurfaceFromDocument();
            UpdateCommandStates();
        }
    }

    /// <summary>Redoes the last undone change to the current document and re-renders it.</summary>
    private void Redo()
    {
        if (_session?.Redo() == true)
        {
            RefreshDesignSurfaceFromDocument();
            UpdateCommandStates();
        }
    }

    /// <summary>
    /// Sets the XAML source pane's text without touching the caret. Used to move the caret to
    /// the end of the text here, so a change that landed outside the currently-scrolled-into-view
    /// area (e.g. a newly added element, appended near the end) would still scroll into view -
    /// but on every full reload (e.g. opening a file) that instead landed on whichever element
    /// happened to be last in the document, and the source->design caret sync read that as
    /// "select the last element". Removed for good
    /// once design->source caret syncing (the thing this was
    /// originally for) was removed entirely.
    /// </summary>
    /// <param name="text">The XAML text to display.</param>
    private void SetXamlSourceText(string text)
    {
        // During an apply after a typing pause, the editor already holds the text being applied
        // (maybe formatted differently) - reloading it would move the caret to the start while
        // the user is typing.
        if (_keepEditorTextOnRefresh)
        {
            UpdateCommandStates();
            return;
        }

        // LoadText, not the Text property - see the comment in FormatDocumentButton_Click for why.
        // Suppressed around the call - see _suppressSourceSelectionSync's own comment for why.
        _sourceEditTimer.Stop();
        _sourceEditPending = false;
        _suppressSourceSelectionSync = true;
        try
        {
            XamlSourceView.LoadText(text, autodetectTabsSpaces: false);

            // Put the caret at the start ourselves, so where the editor leaves it after a load
            // (it can end up at the end of the text) is never read as a user move - see _caretSetByCode.
            XamlSourceView.SetCursorPosition(0, 0, scrollIntoView: false, autoClamp: true);
            _caretSetByCode = (0, 0);
        }
        finally
        {
            _suppressSourceSelectionSync = false;
        }

        UpdateCommandStates();
    }

    /// <summary>
    /// Enables/disables the main toolbar's document buttons: Save only when the in-memory
    /// document differs from what's actually on disk (compared against the text as of the last
    /// load/save), Undo/Redo only when their stack has something to restore, and Format only
    /// when a document is open.
    /// </summary>
    private void UpdateCommandStates()
    {
        var hasDocument = CurrentDocument is not null;
        // Text typed in the XAML source but not applied yet counts as unsaved too.
        SaveButton.IsEnabled = SaveMenuItem.IsEnabled = hasDocument && (_sourceEditPending || _session!.IsModified);
        SaveAsMenuItem.IsEnabled = CloseMenuItem.IsEnabled = hasDocument;
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = _session?.CanUndo == true;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = _session?.CanRedo == true;
        FormatToolbarButton.IsEnabled = FormatMenuItem.IsEnabled = hasDocument;
        UpdateSelectionCommandStates();
        UpdateDocumentStatus();
        _activeTab?.UpdateHeader(SaveButton.IsEnabled);
    }

    private const string AppTitle = "FabWinUI Designer";

    /// <summary>
    /// Window title and status-bar path for the current document, VS-style:
    /// <c>Page1.xaml* - FabWinUI Designer</c>, the <c>*</c> marking unsaved changes (same test as
    /// the Save button). Called from <see cref="UpdateCommandStates"/>, which already runs on
    /// every change to the document, its path, or its saved state.
    /// </summary>
    private void UpdateDocumentStatus()
    {
        UpdateCaretStatus();
        if (CurrentDocument is null)
        {
            Title = AppTitle;
            StatusFilePathText.Text = string.Empty;
            return;
        }

        var name = _activeTab!.DisplayName;
        Title = $"{name}{(SaveButton.IsEnabled ? "*" : string.Empty)} - {AppTitle}";
        StatusFilePathText.Text = CurrentFilePath ?? "(not saved yet)";
    }

    /// <summary>Shows <paramref name="message"/> on the left of the status bar, e.g. "Saved Page1.xaml".</summary>
    /// <param name="message">Short message for the last completed action.</param>
    private void SetStatus(string message) => StatusMessageText.Text = message;

    /// <summary>Shows the XAML editor's caret position (1-based, like VS: "Ln 12, Col 5") in the status bar, or nothing while no document is open.</summary>
    private void UpdateCaretStatus()
    {
        var caret = XamlSourceView.CursorPosition;
        StatusCaretText.Text = CurrentDocument is null
            ? string.Empty
            : $"Ln {caret.LineNumber + 1}, Col {caret.CharacterPosition + 1}";
    }

    /// <summary>Enables Edit → Delete / Deselect only while something is selected. Split out from <see cref="UpdateCommandStates"/> because it runs on every selection change, where re-serializing the document for the Save check would be wasted work.</summary>
    private void UpdateSelectionCommandStates()
    {
        DeleteMenuItem.IsEnabled = DeselectMenuItem.IsEnabled = _selectedDesignElement is not null;
    }

    /// <summary>Edit → Delete - same as the Del key.</summary>
    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => DeleteSelectedControl();

    /// <summary>Edit → Deselect - same as the Esc key.</summary>
    private void DeselectMenuItem_Click(object sender, RoutedEventArgs e) => ClearSelection();

    /// <summary>Toolbar Undo - same as Ctrl+Z, but without Ctrl+Z's focus guard: clicking the button is an explicit document-level undo, so it never competes with a text field's own undo.</summary>
    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    /// <summary>Toolbar Redo - same as Ctrl+Y (see <see cref="UndoButton_Click"/>).</summary>
    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    /// <summary>Drag-resizes a column by attaching pointer handlers directly to a splitter element - there's no built-in GridSplitter in the WinUI SDK.</summary>
    /// <param name="splitter">The draggable splitter element (a <see cref="SplitterThumb"/> in practice, for its hover cursor - see <see cref="CursorGrid"/>).</param>
    /// <param name="column">The column whose <see cref="ColumnDefinition.Width"/> the drag adjusts.</param>
    /// <param name="minWidth">Minimum width the drag can shrink <paramref name="column"/> to.</param>
    /// <param name="maxWidth">Maximum width the drag can grow <paramref name="column"/> to.</param>
    /// <param name="onDragCompleted">Invoked once when a drag ends (pointer released or capture lost), e.g. to persist the new layout.</param>
    private void AttachColumnSplitter(FrameworkElement splitter, ColumnDefinition column, double minWidth, double maxWidth, Action onDragCompleted)
    {
        var dragging = false;
        var lastX = 0.0;

        splitter.PointerPressed += (_, e) =>
        {
            dragging = true;
            lastX = e.GetCurrentPoint(Content).Position.X;
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            var x = e.GetCurrentPoint(Content).Position.X;
            column.Width = new GridLength(Math.Clamp(column.Width.Value + (x - lastX), minWidth, maxWidth));
            lastX = x;
        };

        // PointerCaptureLost can fire instead of PointerReleased (pointer leaves the window,
        // etc.) - without also resetting `dragging` here, a drag can get stuck "on" and keep
        // reacting to unrelated future pointer movement near the splitter.
        void EndDrag(object? s, PointerRoutedEventArgs e)
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            splitter.ReleasePointerCapture(e.Pointer);
            onDragCompleted();
        }

        splitter.PointerReleased += EndDrag;
        splitter.PointerCaptureLost += EndDrag;
    }

    /// <summary>
    /// Same as <see cref="AttachColumnSplitter"/> but for a row's height instead of a column's
    /// width. <paramref name="invert"/> flips the drag direction for splitters whose controlled
    /// row comes *before* (above) the splitter in the pointer-delta math but is visually the one
    /// below it in layout terms - needed for the design-surface/XAML-source splitter specifically but not
    /// the others.
    /// </summary>
    /// <param name="splitter">The draggable splitter element.</param>
    /// <param name="row">The row whose <see cref="RowDefinition.Height"/> the drag adjusts.</param>
    /// <param name="minHeight">Minimum height the drag can shrink <paramref name="row"/> to.</param>
    /// <param name="maxHeight">Maximum height the drag can grow <paramref name="row"/> to.</param>
    /// <param name="onDragCompleted">Invoked once when a drag ends (pointer released or capture lost), e.g. to persist the new layout.</param>
    /// <param name="invert">True if <paramref name="row"/> lies after (below) <paramref name="splitter"/> in the layout, so the drag-delta sign needs flipping.</param>
    private void AttachRowSplitter(FrameworkElement splitter, RowDefinition row, double minHeight, double maxHeight, Action onDragCompleted, bool invert = false)
    {
        var dragging = false;
        var lastY = 0.0;
        var sign = invert ? -1.0 : 1.0;

        splitter.PointerPressed += (_, e) =>
        {
            dragging = true;
            lastY = e.GetCurrentPoint(Content).Position.Y;
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            var y = e.GetCurrentPoint(Content).Position.Y;
            row.Height = new GridLength(Math.Clamp(row.Height.Value + sign * (y - lastY), minHeight, maxHeight));
            lastY = y;
        };

        void EndDrag(object? s, PointerRoutedEventArgs e)
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            splitter.ReleasePointerCapture(e.Pointer);
            onDragCompleted();
        }

        splitter.PointerReleased += EndDrag;
        splitter.PointerCaptureLost += EndDrag;
    }

    /// <summary>On-disk shape of <see cref="LayoutConfigPath"/>.</summary>
    /// <param name="ToolboxWidth">Width of the toolbox column.</param>
    /// <param name="FilePanelHeight">Height of the file-browser panel row.</param>
    /// <param name="XamlSourceHeight">Height of the XAML source pane row.</param>
    /// <param name="SnapToGrid">Whether snap-to-grid was enabled. Defaults to false so layout files saved before this setting existed still deserialize.</param>
    /// <param name="AlphabeticalPropertyView">Whether the Properties/Events grids were showing the flat alphabetical view. Defaults to false (category-grouped) so layout files saved before this setting existed still deserialize.</param>
    /// <param name="AlphabeticalToolboxView">Whether the Toolbox was showing one alphabetical list instead of groups. Defaults to false for older layout files.</param>
    /// <param name="ExpandedToolboxGroups">Names of the expanded Toolbox groups. Null in older layout files, which then get the JSON's ExpandedByDefault groups.</param>
    private sealed record PanelLayout(
        double ToolboxWidth,
        double FilePanelHeight,
        double XamlSourceHeight,
        bool SnapToGrid = false,
        bool AlphabeticalPropertyView = false,
        bool AlphabeticalToolboxView = false,
        string[]? ExpandedToolboxGroups = null);

    /// <summary>Restores panel sizes from <see cref="LayoutConfigPath"/>, leaving the XAML-declared defaults in place if the file is missing or unreadable.</summary>
    private void LoadPanelLayout()
    {
        try
        {
            if (!File.Exists(LayoutConfigPath))
            {
                return;
            }

            var layout = JsonSerializer.Deserialize<PanelLayout>(File.ReadAllText(LayoutConfigPath));
            if (layout is null)
            {
                return;
            }

            ToolboxColumn.Width = new GridLength(layout.ToolboxWidth);
            FilePanelRow.Height = new GridLength(layout.FilePanelHeight);
            XamlSourceRow.Height = new GridLength(layout.XamlSourceHeight);
            _snapToGridEnabled = layout.SnapToGrid;
            _alphabeticalPropertyView = layout.AlphabeticalPropertyView;
            _alphabeticalToolboxView = layout.AlphabeticalToolboxView;
            if (layout.ExpandedToolboxGroups is not null)
            {
                _expandedToolboxGroupsSaved = new HashSet<string>(layout.ExpandedToolboxGroups, StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable config - just keep the XAML defaults.
        }
    }

    /// <summary>Persists current panel sizes to <see cref="LayoutConfigPath"/>. Best-effort - a write failure is swallowed.</summary>
    private void SavePanelLayout()
    {
        try
        {
            var layout = new PanelLayout(
                ToolboxColumn.Width.Value,
                FilePanelRow.Height.Value,
                XamlSourceRow.Height.Value,
                _snapToGridEnabled,
                _alphabeticalPropertyView,
                _alphabeticalToolboxView,
                [.. ExpandedToolboxGroups.Order(StringComparer.Ordinal)]);
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutConfigPath)!);
            File.WriteAllText(LayoutConfigPath, JsonSerializer.Serialize(layout));
        }
        catch (Exception)
        {
            // Best-effort - failing to persist layout isn't fatal.
        }
    }

    /// <summary>Renders <paramref name="element"/> to a PNG file - used to keep a debug preview snapshot of the design surface on disk.</summary>
    /// <param name="element">The element to render.</param>
    /// <param name="path">Destination PNG file path; overwritten if it already exists.</param>
    private static async Task SaveSnapshotAsync(FrameworkElement element, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixelBuffer = await rtb.GetPixelsAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.Create(path).Dispose();
        }

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;

        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            96,
            96,
            pixelBuffer.ToArray());
        await encoder.FlushAsync();
    }
}
