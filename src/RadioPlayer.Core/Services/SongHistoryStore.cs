using System.IO;
using System.Text.Json;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Loads and saves the song history (newest first) as JSON under
/// %AppData%\RadioPlayer\history.json. Best-effort like the other stores — a failed
/// read/write must never affect playback.
/// </summary>
public sealed class SongHistoryStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioPlayer");
    private static readonly string FilePath = Path.Combine(Dir, "history.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<SongHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<SongHistoryEntry>>(json, Options);
                if (list is not null)
                    return list;
            }
        }
        catch
        {
            // Corrupt/unreadable file — start with an empty history rather than crash.
        }
        return [];
    }

    public void Save(IEnumerable<SongHistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, Options));
        }
        catch
        {
            // Best effort; a failed save shouldn't take down playback.
        }
    }
}
