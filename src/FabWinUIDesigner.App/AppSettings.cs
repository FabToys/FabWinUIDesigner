using System.Text.Json;

namespace FabWinUIDesigner.App;

/// <summary>
/// The user's choices from Tools → Options, kept in <c>settings.json</c> under
/// <c>%LocalAppData%\FabWinUIDesigner</c>. Separate from <c>layout.json</c>, which holds window and
/// panel state the app saves on its own. Every default is the value the app used before these
/// were configurable, and a field missing from the file (an older file, or one edited by hand)
/// gets its default.
/// </summary>
/// <param name="GridSize">Spacing of the snap-to-grid dots, in pixels.</param>
/// <param name="ReopenTabsOnStart">Whether the tabs open at the last exit are reopened on start.</param>
/// <param name="PropertyMetadataPath">Full path of a custom property metadata file (Toolbox groups, default attributes, property lists), or null for the built-in one.</param>
/// <param name="EventMetadataPath">Full path of a custom event metadata file, or null for the built-in one.</param>
/// <param name="RecentListLength">How many entries Recent Files and Recent Folders each keep.</param>
/// <param name="XamlApplyDelayMs">How long after the last keystroke in the XAML editor the typed text is applied, in milliseconds.</param>
/// <param name="EditorFontSize">XAML editor font size.</param>
/// <param name="NewPageBackground">XAML colour (e.g. "White", "#FFF0F0F0") of the root Canvas of a page made with New File.</param>
/// <param name="EditorBackground">XAML colour of the XAML editor's background, or null for the editor's own default look.</param>
internal sealed record AppSettings(
    double GridSize = AppSettings.DefaultGridSize,
    bool ReopenTabsOnStart = true,
    string? PropertyMetadataPath = null,
    string? EventMetadataPath = null,
    int RecentListLength = AppSettings.DefaultRecentListLength,
    int XamlApplyDelayMs = AppSettings.DefaultXamlApplyDelayMs,
    int EditorFontSize = AppSettings.DefaultEditorFontSize,
    string NewPageBackground = AppSettings.DefaultNewPageBackground,
    string? EditorBackground = null)
{
    public const double DefaultGridSize = 8;
    public const int DefaultRecentListLength = 8;
    public const int DefaultXamlApplyDelayMs = 800;
    public const int DefaultEditorFontSize = 14;
    public const string DefaultNewPageBackground = "White";

    // Ranges enforced by the Options dialog, and applied again on load in case the file was edited by hand.
    public const double MinGridSize = 2, MaxGridSize = 100;
    public const int MinRecentListLength = 1, MaxRecentListLength = 30;
    public const int MinXamlApplyDelayMs = 100, MaxXamlApplyDelayMs = 5000;
    public const int MinEditorFontSize = 8, MaxEditorFontSize = 40;

    /// <summary>Full path of <c>settings.json</c>, shown in the Options dialog so it can be found for a backup.</summary>
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabWinUIDesigner", "settings.json");

    /// <summary>Reads <c>settings.json</c>, with out-of-range numbers pulled back into range; the defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) is { } loaded)
            {
                return loaded.Clamped();
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable - fall back to the defaults.
        }

        return new AppSettings();
    }

    /// <summary>Writes <c>settings.json</c>. Best-effort - a write failure is swallowed.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Best-effort - the settings just won't survive a restart.
        }
    }

    /// <summary>A copy with every number inside its allowed range, and empty strings read as "not set".</summary>
    private AppSettings Clamped() => this with
    {
        GridSize = Math.Clamp(GridSize, MinGridSize, MaxGridSize),
        RecentListLength = Math.Clamp(RecentListLength, MinRecentListLength, MaxRecentListLength),
        XamlApplyDelayMs = Math.Clamp(XamlApplyDelayMs, MinXamlApplyDelayMs, MaxXamlApplyDelayMs),
        EditorFontSize = Math.Clamp(EditorFontSize, MinEditorFontSize, MaxEditorFontSize),
        PropertyMetadataPath = string.IsNullOrWhiteSpace(PropertyMetadataPath) ? null : PropertyMetadataPath,
        EventMetadataPath = string.IsNullOrWhiteSpace(EventMetadataPath) ? null : EventMetadataPath,
        NewPageBackground = string.IsNullOrWhiteSpace(NewPageBackground) ? DefaultNewPageBackground : NewPageBackground,
        EditorBackground = string.IsNullOrWhiteSpace(EditorBackground) ? null : EditorBackground,
    };
}
