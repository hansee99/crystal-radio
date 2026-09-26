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

    /// <summary>
    /// Secondary line, always artist-led (UX audit). It used to be the AI description *instead
    /// of* the artist whenever one existed, which meant a single list mixed one-line artist rows
    /// with three-line description rows — and the rows carrying a description showed no artist at
    /// all. Now the artist always comes first and the description trails it on the same trimmed
    /// line, so every row is the same height and the list stays scannable. The full description
    /// is still available on the row's tooltip.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var artist = string.IsNullOrWhiteSpace(Song.Artist) ? null : Song.Artist.Trim();
            var description = string.IsNullOrWhiteSpace(Song.Description) ? null : Song.Description!.Trim();
            if (artist is null) return description ?? string.Empty;
            return description is null ? artist : $"{artist} · {description}";
        }
    }

    /// <summary>Full AI description for the row tooltip (null when there isn't one yet).</summary>
    public string? Description => string.IsNullOrWhiteSpace(Song.Description) ? null : Song.Description;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
