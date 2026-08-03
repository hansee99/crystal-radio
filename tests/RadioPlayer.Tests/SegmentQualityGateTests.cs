using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The keep/reject decision is now "how much music is left after the edges are trimmed", not
/// "what fraction of the file scored as music". These cover the arithmetic behind that gate,
/// driven by the real detector over synthesised audio — the full <c>Evaluate</c> path needs BASS
/// to decode a file, so this exercises the part that actually makes the decision.
/// </summary>
public class SegmentQualityGateTests
{
    private const int Rate = 22050;

    private static float[] Buffer(double seconds) => new float[(int)(seconds * Rate)];

    private static void FillMusic(float[] buffer, double fromSec, double toSec)
    {
        var to = Math.Min((int)(toSec * Rate), buffer.Length);
        for (var i = (int)(fromSec * Rate); i < to; i++)
            buffer[i] = 0.5f * MathF.Sin(2 * MathF.PI * 220f * i / Rate);
    }

    /// <summary>Irregular syllable-like bursts. The jitter is load-bearing — a fixed period is a
    /// metronome, which PulseStrength (issue #8) correctly reads as a beat and so as MUSIC. Real
    /// speech has envelope energy near 4 Hz but no steady period.</summary>
    private static void FillSpeechLike(float[] buffer, double fromSec, double toSec)
    {
        var to = Math.Min((int)(toSec * Rate), buffer.Length);
        var rng = new Random(4242);
        var i = (int)(fromSec * Rate);
        while (i < to)
        {
            var period = (int)(Rate / (2.5 + rng.NextDouble() * 3.5)); // 2.5-6 Hz, jittered
            var voiced = (int)(period * (0.35 + rng.NextDouble() * 0.35));
            for (var j = 0; j < period && i < to; j++, i++)
                buffer[i] = j < voiced ? (float)(rng.NextDouble() * 2 - 1) * 0.5f : 0f;
        }
    }

    private static double KeptSecondsFor(float[] audio) =>
        SegmentQualityChecker.KeptSeconds(new MusicDetector().Analyze(audio, Rate));

    [Fact]
    public void AnAdBreak_TrimsAwayToNothing_SoTheGateRejectsIt()
    {
        // 25 s of talk, the shape of every genuine rejection observed in real sessions.
        var audio = Buffer(25);
        FillSpeechLike(audio, 0, 25);

        Assert.True(KeptSecondsFor(audio) < 60, "an all-talk segment should leave nothing to keep");
    }

    [Fact]
    public void ASongWithATalkOutro_KeepsItsMusicAndClearsTheGate()
    {
        // The Southern Cross case: real music, then talk to the end. The trim should remove the
        // outro and leave the song — the whole-file fraction would have thrown the lot away.
        var audio = Buffer(240);
        FillMusic(audio, 0, 170);
        FillSpeechLike(audio, 170, 240);

        var kept = KeptSecondsFor(audio);

        Assert.True(kept > 60, $"the music should survive the gate, got {kept}s");
        // A substantial bite out of the outro, not all of it. White-noise bursts are a crude
        // stand-in for speech and throw the odd music-looking window, so the scan stops partway;
        // on real talk the same setting trims far more (measured: 10 of 15 confirmed talk segments
        // trimmed away entirely, median 35.5s). The bound is a floor that still catches a broken
        // trim — with the scan defeated by a single window, as it was before TrimStopRun, this
        // came back at the full 240.5s.
        Assert.True(kept < 215, $"the talk outro should have been largely trimmed, got {kept}s");
    }

    [Fact]
    public void AShortMusicalSting_IsRejectedForLength()
    {
        // The 9-second sliver a bouncing ICY title produced in a real session, which was kept and
        // played as a "song".
        var audio = Buffer(9);
        FillMusic(audio, 0, 9);

        Assert.True(KeptSecondsFor(audio) < 60, "a 9s fragment is not a song");
    }

    [Fact]
    public void AFullLengthTrack_PassesUntouched()
    {
        var audio = Buffer(240);
        FillMusic(audio, 0, 240);

        Assert.True(KeptSecondsFor(audio) >= 235, "a clean track should not lose audio");
    }
}
