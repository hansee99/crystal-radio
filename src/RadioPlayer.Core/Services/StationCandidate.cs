using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// A playable station fetched from Radio Browser, paired with its directory
/// <c>stationuuid</c>. The uuid is the trust anchor: Pattern B only ever plays a station
/// whose uuid the model picked from candidates we actually fetched.
/// </summary>
public sealed record StationCandidate(
    string StationUuid,
    Station Station,
    int Bitrate,
    string? Country,
    string? Tags,
    string? Homepage);
