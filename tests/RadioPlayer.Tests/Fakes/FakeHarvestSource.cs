using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Lets a test raise SegmentIndexed directly — stands in for DjHarvestService (whose
/// real StartAsync needs a live network connection and its own BASS device) in DjQueueService
/// tests.</summary>
public sealed class FakeHarvestSource : IDjHarvestSource
{
    public event EventHandler<SavedSong>? SegmentIndexed;

    public void RaiseSegmentIndexed(SavedSong song) => SegmentIndexed?.Invoke(this, song);
}
