using System.Text.Json;

namespace FabWinUIDesigner.Core;

/// <summary>
/// Shared loader for the JSON metadata files behind <see cref="PropertyGridSchema"/> and
/// <see cref="EventGridSchema"/> (research/46/47) - a loose file next to the app's own .exe
/// (<see cref="AppContext.BaseDirectory"/>), not embedded, so Fabrice can edit a property/event/
/// toolbox entry and just restart the app, no rebuild.
/// </summary>
internal static class ControlMetadataLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads and parses one metadata file from the app's own directory.</summary>
    /// <typeparam name="T">The shape to deserialize into - a plain <c>Dictionary&lt;string, TDescriptor[]&gt;</c> for the Events file, or <see cref="PropertyGridSchema"/>'s richer envelope (adds the Toolbox's control order) for the Properties file.</typeparam>
    /// <param name="fileName">The file name under the <c>Metadata\</c> folder, e.g. "PropertyMetadata.json".</param>
    public static T Load<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Metadata", fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Metadata file '{path}' not found - check FabWinUIDesigner.Core.csproj still " +
                "copies Metadata\\*.json to the output directory.");
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidOperationException($"Metadata file '{path}' parsed to null.");
    }
}
