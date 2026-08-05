namespace RadioPlayer.Services;

/// <summary>
/// The voice DJ mode introduces tracks in. Set via <c>DjPersonality</c> in settings.json — a
/// file-only knob deliberately, so it can be changed between sessions while we find out which
/// ones are actually worth keeping.
/// </summary>
public enum DjPersonality
{
    /// <summary>Warm and a little playful — the original voice, and the default.</summary>
    Warm,
    /// <summary>Bright and energetic.</summary>
    Upbeat,
    /// <summary>Low, unhurried, small-hours.</summary>
    LateNight,
    /// <summary>Dry and lightly sardonic.</summary>
    Wry,
    /// <summary>Clean and understated; trusts the music.</summary>
    Professional
}

/// <summary>
/// Generates a short (1-2 sentence), tongue-in-cheek DJ-style intro line for the currently
/// playing DJ Mode track — the kind of on-air patter a real radio DJ might give before spinning
/// a song. Purely creative/tone-matching, not a facts lookup, so no web search is involved.
/// Applies uniformly to both curated-library and freshly-harvested songs (neither has a richer
/// "reason" available at the point this is called). Knows nothing about playback.
/// </summary>
/// <summary>A moment in a DJ session the DJ can say something about, other than a track.</summary>
public enum DjMoment
{
    /// <summary>Looking for stations — the very start, before anything is playing.</summary>
    Sourcing,
    /// <summary>Harvesting, nothing kept yet. The mix hasn't begun.</summary>
    Waiting,
    /// <summary>The mix ran dry, so live radio is covering the gap.</summary>
    Bridging,
    /// <summary>The session is ending.</summary>
    SigningOff
}

/// <summary>
/// Lines for the moments between tracks, all generated up front in one call.
///
/// Generated up front on purpose. These moments are transient — "waiting for the first songs" can
/// be true for twenty seconds — so calling the model on entering one would land the line after the
/// state had already changed, and the panel would sit there saying it was still waiting over a
/// playing mix. Pre-generating makes every transition instant for one extra call per session, and
/// the lines are still written for that vibe in that voice rather than being canned.
/// </summary>
public sealed record DjPatter(
    IReadOnlyList<string> Sourcing,
    IReadOnlyList<string> Waiting,
    IReadOnlyList<string> Bridging,
    IReadOnlyList<string> SigningOff)
{
    public IReadOnlyList<string> For(DjMoment moment) => moment switch
    {
        DjMoment.Sourcing => Sourcing,
        DjMoment.Waiting => Waiting,
        DjMoment.Bridging => Bridging,
        _ => SigningOff
    };
}

public interface IDjIntroService
{
    /// <summary>True when the service has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// A few lines for each between-tracks moment, in the configured voice and shaped around the
    /// vibe. Called once when a session starts; null if it couldn't be produced, in which case the
    /// caller falls back to its own wording.
    /// </summary>
    Task<DjPatter?> GetSessionPatterAsync(string? vibe, CancellationToken ct = default);

    /// <summary>
    /// Returns a short intro line for the track, or null if one couldn't be produced. Results are
    /// cached per session keyed by title+artist (a low-watermark library top-up can replay the
    /// same song within a session). Cancellable so a fast track change can abort an in-flight call.
    /// <paramref name="curatorNote"/> — the curator's own one-line reason for picking this track,
    /// when it came from a curated playlist; grounds the patter in the actual selection logic.
    ///
    /// <para><paramref name="album"/> and <paramref name="lyricExcerpt"/> come from LRCLIB and are
    /// absent about two thirds of the time (see <see cref="ILyricsService"/>). That is fine here and
    /// is why this is the right consumer for partial data: a per-track line is better when they are
    /// present and no worse when they aren't, with nothing stored and nothing compared. The same
    /// data is deliberately kept OUT of the embedding index, where uneven coverage would make
    /// cosine stop comparing like with like.</para>
    /// </summary>
    Task<string?> GetIntroAsync(string title, string? artist, string? vibe, string? curatorNote = null,
        string? album = null, string? lyricExcerpt = null, CancellationToken ct = default);
}
