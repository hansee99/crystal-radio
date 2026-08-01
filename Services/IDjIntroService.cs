namespace RadioPlayer.Services;

/// <summary>
/// Generates a short (1-2 sentence), tongue-in-cheek DJ-style intro line for the currently
/// playing DJ Mode track — the kind of on-air patter a real radio DJ might give before spinning
/// a song. Purely creative/tone-matching, not a facts lookup, so no web search is involved.
/// Applies uniformly to both curated-library and freshly-harvested songs (neither has a richer
/// "reason" available at the point this is called). Knows nothing about playback.
/// </summary>
public interface IDjIntroService
{
    /// <summary>True when the service has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a short intro line for the track, or null if one couldn't be produced. Results are
    /// cached per session keyed by title+artist (a low-watermark library top-up can replay the
    /// same song within a session). Cancellable so a fast track change can abort an in-flight call.
    /// <paramref name="curatorNote"/> — the curator's own one-line reason for picking this track,
    /// when it came from a curated playlist; grounds the patter in the actual selection logic.
    /// </summary>
    Task<string?> GetIntroAsync(string title, string? artist, string? vibe, string? curatorNote = null,
        CancellationToken ct = default);
}
