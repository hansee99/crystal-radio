using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Loads and saves the user's station list as JSON under
/// %AppData%\RadioPlayer\stations.json. Falls back to a seeded default list when the
/// file is missing or unreadable.
/// </summary>
public sealed class StationStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioPlayer");
    private static readonly string FilePath = Path.Combine(Dir, "stations.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } // store Format as "Aac" etc.
    };

    public List<Station> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<Station>>(json, Options);
                if (list is { Count: > 0 })
                    return list;
            }
        }
        catch
        {
            // Corrupt/unreadable file — fall back to defaults rather than crash.
        }
        return Defaults();
    }

    public void Save(IEnumerable<Station> stations)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(stations, Options));
        }
        catch
        {
            // Best effort; a failed save shouldn't take down playback.
        }
    }

    private static List<Station> Defaults() =>
    [
        new("Radio Paradise (AAC)", "http://stream.radioparadise.com/aac-128", StreamFormat.Aac),
        // A plain MP3 stream to exercise the non-AAC Bass.CreateStream path.
        new("SomaFM Groove Salad (MP3)", "http://ice1.somafm.com/groovesalad-128-mp3", StreamFormat.Mp3),
    ];
}
