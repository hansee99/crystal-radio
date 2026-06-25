using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A semantically-matched station with its cosine similarity score.</summary>
public sealed record SemanticResult(Station Station, double Score, string? Description);

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
}
