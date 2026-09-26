using RadioPlayer.Mvvm;
using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

/// <summary>
/// One entry in the curated library queue. Observable so the row can highlight itself while it's
/// the track currently playing.
/// </summary>
public sealed class CuratedQueueItem : ObservableObject
{
    private bool _isCurrent;

    public CuratedQueueItem(CuratedSong song)
    {
        Path = song.Path;
        Title = song.Title;
        Artist = song.Artist;
        Reason = song.Reason;
    }

    public string Path { get; }
    public string Title { get; }
    public string Artist { get; }
    public string? Reason { get; }

    /// <summary>Secondary line: the curator's rationale if present, else the artist.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Reason) ? Artist : Reason!;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
