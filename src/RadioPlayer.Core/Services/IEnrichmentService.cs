namespace RadioPlayer.Services;

/// <summary>
/// Owns the fetch → extract → distill → cache enrichment pipeline. Best-effort and
/// non-blocking: it must never break search or playback. Knows nothing about playback or
/// the search loop.
/// </summary>
public interface IEnrichmentService
{
    /// <summary>
    /// Lazily enrich any candidates not already cached/fresh. Fire-and-forget — returns
    /// immediately and never throws; failures degrade silently to tag-based behavior.
    /// </summary>
    void EnrichInBackground(IEnumerable<StationCandidate> candidates);

    /// <summary>Synchronously read a cached description, if one exists. Null otherwise.</summary>
    EnrichmentRecord? GetCached(string stationUuid);

    /// <summary>
    /// One-time/background pass that embeds already-enriched rows missing a current-model
    /// vector. Fire-and-forget; safe to call at startup.
    /// </summary>
    void BackfillEmbeddingsInBackground();
}
