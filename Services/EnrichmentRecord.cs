namespace RadioPlayer.Services;

/// <summary>Where a station's enriched description came from.</summary>
public enum EnrichmentSource
{
    Homepage,
    Web,
    TagsOnly
}

/// <summary>
/// A cached, distilled description for one station (keyed by Radio Browser stationuuid).
/// This is the app's first persistent local state (Phase 1 of semantic search).
/// </summary>
/// <param name="Name">Station name, and the four fields after it, are what make a cached row
/// PLAYABLE without the directory (#26). Optional because the description path can run without
/// them (the seed tool, a re-enrichment from a uuid) — and when they're absent the store keeps
/// whatever it already had rather than blanking it.</param>
public sealed record EnrichmentRecord(
    string StationUuid,
    string Description,
    string? Facets,          // JSON: { genres, moods, era }
    EnrichmentSource Source,
    DateTimeOffset EnrichedAt,
    string? Name = null,
    string? Url = null,      // url_resolved — the one the player can actually open
    string? Codec = null,
    int Bitrate = 0,
    string? Country = null);

/// <summary>A station's description awaiting an embedding (Phase 2 backfill input).</summary>
public sealed record StationDescriptionRow(string StationUuid, string Description);

/// <summary>An embedded station row loaded for in-memory cosine search.</summary>
public sealed record EmbeddedStationRow(string StationUuid, string Description, float[] Vector);

/// <summary>
/// A cached station complete enough to play without the directory: a vector to match against and a
/// stream to open (#26). Only rows carrying both are returned — a description without a url can't
/// be played, and a url without a vector can't be matched.
/// </summary>
/// <param name="Url">The cached <c>url_resolved</c>. Deliberately NOT re-verified: offline is the
/// whole point, so <c>lastcheckok</c> can't be consulted and this may be stale. A harvester that
/// can't connect is retired and replaced from the reserve, which is how that degrades.</param>
public sealed record PlayableStationRow(
    string StationUuid,
    string Description,
    float[] Vector,
    string Name,
    string Url,
    string? Codec,
    int Bitrate,
    string? Country);
