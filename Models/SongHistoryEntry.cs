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

    [JsonIgnore] public bool HasSegment => SegmentFile is not null;
    [JsonIgnore] public bool IsSaved => SavedPath is not null;
    [JsonIgnore] public bool CanSave => HasSegment && !IsSaved;

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
