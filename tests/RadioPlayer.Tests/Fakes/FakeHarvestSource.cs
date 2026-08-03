using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Lets a test raise SegmentIndexed directly — stands in for DjHarvestService (whose
/// real StartAsync needs a live network connection and its own BASS device) in DjQueueService
/// tests.</summary>
public sealed class FakeHarvestSource : IDjHarvestSource
{
    public event EventHandler<HarvestedSong>? SegmentIndexed;

    /// <summary>The generation songs are stamped with. Bump it to simulate the harvest pool
    /// swapping over to a new vibe.</summary>
    public int VibeGeneration { get; set; }

    /// <summary>Raises a song stamped with the current generation — the normal case.</summary>
    public void RaiseSegmentIndexed(SavedSong song) =>
        SegmentIndexed?.Invoke(this, new HarvestedSong(song, VibeGeneration));

    /// <summary>Raises a song stamped with an explicit generation — for the in-flight arrival
    /// recorded before a vibe change that only lands afterwards.</summary>
    public void RaiseSegmentIndexed(SavedSong song, int generation) =>
        SegmentIndexed?.Invoke(this, new HarvestedSong(song, generation));
}
