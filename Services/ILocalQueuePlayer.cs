namespace RadioPlayer.Services;

/// <summary>
/// The local-queue-specific surface <see cref="DjQueueService"/> needs from
/// <see cref="LocalPlaybackEngine"/> — deliberately separate from <see cref="IPlaybackEngine"/>
/// (RadioEngine has no queue concept, so these don't belong on the shared contract). Exists so
/// DjQueueService can be unit-tested against a fake, without constructing a real
/// LocalPlaybackEngine (whose constructor initializes real BASS).
/// </summary>
public interface ILocalQueuePlayer
{
    /// <summary>Number of tracks in the queue (played + upcoming).</summary>
    int QueueCount { get; }

    /// <summary>The queue in order — played, current, upcoming. DJ mode's "Mix" list is a
    /// direct view of this.</summary>
    IReadOnlyList<LocalTrack> Queue { get; }

    /// <summary>Index of the track currently playing, or -1.</summary>
    int CurrentIndex { get; }

    void SetQueue(IReadOnlyList<LocalTrack> tracks, int startIndex = 0);

    void Append(IReadOnlyList<LocalTrack> tracks);

    /// <summary>Position within the queue moved.</summary>
    event EventHandler<(LocalTrack Track, int Index)>? TrackChanged;

    /// <summary>Queue contents changed (replaced or appended).</summary>
    event EventHandler? QueueChanged;
}
