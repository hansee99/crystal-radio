using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A semantically-matched station with its cosine similarity score.</summary>
public sealed record SemanticResult(Station Station, double Score, string? Description, string? Country = null);

/// <summary>
/// Local semantic retrieval over the Phase 1 enriched descriptions: embed the query with the
/// same model used for documents, cosine top-K over the in-memory vector index, then resolve
/// the hits to playable stations. No playback knowledge.
/// </summary>
public interface ISemanticSearchService
{
    /// <summary>False when the embedding model isn't available (callers fall back to Pattern B).</summary>
    bool IsAvailable { get; }

    /// <summary>Top-K semantically similar, playable stations, best score first.</summary>
    Task<IReadOnlyList<SemanticResult>> SearchAsync(string query, int k = 10, CancellationToken ct = default);

    /// <summary>
    /// Top-K from the local catalog <b>alone</b> — no directory call anywhere in the path (#26).
    /// For when the Radio Browser mirrors are unreachable and the normal
    /// <see cref="SearchAsync"/> can't resolve its own hits.
    /// <para>
    /// Unlike <see cref="SearchAsync"/> this applies a hard score floor: it drops everything below
    /// <paramref name="minScore"/> instead of returning the least-bad matches. On a thin catalog
    /// cosine top-K will happily hand back a polka station for "electronic music for coding", and
    /// an empty answer is the correct one there. Returns stations built from cached fields, so the
    /// urls are as fresh as the last time each station was seen — not re-resolved, by definition.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SemanticResult>> SearchOfflineAsync(string query, int k = 10,
        double minScore = SemanticSearchService.DefaultOfflineFloor, CancellationToken ct = default);
}
