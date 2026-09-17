using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
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
    }

    /// <summary>A blank single-Canvas Page, same shape as the sample fixtures minus x:Class (a brand-new file has no code-behind yet).</summary>
    private const string NewDocumentTemplate =
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\">\n" +
        "    <Canvas Width=\"400\" Height=\"300\" Background=\"White\" />\n" +
        "</Page>";

    /// <summary>Starts a blank document, confirming discard first if the current one has unsaved changes.</summary>
    private async void NewButton_Click(object sender, RoutedEventArgs e)
    {
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

        var doc = XamlDocument.Parse(NewDocumentTemplate);
        _currentDocument = doc;
        _currentFilePath = null;
        CurrentFileText.Text = "(no file open)";

        _undoStack.Clear();
        _redoStack.Clear();
        _pendingUndoSnapshot = null;
        _lastSavedXaml = doc.ToXamlString();

        RefreshDesignSurfaceFromDocument();
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
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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
        }

        WriteLog(log.ToString());
        return (null, "all attempts failed");
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

    /// <summary>Ends a move drag: commits the element's final position to the document (undo entry + XAML refresh) and releases pointer capture.</summary>
    private void DesignSurfaceHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_moveElement is not null && _moveDesignElement is not null)
        {
            _moveDesignElement.SetAttribute("Canvas.Left", FormatLength(GetCanvasLeft(_moveElement)));
            _moveDesignElement.SetAttribute("Canvas.Top", FormatLength(GetCanvasTop(_moveElement)));
            CommitUndoableChange();
            RefreshXamlSourceView();
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

    /// <summary>Ends a resize drag: commits the element's final position/size to the document (undo entry + XAML refresh) and releases pointer capture.</summary>
    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeElement is FrameworkElement resizing && _resizeDesignElement is not null)
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

        if (direction.Contains('W'))
        {
            width = Math.Max(MinElementSize, startWidth - deltaX);
            left = startLeft + (startWidth - width);
        }
        else if (direction.Contains('E'))
        {
            width = Math.Max(MinElementSize, startWidth + deltaX);
        }

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
    /// Escape deselects ("cancel"); Delete removes the selected control. Guarded against a
    /// TextBox having focus (e.g. editing a property value) so Delete there edits text as
    /// expected instead of deleting the whole control.
    /// </summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Also guards Ctrl+Z/Y here, not just Escape/Delete - a TextBox has its own built-in
        // undo for text edits, and intercepting Ctrl+Z at the window level while typing in a
        // property field would fight with that instead of undoing the field's own typing.
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            return;
        }

        var ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

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

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
                var textBox = new TextBox { Text = currentText };
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

        var bounds = liveElement.TransformToVisual(DesignSurfaceHost)
            .TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));

        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;

        Canvas.SetLeft(SelectionLabel, bounds.X);
        Canvas.SetTop(SelectionLabel, Math.Max(0, bounds.Y - 20));

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

    /// <summary>Re-renders just the XAML source pane from the current document, without touching the design surface.</summary>
    private void RefreshXamlSourceView()
    {
        if (_currentDocument is not null)
        {
            SetXamlSourceText(_currentDocument.ToXamlString());
        }
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
    /// Setting TextBox.Text from code doesn't move the caret/scroll position, so a change that
    /// lands outside the currently-scrolled-into-view area (e.g. a newly added element, which
    /// is appended near the end) can look like "nothing happened" even though the text really
    /// did update. Moving the caret to the end scrolls it into view.
    /// </summary>
    /// <param name="text">The XAML text to display.</param>
    private void SetXamlSourceText(string text)
    {
        XamlSourceView.Text = text;
        XamlSourceView.SelectionStart = text.Length;
        XamlSourceView.SelectionLength = 0;
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
