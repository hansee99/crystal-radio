namespace RadioPlayer.Services;

/// <summary>
/// Splits an ICY <c>StreamTitle</c> into artist and title.
///
/// <para>"Artist - Title" is only the most common convention, not the only one. A single session
/// on German stations (2026-08-03) threw away most of its songs because nothing else was
/// recognised — no artist meant <see cref="SongHistoryFilter"/> rejected them as idents:</para>
/// <code>
/// SWR3        The motto / Tiesto &amp; Ava Max              Title / Artist
/// SWR3        Hollywood hills / Sunrise Avenue          Title / Artist
/// Bayern 3    Taylor Swift: Anti-Hero                   Artist: Title
/// Radio Eins  "Departure" von Robin Kester              Title von Artist  (German "by")
/// </code>
///
/// <para><b>Slash and "von" put the title FIRST.</b> That isn't a guess — it's what those
/// stations actually broadcast, verified against the real acts (Sunrise Avenue is the band,
/// Hollywood Hills the song).</para>
///
/// <para>Deliberately conservative about what counts as a separator, because a false split
/// invents an artist and lets a station ident through as a song. Notably <c>|</c> is NOT one:
/// stations use it to append a show name ("Count Your Blessings | FM4 Steve Crilley bis 1"), so
/// splitting on it would turn every FM4 track into a fake artist. Where a false split does slip
/// through, <see cref="SongHistoryFilter"/>'s station-name rule is the backstop — an ident like
/// "SWR3 MOVE: Die Feierabendshow" parses to artist "SWR3 MOVE", which still carries the station's
/// own name and is rejected.</para>
/// </summary>
public static class IcyTitleParser
{
    /// <summary>A separator and which side of it the artist sits on.</summary>
    private readonly record struct Separator(string Text, bool ArtistFirst);

    // Order is precedence: " - " first because it's the overwhelmingly common case and a title
    // containing a slash or colon is far more likely than an artist containing " - ".
    private static readonly Separator[] Separators =
    [
        new(" - ", ArtistFirst: true),
        new(" – ", ArtistFirst: true),   // en dash
        new(" — ", ArtistFirst: true),   // em dash
        new(" / ", ArtistFirst: false),  // SWR3 and friends: Title / Artist
        new(" von ", ArtistFirst: false),// German "by"
        new(": ", ArtistFirst: true),    // Bayern 3: Artist: Title
    ];

    /// <summary>
    /// Returns the artist (null when the title carries no recognised separator) and the track
    /// title. A null artist is meaningful downstream: it's how idents, slogans and show names are
    /// told apart from songs, so this must not invent one.
    /// </summary>
    public static (string? Artist, string Title) Split(string streamTitle)
    {
        if (string.IsNullOrWhiteSpace(streamTitle))
            return (null, streamTitle ?? string.Empty);

        // Checked first because it is a far more specific shape than any separator below, and its
        // trailing fields can contain them.
        if (SplitDelimitedRecord(streamTitle) is { } record)
            return record;

        foreach (var sep in Separators)
        {
            var at = streamTitle.IndexOf(sep.Text, StringComparison.OrdinalIgnoreCase);
            if (at <= 0)
                continue; // not present, or the title starts with it — nothing to the left

            var left = streamTitle[..at].Trim();
            var right = streamTitle[(at + sep.Text.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0)
                continue; // "Artist - " and " - Title" are malformed, not a split

            return sep.ArtistFirst
                ? (left, Unquote(right))
                : (right, Unquote(left));
        }

        return (null, streamTitle.Trim());
    }

    /// <summary>
    /// Some playout systems put a whole tilde-delimited record in <c>StreamTitle</c> instead of a
    /// title. Virgin Radio Rockstar sends:
    /// <code>
    /// Not Now John~Pink Floyd~~1983~~287~2026-05-08T08:35:40~2026-05-08T08:35:43~United Music Pink Floyd~3.26~dcbd2283-…
    /// title      ~artist    ~ ~year~ ~dur~started            ~now                ~station           ~elapsed~id
    /// </code>
    ///
    /// <para>Two of those fields — "now" and elapsed seconds — change on <b>every</b> metadata push
    /// while the song plays on. Treating the payload as the title therefore reads as a new song
    /// every few seconds: in one 10-minute stretch on 2026-08-06 that produced 39 cuts, 34 of them
    /// rejected as too short, not a single completed song, and the station was then retired for
    /// producing nothing. Reading the first two fields makes the identity stable, which is what
    /// boundary detection actually depends on.</para>
    ///
    /// <para>Four separators minimum, so an ordinary title that happens to contain a tilde is left
    /// to the separator rules below.</para>
    /// </summary>
    private static (string? Artist, string Title)? SplitDelimitedRecord(string streamTitle)
    {
        var fields = streamTitle.Split(RecordSeparator);
        if (fields.Length < MinRecordFields)
            return null;

        var title = fields[0].Trim();
        if (title.Length == 0)
            return null;   // leading empty field — not the shape we know

        var artist = fields[1].Trim();
        return (artist.Length == 0 ? null : artist, Unquote(title));
    }

    private const char RecordSeparator = '~';

    /// <summary>Five fields — four separators — before a string is read as a record rather than a
    /// title. Real titles do not carry four tildes; the observed payload carries ten.</summary>
    private const int MinRecordFields = 5;

    /// <summary>Strips the quotes some stations wrap a title in — Radio Eins sends
    /// <c>"Departure" von Robin Kester</c>.</summary>
    private static string Unquote(string s)
    {
        if (s.Length < 2)
            return s;
        var first = s[0];
        var last = s[^1];
        var quoted = (first == '"' && last == '"')
                     || (first == '\'' && last == '\'')
                     || (first == '“' && last == '”')   // curly double
                     || (first == '„' && last == '“');  // German „…“
        return quoted ? s[1..^1].Trim() : s;
    }
}
