namespace RadioPlayer.ViewModels;

/// <summary>One row in the DJ panel's "History" tab — a song that already played during the
/// current DJ session (mirrors Radio mode's History tab, for DJ's local-playback side).</summary>
public sealed class DjHistoryItem
{
    public DjHistoryItem(string title, string artist, DateTime playedAt)
    {
        Title = title;
        Artist = artist;
        PlayedAt = playedAt;
    }

    public string Title { get; }
    public string Artist { get; }
    public DateTime PlayedAt { get; }

    // Matches Models/SongHistoryEntry.RelativeTime's exact phrasing for consistency.
    public string RelativeTime
    {
        get
        {
            var delta = DateTime.Now - PlayedAt;
            if (delta.TotalMinutes < 1) return "just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} hr ago";
            return PlayedAt.ToString("d MMM");
        }
    }
}
