using System.IO;
using System.Text.Json;

namespace RadioPlayer.Services;

/// <summary>Persisted application settings.</summary>
public sealed class AppSettings
{
    /// <summary>Output volume, 0.0–1.0.</summary>
    public double Volume { get; set; } = 0.5;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under
/// %AppData%\RadioPlayer\settings.json. Best-effort: failures fall back to defaults
/// rather than disrupting playback.
/// </summary>
public sealed class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioPlayer");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    settings.Volume = Math.Clamp(settings.Volume, 0.0, 1.0);
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best effort.
        }
    }
}
