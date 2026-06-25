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
public sealed record EnrichmentRecord(
    string StationUuid,
    string Description,
    string? Facets,          // JSON: { genres, moods, era }
    EnrichmentSource Source,
    DateTimeOffset EnrichedAt);

/// <summary>A station's description awaiting an embedding (Phase 2 backfill input).</summary>
public sealed record StationDescriptionRow(string StationUuid, string Description);

/// <summary>An embedded station row loaded for in-memory cosine search.</summary>
public sealed record EmbeddedStationRow(string StationUuid, string Description, float[] Vector);
