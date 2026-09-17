using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
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
using WinUIDesigner.Core;
using DesignElement = WinUIDesigner.Document.DesignElement;
using XamlDocument = WinUIDesigner.Document.XamlDocument;

namespace WinUIDesigner.App;

/// <summary>
/// The designer's single window: file browser, toolbox, design surface (with selection/move/resize
/// adorners), XAML source view, and property grid, all wired directly in code-behind against a
/// single in-memory <see cref="XamlDocument"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string SpikeLogPath = Path.Combine(Path.GetTempPath(), "WinUIDesigner", "m2-xamlreader-spike.log");
    private static readonly string SnapshotPath = Path.Combine(Path.GetTempPath(), "WinUIDesigner", "preview-snapshot.png");
    private static readonly string LayoutConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUIDesigner", "layout.json");
    private static readonly string RecentConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUIDesigner", "recent.json");

    private const double MinElementSize = 8;
    private const double HandleSize = 7;

    private string? _currentFilePath;
    private XamlDocument? _currentDocument;
    private IReadOnlyDictionary<UIElement, DesignElement> _liveToDesign = new Dictionary<UIElement, DesignElement>();
    private UIElement? _selectedLiveElement;
    private DesignElement? _selectedDesignElement;

    // TextControlBox (unlike TextBox) doesn't route keyboard focus through a plain TextBox
    // instance, so RootGrid_KeyDown's "is a TextBox focused?" guard can't see it by type -
    // tracked explicitly via this control's own GotFocus/LostFocus instead (see the constructor).
    private bool _xamlSourceViewHasFocus;

    // Undo/Redo: whole-document text snapshots rather than a command pattern - simple, and
    // cheap enough at our document sizes. _pendingUndoSnapshot is set by BeginUndoableChange()
    // right before a mutation starts and consumed by CommitUndoableChange() right after it
    // succeeds, so an edit that's validated-and-rejected (e.g. bad property input) never
    // pollutes the undo stack.
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private string? _pendingUndoSnapshot;

    // Dirty tracking for the Save button: compared against the document text as of the last
    // load/save, not a simple bool, so undoing back to that exact state re-disables Save too.
    private string? _lastSavedXaml;

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
        Title = "WinUI Designer";

        LoadPanelLayout();
        AttachColumnSplitter(ToolboxSplitter, ToolboxColumn, minWidth: 100, maxWidth: 400, SavePanelLayout);
        // invert:true here - XamlSourceRow is the row *after* this splitter (DesignSurfaceRow,
        // splitter, XamlSourceRow), so dragging up should grow it, unlike FilePropertiesSplitter
        // below where the controlled row (FilePanelRow) comes *before* the splitter.
        AttachRowSplitter(DesignXamlSplitter, XamlSourceRow, minHeight: 80, maxHeight: 600, SavePanelLayout, invert: true);
        AttachRowSplitter(FilePropertiesSplitter, FilePanelRow, minHeight: 80, maxHeight: 600, SavePanelLayout);
        Closed += (_, _) => SavePanelLayout();

        // Hover cursor: a plain <Grid> can't show one (UIElement.ProtectedCursor is protected),
        // hence SplitterThumb/ResizeHandle - see their doc comment / research/16-splitter-hover-cursor.md
        // and research/17-resize-handle-hover-cursor.md.
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

        // The built-in XML mode (SelectSyntaxHighlightingById(SyntaxHighlightID.XML)) turned out
        // to look broken on real XAML: its one regex for an opening tag greedily matches the
        // *whole* tag including every attribute, and its attribute-name and attribute-value
        // colors happen to be the exact same green in light theme - so a multi-attribute line
        // like the root Page/Canvas's xmlns block rendered as "tag name, then everything else in
        // one solid color" instead of readable syntax coloring (found via Fabrice's own
        // interactive testing). XamlSyntaxHighlightingJson below is a custom scheme instead -
        // separate regexes for the tag name, each attribute name, and each quoted value, so they
        // never collapse into one match or share a color. Verified against real WinRT-projected
        // JSON parsing (Newtonsoft.Json under the hood - a plain JSON array for Filter, which the
        // library's own type expects as a pipe-delimited *string*, silently failed to deserialize)
        // with a throwaway probe before trusting it here - same technique as research/35.
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
        XamlSourceView.SelectionChanged += (_, _) => XamlSourceView_SelectionChanged();
        XamlSourceView.LostFocus += _ =>
        {
            _xamlSourceViewHasFocus = false;
            XamlSourceView_LostFocus();
        };
        XamlSourceView.GotFocus += _ => _xamlSourceViewHasFocus = true;
    }

    /// <summary>
    /// A custom syntax-highlighting scheme for <see cref="XamlSourceView"/>, in
    /// <c>TextControlBox.GetSyntaxHighlightingFromJson</c>'s own JSON shape (its
    /// <c>JsonSyntaxHighlighting</c> DTO - <c>Filter</c> is a pipe-delimited <em>string</em>, not
    /// a JSON array, confirmed via a throwaway probe against the real package before trusting it
    /// here). Colors follow Visual Studio's classic XML/XAML editor palette (research/35): element
    /// names maroon, attribute names red, quoted values blue, comments green - each its own
    /// regex, deliberately narrower than the built-in XML language's single whole-tag regex (see
    /// the comment in the constructor for why that one looked broken on real XAML).
    /// </summary>
    private const string XamlSyntaxHighlightingJson = """
        {
            "Name": "XAML",
            "Author": "WinUIDesigner",
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

    /// <summary>A blank single-Canvas Page, same shape as the sample fixtures minus x:Class (a brand-new file has no code-behind yet).</summary>
    private const string NewDocumentTemplate =
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\">\n" +
        "    <Canvas Width=\"400\" Height=\"300\" Background=\"White\" />\n" +
        "</Page>";

    /// <summary>Starts a blank document, confirming discard first if the current one has unsaved changes.</summary>
    private async void NewButton_Click(object sender, RoutedEventArgs e)
    {
        // SaveButton.IsEnabled doubles as our "is dirty" flag (see UpdateSaveButtonState) - ask
        // for confirmation only when there's actually something that would be lost.
        if (SaveButton.IsEnabled)
        {
            var dialog = new ContentDialog
            {
                Title = "Discard unsaved changes?",
                Content = "This file has unsaved changes. Starting a new file will discard them.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        // Same reset as LoadFile, but from the blank template and with no path yet.
        var doc = XamlDocument.Parse(NewDocumentTemplate);
        _currentDocument = doc;
        _currentFilePath = null;
        CurrentFileText.Text = "(no file open)";

        _undoStack.Clear();
        _redoStack.Clear();
        _pendingUndoSnapshot = null;
        _lastSavedXaml = doc.ToXamlString();

        RefreshDesignSurfaceFromDocument();
        SelectDocumentRootAfterLayout();
        UpdateSaveButtonState();
    }

    /// <summary>Prompts for a `.xaml` file via the file picker and loads it.</summary>
    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".xaml");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            LoadFile(file.Path);
        }
    }

    /// <summary>Saves the current document, prompting for a location first ("Save As" semantics) if it doesn't have a path yet (i.e. it came from New File).</summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveCurrentDocumentAsync();

    /// <summary>
    /// Commits any pending edit sitting in the XAML source view first (so Ctrl+S while typing
    /// there behaves the way it would in a real text editor - save what you just typed, not the
    /// stale on-disk state), then saves. If the pending edit is invalid XAML, the save is
    /// skipped - saving the old document while an inline error is showing would look like the
    /// edit was silently discarded.
    /// </summary>
    private async Task SaveCurrentDocumentAsync()
    {
        if (!TryApplyXamlSourceEdit())
        {
            return;
        }

        if (_currentDocument is null)
        {
            return;
        }

        // A document created via New File has no path yet - prompt for one, same as "Save As".
        if (_currentFilePath is null)
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("XAML File", new List<string> { ".xaml" });
            picker.SuggestedFileName = "NewPage";

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            _currentFilePath = file.Path;
            CurrentFileText.Text = _currentFilePath;
        }

        _currentDocument.Save(_currentFilePath);
        _lastSavedXaml = _currentDocument.ToXamlString();
        UpdateSaveButtonState();
        AddRecentFile(_currentFilePath);
    }

    /// <summary>Prompts for a folder via the folder picker and populates the file tree from it.</summary>
    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
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

    /// <summary>
    /// Non-recursive: lists .xaml files directly in the chosen folder (name + extension only,
    /// not the full path - that's shown for whichever item is selected instead). Each .xaml
    /// node gets its paired .xaml.cs nested under it if one exists on disk, like VS's Solution
    /// Explorer file nesting - there's no code editor yet (that's later, around M7/M8), so
    /// selecting the .cs node just shows its path rather than opening it.
    /// </summary>
    /// <param name="folderPath">Absolute path of the folder to list `.xaml` files from.</param>
    private void PopulateFileTree(string folderPath)
    {
        FileTreeView.RootNodes.Clear();

        foreach (var xamlPath in Directory.EnumerateFiles(folderPath, "*.xaml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TreeViewNode { Content = new FileTreeNodeInfo(Path.GetFileName(xamlPath), xamlPath, IsXaml: true) };

            var codeBehindPath = xamlPath + ".cs";
            if (File.Exists(codeBehindPath))
            {
                node.Children.Add(new TreeViewNode { Content = new FileTreeNodeInfo(Path.GetFileName(codeBehindPath), codeBehindPath, IsXaml: false) });
                node.IsExpanded = true;
            }

            FileTreeView.RootNodes.Add(node);
        }
    }

    /// <summary>
    /// Single click just selects and shows the full path; double-click (below) loads it.
    /// Uses SelectedNode.Content, not SelectedItem - SelectedItem only reflects the selection
    /// for an ItemsSource-bound TreeView; ours is populated manually via RootNodes/TreeViewNode,
    /// so SelectedNode is the API that actually tracks selection in that mode. (Bug found via
    /// real testing: double-click silently did nothing because of this.)
    /// </summary>
    private void FileTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (FileTreeView.SelectedNode?.Content is FileTreeNodeInfo info)
        {
            CurrentFileText.Text = info.FullPath;
        }
    }

    /// <summary>Loads the double-tapped file tree node, if it's a `.xaml` node (double-tapping a nested `.xaml.cs` node does nothing - see <see cref="FileTreeNodeInfo"/>).</summary>
    private void FileTreeView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileTreeView.SelectedNode?.Content is FileTreeNodeInfo { IsXaml: true } info)
        {
            LoadFile(info.FullPath);
        }
    }

    /// <summary>Content of one file-tree node - either a `.xaml` file or a nested `.xaml.cs` code-behind file.</summary>
    /// <param name="DisplayName">File name shown in the tree (name + extension, no path).</param>
    /// <param name="FullPath">Absolute path used to load the file or show its path.</param>
    /// <param name="IsXaml">True for a `.xaml` node (double-click loads it); false for a nested `.xaml.cs` node (there's no code editor yet, so it's display-only).</param>
    private sealed record FileTreeNodeInfo(string DisplayName, string FullPath, bool IsXaml)
    {
        public override string ToString() => DisplayName;
    }

    private const int MaxRecentEntries = 8;
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

    /// <summary>Moves <paramref name="path"/> to the front of <paramref name="list"/> (de-duplicated, case-insensitively), trims it to <see cref="MaxRecentEntries"/>, then persists and re-renders the Recent menu.</summary>
    /// <param name="list">Either <see cref="_recentFiles"/> or <see cref="_recentFolders"/>.</param>
    /// <param name="path">Absolute path to add.</param>
    private void AddRecent(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxRecentEntries)
        {
            list.RemoveRange(MaxRecentEntries, list.Count - MaxRecentEntries);
        }

        SaveRecentLists();
        RefreshRecentMenu();
    }

    /// <summary>Rebuilds <c>RecentFlyout</c>'s items from <see cref="_recentFiles"/>/<see cref="_recentFolders"/> - two labeled, separator-divided sections, or a disabled placeholder if both are empty.</summary>
    private void RefreshRecentMenu()
    {
        RecentFlyout.Items.Clear();

        if (_recentFiles.Count == 0 && _recentFolders.Count == 0)
        {
            RecentFlyout.Items.Add(new MenuFlyoutItem { Text = "(no recent items)", IsEnabled = false });
            return;
        }

        // A disabled MenuFlyoutItem acts as a non-clickable section header, since MenuFlyout has
        // no built-in grouping/header control. Path is stashed in Tag so the click handler knows
        // which entry was clicked without a closure per item.
        if (_recentFiles.Count > 0)
        {
            RecentFlyout.Items.Add(new MenuFlyoutItem { Text = "Recent Files", IsEnabled = false });
            foreach (var path in _recentFiles)
            {
                var item = new MenuFlyoutItem { Text = path, Tag = path };
                item.Click += RecentFileItem_Click;
                RecentFlyout.Items.Add(item);
            }
        }

        if (_recentFolders.Count > 0)
        {
            if (_recentFiles.Count > 0)
            {
                RecentFlyout.Items.Add(new MenuFlyoutSeparator());
            }

            RecentFlyout.Items.Add(new MenuFlyoutItem { Text = "Recent Folders", IsEnabled = false });
            foreach (var path in _recentFolders)
            {
                var item = new MenuFlyoutItem { Text = path, Tag = path };
                item.Click += RecentFolderItem_Click;
                RecentFlyout.Items.Add(item);
            }
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
            LoadFile(path);
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
        }
        else
        {
            _recentFolders.Remove(path);
            SaveRecentLists();
            RefreshRecentMenu();
        }
    }

    /// <summary>Loads a `.xaml` file as the current document, resets undo/redo and dirty-tracking, refreshes the design surface, and adds it to Recent Files. On failure, shows the error in place of the current-file path instead of throwing.</summary>
    /// <param name="path">Absolute path of the `.xaml` file to load.</param>
    private void LoadFile(string path)
    {
        try
        {
            var doc = XamlDocument.Load(path);
            _currentDocument = doc;
            _currentFilePath = path;
            CurrentFileText.Text = path;

            // A freshly-loaded file has no undo history and nothing unsaved yet.
            _undoStack.Clear();
            _redoStack.Clear();
            _pendingUndoSnapshot = null;
            _lastSavedXaml = doc.ToXamlString();

            RefreshDesignSurfaceFromDocument();
            SelectDocumentRootAfterLayout();
            AddRecentFile(path);
        }
        catch (Exception ex)
        {
            CurrentFileText.Text = $"Failed to open: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-renders the design surface, XAML source pane, and live/design element correlation
    /// from <see cref="_currentDocument"/>'s current in-memory state - used both after opening
    /// a file and after any structural edit (e.g. adding a toolbox control), since those need
    /// a full XamlReader reload rather than an incremental live-object tweak.
    /// </summary>
    private void RefreshDesignSurfaceFromDocument()
    {
        if (_currentDocument is null)
        {
            return;
        }

        var text = _currentDocument.ToXamlString();
        SetXamlSourceText(text);

        var (root, status) = RenderPreview(text);
        DesignSurfaceHost.Child = root ?? new TextBlock
        {
            Text = $"Preview failed ({status}). See {SpikeLogPath}",
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap,
        };

        _liveToDesign = root is not null
            ? LiveTreeCorrelator.Correlate(root, _currentDocument.Root)
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
    /// Loads a design-time preview of the document. The M2 spike (see
    /// research/02-xamlreader-event-handlers.md) confirmed loose XamlReader.Load
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
    /// immediately drag it into place. v1 only supports a Canvas root (see
    /// research/00-scope-and-decisions.md), so this doesn't attempt to target nested containers.
    /// </summary>
    /// <param name="localName">The XAML element name to add, e.g. "Button" (must be one of the types <see cref="ApplyDefaultAttributes"/> knows defaults for).</param>
    private void AddControl(string localName)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var canvasElement = _currentDocument.Root.Children.FirstOrDefault();
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

        ApplyDefaultAttributes(child, localName);

        CommitUndoableChange();
        RefreshDesignSurfaceFromDocument();
        SelectByName(name);
    }

    /// <summary>Sets a handful of sensible default attributes (size, placeholder content, ...) so a freshly-added control isn't invisible or zero-sized on the design surface.</summary>
    /// <param name="element">The just-added element to set attributes on.</param>
    /// <param name="localName">The element's XAML type name, e.g. "Button" - selects which defaults apply.</param>
    private static void ApplyDefaultAttributes(DesignElement element, string localName)
    {
        switch (localName)
        {
            case "Button":
                element.SetAttribute("Content", "Button");
                element.SetAttribute("Width", "100");
                element.SetAttribute("Height", "32");
                break;
            case "TextBlock":
                element.SetAttribute("Text", "TextBlock");
                break;
            case "TextBox":
                element.SetAttribute("Width", "120");
                element.SetAttribute("Height", "32");
                break;
            case "CheckBox":
                element.SetAttribute("Content", "CheckBox");
                break;
            case "ComboBox":
                element.SetAttribute("Width", "120");
                break;
            case "Image":
                element.SetAttribute("Width", "100");
                element.SetAttribute("Height", "100");
                break;
            case "StackPanel":
            case "Grid":
                element.SetAttribute("Width", "150");
                element.SetAttribute("Height", "100");
                // A Panel with no Background (the default) isn't hit-testable across its empty
                // area - only actual child content would be, and a freshly-added container has
                // none yet, making it completely unselectable by clicking inside its bounds.
                // Transparent is a real (if invisible) brush, so hit-testing still works.
                element.SetAttribute("Background", "Transparent");
                break;
        }
    }

    /// <summary>Finds the next unused "{localName}{N}" name by scanning the whole document, so it stays unique even across files that already name things that way.</summary>
    /// <param name="localName">The element's XAML type name, e.g. "Button" - used as the name prefix.</param>
    /// <returns>A name of the form "{localName}{N}" not already used anywhere in the current document.</returns>
    private string GenerateUniqueName(string localName)
    {
        var used = new HashSet<string>();
        CollectNames(_currentDocument!.Root, used);

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
    /// point (see research/34-select-root-adorner-timing.md). Same fix already used for the
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
    /// nothing selected (or, before research/32-select-root-on-load.md's fix, whatever the old
    /// caret-to-end-of-text side effect happened to land on). Called (deferred - see
    /// <see cref="SelectDocumentRootAfterLayout"/>) after
    /// <see cref="RefreshDesignSurfaceFromDocument"/>.
    /// </summary>
    private void SelectDocumentRoot()
    {
        if (_currentDocument is null)
        {
            return;
        }

        var rootDesignElement = _currentDocument.Root.Children.FirstOrDefault();
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

    /// <summary>Hit-tests the press point; if it lands on a design element, selects it and begins a move drag, otherwise clears the selection.</summary>
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
        var newLeft = _moveStartLeft + (point.X - _moveStartPointerPosition.X);
        var newTop = _moveStartTop + (point.Y - _moveStartPointerPosition.Y);

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
            // (see SetXamlSourceText) and - via the source->design caret sync (#25) - re-selects
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

        Canvas.SetLeft(resizing, newLeft);
        Canvas.SetTop(resizing, newTop);
        resizing.Width = newWidth;
        resizing.Height = newHeight;

        UpdateAdornerToMatch(resizing);
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

        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionLabel.Visibility = Visibility.Visible;
        SetHandlesVisibility(Visibility.Visible);

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
    }

    /// <summary>
    /// Escape deselects ("cancel"); Delete removes the selected control; Ctrl+S saves. Escape/
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

    /// <summary>Two-column name/value layout, matching the WPF/WinForms Properties window.</summary>
    /// <param name="designElement">The selected element whose curated properties (per <see cref="PropertyGridSchema"/>) to show editors for.</param>
    private void BuildPropertyGrid(DesignElement designElement)
    {
        PropertyGridPanel.Children.Clear();

        // Column 0 = fixed-width labels, column 1 = editors that stretch to fill the rest.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // One row per property this element type has (per PropertyGridSchema's curated list -
        // WinUI controls don't expose design-time attributes to discover this via reflection).
        var descriptors = PropertyGridSchema.GetProperties(designElement.LocalName);
        for (var row = 0; row < descriptors.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var descriptor = descriptors[row];

            var label = new TextBlock
            {
                Text = descriptor.Name,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var editor = CreatePropertyEditor(designElement, descriptor);
            editor.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
        }

        PropertyGridPanel.Children.Add(grid);
    }

    /// <summary>Creates the property grid's editor control for one property - a <see cref="CheckBox"/>, <see cref="ComboBox"/>, or plain <see cref="TextBox"/> depending on the descriptor's kind - wired to call <see cref="ApplyPropertyEdit"/> when its value changes.</summary>
    /// <param name="designElement">The selected element the property belongs to.</param>
    /// <param name="descriptor">Describes the property's name and editor kind.</param>
    /// <returns>The editor control, ready to place in the property grid.</returns>
    private FrameworkElement CreatePropertyEditor(DesignElement designElement, PropertyDescriptor descriptor)
    {
        var currentText = designElement.GetAttribute(descriptor.Name) ?? string.Empty;

        // Bool -> CheckBox, Enum -> ComboBox of its named values, everything else -> a plain
        // TextBox (numbers, colors, thicknesses, ... are all typed as free text and parsed in
        // ApplyPropertyEdit, same as they'd appear in real XAML).
        switch (descriptor.Kind)
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
                var values = descriptor.EnumValues ?? [];
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
    /// <param name="descriptor">Describes the property's name and value kind (number, bool, enum, brush, thickness, or plain text).</param>
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

        object? liveValue;
        string attributeText;

        switch (descriptor.Kind)
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
    /// design (research/33-one-way-caret-sync.md): an earlier version also moved the source
    /// caret to match whenever a control was selected *on the design surface*, but that made it
    /// impossible to click in the whitespace gap between two elements without the caret
    /// immediately snapping back to the preceding tag, and Fabrice found it more disruptive than
    /// useful in practice - selecting on the design surface no longer touches the source pane's
    /// caret at all. Only acts while the source view's text matches the current document exactly
    /// (normalizing line endings, same as <see cref="TryApplyXamlSourceEdit"/>)
    /// - <c>TextControlBox.SelectionChanged</c> fires on every caret move, including ones caused
    /// by typing, so this deliberately does nothing while there's an uncommitted edit in progress
    /// rather than fighting the user's typing with a selection rebuild on every keystroke.
    /// </summary>
    private void XamlSourceView_SelectionChanged()
    {
        if (_currentDocument is null || _xamlPaneTab != XamlPaneTab.Source)
        {
            return;
        }

        try
        {
            var text = _currentDocument.ToXamlString();
            if (NormalizeLineEndings(XamlSourceView.Text) != NormalizeLineEndings(text))
            {
                return;
            }

            // TextControlBox has no flat character-offset caret property (unlike TextBox's
            // SelectionStart) - CursorPosition gives a zero-based (line, character) pair instead,
            // converted to a flat offset via the same OffsetOf helper the Errors grid's
            // line/column positions already use (it takes 1-based line/column, hence the +1s).
            var caret = XamlSourceView.CursorPosition;
            var caretOffset = OffsetOf(text, caret.LineNumber + 1, caret.CharacterPosition + 1);

            var reparsed = XDocument.Parse(text, LoadOptions.SetLineInfo);
            var index = EnclosingElementIndex(reparsed, text, caretOffset);
            if (index < 0)
            {
                return;
            }

            var actualElement = _currentDocument.Root.Element.DescendantsAndSelf().ElementAtOrDefault(index);
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

    /// <summary>Finds the document-order index of whichever element's start tag is closest at-or-before <paramref name="caretOffset"/> - i.e. the innermost element the caret is currently on/in, approximating by "most recently opened tag" since none of the MVP's controls have separate closing tags to bound the other end.</summary>
    /// <param name="reparsed">A line-info-annotated reparse of <paramref name="text"/>.</param>
    /// <param name="text">The text <paramref name="reparsed"/> was parsed from.</param>
    /// <param name="caretOffset">0-based character offset of the caret.</param>
    /// <returns>The zero-based document-order index of the enclosing element, or -1 if none starts at or before the caret.</returns>
    private static int EnclosingElementIndex(XDocument reparsed, string text, int caretOffset)
    {
        if (reparsed.Root is null)
        {
            return -1;
        }

        var bestIndex = -1;
        var bestOffset = -1;
        var index = 0;

        foreach (var element in reparsed.Root.DescendantsAndSelf())
        {
            if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
            {
                var elementOffset = OffsetOf(text, lineInfo.LineNumber, lineInfo.LinePosition);
                if (elementOffset <= caretOffset && elementOffset > bestOffset)
                {
                    bestOffset = elementOffset;
                    bestIndex = index;
                }
            }

            index++;
        }

        return bestIndex;
    }

    /// <summary>Converts a 1-based (line, column) position, as reported by <see cref="IXmlLineInfo"/>, to a 0-based character offset into <paramref name="text"/>.</summary>
    /// <param name="text">The text the position is within.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>The matching 0-based character offset, clamped to <paramref name="text"/>'s length.</returns>
    private static int OffsetOf(string text, int line, int column)
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

    /// <summary>Re-renders just the XAML source pane from the current document, without touching the design surface.</summary>
    private void RefreshXamlSourceView()
    {
        if (_currentDocument is not null)
        {
            SetXamlSourceText(_currentDocument.ToXamlString());
        }
    }

    /// <summary>Commits whatever's typed in the XAML source view when it loses focus (M6b two-way sync) - the same trigger a real text editor uses for "did the user finish this edit".</summary>
    private void XamlSourceView_LostFocus() => TryApplyXamlSourceEdit();

    /// <summary>
    /// Reformats the current document's XAML with consistent indentation
    /// (<see cref="XamlDocument.ToFormattedXamlString"/>) and commits it through the exact same
    /// pipeline any other source edit goes through - not a separate code path, so it's
    /// undo-tracked and re-validated for free. See research/29-format-document-plan.md.
    /// </summary>
    private void FormatDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null)
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
        // app-level document undo instead - see _undoStack); LoadText resets without touching it.
        XamlSourceView.LoadText(_currentDocument.ToFormattedXamlString(), autodetectTabsSpaces: false);
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
        if (_currentDocument is null)
        {
            return true;
        }

        // WinUI's TextBox normalizes line endings to '\r' internally, so reading .Text back can
        // legitimately differ from ToXamlString()'s '\r\n' (or whatever the source file used)
        // even when the user hasn't typed anything - comparing normalized copies avoids treating
        // that as a real edit (which would otherwise commit a no-op change and force a full
        // design-surface reload just from focusing and then leaving the text box).
        var typedText = XamlSourceView.Text;
        var currentText = _currentDocument.ToXamlString();
        if (NormalizeLineEndings(typedText) == NormalizeLineEndings(currentText))
        {
            SetXamlSourceErrors([]);
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
                    OffsetOf(typedText, xmlEx.LineNumber, xmlEx.LinePosition))
                : new XamlErrorListItem(string.Empty, string.Empty, CleanErrorMessage(ex.Message), null);
            SetXamlSourceErrors([item]);
            return false;
        }

        var parsedText = parsed.ToXamlString();

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
                var offset = line is int l ? OffsetOf(parsedText, l, 1) : (int?)null;
                errors.Add(new XamlErrorListItem(line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, string.Empty, CleanErrorMessage(status), offset));
            }

            SetXamlSourceErrors(errors);
            return false;
        }

        BeginUndoableChange();
        _currentDocument = parsed;
        CommitUndoableChange();
        RefreshDesignSurfaceFromDocument();
        SetXamlSourceErrors([]);
        return true;
    }

    /// <summary>
    /// The MVP control types this designer supports, for validating that a plain attribute in
    /// the XAML source names a real property or event on that control - see
    /// <see cref="FindUnknownPropertyErrors"/>. Deliberately the same set the toolbox offers
    /// (<see cref="AddControl"/>), not every WinUI control that could theoretically appear in
    /// hand-edited XAML.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> KnownControlTypes = new Dictionary<string, Type>
    {
        ["Page"] = typeof(Page),
        ["Canvas"] = typeof(Canvas),
        ["Grid"] = typeof(Grid),
        ["StackPanel"] = typeof(StackPanel),
        ["Button"] = typeof(Button),
        ["TextBlock"] = typeof(TextBlock),
        ["TextBox"] = typeof(TextBox),
        ["CheckBox"] = typeof(CheckBox),
        ["ComboBox"] = typeof(ComboBox),
        ["Image"] = typeof(Image),
    };

    /// <summary>True for an attribute that represents a plain CLR property/event in XAML's default namespace - excludes namespace declarations (xmlns:...), namespaced attributes (x:Name, mc:Ignorable, ...), and attached properties (e.g. "Canvas.Left", which have a '.' in the local name and aren't resolved via GetProperty/GetEvent on the element's own type).</summary>
    /// <param name="attribute">Attribute to check.</param>
    private static bool IsPlainAttribute(XAttribute attribute) =>
        !attribute.IsNamespaceDeclaration
        && attribute.Name.Namespace == XNamespace.None
        && !attribute.Name.LocalName.Contains('.');

    /// <summary>
    /// Finds every unknown-property/event attribute in the document by reflecting directly
    /// against each element's real CLR type (<see cref="KnownControlTypes"/>) - unlike
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
            if (!KnownControlTypes.TryGetValue(element.Name.LocalName, out var type))
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
                var offset = hasLineInfo ? OffsetOf(text, lineInfo!.LineNumber, lineInfo.LinePosition) : (int?)null;
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

    /// <summary>Call right before a mutation starts. Paired with <see cref="CommitUndoableChange"/>.</summary>
    private void BeginUndoableChange()
    {
        if (_currentDocument is not null)
        {
            _pendingUndoSnapshot = _currentDocument.ToXamlString();
        }
    }

    /// <summary>
    /// Call right after a mutation succeeds - pushes the pre-mutation snapshot captured by
    /// <see cref="BeginUndoableChange"/> onto the undo stack and clears redo (a fresh edit
    /// invalidates whatever redo history existed). Does nothing if Begin wasn't called first,
    /// so a validated-and-rejected edit (e.g. unparsable property input) never adds a no-op
    /// undo step.
    /// </summary>
    private void CommitUndoableChange()
    {
        if (_pendingUndoSnapshot is null)
        {
            return;
        }

        _undoStack.Push(_pendingUndoSnapshot);
        _redoStack.Clear();
        _pendingUndoSnapshot = null;
    }

    /// <summary>Restores the document to the top of the undo stack, pushing the current state onto redo first.</summary>
    private void Undo()
    {
        if (_undoStack.Count == 0 || _currentDocument is null)
        {
            return;
        }

        _redoStack.Push(_currentDocument.ToXamlString());
        _currentDocument = XamlDocument.Parse(_undoStack.Pop());
        RefreshDesignSurfaceFromDocument();
    }

    /// <summary>Restores the document to the top of the redo stack, pushing the current state onto undo first.</summary>
    private void Redo()
    {
        if (_redoStack.Count == 0 || _currentDocument is null)
        {
            return;
        }

        _undoStack.Push(_currentDocument.ToXamlString());
        _currentDocument = XamlDocument.Parse(_redoStack.Pop());
        RefreshDesignSurfaceFromDocument();
    }

    /// <summary>
    /// Sets the XAML source pane's text without touching the caret. Used to move the caret to
    /// the end of the text here, so a change that landed outside the currently-scrolled-into-view
    /// area (e.g. a newly added element, appended near the end) would still scroll into view -
    /// but on every full reload (e.g. opening a file) that instead landed on whichever element
    /// happened to be last in the document, and the source->design caret sync (#25) read that as
    /// "select the last element" (research/32-select-root-on-load.md). Removed for good in
    /// research/33-one-way-caret-sync.md, once design->source caret syncing (the thing this was
    /// originally for) was removed entirely at Fabrice's request.
    /// </summary>
    /// <param name="text">The XAML text to display.</param>
    private void SetXamlSourceText(string text)
    {
        // LoadText, not the Text property - see the comment in FormatDocumentButton_Click for why.
        XamlSourceView.LoadText(text, autodetectTabsSpaces: false);
        UpdateSaveButtonState();
    }

    /// <summary>Enabled only when the in-memory document differs from what's actually on disk (compared against the text as of the last load/save).</summary>
    private void UpdateSaveButtonState()
    {
        SaveButton.IsEnabled = _currentDocument is not null && _currentDocument.ToXamlString() != _lastSavedXaml;
    }

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
    /// below it in layout terms - see research/13-splitter-direction-cursor-undo.md for why this
    /// is needed for the design-surface/XAML-source splitter specifically but not the others.
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
    private sealed record PanelLayout(double ToolboxWidth, double FilePanelHeight, double XamlSourceHeight);

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
            var layout = new PanelLayout(ToolboxColumn.Width.Value, FilePanelRow.Height.Value, XamlSourceRow.Height.Value);
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
