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
    public IReadOnlyList<LocalTrack> Queue => _queue;
    public int CurrentIndex { get; private set; } = -1;

    public event EventHandler<(LocalTrack Track, int Index)>? TrackChanged;
    public event EventHandler? QueueChanged;

    public void SetQueue(IReadOnlyList<LocalTrack> tracks, int startIndex = 0)
    {
        SetQueueCalls.Add(tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Append(IReadOnlyList<LocalTrack> tracks)
    {
        AppendCalls.Add(tracks);
        _queue.AddRange(tracks);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TruncateAfterCurrent()
    {
        var keep = CurrentIndex + 1;
        if (keep <= 0 || keep >= _queue.Count) return;
        TruncateCalls++;
        _queue.RemoveRange(keep, _queue.Count - keep);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How many times the queue was truncated behind the playing track.</summary>
    public int TruncateCalls { get; private set; }

    /// <summary>Test hook: simulate the engine advancing to <paramref name="index"/>.</summary>
    public void RaiseTrackChanged(int index)
    {
        CurrentIndex = index;
        TrackChanged?.Invoke(this, (_queue[index], index));
    }
}
