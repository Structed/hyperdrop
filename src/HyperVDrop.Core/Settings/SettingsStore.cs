using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperVDrop.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under <c>%LOCALAPPDATA%\HyperVDrop</c>.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperVDrop",
            "settings.json");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Reads settings, falling back to defaults when the file is missing, unreadable, or corrupt.
    /// Preferences are not worth failing a launch over.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes settings via a temporary file so an interrupted save cannot leave a truncated file
    /// behind.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var folder = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var temporaryPath = _filePath + ".tmp";

            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing preferences is not worth interrupting the user's transfer.
        }
    }
}
