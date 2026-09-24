using System.Text.Json;

namespace FabWinUIDesigner.Core;

/// <summary>
/// Shared loader for the JSON metadata files behind <see cref="PropertyGridSchema"/> and
/// <see cref="EventGridSchema"/>. The built-in files are loose files next to the app's own .exe
/// (<see cref="AppContext.BaseDirectory"/>), not embedded, so editing a property/event/toolbox
/// entry needs no rebuild; the user can also point either schema at a file of their own.
/// </summary>
internal static class ControlMetadataLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Full path of a built-in metadata file, under the <c>Metadata\</c> folder next to the app's .exe.</summary>
    /// <param name="fileName">The file name, e.g. "PropertyMetadata.json".</param>
    public static string BuiltInPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Metadata", fileName);

    /// <summary>Loads and parses one metadata file.</summary>
    /// <typeparam name="T">The shape to deserialize into - a plain <c>Dictionary&lt;string, TDescriptor[]&gt;</c> for the Events file, or <see cref="PropertyGridSchema"/>'s richer envelope (Toolbox groups, default attributes, property lists) for the Properties file.</typeparam>
    /// <param name="path">Full path of the file.</param>
    /// <exception cref="InvalidDataException">The file is missing, unreadable, or not valid JSON for <typeparamref name="T"/>. The message names the file and the reason, ready to show to the user.</exception>
    public static T LoadFile<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"'{path}' not found.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new InvalidDataException($"'{path}' is empty (JSON null).");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException($"'{path}' couldn't be read: {ex.Message}", ex);
        }
    }
}
