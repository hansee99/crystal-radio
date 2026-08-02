namespace RadioPlayer.Services;

/// <summary>
/// The event surface <see cref="DjQueueService"/> needs from <see cref="DjHarvestService"/> —
/// separated out so tests can raise <see cref="SegmentIndexed"/> directly via a fake, without
/// running a real harvest (which needs a live network connection and its own BASS device).
/// </summary>
public interface IDjHarvestSource
{
    /// <summary>Raised (background thread) for each harvested song kept after QC.</summary>
    event EventHandler<SavedSong>? SegmentIndexed;
}
