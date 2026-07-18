using System.Text.Json.Serialization;

namespace RadioPlayer.Models;

/// <summary>
/// One song the user heard on a stream: what played, where, and when. Phase A of the
/// library feature records metadata only; later phases attach a rolling-cache audio
/// segment so entries become saveable.
/// </summary>
public sealed record SongHistoryEntry(string Title, string Artist, string Station, DateTime PlayedAt)
{
    /// <summary>Secondary display line: "Artist · Station · time" (non-empty parts only).</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var time = PlayedAt.Date == DateTime.Today
                ? PlayedAt.ToString("HH:mm")
                : PlayedAt.ToString("d MMM HH:mm");
            var parts = new[] { Artist, Station, time }.Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" · ", parts);
        }
    }
}
