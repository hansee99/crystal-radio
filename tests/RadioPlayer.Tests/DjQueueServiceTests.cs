using System.Diagnostics.CodeAnalysis;
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
[SuppressMessage("Usage", "xUnit1031:Test methods should not use blocking task operations",
    Justification =
        "Deliberate, and the async form would break these tests — see the threading note above. " +
        "The rule guards against blocking on a task whose continuation needs the thread that is " +
        "blocked; here every awaited task is already completed (FakeSongCurator returns " +
        "Task.FromResult), so nothing is ever waited on. Awaiting instead would let the test " +
        "resume on another thread, and PumpDispatcher would then pump a dispatcher that is not " +
        "the one DjQueueService captured, silently flushing nothing. Scoped to this class on " +
        "purpose: other test files should still get the warning.")]
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

    // --- Changing the vibe mid-session (#4) ---------------------------------------------------

    /// <summary>The whole point: the mix carries on. Reusing StartAsync would have been the
    /// obvious shortcut and is exactly wrong — its SetQueue stops the current track dead.</summary>
    [Fact]
    public void ChangeVibe_DoesNotTouchWhatIsPlaying()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1"), Song(NewTempSongFile(), "A2", "T2")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("deep house for coding").GetAwaiter().GetResult();
        var queueCallsBefore = local.SetQueueCalls.Count;

        sut.ChangeVibe("uptempo drum and bass");

        Assert.Equal(queueCallsBefore, local.SetQueueCalls.Count);  // nothing replaced
        Assert.Empty(local.AppendCalls);                            // and nothing appended
        Assert.Equal(2, local.QueueCount);
    }

    [Fact]
    public void ChangeVibe_MakesTheNextTopUpUseTheNewPrompt()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1"), Song(NewTempSongFile(), "A2", "T2")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5);
        sut.StartAsync("deep house for coding").GetAwaiter().GetResult();

        sut.ChangeVibe("uptempo drum and bass");
        curator.Enqueue([Song(NewTempSongFile(), "A3", "T3")]);
        local.RaiseTrackChanged(0);                                  // drops below the watermark

        Assert.Equal("uptempo drum and bass", curator.LastPrompt);
    }

    /// <summary>"The library has nothing more" was a judgement about the OLD prompt, so a new one
    /// has to be allowed to look again — otherwise a session that exhausted its library never
    /// tops up again however the vibe changes.</summary>
    [Fact]
    public void ChangeVibe_LetsAnExhaustedLibraryBeSearchedAgain()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5);
        sut.StartAsync("deep house").GetAwaiter().GetResult();

        curator.Enqueue([]);                 // top-up finds nothing → library marked exhausted
        local.RaiseTrackChanged(0);
        var callsAfterExhaustion = curator.CallCount;

        curator.Enqueue([]);
        local.RaiseTrackChanged(0);
        Assert.Equal(callsAfterExhaustion, curator.CallCount);   // suppressed, as designed

        sut.ChangeVibe("something else entirely");
        curator.Enqueue([]);
        local.RaiseTrackChanged(0);

        Assert.Equal(callsAfterExhaustion + 1, curator.CallCount); // searching again
    }

    [Fact]
    public void ChangeVibe_KeepsSongsAlreadyHeardOutOfTheMix()
    {
        // A song heard this session shouldn't return just because it also fits the new vibe.
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        var file = NewTempSongFile();
        curator.Enqueue([Song(file, "Deary", "Blue Ribbon")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("indie pop").GetAwaiter().GetResult();

        sut.ChangeVibe("dream pop");
        harvest.RaiseSegmentIndexed(new SavedSong(NewTempSongFile(), "Blue Ribbon", "Deary",
            "OtherStation", "mp3", DateTimeOffset.Now, Source: SongSource.Harvested));
        PumpDispatcher();

        Assert.Empty(local.AppendCalls);     // still deduped across the change
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeVibe_IgnoresABlankPrompt(string prompt)
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 5);
        sut.StartAsync("deep house").GetAwaiter().GetResult();

        sut.ChangeVibe(prompt);
        curator.Enqueue([]);
        local.RaiseTrackChanged(0);

        Assert.Equal("deep house", curator.LastPrompt);   // unchanged
    }

    // --- Songs must match the CURRENT vibe (#30) ----------------------------------------------
    // Arrivals used to be appended unconditionally. The warm-start seed and the low-watermark
    // top-up both went through the curator with requireRelevance; the arrival path — the main
    // source of songs in a session — went through nothing, so old-vibe songs kept playing.

    private static SavedSong Harvested(string path, string artist, string title) =>
        new(path, title, artist, "SomeStation", "mp3", DateTimeOffset.Now, Source: SongSource.Harvested);

    [Fact]
    public void ASongHarvestedForAnEarlierVibeIsDropped()
    {
        // Harvesting is a pipeline several minutes deep, so a segment recorded before the swap
        // finishes QC and lands well after it. That is the song the listener doesn't want.
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("deep house").GetAwaiter().GetResult();

        sut.ChangeVibe("uptempo drum and bass");
        harvest.VibeGeneration = 1;          // ...and some time later, the pool swaps over

        harvest.RaiseSegmentIndexed(Harvested(NewTempSongFile(), "Old", "From The Old Vibe"), generation: 0);
        PumpDispatcher();

        Assert.Empty(local.AppendCalls);
    }

    [Fact]
    public void ASongHarvestedForTheCurrentVibeIsStillAppended()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("deep house").GetAwaiter().GetResult();

        sut.ChangeVibe("uptempo drum and bass");
        harvest.VibeGeneration = 1;

        var file = NewTempSongFile();
        harvest.RaiseSegmentIndexed(Harvested(file, "New", "For The New Vibe"));  // current generation
        PumpDispatcher();

        Assert.Equal(file, Assert.Single(Assert.Single(local.AppendCalls)).Path);
    }

    /// <summary>
    /// The ordering trap, which shipped. MainViewModel.ChangeDjVibeAsync calls ChangeVibe FIRST —
    /// on purpose, so library top-ups follow the new prompt straight away — and only then awaits
    /// the harvest sourcing a new pool, which is what bumps the counter. In one real session that
    /// await took 95 seconds.
    ///
    /// <para>So at the moment ChangeVibe runs, the harvester's counter still reads the OLD vibe.
    /// A queue that copies it there is wrong for the whole session: every song of the new vibe
    /// arrives stamped 1 against a copy of 0, all of them are dropped, and the mix bridges live
    /// indefinitely with five perfectly good songs sitting on disk. Both tests above missed it by
    /// bumping the counter before ChangeVibe — the one order production never uses.</para>
    /// </summary>
    [Fact]
    public void ASongForTheNewVibeIsAppendedEvenThoughChangeVibeRanBeforeTheSwap()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "Motley Crue", "Shout At The Devil")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("classic 80s hair metal").GetAwaiter().GetResult();

        sut.ChangeVibe("classic west coast hip hop");   // counter still 0 — sourcing hasn't run
        harvest.VibeGeneration = 1;                     // ...95 seconds later, the pool swaps

        var file = NewTempSongFile();
        harvest.RaiseSegmentIndexed(Harvested(file, "Warren G", "This D.J. (Remix Version)"));
        PumpDispatcher();

        Assert.Equal(file, Assert.Single(Assert.Single(local.AppendCalls)).Path);
    }

    /// <summary>A second change while songs from the first are still in QC. Only the newest vibe
    /// is accepted — the counter is read per arrival, so there is no window where an intermediate
    /// value is the one being compared against.</summary>
    [Fact]
    public void OnlyTheNewestVibeIsAcceptedAfterTwoChangesInARow()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("first").GetAwaiter().GetResult();

        sut.ChangeVibe("second");
        harvest.VibeGeneration = 1;
        sut.ChangeVibe("third");
        harvest.VibeGeneration = 2;

        harvest.RaiseSegmentIndexed(Harvested(NewTempSongFile(), "Stale", "First Vibe"), generation: 0);
        harvest.RaiseSegmentIndexed(Harvested(NewTempSongFile(), "Stale", "Second Vibe"), generation: 1);
        var wanted = NewTempSongFile();
        harvest.RaiseSegmentIndexed(Harvested(wanted, "Fresh", "Third Vibe"), generation: 2);
        PumpDispatcher();

        Assert.Equal(wanted, Assert.Single(Assert.Single(local.AppendCalls)).Path);
    }

    /// <summary>The cross-session trap: the harvester's counter is not reset between sessions, so
    /// a queue that assumed 0 would reject every arrival in any session started after a vibe
    /// change — the feature would appear to work once and never again.</summary>
    [Fact]
    public void ASessionStartedAfterAnEarlierVibeChangeStillAcceptsSongs()
    {
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource { VibeGeneration = 3 };  // an earlier session changed vibe
        var curator = new FakeSongCurator();
        curator.Enqueue([]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("anything").GetAwaiter().GetResult();

        harvest.RaiseSegmentIndexed(Harvested(NewTempSongFile(), "A", "T"));
        PumpDispatcher();

        Assert.Single(local.AppendCalls);
    }

    [Fact]
    public void ChangingTheVibeDropsTheQueuedTailButNotThePlayingTrack()
    {
        // Playing them out would mean the change isn't audible for several minutes; cutting the
        // current track off mid-song is the one thing the player must never do.
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1"), Song(NewTempSongFile(), "A2", "T2"),
                         Song(NewTempSongFile(), "A3", "T3")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("deep house").GetAwaiter().GetResult();
        local.RaiseTrackChanged(0);          // playing the first of three

        sut.ChangeVibe("uptempo drum and bass");

        Assert.Equal(1, local.QueueCount);   // just the one still playing
        Assert.Equal(1, local.TruncateCalls);
    }

    [Fact]
    public void ChangingTheVibeBeforeAnythingPlaysLeavesTheQueueAlone()
    {
        // Nothing is playing yet, so there is no "after the current track" to drop — and throwing
        // the seed away would leave a silent session for no reason.
        var local = new FakeLocalQueuePlayer();
        var harvest = new FakeHarvestSource();
        var curator = new FakeSongCurator();
        curator.Enqueue([Song(NewTempSongFile(), "A1", "T1"), Song(NewTempSongFile(), "A2", "T2")]);

        var sut = new DjQueueService(local, curator, harvest, maxSeed: 20, lowWatermark: 1);
        sut.StartAsync("deep house").GetAwaiter().GetResult();

        sut.ChangeVibe("something else");

        Assert.Equal(0, local.TruncateCalls);
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
