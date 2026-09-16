using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    private XamlDocument? _currentDocument;
    private IReadOnlyDictionary<UIElement, DesignElement> _liveToDesign = new Dictionary<UIElement, DesignElement>();
    private UIElement? _selectedLiveElement;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinUI Designer";

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
            CurrentFileText.Text = path;

            var text = doc.ToXamlString();
            XamlSourceView.Text = text;

            var (root, status) = RenderPreview(text);
            DesignSurfaceHost.Child = root ?? new TextBlock
            {
                Text = $"Preview failed ({status}). See {SpikeLogPath}",
                Foreground = new SolidColorBrush(Colors.Red),
                TextWrapping = TextWrapping.Wrap,
            };

            _liveToDesign = root is not null
                ? LiveTreeCorrelator.Correlate(root, doc.Root)
                : new Dictionary<UIElement, DesignElement>();
            ClearSelection();

            DispatcherQueue.TryEnqueue(async () =>
            {
                DesignSurfaceHost.UpdateLayout();
                await SaveSnapshotAsync(DesignSurfaceHost, SnapshotPath);
            });
        }
        catch (Exception ex)
        {
            CurrentFileText.Text = $"Failed to open: {ex.Message}";
        }
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

    private void DesignSurfaceHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var point = e.GetPosition(DesignSurfaceHost);
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, DesignSurfaceHost);

        foreach (var hit in hits)
        {
            if (hit is UIElement hitElement && _liveToDesign.TryGetValue(hitElement, out var designElement))
            {
                Select(hitElement, designElement);
                return;
            }
        }

        ClearSelection();
    }

    private void Select(UIElement liveElement, DesignElement designElement)
    {
        if (liveElement is not FrameworkElement frameworkElement)
        {
            ClearSelection();
            return;
        }

        _selectedLiveElement = liveElement;

        var bounds = liveElement.TransformToVisual(DesignSurfaceHost)
            .TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));

        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;
        SelectionRectangle.Visibility = Visibility.Visible;

        var label = designElement.Name is { Length: > 0 } name
            ? $"{designElement.LocalName} ({name})"
            : designElement.LocalName;
        SelectionLabelText.Text = label;
        Canvas.SetLeft(SelectionLabel, bounds.X);
        Canvas.SetTop(SelectionLabel, Math.Max(0, bounds.Y - 20));
        SelectionLabel.Visibility = Visibility.Visible;

        SelectionSummaryText.Text = label;
    }

    private void ClearSelection()
    {
        _selectedLiveElement = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionLabel.Visibility = Visibility.Collapsed;
        SelectionSummaryText.Text = "(no selection)";
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
