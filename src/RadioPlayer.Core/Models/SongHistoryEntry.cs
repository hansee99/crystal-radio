using System.Text.Json.Serialization;
using RadioPlayer.Mvvm;

namespace RadioPlayer.Models;

/// <summary>
/// One song the user heard on a stream: what played, where, and when — plus, once the song
/// finished while the rolling cache was capturing, a reference to its cached audio segment
/// (Phase B) and, after the user saved it, the library file it became. Mutable/observable
/// because segment and saved state arrive after the row is created and the UI must follow.
/// JSON-serialized by <see cref="Services.SongHistoryStore"/>.
/// </summary>
public sealed class SongHistoryEntry : ObservableObject
{
    private string? _segmentFile;
    private string? _savedPath;
    private bool _markedForSave;
    private bool _isCurrent;

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Station { get; set; } = string.Empty;
    public DateTime PlayedAt { get; set; }

    /// <summary>
    /// File name (within the cache directory) of the fully-captured audio segment for this
    /// song, or null when none exists (not captured, still playing, or pruned from the cache).
    /// Only ever set for COMPLETE segments — partial ones are discarded at the source.
    /// </summary>
    public string? SegmentFile
    {
        get => _segmentFile;
        set
        {
            if (SetProperty(ref _segmentFile, value))
            {
                OnPropertyChanged(nameof(HasSegment));
                OnPropertyChanged(nameof(CanSave));
                // Must move with the others, or the button goes stale exactly when the recording
                // completes — the trap the IsEnabled comment in MainWindow.xaml already warns about.
                OnPropertyChanged(nameof(CanSaveOrMark));
            }
        }
    }

    /// <summary>Size of the cached segment in bytes (0 when none) — used for the cache cap.</summary>
    public long SegmentBytes { get; set; }

    /// <summary>Full path of the saved library file, or null if this song wasn't saved.</summary>
    public string? SavedPath
    {
        get => _savedPath;
        set
        {
            if (SetProperty(ref _savedPath, value))
            {
                OnPropertyChanged(nameof(IsSaved));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanSaveOrMark));
            }
        }
    }

    /// <summary>User asked (while it was playing) to save this song as soon as its segment is
    /// complete. Transient — a mark that never resolves before the song rolls off is just dropped.</summary>
    [JsonIgnore]
    public bool MarkedForSave
    {
        get => _markedForSave;
        set => SetProperty(ref _markedForSave, value);
    }

    /// <summary>
    /// This row is the song playing right now. Set by the view model as playback moves on.
    ///
    /// <para>It exists so the row can offer the RIGHT save affordance. The playing song has no
    /// completed recording yet, so it used to render a disabled button saying "Can't save this one —
    /// it wasn't recorded while it played", which is simply untrue: it is being recorded, and it
    /// will be saveable in a moment. Now it offers "save when it finishes" instead.</para>
    /// </summary>
    [JsonIgnore]
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
                OnPropertyChanged(nameof(CanSaveOrMark));
        }
    }

    [JsonIgnore] public bool HasSegment => SegmentFile is not null;
    [JsonIgnore] public bool IsSaved => SavedPath is not null;
    [JsonIgnore] public bool CanSave => HasSegment && !IsSaved;

    /// <summary>
    /// Whether the row's save button does anything at all: either the audio is already captured, or
    /// this is the playing song and the click means "save it when it finishes". Everything else is a
    /// row whose audio was genuinely missed, and stays disabled with an honest explanation.
    /// </summary>
    [JsonIgnore] public bool CanSaveOrMark => (HasSegment || IsCurrent) && !IsSaved;

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

    /// <summary>Short relative time ("just now" / "3 min ago" / "1 hr ago" / a date), for rows —
    /// like "Recently on this station" — where the station is already implied and repeating it
    /// would be redundant. Computed once per binding refresh, not live-ticking.</summary>
    [JsonIgnore]
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
