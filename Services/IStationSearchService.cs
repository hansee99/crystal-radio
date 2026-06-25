using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Queries a station directory and returns only stations the BASS engine can play.
/// Knows nothing about LLMs.
/// </summary>
public interface IStationSearchService
{
    Task<IReadOnlyList<Station>> SearchAsync(StationSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Same filtering as <see cref="SearchAsync"/>, but returns candidates carrying their
    /// Radio Browser <c>stationuuid</c> — used by the Pattern B agentic tool.
    /// </summary>
    Task<IReadOnlyList<StationCandidate>> SearchCandidatesAsync(StationSearchQuery query, CancellationToken ct = default);

    /// <summary>Resolve a station by name to playable candidates (with stationuuid).</summary>
    Task<IReadOnlyList<StationCandidate>> SearchByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Resolve specific stationuuids to current, playable candidates (Phase 2: maps semantic
    /// hits back to live stream URLs and re-checks the codec/HLS playability rules).
    /// </summary>
    Task<IReadOnlyList<StationCandidate>> GetByUuidsAsync(IEnumerable<string> uuids, CancellationToken ct = default);

    /// <summary>
    /// Fetch up to <paramref name="count"/> playable stations ordered by popularity
    /// (clickcount, descending), paging the directory. Used to pre-fill the enrichment cache.
    /// </summary>
    Task<IReadOnlyList<StationCandidate>> GetPopularAsync(int count, bool extendedInfoOnly, CancellationToken ct = default);
}
