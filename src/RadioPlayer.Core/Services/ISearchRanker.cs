namespace RadioPlayer.Services;

/// <summary>A candidate to be judged for relevance (id is its index in the caller's pool).</summary>
public sealed record RankCandidate(int Id, string Name, string Text, string? Country = null);

/// <summary>The ranker's verdict for one candidate.</summary>
public sealed record RankVerdict(int Id, double Score);

/// <summary>
/// Judges how well each candidate station's name + description actually matches the user's
/// prompt, so local (semantic) and web results can be ranked on one comparable signal and
/// weak matches dropped. Knows nothing about playback.
/// </summary>
public interface ISearchRanker
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the matching candidates ordered best-first (at most <paramref name="topK"/>),
    /// dropping ones that don't genuinely match. Returns an empty list when nothing matches,
    /// or null when the ranker couldn't run (caller should fall back).
    /// </summary>
    Task<IReadOnlyList<RankVerdict>?> RankAsync(string prompt, IReadOnlyList<RankCandidate> candidates,
        int topK, CancellationToken ct = default);
}
