using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIDesigner.Core;
using DesignElement = WinUIDesigner.Document.DesignElement;
using XamlDocument = WinUIDesigner.Document.XamlDocument;

namespace WinUIDesigner.App;

public sealed partial class MainWindow : Window
{
    private static readonly string SpikeLogPath = Path.Combine(Path.GetTempPath(), "WinUIDesigner", "m2-xamlreader-spike.log");
    private static readonly string SnapshotPath = Path.Combine(Path.GetTempPath(), "WinUIDesigner", "preview-snapshot.png");

    private const double MinElementSize = 8;
    private const double HandleSize = 7;

    private string? _currentFilePath;
    private XamlDocument? _currentDocument;
    private IReadOnlyDictionary<UIElement, DesignElement> _liveToDesign = new Dictionary<UIElement, DesignElement>();
    private UIElement? _selectedLiveElement;
    private DesignElement? _selectedDesignElement;

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

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinUI Designer";

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

        AutoLoadFirstSample();
    }

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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null || _currentFilePath is null)
        {
            return;
        }

        _currentDocument.Save(_currentFilePath);
    }

    private void AutoLoadFirstSample()
    {
        var samplesDir = FindSamplesDirectory();
        if (samplesDir is null)
        {
            return;
        }

        var path = Path.Combine(samplesDir, "SimplePage.xaml");
        if (File.Exists(path))
        {
            LoadFile(path);
        }
    }

    /// <summary>
    /// Dev-time convenience only: walks up from the executable's folder looking for the repo
    /// root (marked by WinUIDesigner.sln) so a sample loads automatically without needing the
    /// file picker. Real file opening always goes through <see cref="OpenButton_Click"/>.
    /// </summary>
    private static string? FindSamplesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "WinUIDesigner.sln")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private void LoadFile(string path)
    {
        try
        {
            var doc = XamlDocument.Load(path);
            _currentDocument = doc;
            _currentFilePath = path;
            CurrentFileText.Text = path;
            SaveButton.IsEnabled = true;

            RefreshDesignSurfaceFromDocument();
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
        XamlSourceView.Text = text;

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

    private static void WriteLog(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SpikeLogPath)!);
        File.AppendAllText(SpikeLogPath, contents + Environment.NewLine);
    }

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

        var name = GenerateUniqueName(localName);
        var child = canvasElement.AddChild(localName);
        child.Name = name;

        // Cascade new controls slightly so repeated adds don't land exactly on top of each other.
        var siblingCount = canvasElement.Children.Count();
        var offset = FormatLength(20 + ((siblingCount - 1) % 8 * 24));
        child.SetAttribute("Canvas.Left", offset);
        child.SetAttribute("Canvas.Top", offset);

        ApplyDefaultAttributes(child, localName);

        RefreshDesignSurfaceFromDocument();
        SelectByName(name);
    }

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

    private void SelectByName(string name)
    {
        var entry = _liveToDesign.FirstOrDefault(kvp => kvp.Value.Name == name);
        if (entry.Key is not null)
        {
            Select(entry.Key, entry.Value);
        }
    }

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

    private void DesignSurfaceHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_moveElement is not null && _moveDesignElement is not null)
        {
            _moveDesignElement.SetAttribute("Canvas.Left", FormatLength(GetCanvasLeft(_moveElement)));
            _moveDesignElement.SetAttribute("Canvas.Top", FormatLength(GetCanvasTop(_moveElement)));
            RefreshXamlSourceView();
        }

        _moveElement = null;
        _moveDesignElement = null;
        DesignSurfaceHost.ReleasePointerCapture(e.Pointer);
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedLiveElement is not FrameworkElement selected || _selectedDesignElement is null)
        {
            return;
        }

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

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeElement is FrameworkElement resizing && _resizeDesignElement is not null)
        {
            _resizeDesignElement.SetAttribute("Canvas.Left", FormatLength(GetCanvasLeft(resizing)));
            _resizeDesignElement.SetAttribute("Canvas.Top", FormatLength(GetCanvasTop(resizing)));
            _resizeDesignElement.SetAttribute("Width", FormatLength(resizing.Width));
            _resizeDesignElement.SetAttribute("Height", FormatLength(resizing.Height));
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

    private void BuildPropertyGrid(DesignElement designElement)
    {
        PropertyGridPanel.Children.Clear();

        foreach (var descriptor in PropertyGridSchema.GetProperties(designElement.LocalName))
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = descriptor.Name, FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray) });
            row.Children.Add(CreatePropertyEditor(designElement, descriptor));
            PropertyGridPanel.Children.Add(row);
        }
    }

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

        RefreshXamlSourceView();
        UpdateAdornerToMatch(_selectedLiveElement);
    }

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

    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    private static double GetCanvasLeft(UIElement element)
    {
        var value = Canvas.GetLeft(element);
        return double.IsNaN(value) ? 0 : value;
    }

    private static double GetCanvasTop(UIElement element)
    {
        var value = Canvas.GetTop(element);
        return double.IsNaN(value) ? 0 : value;
    }

    private static string FormatLength(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    private void RefreshXamlSourceView()
    {
        if (_currentDocument is not null)
        {
            XamlSourceView.Text = _currentDocument.ToXamlString();
        }
    }

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
