using System.IO;
using System.Windows.Threading;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Unit tests for DjQueueService's warm-start / low-watermark refill / dedup / never-starve
/// logic (DJ-MODE-SPEC-HARVEST.md §9), using fakes so nothing here touches BASS or the network —
/// deferred from the DJ-mode production pass, picked up here.
///
/// Threading note: DjQueueService marshals its event handlers through a captured Dispatcher
/// (matching StreamRecorder/LocalPlaybackEngine/SmtcController's idiom), which only actually runs
/// queued callbacks once something pumps it. Tests use FakeSongCurator's synchronously-completing
/// tasks (Task.FromResult) plus `.GetAwaiter().GetResult()` instead of `await` — a completed
/// task's continuation runs inline with no thread hop, so the whole test stays on one thread and
/// PumpDispatcher() below reliably flushes the exact dispatcher DjQueueService captured. (This is
/// the same class of async-continuation pitfall found and fixed in tools/DjQueue's PoC earlier —
/// see that tool's README.)
/// </summary>
public sealed class DjQueueServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string NewTempSongFile()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
    }

    /// <summary>Flushes every dispatcher operation queued at Normal priority or above (e.g. the
    /// BeginInvoke inside DjQueueService's event handlers) without needing a real message loop.</summary>
    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static CuratedSong Song(string path, string artist, string title) => new(path, title, artist, null);

    [Fact]
    public void ColdStart_PlaysFirstHarvestedSongOnceItLands()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue(Array.Empty<CuratedSong>()); // empty library — cold start

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5);
        sut.StartAsync("mellow electronic").GetAwaiter().GetResult();

        Assert.Single(local.SetQueueCalls);
        Assert.Empty(local.SetQueueCalls[0]); // nothing to seed with
        Assert.Empty(local.AppendCalls);

        var file = NewTempSongFile();
        // SavedSong's positional order is (Path, Title, Artist, Station, Codec, SavedAt, ...).
        harvest.RaiseSegmentIndexed(new SavedSong(file, "Nightdrive", "Aurora", "SomaFM",
            "mp3", DateTimeOffset.Now, Source: SongSource.Harvested));
        PumpDispatcher();

        var appended = Assert.Single(local.AppendCalls);
        var track = Assert.Single(appended);
        Assert.Equal(file, track.Path);
        Assert.Equal("Nightdrive", track.Title);
    }

    [Fact]
    public void Dedup_SkipsHarvestedSongAlreadyInWarmStartSeed()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        var seedFile = NewTempSongFile();
        curator.Enqueue([Song(seedFile, "Deary", "Blue Ribbon")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("indie pop").GetAwaiter().GetResult();
        Assert.Single(local.SetQueueCalls[0]);

        // Same artist+title, different file — a different station capturing the same hit.
        var dupFile = NewTempSongFile();
        harvest.RaiseSegmentIndexed(new SavedSong(dupFile, "Blue Ribbon", "Deary", "OtherStation",
            "mp3", DateTimeOffset.Now, Source: SongSource.Harvested));
        PumpDispatcher();
        Assert.Empty(local.AppendCalls); // deduped — nothing appended

        // Genuinely different song — should go through.
        var newFile = NewTempSongFile();
        harvest.RaiseSegmentIndexed(new SavedSong(newFile, "Fumes", "Lisa SQ", "SomaFM",
            "mp3", DateTimeOffset.Now, Source: SongSource.Harvested));
        PumpDispatcher();
        var appended = Assert.Single(local.AppendCalls);
        Assert.Equal(newFile, Assert.Single(appended).Path);
    }

    [Fact]
    public void NonExistentFile_NeverReachesTheQueue()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(@"C:\does\not\exist.mp3", "Ghost", "Nobody")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("anything").GetAwaiter().GetResult();

        Assert.Empty(local.SetQueueCalls[0]); // the missing-file entry was filtered out
    }

    [Fact]
    public void LowWatermark_TriggersTopUpFromLibraryWhenQueueRunsLow()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        var seed = new[] { NewTempSongFile(), NewTempSongFile(), NewTempSongFile() };
        curator.Enqueue([Song(seed[0], "A1", "T1"), Song(seed[1], "A2", "T2"), Song(seed[2], "A3", "T3")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 2);
        sut.StartAsync("prompt").GetAwaiter().GetResult();
        Assert.Equal(1, curator.CallCount);

        var topUpFile = NewTempSongFile();
        curator.Enqueue([Song(topUpFile, "A4", "T4")]);

        // Index 1 of 3 → remaining = 3 - 1 - 1 = 1, below the watermark of 2.
        local.RaiseTrackChanged(1);

        Assert.Equal(2, curator.CallCount); // top-up fired
        var appended = Assert.Single(local.AppendCalls);
        Assert.Equal(topUpFile, Assert.Single(appended).Path);
    }

    [Fact]
    public void LowWatermark_DoesNotStackTopUpsWhileOneIsAlreadyInFlight()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        var seed = new[] { NewTempSongFile(), NewTempSongFile() };
        curator.Enqueue([Song(seed[0], "A1", "T1"), Song(seed[1], "A2", "T2")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5); // always "low"
        sut.StartAsync("prompt").GetAwaiter().GetResult();
        Assert.Equal(1, curator.CallCount);

        var gate = curator.EnqueueGated(); // the first top-up call hangs until we say so

        local.RaiseTrackChanged(0); // triggers TopUpFromLibraryAsync — now in flight
        Assert.Equal(2, curator.CallCount);

        local.RaiseTrackChanged(1); // remaining is still low, but a top-up is already in flight
        Assert.Equal(2, curator.CallCount); // no second call fired

        var topUpFile = NewTempSongFile();
        gate.SetResult([Song(topUpFile, "A3", "T3")]); // let the first top-up complete
        PumpDispatcher();

        var appended = Assert.Single(local.AppendCalls);
        Assert.Equal(topUpFile, Assert.Single(appended).Path);

        // Now that it's finished, a further low-remaining tick can trigger another top-up.
        curator.Enqueue([]);
        local.RaiseTrackChanged(0);
        Assert.Equal(3, curator.CallCount);
    }

    /// <summary>
    /// Stopping a session (which includes switching player modes) while the warm-start seed is
    /// still being curated must not start playback afterwards. SetQueue plays immediately, so a
    /// seed that lands after the user has moved to Library or Radio would put the DJ mix under
    /// someone else's panel — the same wrong-audio-for-the-panel symptom as the mode switch that
    /// left the local engine running, reached by a different route.
    /// </summary>
    [Fact]
    public void StartCancelledMidCuration_NeverStartsPlayback()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        var gate = curator.EnqueueGated();

        using var cts = new CancellationTokenSource();
        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5);
        var starting = sut.StartAsync("deep house for coding", cts.Token);

        // The user leaves DJ mode while the curator is still thinking, then it answers anyway.
        cts.Cancel();
        gate.SetResult([Song(NewTempSongFile(), "Aurora", "Nightdrive")]);

        Assert.Throws<OperationCanceledException>(() => starting.GetAwaiter().GetResult());
        Assert.Empty(local.SetQueueCalls);
    }

    [Fact]
    public void NeverStarve_QueueKeepsGrowingAcrossAPlausibleArrivalTimeline()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue(Array.Empty<CuratedSong>()); // cold start

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 3);
        sut.StartAsync("prompt").GetAwaiter().GetResult();

        // Simulate 10 harvested songs arriving one at a time, as a real harvest session would.
        for (var i = 0; i < 10; i++)
        {
            var file = NewTempSongFile();
            harvest.RaiseSegmentIndexed(new SavedSong(file, $"Title{i}", $"Artist{i}", "Station",
                "mp3", DateTimeOffset.Now, Source: SongSource.Harvested));
            PumpDispatcher();
            // The queue only ever grows — DjQueueService never proactively removes anything, so
            // "never starve" here means it never fails to accept a genuinely new arrival.
            Assert.Equal(i + 1, local.QueueCount);
        }
    }
}
