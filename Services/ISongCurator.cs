namespace RadioPlayer.Services;

/// <summary>A song chosen for a curated playlist, with an optional one-line rationale.</summary>
public sealed record CuratedSong(string Path, string Title, string Artist, string? Reason);

/// <summary>
/// Turns a free-text prompt into an ordered playlist drawn from the local song library: semantic
/// recall over the library's embeddings, then an LLM arranges a sequence with a mood/energy arc
/// and a short reason per pick. Knows nothing about playback — it only selects and orders.
/// </summary>
public interface ISongCurator
{
    /// <summary>True when there is a library index to curate over (embeddings available).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Return up to <paramref name="max"/> library songs ordered as a playlist for the prompt.
    /// Falls back to semantic order (no reasons) when the LLM can't run, and to recency when
    /// there are no embeddings yet. Never throws; returns [] only when the library is empty.
    /// <paramref name="excludeKeys"/> — optional case-insensitive "Artist|Title" keys to leave
    /// out of recall entirely (songs the caller has already queued/played), so repeat calls
    /// reach deeper into the library instead of returning the same top matches again.
    /// <paramref name="requireRelevance"/> — return nothing rather than fall back to the closest
    /// available songs when none genuinely fit. DJ mode sets this: the library accumulates
    /// harvested songs from every past session, so "closest available" happily serves last
    /// week's happy hardcore into a deep-house set. An empty result is useful there — the caller
    /// bridges live radio instead, which beats playing the wrong thing.
    /// </summary>
    Task<IReadOnlyList<CuratedSong>> CurateAsync(string prompt, int max = 20,
        IReadOnlyCollection<string>? excludeKeys = null, bool requireRelevance = false,
        CancellationToken ct = default);
}
