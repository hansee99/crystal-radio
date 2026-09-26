using System.Net.Http;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Telling "the directory is down" apart from "something else went wrong", which decides whether
/// the offline catalog gets a turn (#26). Lives next to the interface because it's a statement
/// about how this service fails, and both the visible search and DJ sourcing need the same answer.
/// </summary>
public static class DirectoryFailure
{
    /// <summary>
    /// True for a transport failure against the directory. A <see cref="TaskCanceledException"/> is
    /// ambiguous — HttpClient raises it for its own timeout as well — so the caller's token
    /// decides: a user who pressed Cancel has not hit an outage, and their intent shouldn't be
    /// papered over with a fallback search.
    /// </summary>
    public static bool IsUnreachable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}

/// <summary>
/// Queries a station directory and returns only stations the BASS engine can play.
/// Knows nothing about LLMs.
/// </summary>
public interface IStationSearchService
{
    Task<IReadOnlyList<Station>> SearchAsync(StationSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Same filtering as <see cref="SearchAsync"/>, but returns candidates carrying their
    /// Radio Browser <c>stationuuid</c> — used by the Pattern B agentic tool. <paramref name="offset"/>
    /// pages past earlier directory rows (used by "Show different" to fetch fresh results).
    /// </summary>
    Task<IReadOnlyList<StationCandidate>> SearchCandidatesAsync(StationSearchQuery query, int offset = 0, CancellationToken ct = default);

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
