namespace RadioPlayer.Services;

/// <summary>
/// What LRCLIB knows about one track. <see cref="Lyrics"/> is null for an instrumental or for a
/// record that exists without lyric text; <see cref="Album"/> is information the app has no other
/// source for, since ICY metadata carries only title and artist.
/// </summary>
/// <param name="Title">LRCLIB's canonical track name, not the ICY one we searched with.</param>
/// <param name="Artist">LRCLIB's canonical artist name.</param>
/// <param name="Album">Album name; present on every match observed, but treat as optional.</param>
/// <param name="Instrumental">
/// True when the track legitimately has no words. Distinct from a miss: "this song has no lyrics"
/// and "we couldn't find this song" are different answers and the UI says so differently.
/// </param>
/// <param name="Lyrics">Plain (unsynced) lyric text, or null.</param>
/// <param name="DurationSeconds">LRCLIB's canonical duration — the full track, which for a
/// harvested segment is NOT what we recorded.</param>
public sealed record TrackLyrics(
    string Title,
    string Artist,
    string? Album,
    bool Instrumental,
    string? Lyrics,
    double DurationSeconds)
{
    /// <summary>A short opening excerpt, for prompts that want a hook to reference without paying
    /// for — or storing — the whole text.</summary>
    public string? Excerpt(int maxChars = 400)
    {
        if (string.IsNullOrWhiteSpace(Lyrics)) return null;
        var text = Lyrics.Trim();
        if (text.Length <= maxChars) return text;

        var limit = Math.Min(maxChars, text.Length - 1);

        // Prefer a line break — an excerpt that stops at the end of a line reads as a quote.
        var line = text.LastIndexOf('\n', limit);
        if (line > 40) return text[..line].TrimEnd();

        // Otherwise a word boundary. The first version hard-cut at maxChars here and produced
        // "...Wipe away the teardro" — precisely the mid-word cut this promises not to make.
        var word = text.LastIndexOf(' ', limit);
        return (word > 20 ? text[..word] : text[..maxChars]).TrimEnd();
    }
}

/// <summary>
/// Looks up lyrics and track metadata from LRCLIB. Knows nothing about playback, the library, or
/// the LLM services that consume it.
///
/// <para><b>Coverage is partial and that is load-bearing.</b> Measured against this app's own
/// library, LRCLIB matched 37% of harvested artist/title pairs — it skews mainstream, and internet
/// radio harvesting surfaces a lot of obscure indie and electronic. Every consumer must treat a
/// miss as ordinary. In particular this is deliberately NOT wired into the embedding index:
/// enriching a third of rows with lyrics while the rest stay title-only would make the vector space
/// bimodal, and cosine would stop comparing like with like.</para>
/// </summary>
public interface ILyricsService
{
    /// <summary>
    /// Finds a track, or returns null if LRCLIB doesn't have it or the lookup failed. Never throws
    /// for an ordinary miss, a transport error or a rate limit.
    /// </summary>
    /// <param name="durationSeconds">
    /// Canonical track length, when known. LRCLIB matches within ±2 s, so this sharpens a lookup
    /// considerably — but pass it ONLY for a full file. A harvested segment has been edge-trimmed
    /// and its length is not the track's, so sending it turns a hit into a miss.
    /// </param>
    Task<TrackLyrics?> LookupAsync(string? artist, string? title,
        double? durationSeconds = null, CancellationToken ct = default);

    /// <summary>
    /// Search on the title alone and return the rows LRCLIB offers, best-first.
    /// <para>
    /// Exists for metadata repair (#31), where the artist is the damaged field and cannot be part of
    /// the query: searching "M?tley Cr?e" + "Looks That Kill" returns <b>zero</b> rows, while the
    /// title alone returns twenty with the real spelling among them. Callers are expected to pick
    /// with <see cref="MetadataRepair.ChooseRepair"/> rather than trusting the order.
    /// </para>
    /// Never throws for a miss, a transport error or a rate limit — returns an empty list.
    /// </summary>
    Task<IReadOnlyList<TrackLyrics>> SearchByTitleAsync(string? title, CancellationToken ct = default);
}
