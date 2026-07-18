namespace RadioPlayer.Services;

/// <summary>
/// Heuristics that decide whether an ICY title change looks like an actual song (vs. an
/// ad, jingle, or station ident) before it is recorded in the song history. Deliberately
/// conservative: when in doubt, keep the entry — a stray jingle in the list is a smaller
/// failure than silently dropping real songs.
/// </summary>
public static class SongHistoryFilter
{
    // Lower-case markers that flag obvious non-song content in either field.
    private static readonly string[] AdMarkers =
        ["advert", "commercial", "jingle", "werbung", "sponsored"];

    public static bool IsLikelySong(string? title, string? artist, string? stationName)
    {
        // Real songs arrive as "Artist - Title"; idents/slogans usually have no artist part.
        // (The initial per-connect station-info publish also has a null artist, so it's
        // filtered here by design.)
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return false;

        // Some streams push URLs as titles during breaks.
        if (title.Contains("http", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var marker in AdMarkers)
        {
            if (title.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                artist.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Jingles often carry the station's own name ("Radio Paradise - commercial free…").
        // Only the full station name is matched — partial overlaps (a band that shares a
        // word with the station) stay in.
        if (!string.IsNullOrWhiteSpace(stationName) &&
            (title.Contains(stationName, StringComparison.OrdinalIgnoreCase) ||
             artist.Contains(stationName, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }
}
