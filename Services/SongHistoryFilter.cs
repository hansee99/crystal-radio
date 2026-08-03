using System.Text.RegularExpressions;

namespace RadioPlayer.Services;

/// <summary>
/// Heuristics that decide whether an ICY title change looks like an actual song (vs. an
/// ad, jingle, or station ident) before it is recorded in the song history. Deliberately
/// conservative: when in doubt, keep the entry — a stray jingle in the list is a smaller
/// failure than silently dropping real songs.
///
/// <para><b>Why this carries real weight in DJ mode.</b> It is not merely tidying a list: for
/// harvested segments it is the ONLY gate that can catch a news bulletin or ad break, because
/// the acoustic detector demonstrably cannot. Measured on one session's own audio (2026-08-03):
/// Ö3's "Nachrichten, Wetter und Verkehr" — five minutes of pure speech — scored 3% music, and
/// Ace of Base's "The Sign" scored 3% too. Same number, opposite content. No threshold on the
/// detector separates those, so when a station labels a segment as itself, that label is the
/// only usable evidence and it must not be missed.</para>
/// </summary>
public static partial class SongHistoryFilter
{
    // Lower-case markers that flag obvious non-song content in either field. "adbreak" and the
    // announcement phrases come from real segments that reached a mix: a station's playout system
    // announces its own ad break in the StreamTitle, and none of the original markers matched.
    private static readonly string[] AdMarkers =
    [
        "advert", "commercial", "jingle", "werbung", "sponsored",
        "adbreak", "ad break", "will continue after", "right back after",
        "station identification"
    ];

    /// <summary>
    /// A playout-system cart ID: uppercase letters, an underscore, then a timestamp or cart
    /// number — <c>ADBREAK_120000</c>, <c>ADWTAG_122000</c>. These are internal scheduling labels
    /// that some stations leak into StreamTitle where a track name belongs.
    ///
    /// Deliberately requires UPPERCASE and four or more digits. A song can plausibly be titled
    /// "Extended_2024"; nothing is plausibly titled "PROMO_143000". Precision matters more than
    /// recall here because a false positive throws away a real song.
    /// </summary>
    [GeneratedRegex(@"\b[A-Z]{3,}_\d{4,}\b")]
    private static partial Regex PlayoutCartRegex();

    /// <summary>
    /// Shortest normalized name allowed to match a station by containment. Guards the
    /// abbreviation rule below from firing on a couple of incidental characters.
    /// </summary>
    private const int MinIdentLength = 6;

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

        if (PlayoutCartRegex().IsMatch(title) || PlayoutCartRegex().IsMatch(artist))
            return false;

        if (string.IsNullOrWhiteSpace(stationName))
            return true;

        // Jingles often carry the station's own name ("Radio Paradise - commercial free…").
        // Only the full station name is matched — partial overlaps (a band that shares a word
        // with the station) stay in.
        if (title.Contains(stationName, StringComparison.OrdinalIgnoreCase) ||
            artist.Contains(stationName, StringComparison.OrdinalIgnoreCase))
            return false;

        // The same idea in the OTHER direction, which is how Ö3's news and livestream idents got
        // through: the station announced artist "HITRADIO Ö3" while the directory calls it
        // "ORF Hitradio Ö3", so the artist was a SUBSET of the station name and the check above
        // could never fire. Stations abbreviate, drop a broadcaster prefix, or shout in caps;
        // comparing on letters and digits alone absorbs all of that.
        //
        // Applied to the artist only. A station identifying ITSELF in the artist slot is strong
        // evidence of an ident; a title that happens to sit inside the station's name is not
        // ("Paradise" on Radio Paradise is a perfectly plausible song).
        if (IsStationIdentifyingItself(artist, stationName))
            return false;

        return true;
    }

    /// <summary>
    /// Whether <paramref name="artist"/> is really the station naming itself, allowing for
    /// abbreviation and decoration on either side.
    ///
    /// The length floor is what keeps this honest: without it any station whose name contains a
    /// short artist name would swallow it. It does still mean an artist whose name is a long
    /// substring of the station's gets dropped — a genre-named station is the plausible case. That
    /// trade is deliberate. A wrongly dropped song costs one track and is recorded in the session
    /// log as skipped/not-song-like; a wrongly kept news bulletin costs five minutes of speech in
    /// a music mix, and nothing downstream can catch it.
    /// </summary>
    private static bool IsStationIdentifyingItself(string artist, string stationName)
    {
        var a = Normalize(artist);
        var s = Normalize(stationName);
        return a.Length >= MinIdentLength && s.Length >= MinIdentLength && s.Contains(a);
    }

    /// <summary>The same identity key the harvest pool compares stations with — letters and digits
    /// only, with codec/bitrate/quality decoration stripped — so "ORF Hitradio Ö3 | HQ" and
    /// "HITRADIO Ö3" reduce to comparable forms.</summary>
    private static string Normalize(string s) => StationNameFormatter.IdentityKey(s);
}
