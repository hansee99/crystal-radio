namespace RadioPlayer.Services;

/// <summary>
/// An AI-generated briefing about a now-playing track: a short paragraph about the song, a
/// short artist bio, and a few "notable" trivia bullets. Produced by <see cref="ITrackInfoService"/>.
/// </summary>
public sealed record TrackInfo(string Song, string Artist, IReadOnlyList<string> Notable);

/// <summary>
/// Generates a short, web-sourced briefing about the currently playing track (song facts +
/// artist context + trivia). Online-only; uses an LLM with server-side web search across varied
/// sources. Knows nothing about playback.
/// </summary>
public interface ITrackInfoService
{
    /// <summary>True when the service has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a brief about the given track, or null if one couldn't be produced (transport
    /// error, unparseable output, or the track couldn't be confidently identified). Results are
    /// cached per session keyed by title+artist; <paramref name="forceRefresh"/> bypasses the
    /// cache and overwrites it. Cancellable so the caller can abort an in-flight request.
    /// </summary>
    Task<TrackInfo?> GetTrackInfoAsync(string title, string? artist, string? station,
        bool forceRefresh = false, CancellationToken ct = default);
}
