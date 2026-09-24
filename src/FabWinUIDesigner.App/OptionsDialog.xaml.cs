using FabWinUIDesigner.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;

namespace FabWinUIDesigner.App;

/// <summary>
/// Tools → Options: edits a copy of the <see cref="AppSettings"/> (plus snap-to-grid, which lives
/// in the panel layout). OK checks every value first - including loading any custom metadata file
/// - and keeps the dialog open with the problem shown if one is wrong, so the window only ever
/// receives settings it can apply.
/// </summary>
internal sealed partial class OptionsDialog : ContentDialog
{
    private readonly nint _windowHandle;

    /// <summary>Fills the dialog from the current settings.</summary>
    /// <param name="settings">The settings in use.</param>
    /// <param name="snapToGrid">Whether snap-to-grid is on.</param>
    /// <param name="windowHandle">The main window's handle, which the file picker needs in an unpackaged app.</param>
    public OptionsDialog(AppSettings settings, bool snapToGrid, nint windowHandle)
    {
        InitializeComponent();
        _windowHandle = windowHandle;
        Result = settings;
        SnapToGrid = snapToGrid;

        SetUpNumberBox(GridSizeBox, settings.GridSize, AppSettings.MinGridSize, AppSettings.MaxGridSize);
        SetUpNumberBox(EditorFontSizeBox, settings.EditorFontSize, AppSettings.MinEditorFontSize, AppSettings.MaxEditorFontSize);
        SetUpNumberBox(XamlApplyDelayBox, settings.XamlApplyDelayMs, AppSettings.MinXamlApplyDelayMs, AppSettings.MaxXamlApplyDelayMs);
        SetUpNumberBox(RecentListLengthBox, settings.RecentListLength, AppSettings.MinRecentListLength, AppSettings.MaxRecentListLength);

        SnapToGridCheckBox.IsChecked = snapToGrid;
        ReopenTabsCheckBox.IsChecked = settings.ReopenTabsOnStart;

        NewPageBackgroundBox.Text = settings.NewPageBackground;
        EditorBackgroundBox.Text = settings.EditorBackground ?? string.Empty;
        AttachColorPicker(NewPageBackgroundPickerButton, NewPageBackgroundBox);
        AttachColorPicker(EditorBackgroundPickerButton, EditorBackgroundBox);

        PropertyMetadataBox.Text = settings.PropertyMetadataPath ?? string.Empty;
        PropertyMetadataBox.PlaceholderText = $"Built-in ({PropertyGridSchema.BuiltInFileName})";
        EventMetadataBox.Text = settings.EventMetadataPath ?? string.Empty;
        EventMetadataBox.PlaceholderText = $"Built-in ({EventGridSchema.BuiltInFileName})";

        SettingsPathText.Text = AppSettings.SettingsPath;
    }

    /// <summary>Opens the settings folder in File Explorer, with settings.json selected when it exists yet.</summary>
    private void OpenSettingsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = AppSettings.SettingsPath;
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.Diagnostics.Process.Start("explorer.exe", arguments);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = $"Couldn't open the folder: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The settings as edited - only meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
    public AppSettings Result { get; private set; }

    /// <summary>Snap-to-grid as edited - only meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
    public bool SnapToGrid { get; private set; }

    private static void SetUpNumberBox(NumberBox box, double value, double minimum, double maximum)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Value = value;
    }

    /// <summary>
    /// Gives a colour row's Pick... button a <see cref="ColorPicker"/> flyout, opened on the box's
    /// current colour; picking writes the colour into the box as hex, so the box stays the one
    /// place the value lives.
    /// </summary>
    private static void AttachColorPicker(DropDownButton button, TextBox box)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = true,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
        };
        var flyout = new Flyout { Content = picker };
        flyout.Opening += (_, _) =>
        {
            if (PropertyValueConverter.TryParseColor(box.Text, out var color))
            {
                picker.Color = color;
            }
        };
        picker.ColorChanged += (_, args) =>
        {
            // Only while the user is picking - setting picker.Color in Opening above also raises
            // ColorChanged, and must not overwrite a named colour like "White" with its hex.
            if (flyout.IsOpen)
            {
                box.Text = FormatColor(args.NewColor);
            }
        };
        button.Flyout = flyout;
    }

    /// <summary>XAML hex text for a colour: <c>#RRGGBB</c>, or <c>#AARRGGBB</c> when it isn't fully opaque.</summary>
    private static string FormatColor(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Keeps the swatch next to a colour box showing the typed colour (empty when it doesn't parse yet).</summary>
    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var swatch = ReferenceEquals(box, NewPageBackgroundBox) ? NewPageBackgroundSwatch : EditorBackgroundSwatch;
        swatch.Background = PropertyValueConverter.TryParseColor(box.Text, out var color) ? new SolidColorBrush(color) : null;
    }

    /// <summary>Browse... next to a metadata path: picks a JSON file into the box named by the button's <c>Tag</c>.</summary>
    private async void BrowseMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox box })
        {
            return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            box.Text = file.Path;
        }
    }

    /// <summary>OK: checks every value; on the first problem shows it and keeps the dialog open, otherwise stores <see cref="Result"/>/<see cref="SnapToGrid"/>.</summary>
    private void OptionsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var error = TryBuildResult(out var result);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        Result = result!;
        SnapToGrid = SnapToGridCheckBox.IsChecked == true;
    }

    /// <summary>Reads and checks every field.</summary>
    /// <param name="result">The new settings, or null when something is wrong.</param>
    /// <returns>Null when everything is valid; otherwise what's wrong, for the user.</returns>
    private string? TryBuildResult(out AppSettings? result)
    {
        result = null;

        // A NumberBox reads NaN when emptied; Minimum/Maximum only clamp what was typed.
        foreach (var (box, label) in new[] { (GridSizeBox, "Grid size"), (EditorFontSizeBox, "Font size"), (XamlApplyDelayBox, "Apply delay"), (RecentListLengthBox, "Recent entries") })
        {
            if (double.IsNaN(box.Value))
            {
                return $"{label} needs a value.";
            }
        }

        var newPageBackground = NewPageBackgroundBox.Text.Trim();
        if (!PropertyValueConverter.TryParseColor(newPageBackground, out _))
        {
            return $"New page background: \"{newPageBackground}\" isn't a colour. Use a name such as White, or hex such as #F0F0F0.";
        }

        var editorBackground = EditorBackgroundBox.Text.Trim();
        if (editorBackground.Length > 0 && !PropertyValueConverter.TryParseColor(editorBackground, out _))
        {
            return $"Editor background: \"{editorBackground}\" isn't a colour. Use a name such as White, hex such as #1E1E1E, or leave it empty for the default.";
        }

        var propertyMetadataPath = CheckMetadataPath(PropertyMetadataBox.Text, PropertyGridSchema.Validate, out var propertyError);
        if (propertyError is not null)
        {
            return $"Controls and properties file: {propertyError}";
        }

        var eventMetadataPath = CheckMetadataPath(EventMetadataBox.Text, EventGridSchema.Validate, out var eventError);
        if (eventError is not null)
        {
            return $"Events file: {eventError}";
        }

        result = Result with
        {
            GridSize = Math.Clamp(GridSizeBox.Value, AppSettings.MinGridSize, AppSettings.MaxGridSize),
            ReopenTabsOnStart = ReopenTabsCheckBox.IsChecked == true,
            PropertyMetadataPath = propertyMetadataPath,
            EventMetadataPath = eventMetadataPath,
            RecentListLength = (int)Math.Clamp(Math.Round(RecentListLengthBox.Value), AppSettings.MinRecentListLength, AppSettings.MaxRecentListLength),
            XamlApplyDelayMs = (int)Math.Clamp(Math.Round(XamlApplyDelayBox.Value), AppSettings.MinXamlApplyDelayMs, AppSettings.MaxXamlApplyDelayMs),
            EditorFontSize = (int)Math.Clamp(Math.Round(EditorFontSizeBox.Value), AppSettings.MinEditorFontSize, AppSettings.MaxEditorFontSize),
            NewPageBackground = newPageBackground,
            EditorBackground = editorBackground.Length > 0 ? editorBackground : null,
        };
        return null;
    }

    /// <summary>Checks a metadata path box: empty means the built-in file; otherwise it must be a full path to a file that loads.</summary>
    /// <param name="text">The box's text.</param>
    /// <param name="validate">The schema's Validate method, which loads the file and throws if it's unusable.</param>
    /// <param name="error">What's wrong, or null.</param>
    /// <returns>The path to store (null = built-in).</returns>
    private static string? CheckMetadataPath(string text, Action<string> validate, out string? error)
    {
        error = null;
        var path = text.Trim().Trim('"');
        if (path.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            error = "use a full path, e.g. C:\\Metadata\\MyControls.json.";
            return null;
        }

        try
        {
            validate(path);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
        }

        return path;
    }
}
