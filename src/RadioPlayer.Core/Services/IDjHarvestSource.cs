namespace RadioPlayer.Services;

/// <summary>
/// The event surface <see cref="DjQueueService"/> needs from <see cref="DjHarvestService"/> —
/// separated out so tests can raise <see cref="SegmentIndexed"/> directly via a fake, without
/// running a real harvest (which needs a live network connection and its own BASS device).
/// </summary>
/// <summary>
/// A harvested song, stamped with the vibe it was collected under.
///
/// The stamp is what makes a mid-session vibe change actually change the mix. Harvesting is a
/// pipeline several minutes deep — a segment that started recording before the swap finishes,
/// passes QC and lands well after it — so without the stamp the old vibe keeps feeding the queue
/// long after the listener asked for something else, which is exactly the complaint in #30.
/// </summary>
public sealed record HarvestedSong(SavedSong Song, int VibeGeneration);

public interface IDjHarvestSource
{
    /// <summary>Raised (background thread) for each harvested song kept after QC.</summary>
    event EventHandler<HarvestedSong>? SegmentIndexed;

    /// <summary>Increments every time the vibe changes; songs carry the value in force when they
    /// were collected.</summary>
    int VibeGeneration { get; }
}
