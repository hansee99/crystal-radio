using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Records SetQueue/Append calls and lets a test raise TrackChanged directly — stands
/// in for LocalPlaybackEngine (whose real constructor initializes actual BASS) in DjQueueService
/// tests.</summary>
public sealed class FakeLocalQueuePlayer : ILocalQueuePlayer
{
    private readonly List<LocalTrack> _queue = new();

    public List<IReadOnlyList<LocalTrack>> SetQueueCalls { get; } = new();
    public List<IReadOnlyList<LocalTrack>> AppendCalls { get; } = new();

    public int QueueCount => _queue.Count;

    public event EventHandler<(LocalTrack Track, int Index)>? TrackChanged;

    public void SetQueue(IReadOnlyList<LocalTrack> tracks, int startIndex = 0)
    {
        SetQueueCalls.Add(tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
    }

    public void Append(IReadOnlyList<LocalTrack> tracks)
    {
        AppendCalls.Add(tracks);
        _queue.AddRange(tracks);
    }

    /// <summary>Test hook: simulate the engine advancing to <paramref name="index"/>.</summary>
    public void RaiseTrackChanged(int index) =>
        TrackChanged?.Invoke(this, (_queue[index], index));
}
