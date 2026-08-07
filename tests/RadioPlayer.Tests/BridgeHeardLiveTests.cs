using System.IO;
using System.Windows.Threading;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// A song heard during a live bridge must not come back as a harvested track (#54).
///
/// <para>The station covering a bridge is one of the pool's own and its harvester keeps recording
/// throughout, so the track the listener is hearing right now is also being captured — and would
/// be queued minutes later. Observed on a real run as the same song twice in a row.</para>
/// </summary>
public sealed class BridgeHeardLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "heardlive-" + Guid.NewGuid().ToString("N"));

    private readonly FakeLocalQueuePlayer _local = new();
    private readonly FakeSongCurator _curator = new();
    private readonly FakeHarvestSource _harvest = new();

    public BridgeHeardLiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private string WriteSong(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    /// <summary>
    /// DjQueueService marshals harvest arrivals through the dispatcher it captured at construction,
    /// so nothing lands until that dispatcher is pumped. Same helper as DjQueueServiceTests.
    /// </summary>
    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private DjQueueService NewQueue() => new(_local, _curator, _harvest);

    private async Task<DjQueueService> StartedQueue()
    {
        var queue = NewQueue();
        await queue.StartAsync("art rock");
        return queue;
    }

    /// <summary>The reported bug: heard live, then harvested, then played again.</summary>
    [Fact]
    public async Task ASongHeardDuringTheBridgeIsNotQueuedWhenItIsHarvested()
    {
        using var queue = await StartedQueue();
        queue.NoteHeardLive("Pink Floyd", "Not Now John");

        _harvest.RaiseSegmentIndexed(new SavedSong(WriteSong("a.mp3"), "Not Now John", "Pink Floyd",
            "Virgin Radio Rockstar", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Empty(_local.AppendCalls);
    }

    /// <summary>Anything else the same station captured still belongs in the mix.</summary>
    [Fact]
    public async Task OtherSongsFromTheSameStationStillReachTheQueue()
    {
        using var queue = await StartedQueue();
        queue.NoteHeardLive("Pink Floyd", "Not Now John");

        _harvest.RaiseSegmentIndexed(new SavedSong(WriteSong("b.mp3"), "Wish You Were Here", "Pink Floyd",
            "Virgin Radio Rockstar", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Single(_local.AppendCalls);
    }

    /// <summary>
    /// A station announcing itself, or an empty title between tracks, is not a song — recording it
    /// as heard would blacklist a key that matches nothing and could mask a real track.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyAnnouncementIsNotRecordedAsHeard(string? title)
    {
        using var queue = await StartedQueue();
        queue.NoteHeardLive("Virgin Radio Rockstar", title);

        // A later song with a blank title would otherwise collide with the blacklisted key.
        _harvest.RaiseSegmentIndexed(new SavedSong(WriteSong("c.mp3"), "Comfortably Numb", "Pink Floyd",
            "Virgin Radio Rockstar", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Single(_local.AppendCalls);
    }

    /// <summary>Hearing the same track announced repeatedly — stations re-announce mid-song — must
    /// stay one entry rather than growing the set on every push.</summary>
    [Fact]
    public async Task RepeatedAnnouncementsOfOneTrackAreHarmless()
    {
        using var queue = await StartedQueue();
        for (var i = 0; i < 5; i++)
            queue.NoteHeardLive("Pink Floyd", "Not Now John");

        _harvest.RaiseSegmentIndexed(new SavedSong(WriteSong("d.mp3"), "Not Now John", "Pink Floyd",
            "Virgin Radio Rockstar", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Empty(_local.AppendCalls);
    }

    /// <summary>A missing artist still identifies a track well enough to suppress it — the queue's
    /// own dedupe key is built the same way.</summary>
    [Fact]
    public async Task ATrackWithNoArtistIsStillSuppressed()
    {
        using var queue = await StartedQueue();
        queue.NoteHeardLive(null, "Fantasia For Fins");

        _harvest.RaiseSegmentIndexed(new SavedSong(WriteSong("e.mp3"), "Fantasia For Fins", "",
            "Radio Caprice", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Empty(_local.AppendCalls);
    }
}
