using RadioPlayer.Models;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The queue state machine, driven headlessly through the real <see cref="LocalPlaybackEngine"/>
/// with only its BASS calls substituted. Every bug this class has produced lived here, not in the
/// audio: a played-to-the-end queue is not empty, so <c>Append</c> silently stopped resuming and a
/// DJ session would sit in silence for the rest of its life while harvesters kept collecting into
/// a queue nobody was reading. The `_ranDry` flag that fixed it had no test until now.
/// </summary>
public class LocalPlaybackEngineQueueTests
{
    /// <summary>The engine with its audio side replaced: records what would have been played,
    /// and can be told to fail a file the way a dead or missing one does.</summary>
    private sealed class HeadlessEngine : LocalPlaybackEngine
    {
        public List<string> Started { get; } = [];
        public int StopAudioCalls { get; private set; }

        /// <summary>Paths that should fail to open, simulating a deleted or corrupt file.</summary>
        public HashSet<string> Unopenable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reported as the current position; drives Previous()'s restart-vs-back choice.</summary>
        public double Position { get; set; }

        public override double PositionSeconds => Position;
        public override double DurationSeconds => 180;

        private protected override void InitAudio() { }             // no device
        private protected override void ArmCrossfadeTrigger(int generation) { }  // BASS-only

        private protected override AudioStart TryStartAudio(LocalTrack track, int generation)
        {
            if (Unopenable.Contains(track.Path))
                return AudioStart.OpenFailed;
            Started.Add(track.Path);
            return AudioStart.Started;
        }

        private protected override void StopAudio() => StopAudioCalls++;
    }

    private static LocalTrack Track(string path) => new(path, path, "Artist", StreamFormat.Mp3, null);

    private static IReadOnlyList<LocalTrack> Tracks(params string[] paths) =>
        paths.Select(Track).ToList();

    /// <summary>Flushes the dispatcher the engine captured, so queued callbacks actually run.</summary>
    private static void Pump() => TestLoop.Pump();

    // --- The regression this file exists for --------------------------------------------------

    [Fact]
    public void AppendAfterTheQueueRanDry_ResumesIntoTheNewTracks()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a", "b"));
        e.Next();                       // -> b
        e.Next();                       // past the end: runs dry

        Assert.Equal(PlaybackState.Stopped, e.State);

        e.Append(Tracks("c"));

        // Resumes, and into the NEW track — not back at the top of the queue.
        Assert.Equal(["a", "b", "c"], e.Started);
        Assert.Equal(PlaybackState.Playing, e.State);
        Assert.Equal(2, e.CurrentIndex);
    }

    [Fact]
    public void AppendAfterADeliberateStop_DoesNotResume()
    {
        // The distinction _ranDry exists for: a user pressing Stop must not be undone by the next
        // harvested song landing.
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a"));
        e.Stop();

        e.Append(Tracks("b"));

        Assert.Equal(["a"], e.Started);          // "b" was queued, not played
        Assert.Equal(PlaybackState.Stopped, e.State);
        Assert.Equal(2, e.QueueCount);
    }

    [Fact]
    public void AppendWhilePlaying_QueuesWithoutInterrupting()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a"));

        e.Append(Tracks("b"));

        Assert.Equal(["a"], e.Started);          // still on "a"
        Assert.Equal(0, e.CurrentIndex);
    }

    [Fact]
    public void AppendToAnEmptyQueue_StartsPlaying()
    {
        var e = new HeadlessEngine();

        e.Append(Tracks("a"));

        Assert.Equal(["a"], e.Started);
        Assert.Equal(PlaybackState.Playing, e.State);
    }

    [Fact]
    public void RunningDryRaisesQueueExhausted_ButAPlainStopDoesNot()
    {
        // DJ mode bridges to live radio on QueueExhausted. Raising it on a deliberate stop would
        // restart the radio under a user who just asked for silence.
        var e = new HeadlessEngine();
        var exhausted = 0;
        e.QueueExhausted += (_, _) => exhausted++;

        e.SetQueue(Tracks("a"));
        e.Next();                        // runs dry
        Assert.Equal(1, exhausted);

        e.SetQueue(Tracks("b"));
        e.Stop();
        Assert.Equal(1, exhausted);      // unchanged
    }

    // --- Ordinary transport --------------------------------------------------------------------

    [Fact]
    public void NextWalksTheQueueThenStopsAtTheEnd()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a", "b"));

        e.Next();
        Assert.Equal(1, e.CurrentIndex);

        e.Next();
        Assert.Equal(PlaybackState.Stopped, e.State);
        Assert.Equal(-1, e.CurrentIndex);
        Assert.Equal(["a", "b"], e.Started);
    }

    [Fact]
    public void PreviousRestartsTheTrackWhenPastTheGracePeriod()
    {
        var e = new HeadlessEngine { Position = 10 };
        e.SetQueue(Tracks("a", "b"));
        e.Next();                        // on "b"

        e.Previous();

        Assert.Equal(1, e.CurrentIndex);            // still "b" — seeks to 0 rather than going back
        Assert.Equal(["a", "b"], e.Started);
    }

    [Fact]
    public void PreviousGoesBackWhenOnlyJustStarted()
    {
        var e = new HeadlessEngine { Position = 1 };
        e.SetQueue(Tracks("a", "b"));
        e.Next();

        e.Previous();

        Assert.Equal(0, e.CurrentIndex);
        Assert.Equal(["a", "b", "a"], e.Started);
    }

    [Fact]
    public void PreviousOnTheFirstTrackRestartsItRatherThanUnderflowing()
    {
        var e = new HeadlessEngine { Position = 1 };
        e.SetQueue(Tracks("a"));

        e.Previous();

        Assert.Equal(0, e.CurrentIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void PlayAtIgnoresAnIndexOutsideTheQueue(int index)
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a", "b"));

        e.PlayAt(index);

        Assert.Equal(0, e.CurrentIndex);            // unchanged
        Assert.Equal(["a"], e.Started);
    }

    // --- SetQueue ------------------------------------------------------------------------------

    [Fact]
    public void SetQueueWithAnEmptyListStopsAndClears()
    {
        // How ending a DJ session tears the mix down: stop, empty, and notify, all in one call.
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a", "b"));
        var changes = 0;
        e.QueueChanged += (_, _) => changes++;

        e.SetQueue([]);

        Assert.Equal(PlaybackState.Stopped, e.State);
        Assert.Equal(0, e.QueueCount);
        Assert.Equal(-1, e.CurrentIndex);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetQueueClampsAnOutOfRangeStartIndex()
    {
        var e = new HeadlessEngine();

        e.SetQueue(Tracks("a", "b"), startIndex: 99);

        Assert.Equal(1, e.CurrentIndex);
        Assert.Equal(["b"], e.Started);
    }

    [Fact]
    public void SetQueueAfterRunningDryStartsCleanly()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a"));
        e.Next();                        // dry

        e.SetQueue(Tracks("x", "y"));

        Assert.Equal(0, e.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, e.State);
    }

    // --- Dead files ----------------------------------------------------------------------------

    [Fact]
    public void AFileThatCannotBeOpenedIsSkipped_NotAllowedToStallTheQueue()
    {
        // Harvested files are evicted by the cache cap while queued, so this is routine.
        var e = new HeadlessEngine();
        e.Unopenable.Add("b");

        e.SetQueue(Tracks("a", "b", "c"));
        e.Next();                        // -> b, which fails, so it should land on c

        Assert.Equal(["a", "c"], e.Started);
        Assert.Equal(2, e.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, e.State);
    }

    [Fact]
    public void AnUnopenableLastTrackCountsAsRunningDry_NotAsStopping()
    {
        // It must leave the engine resumable: the next harvested song should start it again.
        var e = new HeadlessEngine();
        e.Unopenable.Add("b");
        var exhausted = 0;
        e.QueueExhausted += (_, _) => exhausted++;

        e.SetQueue(Tracks("a", "b"));
        e.Next();

        Assert.Equal(1, exhausted);

        e.Append(Tracks("c"));
        Assert.Equal(["a", "c"], e.Started);       // resumed, because it ran dry rather than stopped
    }

    [Fact]
    public void EveryFileFailingDoesNotRecurseOrHang()
    {
        var e = new HeadlessEngine();
        e.Unopenable.UnionWith(["a", "b", "c"]);

        e.SetQueue(Tracks("a", "b", "c"));

        Assert.Empty(e.Started);
        Assert.Equal(PlaybackState.Stopped, e.State);
    }

    // --- Events --------------------------------------------------------------------------------

    [Fact]
    public void TrackChangedReportsTheTrackAndItsIndex()
    {
        var e = new HeadlessEngine();
        var seen = new List<(string Path, int Index)>();
        e.TrackChanged += (_, t) => seen.Add((t.Track.Path, t.Index));

        e.SetQueue(Tracks("a", "b"));
        e.Next();

        Assert.Equal([("a", 0), ("b", 1)], seen);
    }

    [Fact]
    public void QueueChangedFiresForBothSetQueueAndAppend()
    {
        var e = new HeadlessEngine();
        var changes = 0;
        e.QueueChanged += (_, _) => changes++;

        e.SetQueue(Tracks("a"));
        e.Append(Tracks("b"));
        e.Append([]);                    // nothing to add — must not notify

        Assert.Equal(2, changes);
    }

    [Fact]
    public void StateChangedIsNotRaisedForANoOpTransition()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a"));
        var states = new List<PlaybackState>();
        e.StateChanged += (_, st) => states.Add(st);

        e.Stop();
        e.Stop();                        // already stopped

        Assert.Equal([PlaybackState.Stopped], states);
    }

    [Fact]
    public void PauseAndResumeOnlyApplyInTheMatchingState()
    {
        var e = new HeadlessEngine();
        e.SetQueue(Tracks("a"));

        e.Resume();                                          // already playing
        Assert.Equal(PlaybackState.Playing, e.State);

        e.Stop();
        e.Pause();                                           // nothing to pause
        Assert.Equal(PlaybackState.Stopped, e.State);
    }
}
