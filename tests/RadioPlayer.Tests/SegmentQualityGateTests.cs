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

    private static void FillSpeechLike(float[] buffer, double fromSec, double toSec)
    {
        var to = Math.Min((int)(toSec * Rate), buffer.Length);
        var rng = new Random(4242);
        var period = Rate / 4; // ~4 Hz syllabic bursts
        for (var i = (int)(fromSec * Rate); i < to; i++)
            buffer[i] = i % period < period / 2 ? (float)(rng.NextDouble() * 2 - 1) * 0.5f : 0f;
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
        Assert.True(kept < 200, $"the talk outro should have been trimmed off, got {kept}s");
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
