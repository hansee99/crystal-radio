namespace RadioPlayer.Models;

/// <summary>
/// Stream format. Determines which BASS path the engine uses to create the stream
/// (AAC needs the bass_aac add-on; everything else goes through BASS core).
/// </summary>
public enum StreamFormat
{
    Aac,
    Mp3,
    Other
}

/// <summary>
/// A radio station. The engine only needs the URL plus the format so it knows
/// whether to take the AAC add-on path or the core path. <see cref="Description"/> is an
/// optional one-line blurb (carried over when a station is added from a search result) so
/// the fixed list can show the same secondary text as the search panel; it is null for
/// manually-added stations and ignored by the engine.
/// </summary>
public sealed record Station(string Name, string Url, StreamFormat Format, string? Description = null);
