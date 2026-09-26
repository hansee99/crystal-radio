using RadioPlayer.Models;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The intro skip and outro guard — blunt backstops for boundary cuts the content-aware edge-trim
/// didn't catch. Per CLAUDE.md the player's job is uninterrupted playback, not archival accuracy,
/// so giving up a couple of seconds to avoid hearing the previous track is the right trade. What
/// these tests pin is that the trade stays *bounded*: a guard must never swallow a track whole or
/// leave a stub.
/// </summary>
public class PlaybackEdgeGuardTests
{
    /// <summary>Reports a fixed duration so the guard arithmetic can be exercised without audio.</summary>
    private sealed class FixedDurationEngine : LocalPlaybackEngine
    {
        public double Duration { get; init; } = 180;
        public List<string> Started { get; } = [];

        public override double DurationSeconds => Duration;
        public override double PositionSeconds => 0;

        private protected override void InitAudio() { }
        private protected override void ArmCrossfadeTrigger(int generation) { }
        private protected override void StopAudio() { }

        private protected override AudioStart TryStartAudio(LocalTrack track, int generation)
        {
            Started.Add(track.Path);
            return AudioStart.Started;
        }

        // The two derivations under test, reached through the real property setters.
        public double Start() => InvokeEffectiveStart(Duration);
        public double End() => InvokeEffectiveEnd(Duration);
    }

    private static FixedDurationEngine Engine(double duration, double skip, double guard) =>
        new() { Duration = duration, IntroSkipSeconds = skip, OutroGuardSeconds = guard };

    [Fact]
    public void ZeroMeansOff_NothingIsTakenFromEitherEnd()
    {
        var e = Engine(180, skip: 0, guard: 0);

        Assert.Equal(0, e.Start());
        Assert.Equal(180, e.End());
    }

    [Fact]
    public void AGuardTakesItsSecondsFromEachEnd()
    {
        var e = Engine(180, skip: 2, guard: 3);

        Assert.Equal(2, e.Start());
        Assert.Equal(177, e.End());
    }

    /// <summary>
    /// A guard larger than the track must not skip it entirely or leave a stub — a two-second
    /// fragment is worse than the residue the guard exists to hide.
    ///
    /// Asserted as a property rather than exact numbers, because the exact answer differs by
    /// duration and the invariant is what matters: never start past the end, and never shorten a
    /// track below the crossfade minimum. (A 12s track with a 5s guard plays 10s, not 7s and not
    /// all 12 — the clamp gives back what the guard over-reached for.)
    /// </summary>
    [Theory]
    [InlineData(8)]    // shorter than MinDurationForCrossfade
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    public void AShortTrackIsNeverLeftAsAStub(double duration)
    {
        var e = Engine(duration, skip: 5, guard: 5);

        Assert.Equal(0, e.Start());                                   // no skipping into a short track
        Assert.InRange(e.End(), Math.Min(duration, 10), duration);    // still substantially there
    }

    [Fact]
    public void AnAbsurdGuardCannotEatTheTrack()
    {
        var e = Engine(30, skip: 60, guard: 60);

        Assert.Equal(0, e.Start());              // skipping past the end would play nothing
        Assert.True(e.End() >= 10, $"left only {e.End()}s");
    }

    [Fact]
    public void NegativeValuesAreTreatedAsOff()
    {
        var e = Engine(180, skip: -5, guard: -5);

        Assert.Equal(0, e.Start());
        Assert.Equal(180, e.End());
    }

    [Fact]
    public void TheGuardsStillLeaveMostOfANormalTrack()
    {
        // Sanity on the real trade: 3s + 3s off a 3½-minute song is under 3% of it.
        var e = Engine(210, skip: 3, guard: 3);

        Assert.Equal(3, e.Start());
        Assert.Equal(207, e.End());
    }
}
