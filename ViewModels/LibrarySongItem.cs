using RadioPlayer.Mvvm;
using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

/// <summary>
/// One row in the "Songs" tab — every song saved to the local library, browsable independent of
/// any curated playlist. Observable so the row can highlight itself while it's the track
/// currently playing (mirrors <see cref="CuratedQueueItem"/>).
/// </summary>
public sealed class LibrarySongItem : ObservableObject
{
    private bool _isCurrent;

    public LibrarySongItem(SavedSong song)
    {
        Song = song;
    }

    public SavedSong Song { get; }
    public string Title => Song.Title;
    public string Artist => Song.Artist;

    /// <summary>Secondary line: the AI-derived description if present, else the artist.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Song.Description) ? Song.Artist : Song.Description!;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
