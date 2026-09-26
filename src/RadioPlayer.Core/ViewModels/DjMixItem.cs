using RadioPlayer.Mvvm;

namespace RadioPlayer.ViewModels;

/// <summary>
/// One row in the DJ panel's "Mix" tab — a song in the session's queue, whether it's already
/// played, playing now, or still to come. Replaces the separate session-history list: the
/// playback queue never drops played tracks, so it already IS the session record, and keeping a
/// parallel history meant two sources of truth for the same thing.
/// </summary>
public sealed class DjMixItem : ObservableObject
{
    private bool _isCurrent;
    private bool _hasPlayed;

    public DjMixItem(string title, string artist)
    {
        Title = title;
        Artist = artist;
    }

    public string Title { get; }
    public string Artist { get; }

    /// <summary>Playing right now — the row the equalizer sits on.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>Already played this session; the row recedes so what's coming reads first.</summary>
    public bool HasPlayed
    {
        get => _hasPlayed;
        set => SetProperty(ref _hasPlayed, value);
    }
}
